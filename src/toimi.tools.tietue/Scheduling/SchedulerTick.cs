using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Scheduling;

public class SchedulerTick(TietueDbContext db, OccurrenceRunner runner, ILogger<SchedulerTick>? logger = null, ITickLock? tickLock = null)
{
  private readonly ILogger<SchedulerTick> _logger = logger ?? NullLogger<SchedulerTick>.Instance;

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

      if (entity is not null)
      {
        // Already inside the tick lock, so the runner claims without re-acquiring it.
        var outcome = await runner.RunAsync(trigger, entity, occurrence, now, claimLock: null, ct: ct);
        if (!outcome.ShouldAdvance)
        {
          // InProgress: leave the trigger un-advanced so it stays due for retry.
          // EntityDeleted: the trigger was cascade-deleted with its entity — don't touch it.
          continue;
        }
      }

      trigger.LastFiredAt = occurrence;
      trigger.NextFireAt = Schedule.Parse(trigger.Schedule)?.NextAfter(occurrence);
      trigger.Enabled = trigger.NextFireAt is not null;
      trigger.UpdatedAt = now;
      await db.SaveChangesAsync(ct);
    }
  }
}
