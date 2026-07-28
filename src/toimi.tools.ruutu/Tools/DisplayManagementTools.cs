using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.ruutu.Data.Entities;
using toimi.tools.ruutu.Data.Repositories;

namespace toimi.tools.ruutu.Tools;

[McpServerToolType]
public class DisplayManagementTools(DisplayRepository displays)
{
  [McpServerTool, Description("Register a display so it can connect. The identifier becomes part of the URL: http://<host>/ruutu/<identifier>. Optionally lock the capability tier to override auto-detection. Idempotent: re-registering returns the existing display.")]
  public async Task<string> DisplayRegister(
    [Description("URL-safe slug naming the display (e.g. 'kitchen', 'bedroom').")] string identifier,
    [Description("Optional tier override: 'modern' or 'legacy'. Omit to auto-detect.")] string? capabilityTierOverride = null,
    CancellationToken ct = default)
  {
    if (capabilityTierOverride is not null and not "modern" and not "legacy")
    {
      return "Error: capabilityTierOverride must be 'modern', 'legacy', or null.";
    }

    Display d;
    try
    {
      d = await displays.RegisterAsync(identifier, capabilityTierOverride, ct);
    }
    catch (ArgumentException ex)
    {
      return $"Error: {ex.Message}";
    }

    return JsonSerializer.Serialize(new { d.Identifier, d.Tier, d.TierOverride, url = $"/ruutu/{d.Identifier}" });
  }

  [McpServerTool, Description("Unregister a display. Removes the display record and any associated events. Pages opened on this display will fall back to a 'not configured' page.")]
  public async Task<string> DisplayUnregister(
    [Description("The display identifier to remove.")] string identifier,
    CancellationToken ct = default)
  {
    var ok = await displays.UnregisterAsync(identifier, ct);
    return ok ? "ok" : $"Display '{identifier}' not found.";
  }

  [McpServerTool, Description("List all registered displays with their current status. Online means the display sent a heartbeat or tap in the last 30 seconds.")]
  public async Task<string> DisplayList(CancellationToken ct = default)
  {
    var list = await displays.ListAsync(ct);
    var now = DateTimeOffset.UtcNow;
    var view = list.Select(d => new
    {
      d.Identifier,
      d.Tier,
      status = (d.LastSeenAt.HasValue && (now - d.LastSeenAt.Value) < TimeSpan.FromSeconds(30)) ? "online" : "offline",
      last_seen_at = d.LastSeenAt?.ToString("o"),
      current_template = d.CurrentTemplate,
      viewport_width = d.ViewportWidth,
      viewport_height = d.ViewportHeight,
      orientation = d.Orientation
    });
    return JsonSerializer.Serialize(view);
  }

  [McpServerTool, Description("Manually set the capability tier for a display, overriding auto-detection. Use when a display is mis-classified (e.g. a modern iPad shows up as legacy due to a privacy proxy stripping user-agent info).")]
  public async Task<string> DisplaySetTier(
    [Description("The display identifier.")] string identifier,
    [Description("Tier to apply: 'modern' or 'legacy'.")] string tier,
    CancellationToken ct = default)
  {
    if (tier is not "modern" and not "legacy")
    {
      return "Error: tier must be 'modern' or 'legacy'.";
    }

    var ok = await displays.SetTierAsync(identifier, tier, ct);
    return ok ? "ok" : $"Display '{identifier}' not found.";
  }

  [McpServerTool, Description("Set (or clear) the idle scene for a display — what's shown when display_clear is called or on reconnect with no current scene. Pass template=null (omit) to clear; otherwise template + dataJson are saved. Until set, displays fall back to the Toimi splash on idle.")]
  public async Task<string> DisplaySetIdle(
    [Description("The display identifier.")] string identifier,
    [Description("Template name from display_list_templates. Omit or pass null to clear the idle.")] string? template = null,
    [Description("Data matching the template's schema as a JSON object string. Defaults to '{}' if template is set but dataJson is omitted.")] string? dataJson = null,
    CancellationToken ct = default)
  {
    string? storedData = null;
    if (template is not null)
    {
      var json = dataJson ?? "{}";
      try { JsonDocument.Parse(json); }
      catch (JsonException ex) { return $"Error: dataJson is not valid JSON: {ex.Message}"; }
      storedData = json;
    }
    var ok = await displays.SetIdleAsync(identifier, template, storedData, ct);
    return ok ? "ok" : $"Display '{identifier}' not found.";
  }
}
