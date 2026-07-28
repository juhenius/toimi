using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ClaimThenRunTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""";
  private static readonly DateTimeOffset Occurrence = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
  private static readonly DateTimeOffset TickTime = new(2026, 6, 1, 9, 1, 0, TimeSpan.Zero);

  private static async Task<(Data.TietueDbContext db, FakeNotifier notifier, SchedulerTick tick, Guid entityId)> SetupWithDueTriggerAsync()
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("reminder", Schema);
    var repo = new EntityRepository(db, new SchemaValidator());
    var notifier = new FakeNotifier();
    var registry = new HandlerRegistry([new NotifyHandler(notifier)]);
    var tick = new SchedulerTick(db, registry, new EntityEventStore(db));
    var e = await repo.CreateAsync("reminder", JsonNode.Parse("""{"title":"Call"}"""), []);
    await new TriggerRepository(db).CreateAsync(
      e.Id, /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify",
      /*lang=json,strict*/ """{"titleTemplate":"{title}"}""",
      new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero));
    return (db, notifier, tick, e.Id);
  }

  [Fact]
  public async Task Successful_run_leaves_terminal_event_and_advances_trigger()
  {
    var (db, notifier, tick, entityId) = await SetupWithDueTriggerAsync();
    using var _ = db;

    await tick.RunDueAsync(TickTime, default);

    Assert.Single(notifier.Sent);
    var evt = await db.EntityEvents.SingleAsync(e => e.EntityId == entityId && e.Kind == "notify");
    Assert.NotEqual("started", evt.Status);
    var trigger = (await new TriggerRepository(db).ListByEntityAsync(entityId))[0];
    Assert.False(trigger.Enabled); // one-shot consumed
  }

  [Fact]
  public async Task Fresh_started_claim_suppresses_handler_and_does_not_advance_trigger()
  {
    var (db, notifier, tick, entityId) = await SetupWithDueTriggerAsync();
    using var _ = db;
    // Simulate another instance mid-handler: a 'started' event 1 minute old.
    db.EntityEvents.Add(new Data.EntityEvent
    {
      Id = Guid.NewGuid(),
      EntityId = entityId,
      OccurrenceUtc = Occurrence,
      Kind = "notify",
      Status = "started",
      CreatedAt = TickTime.AddMinutes(-1),
    });
    await db.SaveChangesAsync();

    await tick.RunDueAsync(TickTime, default);

    Assert.Empty(notifier.Sent);
    var trigger = (await new TriggerRepository(db).ListByEntityAsync(entityId))[0];
    Assert.True(trigger.Enabled);         // NOT advanced: occurrence stays due
    Assert.NotNull(trigger.NextFireAt);
  }

  [Fact]
  public async Task Stale_started_claim_is_retaken_and_handler_runs()
  {
    var (db, notifier, tick, entityId) = await SetupWithDueTriggerAsync();
    using var _ = db;
    // Simulate an abandoned claim from a crashed pod: 'started' 20 minutes old.
    db.EntityEvents.Add(new Data.EntityEvent
    {
      Id = Guid.NewGuid(),
      EntityId = entityId,
      OccurrenceUtc = Occurrence,
      Kind = "notify",
      Status = "started",
      CreatedAt = TickTime.AddMinutes(-20),
    });
    await db.SaveChangesAsync();

    await tick.RunDueAsync(TickTime, default);

    Assert.Single(notifier.Sent);
    var evt = await db.EntityEvents.SingleAsync(e => e.EntityId == entityId && e.Kind == "notify");
    Assert.NotEqual("started", evt.Status); // finalized
    var trigger = (await new TriggerRepository(db).ListByEntityAsync(entityId))[0];
    Assert.False(trigger.Enabled);          // advanced after successful retry
  }

  [Fact]
  public async Task Terminal_event_from_before_crash_suppresses_handler_and_advances_trigger()
  {
    var (db, notifier, tick, entityId) = await SetupWithDueTriggerAsync();
    using var _ = db;
    // Crash window: handler ran and finalized, pod died before the trigger advanced.
    db.EntityEvents.Add(new Data.EntityEvent
    {
      Id = Guid.NewGuid(),
      EntityId = entityId,
      OccurrenceUtc = Occurrence,
      Kind = "notify",
      Status = "sent",
      CreatedAt = TickTime.AddMinutes(-1),
    });
    await db.SaveChangesAsync();

    await tick.RunDueAsync(TickTime, default);

    Assert.Empty(notifier.Sent);
    var trigger = (await new TriggerRepository(db).ListByEntityAsync(entityId))[0];
    Assert.False(trigger.Enabled); // advanced past the already-handled occurrence
  }

  [Fact]
  public async Task Complete_event_suppresses_handler_but_advances_trigger()
  {
    var (db, notifier, tick, entityId) = await SetupWithDueTriggerAsync();
    using var _ = db;
    await new EntityEventStore(db).CompleteAsync(entityId, Occurrence);

    await tick.RunDueAsync(TickTime, default);

    Assert.Empty(notifier.Sent);
    var trigger = (await new TriggerRepository(db).ListByEntityAsync(entityId))[0];
    Assert.False(trigger.Enabled); // advanced past the completed occurrence
  }
}
