using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Handlers;

namespace toimi.tools.tietue.Scheduling;

public class SchedulerTick(TietueDbContext db, HandlerRegistry handlers, EntityEventStore events)
{
  public async Task RunDueAsync(DateTimeOffset now, CancellationToken ct)
  {
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
      if (entity is not null && !await events.OccurrenceHandledAsync(trigger.EntityId, occurrence, trigger.HandlerKind, ct))
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
            resultJson = System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message });
          }

          // The handler may have deleted the entity (delete handler, or an agent run).
          // Only record an event while the entity exists (the event FKs to it); if it is gone,
          // its trigger was cascade-deleted, so skip advancing the trigger too.
          if (await db.Entities.AnyAsync(e => e.Id == trigger.EntityId, ct))
          {
            await events.RecordAsync(trigger.EntityId, occurrence, trigger.HandlerKind, status, resultJson, ct);
          }
          else
          {
            deletedDuringHandling = true;
          }
        }
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
