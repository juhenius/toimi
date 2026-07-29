using Microsoft.Extensions.AI;
using Xunit;

namespace Toimi.Core.Tests;

public class ContextManagerTests
{
  private static ChatMessage Text(ChatRole role, int chars)
  {
    return new ChatMessage(role, new string('x', chars));
  }

  [Fact]
  public void Estimate_without_anchor_falls_back_to_chars_over_4()
  {
    var budget = new ContextBudget();
    var messages = new List<ChatMessage> { Text(ChatRole.User, 4000) };

    Assert.Equal(1000, budget.Estimate(messages));
  }

  [Fact]
  public void Estimate_with_anchor_uses_real_tokens_plus_conservative_delta()
  {
    var budget = new ContextBudget();
    var messages = new List<ChatMessage> { Text(ChatRole.User, 4000) };
    budget.RecordUsage(2500, messages); // reality: denser than 4 chars/token

    messages.Add(Text(ChatRole.Assistant, 300));

    Assert.Equal(2500 + (300 / 3), budget.Estimate(messages));
  }

  [Fact]
  public void Estimate_clamps_when_messages_shrink_below_the_anchor()
  {
    // The hub's error path removes the last message after an anchor was recorded;
    // the delta must clamp to zero instead of going negative.
    var budget = new ContextBudget();
    var messages = new List<ChatMessage> { Text(ChatRole.User, 4000), Text(ChatRole.Assistant, 1000) };
    budget.RecordUsage(2500, messages);

    messages.RemoveAt(messages.Count - 1);

    Assert.Equal(2500, budget.Estimate(messages));
  }

  [Fact]
  public async Task Compaction_includes_tool_calls_and_results_in_summary_input()
  {
    var client = new FakeChatClient();
    var messages = new List<ChatMessage> { new(ChatRole.System, "sys") };
    for (var i = 0; i < 20; i++)
    {
      messages.Add(Text(ChatRole.User, 10));
      var withTool = new ChatMessage(ChatRole.Assistant, [
        new FunctionCallContent($"call{i}", "search", new Dictionary<string, object?> { ["query"] = "milk" }),
      ]);
      messages.Add(withTool);
      messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call{i}", "found 3 items")]));
    }

    var compacted = await ContextManager.CompactIfNeeded(messages, client, budget: null, maxTokens: 1, ct: default);

    Assert.True(compacted);
    var summaryRequest = Assert.Single(client.Requests);
    var payload = string.Join("\n", summaryRequest.Select(m => m.Text));
    Assert.Contains("search", payload);       // tool call name present
    Assert.Contains("found 3 items", payload); // tool result present
  }

  [Fact]
  public async Task Compaction_resets_the_budget_anchor()
  {
    var client = new FakeChatClient();
    var budget = new ContextBudget();
    var messages = new List<ChatMessage>();
    for (var i = 0; i < 30; i++)
    {
      messages.Add(Text(ChatRole.User, 100));
    }
    budget.RecordUsage(999_999, messages); // absurd anchor forces compaction and must then be discarded

    var compacted = await ContextManager.CompactIfNeeded(messages, client, budget, maxTokens: 100_000, ct: default);

    Assert.True(compacted);
    // Anchor gone: estimate is chars/4 of the compacted list, far below the old anchor.
    Assert.True(budget.Estimate(messages) < 999_999);
  }

  [Fact]
  public async Task Compaction_that_fails_to_summarize_proceeds_uncompacted()
  {
    var client = new FakeChatClient { Throw = true };
    var messages = new List<ChatMessage>();
    for (var i = 0; i < 30; i++)
    {
      messages.Add(Text(ChatRole.User, 100));
    }

    var before = messages.Count;

    var compacted = await ContextManager.CompactIfNeeded(messages, client, budget: null, maxTokens: 1, ct: default);

    Assert.False(compacted);
    Assert.Equal(before, messages.Count); // untouched on failure
  }

  [Fact]
  public async Task No_compaction_below_the_limit()
  {
    var client = new FakeChatClient();
    var messages = new List<ChatMessage> { Text(ChatRole.User, 100) };

    var compacted = await ContextManager.CompactIfNeeded(messages, client, budget: null, maxTokens: 100_000, ct: default);

    Assert.False(compacted);
    Assert.Empty(client.Requests);
  }

  [Fact]
  public async Task Second_compaction_folds_the_prior_summary_instead_of_accumulating()
  {
    var client = new FakeChatClient();
    var messages = new List<ChatMessage> { new(ChatRole.System, "base prompt") };
    for (var i = 0; i < 40; i++)
    {
      messages.Add(Text(ChatRole.User, 100));
    }

    Assert.True(await ContextManager.CompactIfNeeded(messages, client, budget: null, maxTokens: 1, ct: default));

    for (var i = 0; i < 20; i++)
    {
      messages.Add(Text(ChatRole.User, 100));
    }

    Assert.True(await ContextManager.CompactIfNeeded(messages, client, budget: null, maxTokens: 1, ct: default));

    // The old summary must be summarized INTO the new one, not protected beside it —
    // otherwise every compaction leaves one more permanent System message and the
    // reclaimable window shrinks to nothing.
    Assert.Equal(1, messages.Count(m =>
      m.Role == ChatRole.System && (m.Text?.StartsWith("Summary of earlier conversation:", StringComparison.Ordinal) ?? false)));
    Assert.Equal("base prompt", messages[0].Text); // the real system prompt survives
  }

  [Fact]
  public void Estimate_counts_function_call_and_result_content()
  {
    var budget = new ContextBudget();
    var payload = new string('r', 8000);
    var messages = new List<ChatMessage>
    {
      new(ChatRole.Assistant, [new FunctionCallContent("call1", "search", new Dictionary<string, object?> { ["query"] = "milk" })]),
      new(ChatRole.Tool, [new FunctionResultContent("call1", payload)]),
    };

    // Tool-only messages have no TextContent; the estimate must still see their bulk
    // or tool-heavy histories never trigger compaction.
    Assert.True(budget.Estimate(messages) >= payload.Length / 4);
  }

  [Fact]
  public async Task Tool_result_heavy_history_triggers_compaction_without_an_anchor()
  {
    var client = new FakeChatClient();
    var messages = new List<ChatMessage>();
    for (var i = 0; i < 30; i++)
    {
      messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call{i}", new string('r', 1000))]));
    }

    // 30k chars of tool results ≈ 7.5k tokens — over a 5k budget. This is the
    // AgentRunner path (budget: null, chars/4 fallback only).
    var compacted = await ContextManager.CompactIfNeeded(messages, client, budget: null, maxTokens: 5000, ct: default);

    Assert.True(compacted);
  }
}
