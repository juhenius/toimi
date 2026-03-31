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
    [Description("Optional area/room filter (e.g. 'Olohuone', 'Keittiö', 'Makuuhuone')")] string? area = null)
  {
    var states = await ha.GetStatesAsync();
    var areas = await ha.GetEntityAreasAsync();
    var prefix = domain is not null ? domain + "." : null;

    var entities = new List<object>();
    foreach (var entity in states.EnumerateArray())
    {
      var entityId = entity.GetProperty("entity_id").GetString()!;
      if (prefix is not null && !entityId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        continue;

      areas.TryGetValue(entityId, out var entityArea);

      if (area is not null && !string.Equals(entityArea, area, StringComparison.OrdinalIgnoreCase))
        continue;

      var state = entity.GetProperty("state").GetString();
      string? friendlyName = null;
      if (entity.TryGetProperty("attributes", out var attributes) &&
          attributes.TryGetProperty("friendly_name", out var name))
      {
        friendlyName = name.GetString();
      }

      entities.Add(new { entity_id = entityId, state, friendly_name = friendlyName, area = entityArea });
    }

    return JsonSerializer.Serialize(entities);
  }
}
