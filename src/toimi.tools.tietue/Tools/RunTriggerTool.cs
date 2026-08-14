using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class RunTriggerTool(TietueDbContext db, OccurrenceRunner runner, ITickLock? tickLock = null)
{
  [McpServerTool, Description("Fire a trigger immediately, out of schedule, and return the handler result synchronously — including script logs. Use this to test a job or script right after creating or editing it instead of waiting for the scheduler. Does not change the trigger's schedule or NextFireAt. Optional params (a JSON object) reach the handler like a webhook call's would: scripts see input.params; notify templates interpolate {key} tokens; message prompts do NOT interpolate {key} — the agent receives params as a fenced data block. Returns a busy response if a scheduler tick holds the run lock — retry shortly. Note: a message-kind trigger runs a full agent synchronously and may take minutes — do not call run_trigger from within an agent run that was itself started by run_trigger.")]
  public async Task<string> RunTrigger(
    [Description("Trigger id (GUID)")] string triggerId,
    [Description("Optional params JSON object for this firing (what a webhook call would carry)")] string? @params = null)
  {
    if (!Guid.TryParse(triggerId, out var id))
    {
      return "Invalid triggerId. Expected a GUID.";
    }

    JsonElement? parsedParams = null;
    if (@params is not null)
    {
      try
      {
        using var doc = JsonDocument.Parse(@params);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
          return "Invalid params. Expected a JSON object.";
        }

        parsedParams = doc.RootElement.Clone();
      }
      catch (JsonException)
      {
        return "Invalid params. Expected a JSON object.";
      }
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
    var outcome = await runner.RunAsync(trigger, entity, occurrence, occurrence, claimLock: tickLock, @params: parsedParams);

    return outcome.State switch
    {
      OccurrenceState.Busy => /*lang=json,strict*/ """{"status":"busy","error":"a scheduler tick holds the run lock; try again shortly"}""",
      OccurrenceState.InProgress or OccurrenceState.AlreadyHandled => "Could not claim a run for this occurrence; try again.",
      OccurrenceState.UnknownKind => $"No handler registered for kind '{trigger.HandlerKind}'. Recorded an error event for this occurrence.",
      OccurrenceState.Ran or OccurrenceState.Errored or OccurrenceState.EntityDeleted =>
        JsonSerializer.Serialize(new { status = outcome.Status, result = outcome.ResultJson }),
      _ => throw new UnreachableException($"unhandled OccurrenceState {outcome.State}"),
    };
  }
}
