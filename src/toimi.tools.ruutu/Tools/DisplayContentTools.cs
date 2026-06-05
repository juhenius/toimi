using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.ruutu.Rendering;
using toimi.tools.ruutu.Transport;

namespace toimi.tools.ruutu.Tools;

[McpServerToolType]
public class DisplayContentTools(ContentPushService pusher, ILogger<DisplayContentTools> logger)
{
  [McpServerTool, Description("Render a template with the given data and push it as the display's current scene. Replaces whatever was being shown. Use list_templates first to see what's available; create_template if you need a new shape.")]
  public async Task<string> DisplayShow(
    [Description("The display identifier.")] string identifier,
    [Description("Template name from display_list_templates.")] string template,
    [Description("Data matching the template's schema. Pass as a JSON object string.")] string dataJson,
    CancellationToken ct = default)
  {
    try
    {
      var data = JsonDocument.Parse(dataJson).RootElement;
      await pusher.ShowSceneAsync(identifier, template, data, ct);
      return "ok";
    }
    catch (JsonException ex)
    {
      return $"Error: dataJson is not valid JSON: {ex.Message}";
    }
    catch (RenderException ex)
    {
      return $"Error rendering '{template}': {ex.Message}";
    }
    catch (InvalidOperationException ex)
    {
      return $"Error: {ex.Message}";
    }
#pragma warning disable CA1031 // Graceful degradation: MCP tools must return readable error strings, never propagate exceptions
    catch (Exception ex)
    {
      logger.LogError(ex, "display_show failed");
      return $"Error: {ex.Message}";
    }
#pragma warning restore CA1031
  }

  [McpServerTool, Description("Push a template as a temporary overlay on top of the current scene. Stays until the user taps it (no auto-clear). Newest overlay appears on top; tapping dismisses and reveals the next. Most commonly used with the 'notification' template.")]
  public async Task<string> DisplayOverlay(
    [Description("The display identifier.")] string identifier,
    [Description("Template name (any template works as an overlay).")] string template,
    [Description("Data matching the template's schema. Pass as a JSON object string.")] string dataJson,
    CancellationToken ct = default)
  {
    try
    {
      var data = JsonDocument.Parse(dataJson).RootElement;
      await pusher.ShowOverlayAsync(identifier, template, data, ct);
      return "ok";
    }
    catch (JsonException ex) { return $"Error: dataJson is not valid JSON: {ex.Message}"; }
    catch (RenderException ex) { return $"Error rendering '{template}': {ex.Message}"; }
    catch (InvalidOperationException ex) { return $"Error: {ex.Message}"; }
#pragma warning disable CA1031 // Graceful degradation: MCP tools must return readable error strings, never propagate exceptions
    catch (Exception ex) { return $"Error: {ex.Message}"; }
#pragma warning restore CA1031
  }

  [McpServerTool, Description("Reset the display: clear all overlays and return to the configured idle scene (or the Toimi splash if no idle is configured).")]
  public async Task<string> DisplayClear(
    [Description("The display identifier.")] string identifier,
    CancellationToken ct = default)
  {
    try
    {
      await pusher.ClearAsync(identifier, ct);
      return "ok";
    }
#pragma warning disable CA1031 // Graceful degradation: MCP tools must return readable error strings, never propagate exceptions
    catch (Exception ex) { return $"Error: {ex.Message}"; }
#pragma warning restore CA1031
  }
}
