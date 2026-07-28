using System.Text.Json;
using toimi.tools.ruutu.Data;
using toimi.tools.ruutu.Rendering;
using Xunit;

namespace toimi.tools.ruutu.Tests.Rendering;

// Guards against Scriban syntax bugs in seeded templates. The unit tests on
// ScribanRenderer itself use synthetic templates — they don't exercise the
// HTML the pod actually serves at runtime, so a bad filter expression in
// SeedTemplates.cs (e.g. Liquid-style "| default: X" colon syntax that Scriban
// rejects) can ship to prod. This fixture renders every seeded template with
// minimal valid data in both tiers and fails if any throws.
public class SeedTemplatesRenderTests
{
  private static readonly IRenderTemplateSource Source = BuildSource();

  private static MapSource BuildSource()
  {
    var map = SeedTemplates.All.ToDictionary(
      t => t.Name,
      t => new TemplateBody(t.ModernHtml, t.LegacyHtml));
    return new MapSource(map);
  }

  private sealed class MapSource(IReadOnlyDictionary<string, TemplateBody> map) : IRenderTemplateSource
  {
    public Task<TemplateBody?> GetAsync(string name, CancellationToken ct = default)
    {
      return Task.FromResult(map.TryGetValue(name, out var b) ? b : null);
    }
  }

  [Theory]
  // leaf templates
  [InlineData("splash", "{}")]
  [InlineData("clock", "{}")]
  [InlineData("message", /*lang=json,strict*/ """{"body":"hi"}""")]
  [InlineData("notification", /*lang=json,strict*/ """{"title":"T","body":"B"}""")]
  [InlineData("todo_list", /*lang=json,strict*/ """{"title":"T","steps":[]}""")]
  [InlineData("weather", /*lang=json,strict*/ """{"location":"L","current":{"temp":0,"condition":"C"}}""")]
  [InlineData("calendar_day", /*lang=json,strict*/ """{"date":"2026-01-01","events":[]}""")]
  [InlineData("reminders", /*lang=json,strict*/ """{"items":[]}""")]
  // layouts with required sub-template slots — use splash as filler so the
  // composite recursion is also exercised
  [InlineData("split_horizontal", /*lang=json,strict*/ """{"left":{"template":"splash","data":{}},"right":{"template":"splash","data":{}}}""")]
  [InlineData("split_vertical", /*lang=json,strict*/ """{"top":{"template":"splash","data":{}},"bottom":{"template":"splash","data":{}}}""")]
  [InlineData("stack", /*lang=json,strict*/ """{"items":[]}""")]
  [InlineData("stack", /*lang=json,strict*/ """{"items":[{"template":"splash","data":{}},{"template":"splash","data":{}}]}""")]
  [InlineData("webview", /*lang=json,strict*/ """{"url":"https://x.test/a"}""")]
  public async Task Seeded_template_renders_in_modern_tier(string templateName, string dataJson)
  {
    var data = JsonDocument.Parse(dataJson).RootElement;
    var html = await ScribanRenderer.RenderAsync(templateName, data, "modern", Source);
    Assert.False(string.IsNullOrWhiteSpace(html), $"Modern '{templateName}' rendered empty/whitespace.");
  }

  [Theory]
  [InlineData("splash", "{}")]
  [InlineData("clock", "{}")]
  [InlineData("message", /*lang=json,strict*/ """{"body":"hi"}""")]
  [InlineData("notification", /*lang=json,strict*/ """{"title":"T","body":"B"}""")]
  [InlineData("todo_list", /*lang=json,strict*/ """{"title":"T","steps":[]}""")]
  [InlineData("weather", /*lang=json,strict*/ """{"location":"L","current":{"temp":0,"condition":"C"}}""")]
  [InlineData("calendar_day", /*lang=json,strict*/ """{"date":"2026-01-01","events":[]}""")]
  [InlineData("reminders", /*lang=json,strict*/ """{"items":[]}""")]
  [InlineData("split_horizontal", /*lang=json,strict*/ """{"left":{"template":"splash","data":{}},"right":{"template":"splash","data":{}}}""")]
  [InlineData("split_vertical", /*lang=json,strict*/ """{"top":{"template":"splash","data":{}},"bottom":{"template":"splash","data":{}}}""")]
  [InlineData("stack", /*lang=json,strict*/ """{"items":[]}""")]
  [InlineData("stack", /*lang=json,strict*/ """{"items":[{"template":"splash","data":{}},{"template":"splash","data":{}}]}""")]
  [InlineData("webview", /*lang=json,strict*/ """{"url":"https://x.test/a"}""")]
  public async Task Seeded_template_renders_in_legacy_tier(string templateName, string dataJson)
  {
    var data = JsonDocument.Parse(dataJson).RootElement;
    var html = await ScribanRenderer.RenderAsync(templateName, data, "legacy", Source);
    Assert.False(string.IsNullOrWhiteSpace(html), $"Legacy '{templateName}' rendered empty/whitespace.");
  }

  [Fact]
  public void Every_seeded_template_appears_in_test_coverage()
  {
    // Sanity check: if a new template is added to SeedTemplates.All, this test
    // fails until the InlineData lists above are updated. Drift detection.
    var covered = new HashSet<string>
    {
      "splash", "clock", "message", "notification", "todo_list", "weather",
      "calendar_day", "reminders", "split_horizontal", "split_vertical", "stack",
      "webview"
    };
    var seeded = SeedTemplates.All.Select(t => t.Name).ToHashSet();
    Assert.Equal(seeded, covered);
  }
}
