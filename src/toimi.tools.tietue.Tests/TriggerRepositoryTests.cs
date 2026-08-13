using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TriggerRepositoryTests
{
  private static readonly DateTimeOffset Now = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

  [Fact]
  public async Task Create_computes_next_fire_at_from_schedule()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);
    var entityId = Guid.NewGuid();

    var t = await repo.CreateAsync(entityId, /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);

    Assert.NotEqual(Guid.Empty, t.Id);
    Assert.Equal(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), t.NextFireAt);
    Assert.True(t.Enabled);
  }

  [Fact]
  public async Task List_by_entity_returns_its_triggers()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);
    var e1 = Guid.NewGuid();
    await repo.CreateAsync(e1, /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);
    await repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);

    Assert.Single(await repo.ListByEntityAsync(e1));
  }

  [Fact]
  public async Task Delete_removes_trigger()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);
    var t = await repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);

    Assert.True(await repo.DeleteAsync(t.Id));
    Assert.Null(await repo.GetAsync(t.Id));
  }

  [Fact]
  public async Task Update_replaces_schedule_and_recomputes_next_fire()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);
    var t = await repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);

    var updated = await repo.UpdateAsync(t.Id, /*lang=json,strict*/ """{"at":"2026-06-02T09:00:00Z"}""", null, null, Now);

    Assert.Equal(new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero), updated!.NextFireAt);
  }

  [Fact]
  public async Task Update_stamps_default_tz_onto_tz_less_recurring_schedule()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);
    var t = await repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);

    var updated = await repo.UpdateAsync(t.Id, /*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""", null, null, Now);

    Assert.Contains("\"tz\":\"Europe/Helsinki\"", updated!.Schedule);
  }

  [Fact]
  public async Task Update_leaves_explicit_tz_unchanged()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);
    var t = await repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);

    var updated = await repo.UpdateAsync(t.Id, /*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY","tz":"UTC"}""", null, null, Now);

    Assert.Contains("\"tz\":\"UTC\"", updated!.Schedule);
  }

  [Fact]
  public async Task Create_persists_source()
  {
    using var db = TestDb.New();
    var entityId = Guid.NewGuid();
    db.Entities.Add(new Data.Entity { Id = entityId, Type = "t", Data = System.Text.Json.JsonDocument.Parse("{}"), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
    await db.SaveChangesAsync();

    var repo = new TriggerRepository(db, TestConfig.Default);
    var t = await repo.CreateAsync(entityId, /*lang=json,strict*/ """{"at":"2026-09-01T00:00:00Z"}""", "delete", null, DateTimeOffset.UtcNow, "expiry");

    Assert.Equal("expiry", t.Source);
  }

  [Fact]
  public async Task Create_with_unparseable_schedule_throws()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);

    var ex = await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"at":"soon"}""", "notify", null, Now));

    Assert.Contains("Invalid schedule JSON", ex.Message);
    Assert.Empty(db.Triggers);
  }

  [Fact]
  public async Task Create_with_exhausted_recurrence_throws_never_fires()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);

    var ex = await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"start":"2020-01-01T00:00:00Z","rrule":"FREQ=YEARLY;COUNT=1"}""", "notify", null, Now));

    Assert.Contains("does not resolve to a future fire time", ex.Message);
  }

  [Fact]
  public async Task Create_webhook_trigger_mints_secret_and_leaves_next_fire_null()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);

    var t = await repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"webhook":{"rateLimit":6}}""", "notify", null, Now);

    Assert.True(t.Enabled);
    Assert.Null(t.NextFireAt);
    Assert.NotNull(t.Secret);
    Assert.Equal(64, t.Secret.Length);
  }

  [Fact]
  public async Task Create_time_trigger_has_no_secret()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);

    var t = await repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);

    Assert.Null(t.Secret);
  }

  [Fact]
  public async Task Update_without_schedule_change_leaves_webhook_trigger_enabled()
  {
    // The exhausted-re-enable guard treats Enabled && NextFireAt == null as an exhausted
    // time trigger; a webhook trigger lives in that state permanently and must be exempt.
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);
    var t = await repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"webhook":{}}""", "notify", null, Now);

    var afterConfig = await repo.UpdateAsync(t.Id, null, /*lang=json,strict*/ """{"titleTemplate":"hi"}""", null, Now);
    Assert.True(afterConfig!.Enabled);

    var afterReEnable = await repo.UpdateAsync(t.Id, null, null, true, Now);
    Assert.True(afterReEnable!.Enabled);
    Assert.Null(afterReEnable.NextFireAt);
    Assert.NotNull(afterReEnable.Secret);
  }

  [Fact]
  public async Task Update_swapping_time_anchor_to_webhook_mints_secret()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);
    var t = await repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);

    var updated = await repo.UpdateAsync(t.Id, /*lang=json,strict*/ """{"webhook":{}}""", null, null, Now);

    Assert.NotNull(updated!.Secret);
    Assert.Null(updated.NextFireAt);
    Assert.True(updated.Enabled);
  }

  [Fact]
  public async Task Update_swapping_webhook_to_time_anchor_revokes_secret()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);
    var t = await repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"webhook":{}}""", "notify", null, Now);

    var updated = await repo.UpdateAsync(t.Id, /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", null, null, Now);

    Assert.Null(updated!.Secret);
    Assert.Equal(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), updated.NextFireAt);
  }

  [Fact]
  public async Task Update_webhook_schedule_keeps_existing_secret()
  {
    // Editing the window/rate limit must not rotate the capability URL callers already hold.
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);
    var t = await repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"webhook":{}}""", "notify", null, Now);
    var original = t.Secret;

    var updated = await repo.UpdateAsync(t.Id, /*lang=json,strict*/ """{"webhook":{"rateLimit":2}}""", null, null, Now);

    Assert.Equal(original, updated!.Secret);
  }

  [Fact]
  public async Task Create_webhook_with_past_active_until_throws()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);

    var ex = await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"webhook":{"activeUntil":"2020-01-01T00:00:00Z"}}""", "notify", null, Now));

    Assert.Contains("already in the past", ex.Message);
    Assert.Empty(db.Triggers);
  }

  [Fact]
  public async Task Create_webhook_with_future_window_is_accepted()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);

    var t = await repo.CreateAsync(Guid.NewGuid(),
      /*lang=json,strict*/ """{"webhook":{"activeAfter":"2026-07-01T00:00:00Z","activeUntil":"2026-08-01T00:00:00Z"}}""", "notify", null, Now);

    Assert.NotNull(t.Secret);
  }

  [Fact]
  public async Task Create_webhook_with_invalid_spec_throws()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);

    var ex = await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"webhook":{"rateLimit":0}}""", "notify", null, Now));

    Assert.Contains("rateLimit", ex.Message);
    Assert.Empty(db.Triggers);
  }

  [Fact]
  public async Task Create_with_garbage_rrule_throws_instead_of_crashing()
  {
    // Regression: garbage rrule used to escape as an unhandled Ical.Net exception from
    // InitialNextFireAt. Whether TryValidate's RecurrencePattern parse or the never-fires
    // check catches it, the write must fail with a TietueValidationException.
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);

    await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"NOT-AN-RRULE"}""", "notify", null, Now));
    Assert.Empty(db.Triggers);
  }
}
