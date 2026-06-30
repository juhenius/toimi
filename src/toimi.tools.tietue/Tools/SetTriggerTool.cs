using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class SetTriggerTool(TriggerRepository repository)
{
  [McpServerTool, Description("Schedule a trigger on an entity. 'schedule' is JSON: {\"at\":\"<iso utc>\"} for one-shot, or {\"start\":\"<iso utc>\",\"rrule\":\"FREQ=...\",\"tz\":\"Europe/Helsinki\"} for recurring (RFC 5545). 'handlerKind' is 'notify' or 'set-field'; 'handlerConfig' is its JSON config.")]
  public async Task<string> SetTrigger(
      [Description("Entity id (GUID)")] string entityId,
      [Description("Schedule spec JSON")] string schedule,
      [Description("Handler kind: notify | set-field")] string handlerKind,
      [Description("Handler config JSON (optional)")] string? handlerConfig = null)
  {
    if (!Guid.TryParse(entityId, out var id))
    {
      return "Invalid entityId. Expected a GUID.";
    }

    var t = await repository.CreateAsync(id, schedule, handlerKind, handlerConfig, DateTimeOffset.UtcNow);
    return JsonSerializer.Serialize(new { id = t.Id.ToString(), nextFireAt = t.NextFireAt?.ToString("o") });
  }
}
