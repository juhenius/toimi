using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class RunTriggerTool(TietueDbContext db, OccurrenceRunner runner, ITickLock? tickLock = null)
{
  [McpServerTool, Description("Fire a trigger immediately, out of schedule, and return the handler result synchronously — including script logs. Use this to test a job or script right after creating or editing it instead of waiting for the scheduler. Does not change the trigger's schedule or NextFireAt. Returns a busy response if a scheduler tick holds the run lock — retry shortly. Note: a message-kind trigger runs a full agent synchronously and may take minutes — do not call run_trigger from within an agent run that was itself started by run_trigger.")]
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

    // Accepted race: a manual run may interleave with a scheduled run of the same
    // trigger — both snapshot Data and the last writer wins (single-user, accepted).
    // A fresh 'now' occurrence never collides with scheduled occurrences, so the
    // normal claim/finalize idempotency machinery applies cleanly to manual runs.
    // The tick lock is handed to the runner so the claim itself is serialized
    // against scheduler ticks (see OccurrenceRunner.ClaimAsync).
    var occurrence = DateTimeOffset.UtcNow;
    var outcome = await runner.RunAsync(trigger, entity, occurrence, occurrence, claimLock: tickLock);

#pragma warning disable IDE0072
    return outcome.State switch
    {
      OccurrenceState.Busy => /*lang=json,strict*/ """{"status":"busy","error":"a scheduler tick holds the run lock; try again shortly"}""",
      OccurrenceState.InProgress or OccurrenceState.AlreadyHandled => "Could not claim a run for this occurrence; try again.",
      OccurrenceState.UnknownKind => $"No handler registered for kind '{trigger.HandlerKind}'. Recorded an error event for this occurrence.",
      _ => JsonSerializer.Serialize(new { status = outcome.Status, result = outcome.ResultJson }),
    };
#pragma warning restore IDE0072
  }
}
