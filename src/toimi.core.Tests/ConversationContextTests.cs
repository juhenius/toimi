using Microsoft.Extensions.AI;
using Xunit;

namespace Toimi.Core.Tests;

public class ConversationContextTests
{
  private sealed class FakeTime : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = new(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
      return Now;
    }
  }

  [Fact]
  public void Initial_layout_is_system_prompt_then_dynamic_context()
  {
    var messages = new ConversationContext().ToChatMessages();

    Assert.Equal(2, messages.Count);
    Assert.All(messages, m => Assert.Equal(ChatRole.System, m.Role));
    Assert.Contains("You are Toimi", messages[0].Text ?? "");
    Assert.StartsWith("Current time: ", messages[1].Text ?? "");
  }

  [Fact]
  public void Dynamic_context_omits_absent_catalog_sections()
  {
    var messages = new ConversationContext(skillSummary: null, typeCatalog: null).ToChatMessages();

    var context = messages[1].Text ?? "";
    Assert.DoesNotContain("Available skills", context);
    Assert.DoesNotContain("Available data types", context);
  }

  [Fact]
  public void Refresh_updates_the_clock_and_preserves_the_catalogs()
  {
    var time = new FakeTime();
    var context = new ConversationContext("skillA — does things", "typeB — a schema", timeProvider: time);
    Assert.Contains("1999", context.ToChatMessages()[1].Text ?? "");

    time.Now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    context.RefreshDynamicContext();

    var refreshed = context.ToChatMessages()[1].Text ?? "";
    Assert.DoesNotContain("1999", refreshed);
    Assert.StartsWith("Current time: 2026-08-05 12:00", refreshed);
    // The catalogs must survive the refresh — losing them mid-session silently
    // strips the model's knowledge of available skills and types.
    Assert.Contains("Available skills", refreshed);
    Assert.Contains("skillA", refreshed);
    Assert.Contains("Available data types", refreshed);
    Assert.Contains("typeB", refreshed);
  }

  [Fact]
  public void Refresh_cannot_silently_degrade_whatever_the_transcript_shape()
  {
    // The old ToimiClientFactory.RefreshDynamicContext located the clock by
    // index-1 + "Current time: " prefix and silently no-opped on any other
    // shape (pinned by the deleted ToimiClientFactoryTests). The slot is a
    // field now: refresh works no matter what the window holds.
    var time = new FakeTime();
    var context = new ConversationContext(timeProvider: time);
    context.Append(ChatRole.System, "fenced entity context");
    context.AppendUser("hi");
    context.AppendAssistant("hello");

    time.Now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    context.RefreshDynamicContext();

    Assert.StartsWith("Current time: 2026-08-05 12:00", context.ToChatMessages()[1].Text ?? "");
  }

  [Fact]
  public void Appends_land_in_order_after_the_slots()
  {
    var context = new ConversationContext();
    context.Append(ChatRole.System, "entity data");
    context.AppendUser("question");
    context.AppendAssistant("answer");

    var messages = context.ToChatMessages();
    Assert.Equal(5, messages.Count);
    Assert.Equal("entity data", messages[2].Text);
    Assert.Equal(ChatRole.User, messages[3].Role);
    Assert.Equal("question", messages[3].Text);
    Assert.Equal(ChatRole.Assistant, messages[4].Role);
    Assert.Equal("answer", messages[4].Text);
  }

  [Fact]
  public void AppendAssistant_anchors_the_budget_before_the_append()
  {
    var budget = new ContextBudget();
    var context = new ConversationContext(budget: budget);
    context.AppendUser("q");

    context.AppendAssistant("123456789", promptTokensAsSent: 100);

    // Anchored to the transcript AS SENT (before the 9-char reply was appended):
    // estimate = 100 + 9/3. If the anchor were taken after the append, the
    // estimate would be a flat 100 — undercounting by one response. The old
    // code enforced this ordering by comment; now it is unwritable.
    Assert.Equal(103, context.Estimate());
  }

  [Fact]
  public void AppendAssistant_without_usage_leaves_the_estimate_on_chars_over_4()
  {
    var context = new ConversationContext();
    context.AppendUser("q");
    context.AppendAssistant("hello");

    Assert.Equal(ContextBudget.TotalChars(context.ToChatMessages()) / 4, context.Estimate());
  }

  [Fact]
  public void Discard_removes_only_a_trailing_assistant_message_and_never_a_slot()
  {
    var context = new ConversationContext();
    Assert.False(context.DiscardLastAssistantMessage()); // empty window: the slots are untouchable
    Assert.Equal(2, context.ToChatMessages().Count);

    context.AppendUser("q");
    Assert.False(context.DiscardLastAssistantMessage()); // trailing user: no-op
    Assert.Equal(3, context.ToChatMessages().Count);

    context.AppendAssistant("a");
    Assert.True(context.DiscardLastAssistantMessage());
    Assert.Equal(ChatRole.User, context.ToChatMessages()[^1].Role);
  }

  [Fact]
  public void Reset_clears_window_and_budget_but_keeps_the_catalogs()
  {
    var budget = new ContextBudget();
    var context = new ConversationContext("skillA", "typeB", budget);
    context.AppendUser("q");
    context.AppendAssistant("a", promptTokensAsSent: 500);

    context.Reset();

    var messages = context.ToChatMessages();
    Assert.Equal(2, messages.Count);
    Assert.Contains("skillA", messages[1].Text ?? "");
    Assert.Contains("typeB", messages[1].Text ?? "");
    // Anchor cleared: back to the plain chars/4 heuristic.
    Assert.Equal(ContextBudget.TotalChars(messages) / 4, context.Estimate());
  }

  [Fact]
  public void ToChatMessages_returns_an_immutable_snapshot()
  {
    var context = new ConversationContext();
    var before = context.ToChatMessages();

    context.AppendUser("added later");

    Assert.Equal(2, before.Count); // an earlier snapshot stays frozen
    Assert.Equal(3, context.ToChatMessages().Count);
    // The C2 review flagged that Messages could be downcast to the live list
    // and mutated behind the agent's back — the snapshot must not be a List.
    Assert.IsNotType<List<ChatMessage>>(before);
  }
}
