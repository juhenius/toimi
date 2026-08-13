using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using toimi.tools.tietue.Webhooks;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class WebhookDispatcherTests
{
  private static readonly DateTimeOffset Occurrence = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
  private static readonly DateTimeOffset Now = new(2026, 6, 1, 9, 0, 1, TimeSpan.Zero);

  private static async Task<(Data.TietueDbContext db, Data.Entity entity, Data.Trigger trigger)> SetupAsync()
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("reminder", /*lang=json,strict*/ """{"type":"object","properties":{"title":{"type":"string"}}}""");
    var entity = await new EntityRepository(db, new SchemaValidator()).CreateAsync("reminder", JsonNode.Parse("""{"title":"Ring"}"""), []);
    var trigger = await new TriggerRepository(db, TestConfig.Default).CreateAsync(
      entity.Id, /*lang=json,strict*/ """{"webhook":{}}""", "notify",
      /*lang=json,strict*/ """{"messageTemplate":"door: {door}"}""", Occurrence.AddHours(-1));
    return (db, entity, trigger);
  }

  private static OccurrenceRunner Runner(Data.TietueDbContext db, params INativeHandler[] handlers)
  {
    return new OccurrenceRunner(db, new HandlerRegistry(handlers), new EntityEventStore(db), claimLockRetryDelay: TimeSpan.Zero);
  }

  private static WebhookFiring Firing(Guid triggerId, string paramsJson = "{}")
  {
    using var doc = JsonDocument.Parse(paramsJson);
    return new WebhookFiring(triggerId, Occurrence, doc.RootElement.Clone());
  }

  [Fact]
  public async Task Runs_the_handler_with_params_and_finalizes_the_firing_occurrence()
  {
    var (db, entity, trigger) = await SetupAsync();
    using var _ = db;
    var notifier = new FakeNotifier();

    await WebhookDispatcher.ProcessAsync(
      Firing(trigger.Id, /*lang=json,strict*/ """{"door":"front"}"""), db, Runner(db, new NotifyHandler(notifier)),
      tickLock: null, Now, NullLogger.Instance, default, TimeSpan.Zero);

    Assert.Equal("door: front", notifier.Sent.Single().Message);
    var evt = await db.EntityEvents.SingleAsync(e => e.EntityId == entity.Id);
    Assert.Equal(Occurrence, evt.OccurrenceUtc);
    Assert.Equal("sent", evt.Status);
  }

  [Fact]
  public async Task Does_not_touch_the_trigger_lifecycle_fields()
  {
    var (db, _, trigger) = await SetupAsync();
    using var _ = db;

    await WebhookDispatcher.ProcessAsync(
      Firing(trigger.Id), db, Runner(db, new NotifyHandler(new FakeNotifier())),
      tickLock: null, Now, NullLogger.Instance, default, TimeSpan.Zero);

    var after = await db.Triggers.SingleAsync(t => t.Id == trigger.Id);
    Assert.True(after.Enabled);
    Assert.Null(after.NextFireAt);
    Assert.Null(after.LastFiredAt);
  }

  [Fact]
  public async Task Deleted_trigger_is_dropped_without_throwing()
  {
    var (db, entity, trigger) = await SetupAsync();
    using var _ = db;
    db.Triggers.Remove(trigger);
    await db.SaveChangesAsync();

    await WebhookDispatcher.ProcessAsync(
      Firing(trigger.Id), db, Runner(db, new NotifyHandler(new FakeNotifier())),
      tickLock: null, Now, NullLogger.Instance, default, TimeSpan.Zero);

    Assert.DoesNotContain(db.EntityEvents, e => e.EntityId == entity.Id);
  }

  [Fact]
  public async Task Disabled_since_acceptance_is_dropped()
  {
    var (db, entity, trigger) = await SetupAsync();
    using var _ = db;
    trigger.Enabled = false;
    await db.SaveChangesAsync();
    var notifier = new FakeNotifier();

    await WebhookDispatcher.ProcessAsync(
      Firing(trigger.Id), db, Runner(db, new NotifyHandler(notifier)),
      tickLock: null, Now, NullLogger.Instance, default, TimeSpan.Zero);

    Assert.Empty(notifier.Sent);
    Assert.DoesNotContain(db.EntityEvents, e => e.EntityId == entity.Id);
  }

  [Fact]
  public async Task Deleted_entity_is_dropped()
  {
    var (db, entity, trigger) = await SetupAsync();
    using var _ = db;
    db.Entities.Remove(entity);
    await db.SaveChangesAsync();
    var notifier = new FakeNotifier();

    await WebhookDispatcher.ProcessAsync(
      Firing(trigger.Id), db, Runner(db, new NotifyHandler(notifier)),
      tickLock: null, Now, NullLogger.Instance, default, TimeSpan.Zero);

    Assert.Empty(notifier.Sent);
  }

  [Fact]
  public async Task Busy_lock_retries_bounded_then_drops()
  {
    var (db, entity, trigger) = await SetupAsync();
    using var _ = db;
    var denied = new AlwaysDeniedLock();
    var notifier = new FakeNotifier();

    await WebhookDispatcher.ProcessAsync(
      Firing(trigger.Id), db, Runner(db, new NotifyHandler(notifier)),
      denied, Now, NullLogger.Instance, default, TimeSpan.Zero);

    // BusyAttempts dispatcher attempts × 3 runner lock attempts each.
    Assert.Equal(WebhookDispatcher.BusyAttempts * 3, denied.Attempts);
    Assert.Empty(notifier.Sent);
    Assert.DoesNotContain(db.EntityEvents, e => e.EntityId == entity.Id);
  }

  private sealed class AlwaysDeniedLock : ITickLock
  {
    public int Attempts { get; private set; }

    public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
    {
      Attempts++;
      return Task.FromResult<IAsyncDisposable?>(null);
    }
  }
}
