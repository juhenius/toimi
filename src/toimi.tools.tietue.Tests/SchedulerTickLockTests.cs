using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SchedulerTickLockTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""";

  private sealed class DeniedTickLock : ITickLock
  {
    public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
    {
      return Task.FromResult<IAsyncDisposable?>(null);
    }
  }

  private sealed class RecordingLease : IAsyncDisposable
  {
    public bool Disposed { get; private set; }

    public ValueTask DisposeAsync()
    {
      Disposed = true;
      return ValueTask.CompletedTask;
    }
  }

  private sealed class GrantedTickLock(RecordingLease lease) : ITickLock
  {
    public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
    {
      return Task.FromResult<IAsyncDisposable?>(lease);
    }
  }

  [Fact]
  public async Task Skips_all_triggers_when_lock_denied()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("reminder", Schema);
    var repo = new EntityRepository(db, new SchemaValidator());
    var notifier = new FakeNotifier();
    var registry = new HandlerRegistry([new NotifyHandler(notifier)]);
    var tick = new SchedulerTick(db, registry, new EntityEventStore(db), tickLock: new DeniedTickLock());

    var e = await repo.CreateAsync("reminder", JsonNode.Parse("""{"title":"Call"}"""), []);
    await new TriggerRepository(db, TestConfig.Default).CreateAsync(
      e.Id, /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify",
      /*lang=json,strict*/ """{"titleTemplate":"{title}"}""",
      new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero));

    await tick.RunDueAsync(new DateTimeOffset(2026, 6, 1, 9, 1, 0, TimeSpan.Zero), default);

    Assert.Empty(notifier.Sent);
    var trigger = (await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(e.Id))[0];
    Assert.True(trigger.Enabled);
    Assert.NotNull(trigger.NextFireAt);
  }

  [Fact]
  public async Task Releases_lease_after_processing()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("reminder", Schema);
    var notifier = new FakeNotifier();
    var registry = new HandlerRegistry([new NotifyHandler(notifier)]);
    var lease = new RecordingLease();
    var tick = new SchedulerTick(db, registry, new EntityEventStore(db), tickLock: new GrantedTickLock(lease));

    await tick.RunDueAsync(DateTimeOffset.UtcNow, default);

    Assert.True(lease.Disposed);
  }

  [Fact]
  public async Task Releases_lease_when_tick_body_throws()
  {
    var db = TestDb.New();
    var notifier = new FakeNotifier();
    var registry = new HandlerRegistry([new NotifyHandler(notifier)]);
    var lease = new RecordingLease();
    var tick = new SchedulerTick(db, registry, new EntityEventStore(db), tickLock: new GrantedTickLock(lease));

    db.Dispose(); // Poison the context so the due-trigger query throws.

    await Assert.ThrowsAnyAsync<Exception>(() => tick.RunDueAsync(DateTimeOffset.UtcNow, default));

    Assert.True(lease.Disposed);
  }
}
