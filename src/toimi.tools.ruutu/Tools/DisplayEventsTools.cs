using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.ruutu.Data.Repositories;

namespace toimi.tools.ruutu.Tools;

[McpServerToolType]
public class DisplayEventsTools(DisplayRepository displays, DisplayEventRepository events)
{
  [McpServerTool, Description("Return recent tap-back events from a display. Use when reacting to user interaction (e.g. user tapped a step in an in-progress routine). Events are append-only; the same event will be returned again if you don't advance the 'since' cursor.")]
  public async Task<string> DisplayGetEvents(
    [Description("The display identifier.")] string identifier,
    [Description("Optional ISO 8601 timestamp; only events strictly after this are returned. Pass the timestamp of the last event you previously processed.")] string? sinceUtc = null,
    CancellationToken ct = default)
  {
    var d = await displays.GetAsync(identifier, ct);
    if (d is null)
    {
      return $"Display '{identifier}' not found.";
    }

    DateTimeOffset? since = null;
    if (sinceUtc is not null)
    {
      if (!DateTimeOffset.TryParse(sinceUtc, out var parsed))
      {
        return "Error: sinceUtc must be ISO 8601.";
      }

      since = parsed;
    }

    var rows = await events.GetSinceAsync(d.Id, since, ct);
    var view = rows.Select(e => new
    {
      type = e.EventType,
      target = e.Target,
      value = e.Value is null ? (object?)null : JsonDocument.Parse(e.Value).RootElement,
      forwarded = e.ForwardOutcome,
      timestamp = e.CreatedAt.ToString("o")
    });
    return JsonSerializer.Serialize(view);
  }
}
