using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class SetTriggerTool(TriggerRepository repository, TietueDbContext db, HandlerRegistry handlers, Toimi.Core.Configuration.ToimiConfiguration config)
{
  [McpServerTool, Description("Schedule a trigger on an entity. 'schedule' is JSON: {\"at\":\"<iso utc>\"} for one-shot, or {\"start\":\"<iso utc>\",\"rrule\":\"FREQ=...\",\"tz\":\"Europe/Helsinki\"} for recurring (RFC 5545); recurring schedules without a tz default to the server's user timezone, pass \"tz\":\"UTC\" for fixed-UTC recurrence. 'handlerKind' is 'notify' or 'set-field'; 'handlerConfig' is its JSON config.")]
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

    if (!await db.Entities.AnyAsync(e => e.Id == id))
    {
      return $"No entity found with id {id}.";
    }

    if (handlers.Resolve(handlerKind) is null)
    {
      return $"Unknown handlerKind '{handlerKind}'. Valid kinds: {string.Join(", ", handlers.Kinds)}.";
    }

    // Validate the tz-stamped schedule, matching what CreateAsync will actually persist —
    // a bounded (COUNT/UNTIL) recurring rule can resolve differently once the default tz is
    // stamped on, so validating the raw schedule could let a dead trigger through.
    var stampedSchedule = Schedules.WithDefaultTimeZone(schedule, config.UserTimeZone);
    if (Schedules.InitialNextFireAt(stampedSchedule, DateTimeOffset.UtcNow) is null)
    {
      return "Schedule does not resolve to a future fire time. Check the 'at'/'start'+'rrule' fields.";
    }

    var t = await repository.CreateAsync(id, schedule, handlerKind, handlerConfig, DateTimeOffset.UtcNow);
    return JsonSerializer.Serialize(new { id = t.Id.ToString(), nextFireAt = t.NextFireAt?.ToString("o") });
  }
}
