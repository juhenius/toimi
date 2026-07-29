using Microsoft.Extensions.AI;
using Xunit;

namespace Toimi.Core.Tests;

public class ToimiClientFactoryTests
{
  [Fact]
  public void Refresh_replaces_only_the_time_line_and_preserves_the_catalogs()
  {
    var messages = ToimiClientFactory.CreateInitialMessages("skillA — does things", "typeB — a schema");

    // Simulate a stale session: rewrite the first line to an obviously old timestamp,
    // exactly the shape RefreshDynamicContext must recognize.
    var text = messages[1].Text ?? "";
    var rest = text[text.IndexOf('\n')..];
    messages[1] = new ChatMessage(ChatRole.System, "Current time: 1999-01-01 00:00 UTC (stale)" + rest);

    ToimiClientFactory.RefreshDynamicContext(messages);

    var refreshed = messages[1].Text ?? "";
    Assert.DoesNotContain("1999", refreshed);
    Assert.StartsWith("Current time: ", refreshed);
    // The injected catalogs must survive the refresh — losing them mid-session
    // silently strips the model's knowledge of available skills and types.
    Assert.Contains("Available skills", refreshed);
    Assert.Contains("skillA", refreshed);
    Assert.Contains("Available data types", refreshed);
    Assert.Contains("typeB", refreshed);
  }

  [Fact]
  public void Refresh_is_a_silent_no_op_when_the_structure_does_not_match()
  {
    // Each of these shapes must neither throw nor mutate — but note the flip side
    // pinned here: if CreateInitialMessages ever changes its layout, Refresh
    // degrades to never updating the clock (silently), which is why this
    // round-trip suite exists.
    var single = new List<ChatMessage> { new(ChatRole.System, "only one") };
    ToimiClientFactory.RefreshDynamicContext(single);
    Assert.Equal("only one", single[0].Text);

    var wrongRole = new List<ChatMessage> { new(ChatRole.System, "sys"), new(ChatRole.User, "hi") };
    ToimiClientFactory.RefreshDynamicContext(wrongRole);
    Assert.Equal("hi", wrongRole[1].Text);

    var wrongPrefix = new List<ChatMessage> { new(ChatRole.System, "sys"), new(ChatRole.System, "not a time line\nrest") };
    ToimiClientFactory.RefreshDynamicContext(wrongPrefix);
    Assert.Equal("not a time line\nrest", wrongPrefix[1].Text);
  }

  [Fact]
  public void Initial_messages_omit_absent_catalog_sections()
  {
    var messages = ToimiClientFactory.CreateInitialMessages(skillSummary: null, typeCatalog: null);

    Assert.Equal(2, messages.Count);
    var context = messages[1].Text ?? "";
    Assert.StartsWith("Current time: ", context);
    Assert.DoesNotContain("Available skills", context);
    Assert.DoesNotContain("Available data types", context);
  }
}
