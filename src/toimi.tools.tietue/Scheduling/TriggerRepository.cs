using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Scheduling;

public class TriggerRepository(TietueDbContext db, Toimi.Core.Configuration.ToimiConfiguration config)
{
  public async Task<Trigger> CreateAsync(Guid entityId, string scheduleJson, string handlerKind, string? handlerConfig, DateTimeOffset now, string? source = null, CancellationToken ct = default)
  {
    // Stamp the user's default tz onto recurring schedules that omit one, at creation time, so the
    // persisted schedule is self-describing and its wall-clock survives DST forever.
    scheduleJson = Schedules.WithDefaultTimeZone(scheduleJson, config.UserTimeZone);

    var nextFireAt = Schedules.InitialNextFireAt(scheduleJson, now);
    var trigger = new Trigger
    {
      Id = Guid.NewGuid(),
      EntityId = entityId,
      Schedule = scheduleJson,
      HandlerKind = handlerKind,
      HandlerConfig = handlerConfig,
      Source = source,
      // A trigger that can never fire must not sit enabled and invisible to the scheduler.
      Enabled = nextFireAt is not null,
      NextFireAt = nextFireAt,
      CreatedAt = now,
      UpdatedAt = now,
    };
    db.Triggers.Add(trigger);
    await db.SaveChangesAsync(ct);
    return trigger;
  }

  public Task<Trigger?> GetAsync(Guid id, CancellationToken ct = default)
  {
    return db.Triggers.FirstOrDefaultAsync(t => t.Id == id, ct);
  }

  public async Task<IReadOnlyList<Trigger>> ListByEntityAsync(Guid entityId, CancellationToken ct = default)
  {
    return await db.Triggers.Where(t => t.EntityId == entityId).OrderBy(t => t.CreatedAt).ToListAsync(ct);
  }

  public async Task<Trigger?> UpdateAsync(Guid id, string? scheduleJson, string? handlerConfig, bool? enabled, DateTimeOffset now, CancellationToken ct = default)
  {
    var trigger = await db.Triggers.FirstOrDefaultAsync(t => t.Id == id, ct);
    if (trigger is null)
    {
      return null;
    }

    if (scheduleJson is not null)
    {
      // Same stamping as CreateAsync: recurring schedules that omit a tz get the user's default,
      // so an update can't silently reintroduce DST drift.
      scheduleJson = Schedules.WithDefaultTimeZone(scheduleJson, config.UserTimeZone);
      trigger.Schedule = scheduleJson;
      trigger.NextFireAt = Schedules.InitialNextFireAt(scheduleJson, now);
    }

    if (handlerConfig is not null)
    {
      trigger.HandlerConfig = handlerConfig;
    }

    if (enabled is not null)
    {
      trigger.Enabled = enabled.Value;
    }

    // Re-enabling an exhausted trigger must not produce Enabled=true with a null
    // NextFireAt — such a trigger is invisible to the scheduler's due query forever.
    // Recompute from the schedule; a one-shot 'at' in the past resolves to a non-null
    // but already-elapsed instant (InitialNextFireAt does not compare 'at' to 'now'),
    // so also require the recomputed fire time to still be in the future before
    // allowing the re-enable; otherwise refuse it and leave NextFireAt null.
    if (trigger.Enabled && trigger.NextFireAt is null)
    {
      var recomputed = Schedules.InitialNextFireAt(trigger.Schedule, now);
      trigger.NextFireAt = recomputed is not null && recomputed > now ? recomputed : null;
      trigger.Enabled = trigger.NextFireAt is not null;
    }

    trigger.UpdatedAt = now;
    await db.SaveChangesAsync(ct);
    return trigger;
  }

  public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
  {
    var trigger = await db.Triggers.FirstOrDefaultAsync(t => t.Id == id, ct);
    if (trigger is null)
    {
      return false;
    }

    db.Triggers.Remove(trigger);
    await db.SaveChangesAsync(ct);
    return true;
  }
}
