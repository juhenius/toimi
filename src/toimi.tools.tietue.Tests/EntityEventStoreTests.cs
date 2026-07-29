using toimi.tools.tietue.Events;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class EntityEventStoreTests
{
  private static readonly DateTimeOffset Occ = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

  [Fact]
  public async Task Records_an_event()
  {
    using var db = TestDb.New();
    var store = new EntityEventStore(db);
    var e = Guid.NewGuid();

    await store.RecordAsync(e, Occ, "notify", "sent", null);

    Assert.True(await store.HasEventAsync(e, Occ, "notify"));
  }

  [Fact]
  public async Task Occurrence_handled_when_kind_or_complete_present()
  {
    using var db = TestDb.New();
    var store = new EntityEventStore(db);
    var e = Guid.NewGuid();
    await store.RecordAsync(e, Occ, "complete", "done", null);

    Assert.Equal(ClaimResult.AlreadyHandled, await store.TryClaimAsync(e, Occ, "notify", Occ));
  }

  [Fact]
  public async Task Unhandled_when_no_matching_event()
  {
    using var db = TestDb.New();
    var store = new EntityEventStore(db);
    Assert.Equal(ClaimResult.Claimed, await store.TryClaimAsync(Guid.NewGuid(), Occ, "notify", Occ));
  }

  [Fact]
  public async Task Complete_is_idempotent()
  {
    using var db = TestDb.New();
    var store = new EntityEventStore(db);
    var e = Guid.NewGuid();

    await store.CompleteAsync(e, Occ);
    await store.CompleteAsync(e, Occ);

    Assert.Equal(ClaimResult.AlreadyHandled, await store.TryClaimAsync(e, Occ, "notify", Occ));
  }

  [Theory]
  [InlineData(899, false)] // 14m59s old: still in progress, claim refused
  [InlineData(900, true)]  // exactly 15m: stale, taken over
  [InlineData(901, true)]  // past 15m: stale, taken over
  public async Task Stale_claim_boundary_is_exactly_fifteen_minutes(int ageSeconds, bool expectTakeover)
  {
    using var db = TestDb.New();
    var store = new EntityEventStore(db);
    var e = Guid.NewGuid();
    var now = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    db.EntityEvents.Add(new Data.EntityEvent
    {
      Id = Guid.NewGuid(),
      EntityId = e,
      OccurrenceUtc = Occ,
      Kind = "notify",
      Status = "started",
      CreatedAt = now - TimeSpan.FromSeconds(ageSeconds),
    });
    await db.SaveChangesAsync();

    var result = await store.TryClaimAsync(e, Occ, "notify", now);

    // An off-by-one here means either a duplicate handler run (too eager) or a
    // permanently wedged occurrence (too lazy) when an instance crashes mid-run.
    Assert.Equal(expectTakeover ? ClaimResult.Claimed : ClaimResult.InProgress, result);
  }
}
