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

public class OccurrenceRunnerTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""";
  private static readonly DateTimeOffset Occurrence = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
  private static readonly DateTimeOffset Now = new(2026, 6, 1, 9, 1, 0, TimeSpan.Zero);

  private static async Task<(Data.TietueDbContext db, Data.Entity entity, Data.Trigger trigger, EntityRepository repo)> SetupAsync(string handlerKind = "notify")
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("reminder", Schema);
    var repo = new EntityRepository(db, new SchemaValidator());
    var entity = await repo.CreateAsync("reminder", JsonNode.Parse("""{"title":"Call"}"""), []);
    var trigger = await new TriggerRepository(db, TestConfig.Default).CreateAsync(
      entity.Id, /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", handlerKind,
      /*lang=json,strict*/ """{"titleTemplate":"{title}"}""",
      new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero));
    return (db, entity, trigger, repo);
  }

  private static OccurrenceRunner NewRunner(Data.TietueDbContext db, params INativeHandler[] handlers)
  {
    return new OccurrenceRunner(db, new HandlerRegistry(handlers), new EntityEventStore(db), claimLockRetryDelay: TimeSpan.Zero);
  }

  [Fact]
  public async Task Ran_finalizes_the_event_with_the_handler_status()
  {
    var (db, entity, trigger, _) = await SetupAsync();
    using var _ = db;
    var notifier = new FakeNotifier();

    var outcome = await NewRunner(db, new NotifyHandler(notifier)).RunAsync(trigger, entity, Occurrence, Now);

    Assert.Equal(OccurrenceState.Ran, outcome.State);
    Assert.Equal("sent", outcome.Status);
    Assert.True(outcome.ShouldAdvance);
    Assert.Single(notifier.Sent);
    var evt = await db.EntityEvents.SingleAsync(e => e.EntityId == entity.Id && e.Kind == "notify");
    Assert.Equal("sent", evt.Status);
  }

  private sealed class ThrowingHandler(string message) : INativeHandler
  {
    public string Kind => "notify";

    public Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
    {
      throw new InvalidOperationException(message);
    }
  }

  [Fact]
  public async Task Throwing_handler_yields_Errored_with_a_capped_message_and_still_advances()
  {
    var (db, entity, trigger, _) = await SetupAsync();
    using var _ = db;

    var outcome = await NewRunner(db, new ThrowingHandler(new string('y', 20_000))).RunAsync(trigger, entity, Occurrence, Now);

    Assert.Equal(OccurrenceState.Errored, outcome.State);
    Assert.Equal("error", outcome.Status);
    Assert.True(outcome.ShouldAdvance);
    Assert.NotNull(outcome.ResultJson);
    Assert.True(outcome.ResultJson.Length < 2000, $"result was {outcome.ResultJson.Length} chars; expected the message to be capped");
    Assert.Contains("[truncated]", outcome.ResultJson);
    var evt = await db.EntityEvents.SingleAsync(e => e.EntityId == entity.Id && e.Kind == "notify");
    Assert.Equal("error", evt.Status);
    Assert.True(evt.Result!.Length < 2000);
  }

  [Fact]
  public async Task Complete_event_yields_AlreadyHandled_without_running_the_handler()
  {
    var (db, entity, trigger, _) = await SetupAsync();
    using var _ = db;
    await new EntityEventStore(db).CompleteAsync(entity.Id, Occurrence);
    var notifier = new FakeNotifier();

    var outcome = await NewRunner(db, new NotifyHandler(notifier)).RunAsync(trigger, entity, Occurrence, Now);

    Assert.Equal(OccurrenceState.AlreadyHandled, outcome.State);
    Assert.True(outcome.ShouldAdvance);
    Assert.Empty(notifier.Sent);
  }

  [Fact]
  public async Task Fresh_started_claim_yields_InProgress_which_does_not_advance()
  {
    var (db, entity, trigger, _) = await SetupAsync();
    using var _ = db;
    db.EntityEvents.Add(new Data.EntityEvent
    {
      Id = Guid.NewGuid(),
      EntityId = entity.Id,
      OccurrenceUtc = Occurrence,
      Kind = "notify",
      Status = "started",
      CreatedAt = Now.AddMinutes(-1),
    });
    await db.SaveChangesAsync();
    var notifier = new FakeNotifier();

    var outcome = await NewRunner(db, new NotifyHandler(notifier)).RunAsync(trigger, entity, Occurrence, Now);

    Assert.Equal(OccurrenceState.InProgress, outcome.State);
    Assert.False(outcome.ShouldAdvance);
    Assert.Empty(notifier.Sent);
  }

  [Fact]
  public async Task Unknown_kind_records_an_error_event_and_reports_UnknownKind()
  {
    var (db, entity, trigger, _) = await SetupAsync();
    using var _ = db;

    var outcome = await NewRunner(db).RunAsync(trigger, entity, Occurrence, Now);

    Assert.Equal(OccurrenceState.UnknownKind, outcome.State);
    Assert.Equal("error", outcome.Status);
    Assert.True(outcome.ShouldAdvance);
    var evt = await db.EntityEvents.SingleAsync(e => e.EntityId == entity.Id && e.Kind == "notify");
    Assert.Equal("error", evt.Status);
    Assert.Contains("no handler registered", evt.Result);
  }

  [Fact]
  public async Task Handler_deleting_the_entity_yields_EntityDeleted_and_leaves_no_event_rows()
  {
    var (db, entity, trigger, repo) = await SetupAsync(handlerKind: "delete");
    using var _ = db;

    var outcome = await NewRunner(db, new DeleteHandler(repo)).RunAsync(trigger, entity, Occurrence, Now);

    Assert.Equal(OccurrenceState.EntityDeleted, outcome.State);
    Assert.Equal("deleted", outcome.Status);
    Assert.False(outcome.ShouldAdvance);
    Assert.Null(await repo.GetAsync(entity.Id));
    Assert.False(await db.EntityEvents.AnyAsync(e => e.EntityId == entity.Id));
  }

  private sealed class CountingDeniedLock : ITickLock
  {
    public int Attempts { get; private set; }

    public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
    {
      Attempts++;
      return Task.FromResult<IAsyncDisposable?>(null);
    }
  }

  [Fact]
  public async Task Denied_claim_lock_yields_Busy_after_three_attempts_without_claiming()
  {
    var (db, entity, trigger, _) = await SetupAsync();
    using var _ = db;
    var notifier = new FakeNotifier();
    var tickLock = new CountingDeniedLock();

    var outcome = await NewRunner(db, new NotifyHandler(notifier)).RunAsync(trigger, entity, Occurrence, Now, claimLock: tickLock);

    Assert.Equal(OccurrenceState.Busy, outcome.State);
    Assert.False(outcome.ShouldAdvance);
    Assert.Equal(3, tickLock.Attempts);
    Assert.Empty(notifier.Sent);
    Assert.False(await db.EntityEvents.AnyAsync(e => e.EntityId == entity.Id));
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

  private sealed class LeaseObservingHandler(RecordingLease lease) : INativeHandler
  {
    public bool? LeaseDisposedAtDispatch { get; private set; }

    public string Kind => "notify";

    public Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
    {
      LeaseDisposedAtDispatch = lease.Disposed;
      return Task.FromResult(new HandlerResult("sent"));
    }
  }

  [Fact]
  public async Task Claim_lock_is_released_before_the_handler_runs()
  {
    var (db, entity, trigger, _) = await SetupAsync();
    using var _ = db;
    var lease = new RecordingLease();
    var handler = new LeaseObservingHandler(lease);

    var outcome = await NewRunner(db, handler).RunAsync(trigger, entity, Occurrence, Now, claimLock: new GrantedTickLock(lease));

    Assert.Equal(OccurrenceState.Ran, outcome.State);
    Assert.True(handler.LeaseDisposedAtDispatch);
  }
}
