using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Tools;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class RunTriggerToolTests
{
  private static async Task<(Data.Entity e, Data.Trigger trigger, RunTriggerTool tool, FakeNotifier notifier)> SetupAsync(Data.TietueDbContext db)
  {
    await new TypeRepository(db).DefineAsync("task", /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"}}}""");
    var entities = new EntityRepository(db, new SchemaValidator());
    var e = await entities.CreateAsync("task", JsonNode.Parse("""{"name":"x"}"""), []);
    var triggers = new TriggerRepository(db, TestConfig.Default);
    var trigger = await triggers.CreateAsync(e.Id, /*lang=json,strict*/ """{"at":"2030-01-01T00:00:00Z"}""", "notify",
      /*lang=json,strict*/ """{"messageTemplate":"ping"}""", DateTimeOffset.UtcNow);
    var notifier = new FakeNotifier();
    var tool = new RunTriggerTool(db, Runner(db, new NotifyHandler(notifier)));
    return (e, trigger, tool, notifier);
  }

  private static OccurrenceRunner Runner(Data.TietueDbContext db, params INativeHandler[] handlers)
  {
    return new OccurrenceRunner(db, new HandlerRegistry(handlers), new EntityEventStore(db), claimLockRetryDelay: TimeSpan.Zero);
  }

  [Fact]
  public async Task Fires_the_handler_immediately_and_returns_result()
  {
    using var db = TestDb.New();
    var (_, trigger, tool, notifier) = await SetupAsync(db);

    var result = await tool.RunTrigger(trigger.Id.ToString());

    Assert.Single(notifier.Sent);
    Assert.Contains("\"status\"", result);
  }

  [Fact]
  public async Task Does_not_advance_the_schedule()
  {
    using var db = TestDb.New();
    var (_, trigger, tool, _) = await SetupAsync(db);
    var before = trigger.NextFireAt;

    await tool.RunTrigger(trigger.Id.ToString());

    Assert.Equal(before, trigger.NextFireAt);
  }

  [Fact]
  public async Task Records_an_entity_event()
  {
    using var db = TestDb.New();
    var (e, trigger, tool, _) = await SetupAsync(db);

    await tool.RunTrigger(trigger.Id.ToString());

    Assert.Contains(db.EntityEvents, ev => ev.EntityId == e.Id);
  }

  [Fact]
  public async Task Unknown_trigger_returns_message()
  {
    using var db = TestDb.New();
    var (_, _, tool, _) = await SetupAsync(db);

    var result = await tool.RunTrigger(Guid.NewGuid().ToString());

    Assert.Contains("No trigger", result);
  }

  [Fact]
  public async Task Invalid_guid_returns_message()
  {
    using var db = TestDb.New();
    var (_, _, tool, _) = await SetupAsync(db);

    Assert.Contains("Invalid", await tool.RunTrigger("nope"));
  }

  [Fact]
  public async Task Handler_exception_is_reported_not_thrown()
  {
    using var db = TestDb.New();
    var (e, _, _, _) = await SetupAsync(db);
    var triggers = new TriggerRepository(db, TestConfig.Default);
    var bad = await triggers.CreateAsync(e.Id, /*lang=json,strict*/ """{"at":"2030-01-01T00:00:00Z"}""", "script", null, DateTimeOffset.UtcNow);
    var tool = new RunTriggerTool(db, Runner(db, new ThrowingHandler("kaboom")));

    var result = await tool.RunTrigger(bad.Id.ToString());

    Assert.Contains("error", result);
  }

  [Fact]
  public async Task Handler_error_text_is_capped_in_the_response_and_the_event()
  {
    using var db = TestDb.New();
    var (e, _, _, _) = await SetupAsync(db);
    var triggers = new TriggerRepository(db, TestConfig.Default);
    var bad = await triggers.CreateAsync(e.Id, /*lang=json,strict*/ """{"at":"2030-01-01T00:00:00Z"}""", "script", null, DateTimeOffset.UtcNow);
    var tool = new RunTriggerTool(db, Runner(db, new ThrowingHandler(new string('y', 20_000))));

    var result = await tool.RunTrigger(bad.Id.ToString());

    Assert.True(result.Length < 3000, $"response was {result.Length} chars; expected the error to be capped");
    Assert.Contains("[truncated]", result);
    var evt = Assert.Single(db.EntityEvents.Where(ev => ev.EntityId == e.Id));
    Assert.Equal("error", evt.Status);
    Assert.True(evt.Result!.Length < 2000, $"event result was {evt.Result.Length} chars; expected the error to be capped");
  }

  [Fact]
  public async Task Unknown_handler_kind_records_an_error_event_and_reports_it()
  {
    using var db = TestDb.New();
    var (e, trigger, _, _) = await SetupAsync(db);
    var tool = new RunTriggerTool(db, Runner(db));

    var result = await tool.RunTrigger(trigger.Id.ToString());

    Assert.Contains("No handler registered", result);
    var evt = Assert.Single(db.EntityEvents.Where(ev => ev.EntityId == e.Id));
    Assert.Equal("error", evt.Status);
  }

  [Fact]
  public async Task Denied_tick_lock_returns_busy_json_without_running_the_handler()
  {
    using var db = TestDb.New();
    var (e, trigger, _, notifier) = await SetupAsync(db);
    var tickLock = new CountingDeniedLock();
    var tool = new RunTriggerTool(db, Runner(db, new NotifyHandler(notifier)), tickLock);

    var result = await tool.RunTrigger(trigger.Id.ToString());

    Assert.Contains("busy", result);
    Assert.Equal(3, tickLock.Attempts);
    Assert.Empty(notifier.Sent);
    Assert.DoesNotContain(db.EntityEvents, ev => ev.EntityId == e.Id);
  }

  [Fact]
  public async Task Injected_tick_lock_is_acquired_for_the_claim()
  {
    using var db = TestDb.New();
    var (_, trigger, _, notifier) = await SetupAsync(db);
    var tickLock = new CountingGrantedLock();
    var tool = new RunTriggerTool(db, Runner(db, new NotifyHandler(notifier)), tickLock);

    var result = await tool.RunTrigger(trigger.Id.ToString());

    Assert.Equal(1, tickLock.Acquires);
    Assert.Single(notifier.Sent);
    Assert.Contains("\"status\"", result);
  }

  private sealed class ThrowingHandler(string message) : INativeHandler
  {
    public string Kind => "script";

    public Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
    {
      throw new InvalidOperationException(message);
    }
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

  private sealed class CountingGrantedLock : ITickLock
  {
    public int Acquires { get; private set; }

    public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
    {
      Acquires++;
      return Task.FromResult<IAsyncDisposable?>(new NoopLease());
    }

    private sealed class NoopLease : IAsyncDisposable
    {
      public ValueTask DisposeAsync()
      {
        return ValueTask.CompletedTask;
      }
    }
  }
}
