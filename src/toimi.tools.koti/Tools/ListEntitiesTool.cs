using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.koti.HomeAssistant;

namespace toimi.tools.koti.Tools;

[McpServerToolType]
public class ListEntitiesTool(HomeAssistantClient ha)
{
  [McpServerTool, Description("List Home Assistant entities, optionally filtered by domain or area. Returns entity IDs, states, friendly names, and area assignments.")]
  public async Task<string> ListEntities(
    [Description("Optional domain filter (e.g. 'light', 'sensor', 'switch', 'climate')")] string? domain = null,
    [Description("Optional area/room filter (e.g. 'Olohuone', 'Keittiö', 'Makuuhuone')")] string? area = null,
    [Description("Maximum entities to return (default 100)")] int limit = 100)
  {
    limit = Math.Clamp(limit, 1, 500);
    var states = await ha.GetStatesAsync();
    if (states.ValueKind != JsonValueKind.Array)
    {
      return "Unexpected response from Home Assistant when listing entities.";
    }

    Dictionary<string, string> areas;
    try
    {
      areas = await ha.GetEntityAreasAsync();
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
      if (area is not null)
      {
        // Degrading to "no areas" here would return [] — indistinguishable from a
        // genuinely empty room. Report the real cause instead.
        return "Area lookup failed (template API unavailable) — cannot filter by area right now. Retry without the area filter.";
      }

      areas = [];
    }

    var prefix = domain is not null ? domain + "." : null;

    var truncated = false;
    var entities = new List<object>();
    foreach (var entity in states.EnumerateArray())
    {
      if (!entity.TryGetProperty("entity_id", out var idProperty) || idProperty.GetString() is not { } entityId)
      {
        continue; // malformed entity — skip it rather than failing the whole listing
      }

      if (prefix is not null && !entityId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      areas.TryGetValue(entityId, out var entityArea);

      if (area is not null && !string.Equals(entityArea, area, StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      if (entities.Count >= limit)
      {
        truncated = true;
        break;
      }

      var state = entity.TryGetProperty("state", out var stateProperty) ? stateProperty.GetString() : null;
      string? friendlyName = null;
      if (entity.TryGetProperty("attributes", out var attributes) &&
          attributes.TryGetProperty("friendly_name", out var name))
      {
        friendlyName = name.GetString();
      }

      entities.Add(new { entity_id = entityId, state, friendly_name = friendlyName, area = entityArea });
    }

    var json = JsonSerializer.Serialize(entities);
    return truncated
      ? json + "\n[truncated at " + limit + " entities — refine with domain/area filters]"
      : json;
  }
}
