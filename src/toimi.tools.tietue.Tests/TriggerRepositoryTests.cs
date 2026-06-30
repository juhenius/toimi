using toimi.tools.tietue.Scheduling;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TriggerRepositoryTests
{
  private static readonly DateTimeOffset Now = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

  [Fact]
  public async Task Create_computes_next_fire_at_from_schedule()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db);
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
    var repo = new TriggerRepository(db);
    var e1 = Guid.NewGuid();
    await repo.CreateAsync(e1, /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);
    await repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);

    Assert.Single(await repo.ListByEntityAsync(e1));
  }

  [Fact]
  public async Task Delete_removes_trigger()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db);
    var t = await repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);

    Assert.True(await repo.DeleteAsync(t.Id));
    Assert.Null(await repo.GetAsync(t.Id));
  }

  [Fact]
  public async Task Update_replaces_schedule_and_recomputes_next_fire()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db);
    var t = await repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);

    var updated = await repo.UpdateAsync(t.Id, /*lang=json,strict*/ """{"at":"2026-06-02T09:00:00Z"}""", null, null, Now);

    Assert.Equal(new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero), updated!.NextFireAt);
  }

  [Fact]
  public async Task Create_persists_source()
  {
    using var db = TestDb.New();
    var entityId = Guid.NewGuid();
    db.Entities.Add(new Data.Entity { Id = entityId, Type = "t", Data = System.Text.Json.JsonDocument.Parse("{}"), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
    await db.SaveChangesAsync();

    var repo = new TriggerRepository(db);
    var t = await repo.CreateAsync(entityId, /*lang=json,strict*/ """{"at":"2026-09-01T00:00:00Z"}""", "delete", null, DateTimeOffset.UtcNow, "expiry");

    Assert.Equal("expiry", t.Source);
  }
}
