using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Handlers;

namespace toimi.tools.tietue.Scheduling;

public class SchedulerTick(TietueDbContext db, HandlerRegistry handlers, EntityEventStore events, ILogger<SchedulerTick>? logger = null, ITickLock? tickLock = null)
{
  private readonly ILogger<SchedulerTick> _logger = logger ?? NullLogger<SchedulerTick>.Instance;
  private const int MaxErrorMessageChars = 1000;

  public async Task RunDueAsync(DateTimeOffset now, CancellationToken ct)
  {
    IAsyncDisposable? lease = null;
    if (tickLock is not null)
    {
      lease = await tickLock.TryAcquireAsync(ct);
      if (lease is null)
      {
        _logger.LogDebug("Scheduler tick skipped: another instance holds the tick lock.");
        return;
      }
    }
    await using var _ = lease;

    var due = await db.Triggers
      .Where(t => t.Enabled && t.NextFireAt != null && t.NextFireAt <= now)
      .OrderBy(t => t.NextFireAt)
      .ToListAsync(ct);

    foreach (var trigger in due)
    {
      if (ct.IsCancellationRequested)
      {
        break;
      }

      var occurrence = trigger.NextFireAt!.Value;
      var entity = await db.Entities.FirstOrDefaultAsync(e => e.Id == trigger.EntityId, ct);

      var deletedDuringHandling = false;
      if (entity is not null)
      {
        var claim = await events.TryClaimAsync(trigger.EntityId, occurrence, trigger.HandlerKind, now, ct);
        if (claim == ClaimResult.InProgress)
        {
          // Another instance (or a crashed one, within the stale window) owns this
          // occurrence. Leave the trigger un-advanced so it stays due for retry.
          continue;
        }

        if (claim == ClaimResult.Claimed)
        {
          var handler = handlers.Resolve(trigger.HandlerKind);
          if (handler is not null)
          {
            string status;
            string? resultJson;
            try
            {
              var result = await handler.HandleAsync(new HandlerContext(entity, trigger.HandlerConfig, occurrence), ct);
              status = result.Status;
              resultJson = result.Result;
            }
            catch (Exception ex)
            {
              status = "error";
              // Generic insurance: any handler's exception message lands in a jsonb column.
              var message = ex.Message.Length > MaxErrorMessageChars
                ? ex.Message[..MaxErrorMessageChars] + "… [truncated]"
                : ex.Message;
              resultJson = System.Text.Json.JsonSerializer.Serialize(new { error = message });
              _logger.LogError(ex, "Handler {HandlerKind} failed for trigger {TriggerId} (entity {EntityId}).",
                trigger.HandlerKind, trigger.Id, trigger.EntityId);
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
              _logger.LogInformation("Trigger {TriggerId} ({HandlerKind}) fired for entity {EntityId}: {Status}",
                trigger.Id, trigger.HandlerKind, trigger.EntityId, status);
            }

            // The handler may have deleted the entity (delete handler, or an agent run).
            // FinalizeAsync no-ops if the claim row was cascade-deleted with it; skip
            // advancing the trigger too — it was cascade-deleted as well.
            if (await db.Entities.AnyAsync(e => e.Id == trigger.EntityId, ct))
            {
              await events.FinalizeAsync(trigger.EntityId, occurrence, trigger.HandlerKind, status, resultJson, ct);
            }
            else
            {
              deletedDuringHandling = true;
            }
          }
          else
          {
            _logger.LogWarning("No handler registered for kind {HandlerKind} (trigger {TriggerId}, entity {EntityId}); trigger advances without firing.",
              trigger.HandlerKind, trigger.Id, trigger.EntityId);
            await events.FinalizeAsync(trigger.EntityId, occurrence, trigger.HandlerKind, "error", /*lang=json,strict*/ """{"error":"no handler registered"}""", ct);
          }
        }
        // ClaimResult.AlreadyHandled falls through: advance the trigger without firing.
      }

      if (deletedDuringHandling)
      {
        continue;
      }

      trigger.LastFiredAt = occurrence;
      trigger.NextFireAt = Schedules.NextAfter(trigger.Schedule, occurrence);
      trigger.Enabled = trigger.NextFireAt is not null;
      trigger.UpdatedAt = now;
      await db.SaveChangesAsync(ct);
    }
  }
}
