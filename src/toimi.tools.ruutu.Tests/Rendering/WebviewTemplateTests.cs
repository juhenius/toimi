using System.Text.Json;
using toimi.tools.ruutu.Data;
using toimi.tools.ruutu.Rendering;
using Xunit;

namespace toimi.tools.ruutu.Tests.Rendering;

public class WebviewTemplateTests
{
  private static readonly IRenderTemplateSource Source = new MapSource(
    SeedTemplates.All.ToDictionary(t => t.Name, t => new TemplateBody(t.ModernHtml, t.LegacyHtml)));

  private sealed class MapSource(IReadOnlyDictionary<string, TemplateBody> map) : IRenderTemplateSource
  {
    public Task<TemplateBody?> GetAsync(string name, CancellationToken ct = default) =>
      Task.FromResult(map.TryGetValue(name, out var b) ? b : null);
  }

  private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

  private static Task<string> Render(string dataJson) =>
    ScribanRenderer.RenderAsync("webview", Json(dataJson), "modern", Source);

  [Fact]
  public async Task Renders_sandboxed_iframe_with_the_url()
  {
    var html = await Render("""{ "url": "https://posti.fi/track/9" }""");
    Assert.Contains("<iframe", html);
    Assert.Contains("sandbox=\"allow-scripts allow-same-origin\"", html);
    Assert.Contains("src=\"https://posti.fi/track/9\"", html);
  }

  [Fact]
  public async Task Shows_a_header_only_when_title_is_present()
  {
    var withTitle = await Render("""{ "url": "https://t.test/x", "title": "Parcel tracking" }""");
    Assert.Contains("Parcel tracking", withTitle);
    Assert.Contains("height:40px", withTitle);

    var noTitle = await Render("""{ "url": "https://t.test/x" }""");
    Assert.DoesNotContain("height:40px", noTitle);
    Assert.Contains("height:100%", noTitle);
  }

  [Fact]
  public async Task Escapes_the_title()
  {
    var html = await Render("""{ "url": "https://t.test/x", "title": "<b>hi</b>" }""");
    Assert.DoesNotContain("<b>hi</b>", html);
    Assert.Contains("&lt;b&gt;hi&lt;/b&gt;", html);
  }

  [Fact]
  public async Task Collapses_a_non_https_url_to_about_blank()
  {
    var html = await Render("""{ "url": "javascript:alert(1)" }""");
    Assert.Contains("src=\"about:blank\"", html);
    Assert.DoesNotContain("javascript:", html);
  }

  [Fact]
  public async Task Prevents_url_attribute_breakout()
  {
    var html = await Render("""{ "url": "https://x.test/a\"><script>alert(1)</script>" }""");
    Assert.DoesNotContain("<script>alert(1)</script>", html);
  }

  [Fact]
  public void Legacy_body_passes_the_tier_linter()
  {
    var legacy = SeedTemplates.All.Single(t => t.Name == "webview").LegacyHtml;
    var result = TierLinter.Lint("legacy", legacy);
    Assert.True(result.Valid,
      "webview legacy body must pass the linter; issues: " +
      string.Join(", ", result.Issues.Select(i => i.Rule)));
  }
}
