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

    var trigger = new Trigger
    {
      Id = Guid.NewGuid(),
      EntityId = entityId,
      Schedule = scheduleJson,
      HandlerKind = handlerKind,
      HandlerConfig = handlerConfig,
      Source = source,
      Enabled = true,
      NextFireAt = Schedules.InitialNextFireAt(scheduleJson, now),
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
