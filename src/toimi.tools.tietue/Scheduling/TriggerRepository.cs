using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Scheduling;

public class TriggerRepository(TietueDbContext db, Toimi.Core.Configuration.ToimiConfiguration config)
{
  internal const string InvalidScheduleJsonError =
    "Invalid schedule JSON. Expected {\"at\":\"<iso utc>\"} for one-shot, {\"start\":\"<iso utc>\",\"rrule\":\"FREQ=...\"} for recurring, or {\"webhook\":{...}} for call-anchored.";
  internal const string NeverFiresError =
    "Schedule does not resolve to a future fire time. Check the 'at'/'start'+'rrule' fields.";
  internal const string WebhookWindowClosedError =
    "Webhook 'activeUntil' is already in the past — the URL could never fire.";

  public Task<Trigger> CreateAsync(Guid entityId, string scheduleJson, string handlerKind, string? handlerConfig, DateTimeOffset now, string? source = null, CancellationToken ct = default)
  {
    return CreateAsync(entityId, ParseOrThrow(scheduleJson), handlerKind, handlerConfig, now, source, ct);
  }

  public async Task<Trigger> CreateAsync(Guid entityId, Schedule schedule, string handlerKind, string? handlerConfig, DateTimeOffset now, string? source = null, CancellationToken ct = default)
  {
    var (stamped, nextFireAt) = ResolveOrThrow(schedule, now);
    var trigger = new Trigger
    {
      Id = Guid.NewGuid(),
      EntityId = entityId,
      Schedule = stamped.ToJson(),
      HandlerKind = handlerKind,
      HandlerConfig = handlerConfig,
      Source = source,
      Secret = stamped.IsWebhook ? MintSecret() : null,
      Enabled = true,
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

    // Validate and resolve BEFORE mutating the tracked row: a validation throw must not leave
    // half-applied changes for a later SaveChangesAsync in the same scope to sweep up.
    if (scheduleJson is not null)
    {
      var (stamped, nextFireAt) = ResolveOrThrow(ParseOrThrow(scheduleJson), now);
      trigger.Schedule = stamped.ToJson();
      trigger.NextFireAt = nextFireAt;
      // Anchor swap: time→webhook mints a capability secret, webhook→time revokes it.
      trigger.Secret = stamped.IsWebhook ? trigger.Secret ?? MintSecret() : null;
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
    // but already-elapsed instant (NextOnOrAfter does not compare 'at' to 'now'),
    // so also require the recomputed fire time to still be in the future before
    // allowing the re-enable; otherwise refuse it and leave NextFireAt null.
    // Webhook anchors are exempt: a null NextFireAt is their permanent, healthy state,
    // not exhaustion — without this check every update would silently disable them.
    if (trigger.Enabled && trigger.NextFireAt is null && Schedule.Parse(trigger.Schedule)?.IsWebhook != true)
    {
      var recomputed = Schedule.Parse(trigger.Schedule)?.NextOnOrAfter(now);
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

  private static Schedule ParseOrThrow(string scheduleJson)
  {
    return Schedule.Parse(scheduleJson) ?? throw new TietueValidationException([InvalidScheduleJsonError]);
  }

  // Stamp the user's default tz (so the persisted schedule is self-describing and its
  // wall-clock survives DST) → validate → resolve the first fire. Throwing — not silently
  // disabling — is the contract: every persisted time-anchored trigger is born enabled
  // with a real NextFireAt; a webhook (call-anchored) trigger is born enabled with a null
  // one, permanently invisible to the scheduler. "Invalid" (grammar/rrule/sub-daily) and
  // "exhausted" (valid but no future occurrence) get distinct messages.
  private (Schedule Schedule, DateTimeOffset? NextFireAt) ResolveOrThrow(Schedule schedule, DateTimeOffset now)
  {
    var stamped = schedule.WithDefaultTz(config.UserTimeZone);
    if (!stamped.TryValidate(out var error))
    {
      throw new TietueValidationException([error!]);
    }

    // The clock-dependent half of validation (TryValidate is deliberately clock-free):
    // a webhook whose window already closed is the call-anchored analogue of an
    // exhausted recurrence — reject it the same way instead of minting a dead URL.
    if (stamped.Webhook is { ActiveUntil: { } until } && until <= now)
    {
      throw new TietueValidationException([WebhookWindowClosedError]);
    }

    var nextFireAt = stamped.IsWebhook
      ? (DateTimeOffset?)null
      : stamped.NextOnOrAfter(now) ?? throw new TietueValidationException([NeverFiresError]);
    return (stamped, nextFireAt);
  }

  private static string MintSecret()
  {
    return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
  }
}
