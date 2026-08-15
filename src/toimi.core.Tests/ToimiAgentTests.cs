using Microsoft.Extensions.AI;
using Toimi.Core.Configuration;
using Toimi.Core.Llm;
using Xunit;

namespace Toimi.Core.Tests;

public class ToimiAgentTests
{
  private sealed class FakeLlmProvider(FakeChatClient chat) : ILlmClientProvider
  {
    public string ResolveModel(ModelTier tier)
    {
      return "fake-model";
    }

    public LlmSession Create(ModelTier tier = ModelTier.Fast)
    {
      var notifier = new ToolCallNotifier(chat);
      return new LlmSession(notifier, notifier, ResolveModel(tier));
    }
  }

  // Empty McpServers: the aggregator connects to nothing, list_skills/list_types
  // return null, and the whole agent runs fully offline.
  private static ToimiConfiguration Config()
  {
    return new ToimiConfiguration { OpenAI = new OpenAIOptions { ApiKey = "test" } };
  }

  private static Task<ToimiAgent> StartAsync(FakeChatClient chat, ContextBudget? budget = null)
  {
    return ToimiAgent.StartAsync(Config(), new FakeLlmProvider(chat), budget: budget);
  }

  private static async Task<List<TurnUpdate>> CollectAsync(ToimiAgent agent, string text)
  {
    var updates = new List<TurnUpdate>();
    await foreach (var update in agent.SendAsync(text))
    {
      updates.Add(update);
    }

    return updates;
  }

  [Fact]
  public async Task Start_with_no_servers_yields_zero_tools_and_the_two_system_messages()
  {
    await using var agent = await StartAsync(new FakeChatClient());

    Assert.Equal(0, agent.ToolCount);
    Assert.Null(agent.SkillSummary);
    Assert.Null(agent.TypeCatalog);
    Assert.Equal(2, agent.Messages.Count);
    Assert.All(agent.Messages, m => Assert.Equal(ChatRole.System, m.Role));
  }

  [Fact]
  public async Task SendAsync_streams_tokens_and_appends_user_and_assistant_to_the_transcript()
  {
    var chat = new FakeChatClient
    {
      StreamUpdates = [new(ChatRole.Assistant, "hello "), new(ChatRole.Assistant, "world")],
    };
    await using var agent = await StartAsync(chat);

    var updates = await CollectAsync(agent, "hi");

    Assert.Equal(["hello ", "world"], updates.OfType<TokenUpdate>().Select(t => t.Text));
    var completed = Assert.IsType<TurnCompleted>(updates[^1]);
    Assert.Equal("hello world", completed.ResponseText);
    Assert.Null(completed.ToolCallsJson);
    Assert.Equal(ChatRole.User, agent.Messages[^2].Role);
    Assert.Equal("hi", agent.Messages[^2].Text);
    Assert.Equal(ChatRole.Assistant, agent.Messages[^1].Role);
    Assert.Equal("hello world", agent.Messages[^1].Text);
  }

  [Fact]
  public async Task Tool_events_surface_as_updates_and_as_the_unified_wire_json()
  {
    var chat = new FakeChatClient
    {
      StreamUpdates =
      [
        new(ChatRole.Assistant, [new FunctionCallContent("c1", "search", new Dictionary<string, object?> { ["query"] = "milk" })]),
        new(ChatRole.Assistant, "found it"),
      ],
    };
    await using var agent = await StartAsync(chat);

    var updates = await CollectAsync(agent, "find milk");

    var call = Assert.Single(updates.OfType<ToolCallUpdate>());
    Assert.Equal("c1", call.CallId);
    Assert.Equal("search", call.Name);
    Assert.Contains("milk", call.Arguments);
    var completed = Assert.IsType<TurnCompleted>(updates[^1]);
    Assert.NotNull(completed.ToolCallsJson);
    Assert.Contains("\"type\":\"call\"", completed.ToolCallsJson);
    Assert.Contains("\"CallId\":\"c1\"", completed.ToolCallsJson);
  }

  [Fact]
  public async Task Mid_stream_failure_keeps_the_user_message_and_appends_no_assistant()
  {
    var chat = new FakeChatClient
    {
      StreamUpdates = [new(ChatRole.Assistant, "partial")],
      ThrowAfterStreamUpdates = 1,
    };
    await using var agent = await StartAsync(chat);
    var before = agent.Messages.Count;

    await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(agent, "doomed"));

    // The host persists the user message BEFORE SendAsync, so on failure it must
    // STAY in the transcript; the assistant response must not.
    Assert.Equal(before + 1, agent.Messages.Count);
    Assert.Equal(ChatRole.User, agent.Messages[^1].Role);
    Assert.Equal("doomed", agent.Messages[^1].Text);
  }

  [Fact]
  public async Task Real_usage_anchors_the_budget_before_the_assistant_append()
  {
    var budget = new ContextBudget();
    var chat = new FakeChatClient
    {
      StreamUpdates =
      [
        new(ChatRole.Assistant, "123456789"),
        new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 100, OutputTokenCount = 7, TotalTokenCount = 107 })]),
      ],
    };
    await using var agent = await StartAsync(chat, budget);

    var updates = await CollectAsync(agent, "q");

    var completed = Assert.IsType<TurnCompleted>(updates[^1]);
    Assert.Equal(100, completed.PromptTokens);
    Assert.Equal(7, completed.CompletionTokens);
    Assert.Equal(107, completed.TotalTokens);
    // The anchor was recorded BEFORE the 9-char assistant reply was appended, so
    // the estimate is anchor + delta/3 = 100 + 3. If RecordUsage ran after the
    // append the estimate would be a flat 100 — undercounting by one response.
    Assert.Equal(103, budget.Estimate([.. agent.Messages]));
  }

  [Fact]
  public async Task Missing_usage_falls_back_to_chars_based_estimates()
  {
    var chat = new FakeChatClient { StreamUpdates = [new(ChatRole.Assistant, "hello from fake")] };
    await using var agent = await StartAsync(chat);

    var updates = await CollectAsync(agent, "q");

    var completed = Assert.IsType<TurnCompleted>(updates[^1]);
    Assert.Equal(ContextBudget.TotalChars([.. agent.Messages]) / 4, completed.PromptTokens);
    Assert.Equal("hello from fake".Length / 4, completed.CompletionTokens);
    Assert.Equal(completed.PromptTokens + completed.CompletionTokens, completed.TotalTokens);
  }

  [Fact]
  public async Task DiscardLastAssistantMessage_removes_only_a_trailing_assistant_message()
  {
    var chat = new FakeChatClient { StreamUpdates = [new(ChatRole.Assistant, "answer")] };
    await using var agent = await StartAsync(chat);
    await CollectAsync(agent, "q");
    var count = agent.Messages.Count;

    agent.DiscardLastAssistantMessage();

    Assert.Equal(count - 1, agent.Messages.Count);
    Assert.Equal(ChatRole.User, agent.Messages[^1].Role);

    // Trailing message is now the user's — a second discard must be a no-op.
    agent.DiscardLastAssistantMessage();
    Assert.Equal(count - 1, agent.Messages.Count);
  }

  [Fact]
  public async Task Reset_restores_the_initial_messages_and_clears_the_budget_anchor()
  {
    var budget = new ContextBudget();
    var chat = new FakeChatClient
    {
      StreamUpdates =
      [
        new(ChatRole.Assistant, "answer"),
        new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 500 })]),
      ],
    };
    await using var agent = await StartAsync(chat, budget);
    await CollectAsync(agent, "q");
    Assert.True(agent.Messages.Count > 2);

    agent.Reset();

    Assert.Equal(2, agent.Messages.Count);
    Assert.All(agent.Messages, m => Assert.Equal(ChatRole.System, m.Role));
    // Anchor cleared: the estimate is back to the plain chars/4 heuristic.
    Assert.Equal(ContextBudget.TotalChars([.. agent.Messages]) / 4, budget.Estimate([.. agent.Messages]));
  }

  [Fact]
  public async Task RunTurnAsync_returns_the_terminal_update()
  {
    var chat = new FakeChatClient { StreamUpdates = [new(ChatRole.Assistant, "done")] };
    await using var agent = await StartAsync(chat);

    var completed = await agent.RunTurnAsync("go");

    Assert.Equal("done", completed.ResponseText);
  }

  [Fact]
  public async Task AppendMessage_adds_context_without_running_a_turn()
  {
    var chat = new FakeChatClient { StreamUpdates = [new(ChatRole.Assistant, "ok")] };
    await using var agent = await StartAsync(chat);

    agent.AppendMessage(ChatRole.User, "old question");
    agent.AppendMessage(ChatRole.Assistant, "old answer");
    Assert.Equal(4, agent.Messages.Count);

    // Replayed history rides along as context on the next turn.
    await CollectAsync(agent, "new question");
    Assert.Contains(chat.Requests[^1], m => (m.Text ?? "").Contains("old question"));
  }

  [Fact]
  public async Task Sequential_turns_succeed_but_re_enumerating_the_same_turn_throws()
  {
    var chat = new FakeChatClient { StreamUpdates = [new(ChatRole.Assistant, "ok")] };
    await using var agent = await StartAsync(chat);

    // Sequential turns: two separate SendAsync calls, each enumerated fully — fine.
    await CollectAsync(agent, "first");
    await CollectAsync(agent, "second");

    // Re-enumerating the SAME returned sequence must throw and must not re-append
    // a duplicate user message.
    var turn = agent.SendAsync("q");
    await foreach (var _ in turn)
    {
    }

    var countAfterFirstEnumeration = agent.Messages.Count;

    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
    {
      await foreach (var _ in turn)
      {
      }
    });

    Assert.Equal(countAfterFirstEnumeration, agent.Messages.Count);
    Assert.Equal(1, agent.Messages.Count(m => m.Role == ChatRole.User && m.Text == "q"));
  }
}
