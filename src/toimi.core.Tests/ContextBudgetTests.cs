using Microsoft.Extensions.AI;
using Xunit;

namespace Toimi.Core.Tests;

public class ContextBudgetTests
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
}
