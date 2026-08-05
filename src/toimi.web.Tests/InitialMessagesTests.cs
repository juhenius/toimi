using Toimi.Core;
using Xunit;

namespace Toimi.Web.Tests;

public class InitialMessagesTests
{
  [Fact]
  public void Includes_type_catalog_when_provided()
  {
    var messages = new ConversationContext(skillSummary: null, typeCatalog: /*lang=json,strict*/ """[{"name":"memory"}]""").ToChatMessages();
    var context = string.Join("\n", messages.Select(m => m.Text));
    Assert.Contains("Available data types", context);
    Assert.Contains("memory", context);
  }

  [Fact]
  public void Omits_type_catalog_when_absent()
  {
    var messages = new ConversationContext().ToChatMessages();
    var context = string.Join("\n", messages.Select(m => m.Text));
    Assert.DoesNotContain("Available data types", context);
  }

  [Fact]
  public void Includes_both_skills_and_type_catalog_when_both_provided()
  {
    var messages = new ConversationContext(skillSummary: /*lang=json,strict*/ """[{"name":"daily-briefing"}]""", typeCatalog: /*lang=json,strict*/ """[{"name":"memory"}]""").ToChatMessages();
    var context = string.Join("\n", messages.Select(m => m.Text));
    Assert.Contains("Available skills", context);
    Assert.Contains("daily-briefing", context);
    Assert.Contains("Available data types", context);
    Assert.Contains("memory", context);
  }
}
