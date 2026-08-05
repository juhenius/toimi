using Microsoft.Extensions.AI;
using Xunit;

namespace Toimi.Core.Tests;

public class ConversationContextCompactionTests
{
  private static ConversationContext Filled(int userMessages, int charsEach = 100, ContextBudget? budget = null)
  {
    var context = new ConversationContext(budget: budget);
    for (var i = 0; i < userMessages; i++)
    {
      context.AppendUser(new string('x', charsEach));
    }

    return context;
  }

  [Fact]
  public async Task Compaction_replaces_older_window_messages_with_one_summary_and_keeps_the_slots()
  {
    var client = new FakeChatClient { NextResponseText = "the gist" };
    var context = Filled(30);

    var compacted = await context.CompactIfNeededAsync(client, maxTokens: 1);

    Assert.True(compacted);
    var messages = context.ToChatMessages();
    // Slots intact, summary in its slot, the 10 most recent window messages kept.
    Assert.Contains("You are Toimi", messages[0].Text ?? "");
    Assert.StartsWith("Current time: ", messages[1].Text ?? "");
    Assert.StartsWith("Summary of earlier conversation:", messages[2].Text ?? "");
    Assert.Contains("the gist", messages[2].Text ?? "");
    Assert.Equal(2 + 1 + 10, messages.Count);
  }

  [Fact]
  public async Task No_compaction_below_the_limit()
  {
    var client = new FakeChatClient();
    var context = Filled(1);

    var compacted = await context.CompactIfNeededAsync(client, maxTokens: 100_000);

    Assert.False(compacted);
    Assert.Empty(client.Requests);
  }

  [Fact]
  public async Task Compaction_preserves_leading_window_system_messages()
  {
    // AgentRunner appends the fenced entity payload as a window-leading System
    // message; compaction must protect it exactly like the old leading-run rule.
    var client = new FakeChatClient();
    var context = new ConversationContext();
    context.Append(ChatRole.System, "<entity_data>payload</entity_data>");
    for (var i = 0; i < 30; i++)
    {
      context.AppendUser(new string('x', 100));
    }

    Assert.True(await context.CompactIfNeededAsync(client, maxTokens: 1));

    var messages = context.ToChatMessages();
    Assert.Contains(messages, m => m.Role == ChatRole.System && (m.Text ?? "").Contains("<entity_data>"));
    // ...and it was protected FROM summarization, not summarized away.
    var summaryInput = string.Join("\n", Assert.Single(client.Requests).Select(m => m.Text));
    Assert.DoesNotContain("<entity_data>", summaryInput);
  }

  [Fact]
  public async Task Compaction_includes_tool_calls_and_results_in_summary_input()
  {
    var client = new FakeChatClient();
    var context = new ConversationContext();
    for (var i = 0; i < 20; i++)
    {
      context.AppendUser(new string('x', 10));
      context.Append(new ChatMessage(ChatRole.Assistant, [
        new FunctionCallContent($"call{i}", "search", new Dictionary<string, object?> { ["query"] = "milk" }),
      ]));
      context.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call{i}", "found 3 items")]));
    }

    Assert.True(await context.CompactIfNeededAsync(client, maxTokens: 1));

    var payload = string.Join("\n", Assert.Single(client.Requests).Select(m => m.Text));
    Assert.Contains("search", payload);       // tool call name present
    Assert.Contains("found 3 items", payload); // tool result present
  }

  [Fact]
  public async Task Compaction_resets_the_budget_anchor()
  {
    var client = new FakeChatClient();
    var budget = new ContextBudget();
    var context = Filled(30, budget: budget);
    // Absurd anchor forces compaction and must then be discarded.
    context.AppendAssistant("a", promptTokensAsSent: 999_999);

    Assert.True(await context.CompactIfNeededAsync(client, maxTokens: 100_000));

    // Anchor gone: the estimate is chars/4 of the compacted transcript.
    Assert.True(context.Estimate() < 999_999);
  }

  [Fact]
  public async Task Compaction_that_fails_to_summarize_proceeds_uncompacted()
  {
    var client = new FakeChatClient { Throw = true };
    var context = Filled(30);
    var before = context.ToChatMessages().Count;

    var compacted = await context.CompactIfNeededAsync(client, maxTokens: 1);

    Assert.False(compacted);
    Assert.Equal(before, context.ToChatMessages().Count); // untouched on failure
  }

  [Fact]
  public async Task Second_compaction_folds_the_prior_summary_instead_of_accumulating()
  {
    var client = new FakeChatClient { NextResponseText = "first summary gist" };
    var context = Filled(40);
    Assert.True(await context.CompactIfNeededAsync(client, maxTokens: 1));

    client.NextResponseText = "second summary gist";
    for (var i = 0; i < 20; i++)
    {
      context.AppendUser(new string('x', 100));
    }

    Assert.True(await context.CompactIfNeededAsync(client, maxTokens: 1));

    // The old summary must be summarized INTO the new one, not kept beside it —
    // otherwise every compaction leaves one more permanent System message and
    // the reclaimable window shrinks to nothing.
    var messages = context.ToChatMessages();
    Assert.Equal(1, messages.Count(m =>
      m.Role == ChatRole.System && (m.Text ?? "").StartsWith("Summary of earlier conversation:", StringComparison.Ordinal)));
    Assert.Contains("second summary gist", messages[2].Text ?? "");
    Assert.Contains("first summary gist", string.Join("\n", client.Requests[^1].Select(m => m.Text)));
    Assert.Contains("You are Toimi", messages[0].Text ?? ""); // the real system prompt survives
  }

  [Fact]
  public async Task Tool_result_heavy_history_triggers_compaction_without_an_anchor()
  {
    var client = new FakeChatClient();
    var context = new ConversationContext();
    for (var i = 0; i < 30; i++)
    {
      context.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call{i}", new string('r', 1000))]));
    }

    // 30k chars of tool results ≈ 7.5k tokens — over a 5k budget with no anchor
    // recorded (the AgentRunner path).
    Assert.True(await context.CompactIfNeededAsync(client, maxTokens: 5000));
  }
}
