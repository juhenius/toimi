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
    public Task<TemplateBody?> GetAsync(string name, CancellationToken ct = default) =>
      Task.FromResult(map.TryGetValue(name, out var b) ? b : null);
  }

  [Theory]
  // leaf templates
  [InlineData("splash", "{}")]
  [InlineData("clock", "{}")]
  [InlineData("message", """{"body":"hi"}""")]
  [InlineData("notification", """{"title":"T","body":"B"}""")]
  [InlineData("todo_list", """{"title":"T","steps":[]}""")]
  [InlineData("weather", """{"location":"L","current":{"temp":0,"condition":"C"}}""")]
  [InlineData("calendar_day", """{"date":"2026-01-01","events":[]}""")]
  [InlineData("reminders", """{"items":[]}""")]
  // layouts with required sub-template slots — use splash as filler so the
  // composite recursion is also exercised
  [InlineData("split_horizontal", """{"left":{"template":"splash","data":{}},"right":{"template":"splash","data":{}}}""")]
  [InlineData("split_vertical", """{"top":{"template":"splash","data":{}},"bottom":{"template":"splash","data":{}}}""")]
  [InlineData("stack", """{"items":[]}""")]
  [InlineData("stack", """{"items":[{"template":"splash","data":{}},{"template":"splash","data":{}}]}""")]
  public async Task Seeded_template_renders_in_modern_tier(string templateName, string dataJson)
  {
    var data = JsonDocument.Parse(dataJson).RootElement;
    var html = await ScribanRenderer.RenderAsync(templateName, data, "modern", Source);
    Assert.False(string.IsNullOrWhiteSpace(html), $"Modern '{templateName}' rendered empty/whitespace.");
  }

  [Theory]
  [InlineData("splash", "{}")]
  [InlineData("clock", "{}")]
  [InlineData("message", """{"body":"hi"}""")]
  [InlineData("notification", """{"title":"T","body":"B"}""")]
  [InlineData("todo_list", """{"title":"T","steps":[]}""")]
  [InlineData("weather", """{"location":"L","current":{"temp":0,"condition":"C"}}""")]
  [InlineData("calendar_day", """{"date":"2026-01-01","events":[]}""")]
  [InlineData("reminders", """{"items":[]}""")]
  [InlineData("split_horizontal", """{"left":{"template":"splash","data":{}},"right":{"template":"splash","data":{}}}""")]
  [InlineData("split_vertical", """{"top":{"template":"splash","data":{}},"bottom":{"template":"splash","data":{}}}""")]
  [InlineData("stack", """{"items":[]}""")]
  [InlineData("stack", """{"items":[{"template":"splash","data":{}},{"template":"splash","data":{}}]}""")]
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
      "calendar_day", "reminders", "split_horizontal", "split_vertical", "stack"
    };
    var seeded = SeedTemplates.All.Select(t => t.Name).ToHashSet();
    Assert.Equal(seeded, covered);
  }
}
