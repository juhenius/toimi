using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class ListTriggersTool(TriggerRepository repository)
{
  [McpServerTool, Description("List the triggers on an entity.")]
  public async Task<string> ListTriggers(
      [Description("Entity id (GUID)")] string entityId)
  {
    if (!Guid.TryParse(entityId, out var id))
    {
      return "Invalid entityId. Expected a GUID.";
    }

    var triggers = await repository.ListByEntityAsync(id);
    var rows = triggers.Select(t => new JsonObject
    {
      ["id"] = t.Id.ToString(),
      ["schedule"] = JsonNode.Parse(t.Schedule),
      ["handlerKind"] = t.HandlerKind,
      ["enabled"] = t.Enabled,
      ["nextFireAt"] = t.NextFireAt?.ToString("o"),
    }).ToArray();
    return JsonSerializer.Serialize(new JsonArray(rows));
  }
}
