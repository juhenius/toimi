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

public class SchedulerTickTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""";

  private static async Task<(Data.TietueDbContext db, FakeNotifier notifier, SchedulerTick tick, EntityRepository repo)> SetupAsync()
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("reminder", Schema);
    var repo = new EntityRepository(db, new SchemaValidator());
    var notifier = new FakeNotifier();
    var registry = new HandlerRegistry([new NotifyHandler(notifier)]);
    var tick = new SchedulerTick(db, new OccurrenceRunner(db, registry, new EntityEventStore(db)));
    return (db, notifier, tick, repo);
  }

  [Fact]
  public async Task Fires_due_one_shot_then_disables_it()
  {
    var (db, notifier, tick, repo) = await SetupAsync();
    using var _ = db;
    var e = await repo.CreateAsync("reminder", JsonNode.Parse("""{"title":"Call"}"""), []);
    var handlerConfig = /*lang=json,strict*/ """{"titleTemplate":"{title}"}""";
    await new TriggerRepository(db, TestConfig.Default).CreateAsync(e.Id, /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify", handlerConfig, new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero));

    await tick.RunDueAsync(new DateTimeOffset(2026, 6, 1, 9, 1, 0, TimeSpan.Zero), default);

    Assert.Single(notifier.Sent);
    var trigger = (await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(e.Id))[0];
    Assert.False(trigger.Enabled);
    Assert.Null(trigger.NextFireAt);
    Assert.True(await new EntityEventStore(db).HasEventAsync(e.Id, new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), "notify"));
  }

  [Fact]
  public async Task Recurring_reschedules_next_fire()
  {
    var (db, _, tick, repo) = await SetupAsync();
    using var _ = db;
    var e = await repo.CreateAsync("reminder", JsonNode.Parse("""{"title":"Standup"}"""), []);
    var handlerConfig = /*lang=json,strict*/ """{"titleTemplate":"{title}"}""";
    await new TriggerRepository(db, TestConfig.Default).CreateAsync(e.Id, /*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""", "notify", handlerConfig, new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero));

    await tick.RunDueAsync(new DateTimeOffset(2026, 6, 1, 9, 1, 0, TimeSpan.Zero), default);

    var trigger = (await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(e.Id))[0];
    Assert.True(trigger.Enabled);
    Assert.Equal(new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero), trigger.NextFireAt);
  }

  [Fact]
  public async Task Does_not_fire_a_completed_occurrence()
  {
    var (db, notifier, tick, repo) = await SetupAsync();
    using var _ = db;
    var e = await repo.CreateAsync("reminder", JsonNode.Parse("""{"title":"Skip me"}"""), []);
    var occ = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
    var handlerConfig = /*lang=json,strict*/ """{"titleTemplate":"{title}"}""";
    await new TriggerRepository(db, TestConfig.Default).CreateAsync(e.Id, /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify", handlerConfig, new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero));
    await new EntityEventStore(db).CompleteAsync(e.Id, occ);

    await tick.RunDueAsync(new DateTimeOffset(2026, 6, 1, 9, 1, 0, TimeSpan.Zero), default);

    Assert.Empty(notifier.Sent);
  }

  private sealed class ThrowingHandler : INativeHandler
  {
    public string Kind => "boom";

    public Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
    {
      throw new InvalidOperationException("kaboom");
    }
  }

  [Fact]
  public async Task Failing_handler_is_isolated_and_trigger_advances()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("reminder", Schema);
    var repo = new EntityRepository(db, new SchemaValidator());
    var e = await repo.CreateAsync("reminder", JsonNode.Parse("""{"title":"x"}"""), []);
    var registry = new HandlerRegistry([new ThrowingHandler()]);
    var tick = new SchedulerTick(db, new OccurrenceRunner(db, registry, new EntityEventStore(db)));
    await new TriggerRepository(db, TestConfig.Default).CreateAsync(e.Id, /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "boom", null,
      new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero));

    // Must NOT throw despite the handler throwing.
    await tick.RunDueAsync(new DateTimeOffset(2026, 6, 1, 9, 1, 0, TimeSpan.Zero), default);

    var trigger = (await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(e.Id))[0];
    Assert.False(trigger.Enabled);     // one-shot advanced + disabled (no poison loop)
    Assert.Null(trigger.NextFireAt);
    Assert.True(await new EntityEventStore(db).HasEventAsync(e.Id, new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), "boom")); // error occurrence recorded
  }

  [Fact]
  public async Task Unregistered_handler_kind_records_an_error_and_still_advances_the_trigger()
  {
    // A trigger persisted by an older build can reference a handler that no longer
    // exists. It must not wedge the scheduler: error event recorded, trigger advances
    // (a one-shot is consumed/disabled).
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("reminder", Schema);
    var repo = new EntityRepository(db, new SchemaValidator());
    var e = await repo.CreateAsync("reminder", JsonNode.Parse("""{"title":"Call"}"""), []);
    var handlerConfig = /*lang=json,strict*/ """{"titleTemplate":"{title}"}""";
    await new TriggerRepository(db, TestConfig.Default).CreateAsync(e.Id, /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify", handlerConfig,
      new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero));
    var registry = new HandlerRegistry([]);
    var tick = new SchedulerTick(db, new OccurrenceRunner(db, registry, new EntityEventStore(db)));

    await tick.RunDueAsync(new DateTimeOffset(2026, 6, 1, 9, 1, 0, TimeSpan.Zero), default);

    var evt = await db.EntityEvents.SingleAsync(ev => ev.EntityId == e.Id && ev.Kind == "notify");
    Assert.Equal("error", evt.Status);
    Assert.Contains("no handler registered", evt.Result);
    var trigger = (await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(e.Id))[0];
    Assert.False(trigger.Enabled); // one-shot consumed, not wedged
    Assert.NotNull(trigger.LastFiredAt);
  }

  private sealed class ExplodingHandler(string message) : INativeHandler
  {
    public string Kind => "notify";

    public Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
    {
      throw new InvalidOperationException(message);
    }
  }

  [Fact]
  public async Task Handler_error_text_is_capped_before_it_reaches_the_event_log()
  {
    // A handler's exception message is serialized straight into EntityEvent.Result
    // (jsonb) by SchedulerTick. Cap it generically so any handler's failure — not just
    // ntfy's — is bounded before it reaches the database.
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("reminder", Schema);
    var repo = new EntityRepository(db, new SchemaValidator());
    var e = await repo.CreateAsync("reminder", JsonNode.Parse("""{"title":"x"}"""), []);
    var registry = new HandlerRegistry([new ExplodingHandler(new string('y', 20_000))]);
    var tick = new SchedulerTick(db, new OccurrenceRunner(db, registry, new EntityEventStore(db)));
    await new TriggerRepository(db, TestConfig.Default).CreateAsync(e.Id, /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify", null,
      new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero));

    await tick.RunDueAsync(new DateTimeOffset(2026, 6, 1, 9, 1, 0, TimeSpan.Zero), default);

    var evt = await db.EntityEvents.SingleAsync(ev => ev.EntityId == e.Id && ev.Kind == "notify");
    Assert.Equal("error", evt.Status);
    Assert.NotNull(evt.Result);
    Assert.True(evt.Result.Length < 2000, $"result was {evt.Result.Length} chars; expected the message to be capped");
    Assert.Contains("yyy", evt.Result);
  }

  [Fact]
  public async Task Entity_deleted_by_handler_does_not_throw_and_removes_entity()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("task", /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"}}}""");
    var repo = new EntityRepository(db, new SchemaValidator());
    var entity = await repo.CreateAsync("task", JsonNode.Parse("""{"name":"x"}"""), []);

    var triggers = new TriggerRepository(db, TestConfig.Default);
    var occurrence = DateTimeOffset.UtcNow.AddMinutes(-1);
    await triggers.CreateAsync(entity.Id, $$"""{"at":"{{occurrence:O}}"}""", "delete", null, DateTimeOffset.UtcNow.AddMinutes(-2));

    var registry = new HandlerRegistry([new DeleteHandler(repo)]);
    var tick = new SchedulerTick(db, new OccurrenceRunner(db, registry, new EntityEventStore(db)));

    await tick.RunDueAsync(DateTimeOffset.UtcNow, CancellationToken.None);

    Assert.Null(await repo.GetAsync(entity.Id));
    // The guard skips recording an event for an entity the handler deleted
    // (the event FKs to it). Without the guard, an event row would be written.
    Assert.False(await db.EntityEvents.AnyAsync(e => e.EntityId == entity.Id));
  }
}
