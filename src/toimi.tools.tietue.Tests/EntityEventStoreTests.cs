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

    Assert.True(await store.OccurrenceHandledAsync(e, Occ, "notify"));
  }

  [Fact]
  public async Task Unhandled_when_no_matching_event()
  {
    using var db = TestDb.New();
    var store = new EntityEventStore(db);
    Assert.False(await store.OccurrenceHandledAsync(Guid.NewGuid(), Occ, "notify"));
  }

  [Fact]
  public async Task Complete_is_idempotent()
  {
    using var db = TestDb.New();
    var store = new EntityEventStore(db);
    var e = Guid.NewGuid();

    await store.CompleteAsync(e, Occ);
    await store.CompleteAsync(e, Occ);

    Assert.True(await store.OccurrenceHandledAsync(e, Occ, "notify"));
  }
}
