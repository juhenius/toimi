using System.Text.Json;
using toimi.tools.tietue.Handlers;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TemplateRendererTests
{
  private static JsonDocument Doc(string j)
  {
    return JsonDocument.Parse(j);
  }

  [Fact]
  public void Substitutes_braced_fields()
  {
    var s = TemplateRenderer.Render("Hi {name}, due {when}", Doc(/*lang=json,strict*/ """{"name":"Jari","when":"9am"}"""));
    Assert.Equal("Hi Jari, due 9am", s);
  }

  [Fact]
  public void Missing_field_becomes_empty()
  {
    Assert.Equal("Hi ", TemplateRenderer.Render("Hi {name}", Doc("""{}""")));
  }

  [Fact]
  public void Null_template_returns_empty()
  {
    Assert.Equal("", TemplateRenderer.Render(null, Doc(/*lang=json,strict*/ """{"a":1}""")));
  }
}
