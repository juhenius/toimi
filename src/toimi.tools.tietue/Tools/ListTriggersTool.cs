using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Webhooks;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class ListTriggersTool(TriggerRepository repository, WebhookOptions webhookOptions)
{
  [McpServerTool, Description("List the triggers on an entity. Webhook (call-anchored) triggers include their capability url.")]
  public async Task<string> ListTriggers(
      [Description("Entity id (GUID)")] string entityId)
  {
    if (!Guid.TryParse(entityId, out var id))
    {
      return "Invalid entityId. Expected a GUID.";
    }

    var triggers = await repository.ListByEntityAsync(id);
    var rows = triggers.Select(t =>
    {
      var row = new JsonObject
      {
        ["id"] = t.Id.ToString(),
        ["schedule"] = JsonNode.Parse(t.Schedule),
        ["handlerKind"] = t.HandlerKind,
        ["enabled"] = t.Enabled,
        ["nextFireAt"] = t.NextFireAt?.ToString("o"),
      };
      if (t.Secret is not null)
      {
        // The secret rides along even when no PublicBaseUrl composes a url (ADR 0001:
        // "retrievable through the trigger tools") — without it, a lost creation
        // response would make the capability URL unrecoverable.
        row["url"] = WebhookEndpoints.Url(webhookOptions, t);
        row["secret"] = t.Secret;
      }

      return row;
    }).ToArray();
    return JsonSerializer.Serialize(new JsonArray(rows));
  }
}
