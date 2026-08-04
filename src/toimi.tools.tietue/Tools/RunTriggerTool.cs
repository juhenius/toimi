using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Handlers;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class RunTriggerTool(TietueDbContext db, HandlerRegistry handlers, EntityEventStore events)
{
  [McpServerTool, Description("Fire a trigger immediately, out of schedule, and return the handler result synchronously — including script logs. Use this to test a job or script right after creating or editing it instead of waiting for the scheduler. Does not change the trigger's schedule or NextFireAt. Note: a message-kind trigger runs a full agent synchronously and may take minutes — do not call run_trigger from within an agent run that was itself started by run_trigger.")]
  public async Task<string> RunTrigger([Description("Trigger id (GUID)")] string triggerId)
  {
    if (!Guid.TryParse(triggerId, out var id))
    {
      return "Invalid triggerId. Expected a GUID.";
    }

    var trigger = await db.Triggers.FirstOrDefaultAsync(t => t.Id == id);
    if (trigger is null)
    {
      return $"No trigger found with id {id}.";
    }

    var entity = await db.Entities.FirstOrDefaultAsync(e => e.Id == trigger.EntityId);
    if (entity is null)
    {
      return $"Trigger's entity {trigger.EntityId} no longer exists.";
    }

    var handler = handlers.Resolve(trigger.HandlerKind);
    if (handler is null)
    {
      return $"No handler registered for kind '{trigger.HandlerKind}'.";
    }

    // Accepted race: a manual run may interleave with a scheduled run of the same
    // trigger — both snapshot Data and the last writer wins (single-user, accepted).
    // A fresh 'now' occurrence never collides with scheduled occurrences, so the
    // normal claim/finalize idempotency machinery applies cleanly to manual runs.
    var occurrence = DateTimeOffset.UtcNow;
    var claim = await events.TryClaimAsync(entity.Id, occurrence, trigger.HandlerKind, occurrence);
    if (claim != ClaimResult.Claimed)
    {
      return "Could not claim a run for this occurrence; try again.";
    }

    string status;
    string? resultJson;
    try
    {
      var result = await handler.HandleAsync(new HandlerContext(entity, trigger.HandlerConfig, occurrence));
      status = result.Status;
      resultJson = result.Result;
    }
    catch (Exception ex)
    {
      status = "error";
      resultJson = JsonSerializer.Serialize(new { error = ex.Message });
    }

    // The handler may have deleted the entity; the claim row was cascade-deleted
    // with it, so finalizing would silently no-op — skip it explicitly.
    if (await db.Entities.AnyAsync(e => e.Id == entity.Id))
    {
      await events.FinalizeAsync(entity.Id, occurrence, trigger.HandlerKind, status, resultJson);
    }

    return JsonSerializer.Serialize(new { status, result = resultJson });
  }
}
