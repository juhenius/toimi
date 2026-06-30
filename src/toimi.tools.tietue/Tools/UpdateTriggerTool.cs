using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class UpdateTriggerTool(TriggerRepository repository)
{
  [McpServerTool, Description("Update a trigger's schedule, handler config, and/or enabled flag.")]
  public async Task<string> UpdateTrigger(
      [Description("Trigger id (GUID)")] string id,
      [Description("New schedule spec JSON (optional)")] string? schedule = null,
      [Description("New handler config JSON (optional)")] string? handlerConfig = null,
      [Description("Enable/disable the trigger (optional)")] bool? enabled = null)
  {
    if (!Guid.TryParse(id, out var triggerId))
    {
      return "Invalid id. Expected a GUID.";
    }

    var t = await repository.UpdateAsync(triggerId, schedule, handlerConfig, enabled, DateTimeOffset.UtcNow);
    return t is null
      ? $"Trigger '{id}' not found."
      : JsonSerializer.Serialize(new { id = t.Id.ToString(), enabled = t.Enabled, nextFireAt = t.NextFireAt?.ToString("o") });
  }
}
