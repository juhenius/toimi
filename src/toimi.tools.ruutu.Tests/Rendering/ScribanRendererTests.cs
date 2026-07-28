using System.Text.Json;
using toimi.tools.ruutu.Rendering;
using Xunit;

namespace toimi.tools.ruutu.Tests.Rendering;

public class ScribanRendererTests
{
  private static InMemorySource Source(params (string name, string modern, string legacy)[] tpls)
  {
    return new(tpls.ToDictionary(t => t.name, t => (t.modern, t.legacy)));
  }

  private sealed class InMemorySource(IReadOnlyDictionary<string, (string Modern, string Legacy)> map) : IRenderTemplateSource
  {
    public Task<TemplateBody?> GetAsync(string name, CancellationToken ct = default)
    {
      return map.TryGetValue(name, out var pair)
        ? Task.FromResult<TemplateBody?>(new TemplateBody(pair.Modern, pair.Legacy))
        : Task.FromResult<TemplateBody?>(null);
    }
  }

  private static JsonElement Json(string raw)
  {
    return JsonDocument.Parse(raw).RootElement;
  }

  [Fact]
  public async Task Renders_a_leaf_template_with_substitution()
  {
    var src = Source(("greet", "<p>Hello {{ name }}</p>", "<p>Hello {{ name }}</p>"));
    var html = await ScribanRenderer.RenderAsync("greet", Json(/*lang=json,strict*/ """{ "name": "World" }"""), "modern", src);
    Assert.Contains("Hello World", html);
  }

  [Fact]
  public async Task Picks_legacy_html_for_legacy_tier()
  {
    var src = Source(("greet", "<modern/>", "<legacy/>"));
    Assert.Equal("<legacy/>", await ScribanRenderer.RenderAsync("greet", Json("{}"), "legacy", src));
    Assert.Equal("<modern/>", await ScribanRenderer.RenderAsync("greet", Json("{}"), "modern", src));
  }

  [Fact]
  public async Task Throws_on_unknown_template()
  {
    var src = Source();
    var ex = await Assert.ThrowsAsync<RenderException>(
      () => ScribanRenderer.RenderAsync("missing", Json("{}"), "modern", src));
    Assert.Contains("missing", ex.Message);
  }

  [Fact]
  public async Task Throws_on_scriban_syntax_error()
  {
    var src = Source(("bad", "{{ this is not valid }", "{{ this is not valid }"));
    await Assert.ThrowsAsync<RenderException>(
      () => ScribanRenderer.RenderAsync("bad", Json("{}"), "modern", src));
  }

  [Fact]
  public async Task Renders_composite_with_sub_template_slot()
  {
    var src = Source(
      ("inner", "<span>{{ msg }}</span>", "<span>{{ msg }}</span>"),
      ("outer", "<div>{{ slot_html }}</div>", "<div>{{ slot_html }}</div>"));
    var data = Json(/*lang=json,strict*/ """{ "slot": { "template": "inner", "data": { "msg": "hi" } } }""");
    var html = await ScribanRenderer.RenderAsync("outer", data, "modern", src);
    Assert.Contains("<div><span>hi</span></div>", html);
  }

  [Fact]
  public async Task Renders_array_of_sub_templates_into_array_variable()
  {
    var src = Source(
      ("item", "<li>{{ label }}</li>", "<li>{{ label }}</li>"),
      ("list", "<ul>{{ for it in items_html }}{{ it }}{{ end }}</ul>", "<ul>{{ for it in items_html }}{{ it }}{{ end }}</ul>"));
    var data = Json(/*lang=json,strict*/ """
      { "items": [
          { "template": "item", "data": { "label": "a" } },
          { "template": "item", "data": { "label": "b" } }
      ] }
      """);
    var html = await ScribanRenderer.RenderAsync("list", data, "modern", src);
    Assert.Equal("<ul><li>a</li><li>b</li></ul>", html);
  }

  [Fact]
  public async Task Caps_recursion_depth_at_three()
  {
    var src = Source(
      ("leaf", "leaf", "leaf"),
      ("wrap", "[{{ inner_html }}]", "[{{ inner_html }}]"));
    var deep = Json(/*lang=json,strict*/ """
      {
        "inner": {
          "template": "wrap",
          "data": {
            "inner": {
              "template": "wrap",
              "data": {
                "inner": {
                  "template": "wrap",
                  "data": {
                    "inner": {
                      "template": "leaf",
                      "data": {}
                    }
                  }
                }
              }
            }
          }
        }
      }
      """);
    var ex = await Assert.ThrowsAsync<RenderException>(
      () => ScribanRenderer.RenderAsync("wrap", deep, "modern", src));
    Assert.Contains("depth", ex.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Plain_scalar_values_pass_through_unchanged()
  {
    var src = Source(("t", "n={{ count }} f={{ flag }}", "n={{ count }} f={{ flag }}"));
    var html = await ScribanRenderer.RenderAsync("t", Json(/*lang=json,strict*/ """{ "count": 5, "flag": true }"""), "modern", src);
    Assert.Equal("n=5 f=true", html);
  }
}
