using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Toimi.Core.Tools;
using toimi.tools.ruutu.Rendering;
using toimi.tools.ruutu.Transport;

namespace toimi.tools.ruutu.Tools;

[McpServerToolType]
public class DisplayContentTools(ContentPushService pusher, ILogger<DisplayContentTools> logger)
{
  [McpServerTool, Description("Render a template with the given data and push it as the display's current scene. Replaces whatever was being shown. Use list_templates first to see what's available; create_template if you need a new shape.")]
  public Task<string> DisplayShow(
    [Description("The display identifier.")] string identifier,
    [Description("Template name from display_list_templates.")] string template,
    [Description("Data matching the template's schema. Pass as a JSON object string.")] string dataJson,
    CancellationToken ct = default)
  {
    return ToolGuard.RunAsync(async () =>
    {
      var data = JsonDocument.Parse(dataJson).RootElement;
      await pusher.ShowSceneAsync(identifier, template, data, ct);
      return "ok";
    }, translate: ex => TranslateContentFailure(ex, template), logger: logger);
  }

  [McpServerTool, Description("Push a template as a temporary overlay on top of the current scene. Stays until the user taps it (no auto-clear). Newest overlay appears on top; tapping dismisses and reveals the next. Most commonly used with the 'notification' template.")]
  public Task<string> DisplayOverlay(
    [Description("The display identifier.")] string identifier,
    [Description("Template name (any template works as an overlay).")] string template,
    [Description("Data matching the template's schema. Pass as a JSON object string.")] string dataJson,
    CancellationToken ct = default)
  {
    return ToolGuard.RunAsync(async () =>
    {
      var data = JsonDocument.Parse(dataJson).RootElement;
      await pusher.ShowOverlayAsync(identifier, template, data, ct);
      return "ok";
    }, translate: ex => TranslateContentFailure(ex, template));
  }

  [McpServerTool, Description("Reset the display: clear all overlays and return to the configured idle scene (or the Toimi splash if no idle is configured).")]
  public Task<string> DisplayClear(
    [Description("The display identifier.")] string identifier,
    CancellationToken ct = default)
  {
    return ToolGuard.RunAsync(async () =>
    {
      await pusher.ClearAsync(identifier, ct);
      return "ok";
    });
  }

  private static string? TranslateContentFailure(Exception ex, string template)
  {
    return ex is RenderException render
      ? $"Error rendering '{template}': {render.Message}"
      : RuutuErrors.TranslateJson(ex, "dataJson") ?? RuutuErrors.Translate(ex);
  }
}
