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

  private static JsonElement Params(string j)
  {
    using var doc = JsonDocument.Parse(j);
    return doc.RootElement.Clone();
  }

  [Fact]
  public void Params_fill_tokens_missing_from_data()
  {
    var s = TemplateRenderer.Render("Hi {name}, door {door}", Doc(/*lang=json,strict*/ """{"name":"Jari"}"""),
      Params(/*lang=json,strict*/ """{"door":"front"}"""));
    Assert.Equal("Hi Jari, door front", s);
  }

  [Fact]
  public void Data_wins_over_params_on_collision()
  {
    // Params are attacker-controlled (webhook callers); they must not shadow entity fields.
    var s = TemplateRenderer.Render("{name}", Doc(/*lang=json,strict*/ """{"name":"Jari"}"""),
      Params(/*lang=json,strict*/ """{"name":"Mallory"}"""));
    Assert.Equal("Jari", s);
  }

  [Fact]
  public void Token_missing_from_both_becomes_empty()
  {
    Assert.Equal("Hi ", TemplateRenderer.Render("Hi {name}", Doc("""{}"""), Params("""{}""")));
  }

  [Fact]
  public void Non_string_param_renders_raw_json()
  {
    var s = TemplateRenderer.Render("{count}", Doc("""{}"""), Params(/*lang=json,strict*/ """{"count":3}"""));
    Assert.Equal("3", s);
  }
}
