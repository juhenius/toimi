using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Agents;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class ActivateTool(EntityRepository entities, IAgentRunner runner, EntityEventStore events, TriggerRepository triggers)
{
  [McpServerTool, Description("Activate an entity's agent: run a prompt against it now (omit 'when'), or schedule it for later ('when' = ISO 8601 UTC). The agent can act on the entity and schedule its own next run via set_trigger.")]
  public async Task<string> Activate(
      [Description("Entity id (GUID)")] string entityId,
      [Description("The prompt/message for the agent")] string message,
      [Description("Optional ISO 8601 UTC time to schedule it for; omit to run now")] string? when = null)
  {
    if (!Guid.TryParse(entityId, out var id))
    {
      return "Invalid entityId. Expected a GUID.";
    }

    if (when is not null)
    {
      if (!DateTimeOffset.TryParse(when, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
      {
        return "Invalid 'when'. Use ISO 8601 (e.g. 2026-07-01T09:00:00Z).";
      }

      var schedule = new JsonObject { ["at"] = at.ToString("o") }.ToJsonString();
      var config = new JsonObject { ["promptTemplate"] = message }.ToJsonString();
      var t = await triggers.CreateAsync(id, schedule, "message", config, DateTimeOffset.UtcNow);
      return JsonSerializer.Serialize(new { scheduled = true, triggerId = t.Id.ToString(), at = at.ToString("o") });
    }

    var entity = await entities.GetAsync(id);
    if (entity is null)
    {
      return $"Entity '{entityId}' not found.";
    }

    var now = DateTimeOffset.UtcNow;
    var run = await runner.RunAsync(entity, message, default);
    await events.RecordAsync(id, now, "message", run.Success ? "ran" : "error",
      JsonSerializer.Serialize(new { run.Response, run.Success, run.Error }));
    return JsonSerializer.Serialize(new { ran = true, run.Success, run.Response, run.Error });
  }
}
