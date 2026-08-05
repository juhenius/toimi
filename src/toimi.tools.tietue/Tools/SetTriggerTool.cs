using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class SetTriggerTool(TriggerRepository repository, TietueDbContext db, HandlerRegistry handlers)
{
  [McpServerTool, Description("Schedule a trigger on an entity. 'schedule' is JSON: {\"at\":\"<iso utc>\"} for one-shot, or {\"start\":\"<iso utc>\",\"rrule\":\"FREQ=...\",\"tz\":\"Europe/Helsinki\"} for recurring (RFC 5545); recurring schedules without a tz default to the server's user timezone, pass \"tz\":\"UTC\" for fixed-UTC recurrence. 'handlerKind' is one of: notify, set-field, delete, script, message; 'handlerConfig' is its JSON config.")]
  public async Task<string> SetTrigger(
      [Description("Entity id (GUID)")] string entityId,
      [Description("Schedule spec JSON")] string schedule,
      [Description("Handler kind: one of: notify, set-field, delete, script, message")] string handlerKind,
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

    if (handlers.Resolve(handlerKind) is not { } handler)
    {
      return $"Unknown handlerKind '{handlerKind}'. Valid kinds: {string.Join(", ", handlers.Kinds)}.";
    }

    var configCheck = handler.ValidateConfig(handlerConfig);
    if (!configCheck.IsValid)
    {
      return string.Join("; ", configCheck.Errors);
    }

    try
    {
      // Stamping + schedule validation live in the repository — the single choke point
      // every trigger-writing path goes through.
      var t = await repository.CreateAsync(id, schedule, handlerKind, handlerConfig, DateTimeOffset.UtcNow);
      return JsonSerializer.Serialize(new { id = t.Id.ToString(), nextFireAt = t.NextFireAt?.ToString("o") });
    }
    catch (TietueValidationException ex)
    {
      return string.Join("; ", ex.Errors);
    }
  }
}
