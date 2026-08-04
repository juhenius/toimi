# OccurrenceRunner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the occurrence-execution protocol (claim → handler resolution → dispatch → capped error capture → deleted-entity guard → finalize) into one `OccurrenceRunner` module shared by `SchedulerTick` and `RunTriggerTool`, closing run_trigger's two defects: uncapped exception text in jsonb and claiming outside the advisory tick lock.

**Architecture:** A new `OccurrenceRunner` (in `Scheduling/`) owns the whole protocol currently duplicated across `SchedulerTick.cs:54-102` and `RunTriggerTool.cs:44-72`, returning an `OccurrenceOutcome` (state + handler status + result JSON) that tells callers everything they need. `SchedulerTick` shrinks to lock-scan-run-advance; `RunTriggerTool` becomes a thin adapter that loads the trigger/entity, hands the injected `ITickLock` to the runner as a claim lock, and formats the outcome. The runner acquires the claim lock (with a short bounded retry) around **only** the `TryClaimAsync` call, releasing it before dispatch.

**Tech Stack:** .NET 10, xUnit, EF Core (InMemory in tests), Npgsql advisory locks

## Global Constraints

- dotnet is NOT on PATH: every command uses `export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"` first.
- Test command: `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj` (use `--filter` per task where possible).
- Before final commit of each task: `dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj` and the test csproj, then verify `--verify-no-changes` exits 0. Enforced as errors: IDE0005, IDE0022, IDE0046, whitespace.
- Commit style: `<type>(<scope>): <subject>` e.g. `refactor(tietue): ...`.
- 2-space indent, file-scoped namespaces; match surrounding code idiom; comments only for constraints code can't show.
- All 294 existing tietue tests must keep passing; do not weaken or delete existing assertions unless the plan task explicitly says why.

## Design Decisions

**Lock scope: claim only, not claim+run.** `Events/EntityEventStore.cs:85-91` documents that the stale-claim takeover (plain read-modify-write on the claim row) and the complete-check + insert sequence are safe only when claimants are serialized by the Postgres advisory tick lock. Those are the *only* non-atomic sections: once a fresh `started` claim row is committed, the unique `(entity, occurrence, kind)` index plus the 15-minute freshness window (`StaleClaimAfter`) protect the run — and a scheduler tick only ever claims occurrences equal to a trigger's `NextFireAt`, never a manual `UtcNow` occurrence, so no tick touches a manual run's claim row after commit. Holding the lock across the run would instead starve scheduled triggers for the duration of a possibly minutes-long `message` handler. Therefore the runner acquires the claim lock around `TryClaimAsync` alone and releases it before dispatch. If the lock stays denied after 3 attempts (default 500 ms apart, injectable for tests), the runner reports `Busy` and the tool returns an informative busy JSON instead of blocking.

**Unknown-kind alignment (deliberate behavior change in run_trigger only).** Today `RunTriggerTool` returns early on an unknown handler kind without touching the event log, while `SchedulerTick` records an error event. Unifying on the scheduler's policy means a manual run of an unknown kind now also records an error event; the tool still returns a human-readable "No handler registered..." message. No existing test asserts the old early-return, and the event row makes the failure visible in the entity's history — this is the divergence the refactor exists to remove.

**DI note.** `Program.cs` already relies on optional constructor parameters being satisfied from the container (`SchedulerTick`'s `ILogger`/`ITickLock`). `OccurrenceRunner` and the new `RunTriggerTool` signature use the same pattern: `ITickLock` is registered (scoped `PostgresTickLock`) so it is injected; `TimeSpan?` is not registered so `claimLockRetryDelay` stays at its production default.

---

## Task 1: `OccurrenceRunner` + `OccurrenceOutcome` with direct tests

**Files**
- Create: `src/toimi.tools.tietue/Scheduling/OccurrenceRunner.cs`
- Test (create): `src/toimi.tools.tietue.Tests/OccurrenceRunnerTests.cs`

**Interfaces**
- Consumes: `EntityEventStore.TryClaimAsync(Guid, DateTimeOffset, string, DateTimeOffset, CancellationToken)` / `.FinalizeAsync(Guid, DateTimeOffset, string, string, string?, CancellationToken)` (`Events/EntityEventStore.cs`), `HandlerRegistry.Resolve(string)`, `INativeHandler.HandleAsync(HandlerContext, CancellationToken)`, `ITickLock.TryAcquireAsync(CancellationToken)`, `TietueDbContext.Entities`.
- Produces:
  - `public enum OccurrenceState { Ran, Errored, AlreadyHandled, InProgress, UnknownKind, EntityDeleted, Busy }`
  - `public record OccurrenceOutcome(OccurrenceState State, string? Status = null, string? ResultJson = null)` with `public bool ShouldAdvance { get; }`
  - `public class OccurrenceRunner(TietueDbContext db, HandlerRegistry handlers, EntityEventStore events, ILogger<OccurrenceRunner>? logger = null, TimeSpan? claimLockRetryDelay = null)` exposing `public const int MaxErrorMessageChars = 1000;` and `public async Task<OccurrenceOutcome> RunAsync(Trigger trigger, Entity entity, DateTimeOffset occurrence, DateTimeOffset now, ITickLock? claimLock = null, CancellationToken ct = default)`

**Steps**

- [ ] Write the failing test file `src/toimi.tools.tietue.Tests/OccurrenceRunnerTests.cs`:

```csharp
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
```

- [ ] Run it and see it fail:
  `cd /Users/jari/private/toimi && export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH" && dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~OccurrenceRunnerTests"`
  Expected failure: build errors `CS0246: The type or namespace name 'OccurrenceRunner' could not be found` (and `OccurrenceState`, `OccurrenceOutcome`).

- [ ] Implement `src/toimi.tools.tietue/Scheduling/OccurrenceRunner.cs`. This is the protocol of `SchedulerTick.cs` lines 46–102 merged with `RunTriggerTool.cs` lines 44–70 (scheduler's capped-error variant wins), plus the claim-lock section per the Design Decisions:

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Handlers;

namespace toimi.tools.tietue.Scheduling;

public enum OccurrenceState
{
  Ran,            // handler ran and reported a status; event finalized
  Errored,        // handler threw; error event finalized with a capped message
  AlreadyHandled, // terminal or 'complete' event exists; handler not run
  InProgress,     // another instance holds a fresh claim; handler not run
  UnknownKind,    // no handler registered; error event finalized
  EntityDeleted,  // handler deleted the entity; claim row cascade-deleted, nothing finalized
  Busy,           // claim lock unavailable after bounded retries; nothing claimed
}

public record OccurrenceOutcome(OccurrenceState State, string? Status = null, string? ResultJson = null)
{
  /// <summary>
  /// True when the occurrence is settled and the trigger may advance. InProgress must stay
  /// due for retry; EntityDeleted's trigger was cascade-deleted; Busy never claimed at all.
  /// </summary>
  public bool ShouldAdvance => State is OccurrenceState.Ran or OccurrenceState.Errored
    or OccurrenceState.AlreadyHandled or OccurrenceState.UnknownKind;
}

/// <summary>
/// Owns the occurrence-execution protocol shared by the scheduler and run_trigger:
/// claim → resolve handler → dispatch → capped error capture → deleted-entity guard →
/// finalize. Callers decide only what to do with the outcome (advance the trigger,
/// format a tool response).
/// </summary>
public class OccurrenceRunner(TietueDbContext db, HandlerRegistry handlers, EntityEventStore events,
  ILogger<OccurrenceRunner>? logger = null, TimeSpan? claimLockRetryDelay = null)
{
  public const int MaxErrorMessageChars = 1000;
  private const int ClaimLockAttempts = 3;
  private const string NoHandlerResultJson = /*lang=json,strict*/ """{"error":"no handler registered"}""";

  private readonly ILogger<OccurrenceRunner> _logger = logger ?? NullLogger<OccurrenceRunner>.Instance;
  private readonly TimeSpan _claimLockRetryDelay = claimLockRetryDelay ?? TimeSpan.FromMilliseconds(500);

  public async Task<OccurrenceOutcome> RunAsync(Trigger trigger, Entity entity, DateTimeOffset occurrence, DateTimeOffset now, ITickLock? claimLock = null, CancellationToken ct = default)
  {
    var claim = await ClaimAsync(trigger, occurrence, now, claimLock, ct);
    if (claim is null)
    {
      return new OccurrenceOutcome(OccurrenceState.Busy);
    }

    if (claim == ClaimResult.InProgress)
    {
      return new OccurrenceOutcome(OccurrenceState.InProgress);
    }

    if (claim == ClaimResult.AlreadyHandled)
    {
      return new OccurrenceOutcome(OccurrenceState.AlreadyHandled);
    }

    var handler = handlers.Resolve(trigger.HandlerKind);
    if (handler is null)
    {
      _logger.LogWarning("No handler registered for kind {HandlerKind} (trigger {TriggerId}, entity {EntityId}); occurrence recorded as error.",
        trigger.HandlerKind, trigger.Id, trigger.EntityId);
      await events.FinalizeAsync(trigger.EntityId, occurrence, trigger.HandlerKind, "error", NoHandlerResultJson, ct);
      return new OccurrenceOutcome(OccurrenceState.UnknownKind, "error", NoHandlerResultJson);
    }

    var state = OccurrenceState.Ran;
    string status;
    string? resultJson;
    try
    {
      var result = await handler.HandleAsync(new HandlerContext(entity, trigger.HandlerConfig, occurrence), ct);
      status = result.Status;
      resultJson = result.Result;
    }
    catch (Exception ex)
    {
      state = OccurrenceState.Errored;
      status = "error";
      // Generic insurance: any handler's exception message lands in a jsonb column.
      var message = ex.Message.Length > MaxErrorMessageChars
        ? ex.Message[..MaxErrorMessageChars] + "… [truncated]"
        : ex.Message;
      resultJson = JsonSerializer.Serialize(new { error = message });
      _logger.LogError(ex, "Handler {HandlerKind} failed for trigger {TriggerId} (entity {EntityId}).",
        trigger.HandlerKind, trigger.Id, trigger.EntityId);
    }

    if (_logger.IsEnabled(LogLevel.Information))
    {
      _logger.LogInformation("Trigger {TriggerId} ({HandlerKind}) fired for entity {EntityId}: {Status}",
        trigger.Id, trigger.HandlerKind, trigger.EntityId, status);
    }

    // The handler may have deleted the entity (delete handler, or an agent run). The claim
    // row was cascade-deleted with it, so there is nothing to finalize — and the caller
    // must not touch the trigger either (cascade-deleted too).
    if (!await db.Entities.AnyAsync(e => e.Id == trigger.EntityId, ct))
    {
      return new OccurrenceOutcome(OccurrenceState.EntityDeleted, status, resultJson);
    }

    await events.FinalizeAsync(trigger.EntityId, occurrence, trigger.HandlerKind, status, resultJson, ct);
    return new OccurrenceOutcome(state, status, resultJson);
  }

  // Returns null when the claim lock stayed denied. The lock spans ONLY the claim: the
  // complete-check + insert and the stale-claim takeover in EntityEventStore.TryClaimAsync
  // are the protocol's only non-atomic sections (see the takeover comment there). Once a
  // fresh 'started' row is committed, the unique (entity, occurrence, kind) index and the
  // staleness window protect the run — and ticks only ever claim NextFireAt occurrences,
  // never a manual 'now' one — so holding the lock through a possibly minutes-long handler
  // would only starve the scheduler.
  private async Task<ClaimResult?> ClaimAsync(Trigger trigger, DateTimeOffset occurrence, DateTimeOffset now, ITickLock? claimLock, CancellationToken ct)
  {
    if (claimLock is null)
    {
      return await events.TryClaimAsync(trigger.EntityId, occurrence, trigger.HandlerKind, now, ct);
    }

    for (var attempt = 1; attempt <= ClaimLockAttempts; attempt++)
    {
      var lease = await claimLock.TryAcquireAsync(ct);
      if (lease is not null)
      {
        await using var _ = lease;
        return await events.TryClaimAsync(trigger.EntityId, occurrence, trigger.HandlerKind, now, ct);
      }

      if (attempt < ClaimLockAttempts)
      {
        await Task.Delay(_claimLockRetryDelay, ct);
      }
    }

    return null;
  }
}
```

- [ ] Run the new tests and see them pass:
  `cd /Users/jari/private/toimi && export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH" && dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~OccurrenceRunnerTests"`
  Expected: 8 passed.

- [ ] Run the full suite (nothing else touched, must stay green):
  `cd /Users/jari/private/toimi && export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH" && dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`
  Expected: 302 passed (294 existing + 8 new), 0 failed (Docker-gated tests may report skipped on docker-less machines).

- [ ] Format and verify:
  `cd /Users/jari/private/toimi && export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH" && dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj && dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj && dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes && dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes`
  Expected: both verifies exit 0.

- [ ] Commit:
  `cd /Users/jari/private/toimi && git add src/toimi.tools.tietue/Scheduling/OccurrenceRunner.cs src/toimi.tools.tietue.Tests/OccurrenceRunnerTests.cs && git commit -m "refactor(tietue): add OccurrenceRunner owning the occurrence-execution protocol" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"`

---

## Task 2: Rewire `SchedulerTick` onto the runner

**Files**
- Modify: `src/toimi.tools.tietue/Scheduling/SchedulerTick.cs` (whole class body; lines 46–102 of the current file are replaced by one runner call)
- Modify: `src/toimi.tools.tietue/Program.cs` (line 55 area: register `OccurrenceRunner`)
- Test (modify constructor call sites only, assertions unchanged):
  - `src/toimi.tools.tietue.Tests/SchedulerTickTests.cs` lines 24, 96, 123, 156, 182
  - `src/toimi.tools.tietue.Tests/SchedulerTickLockTests.cs` lines 51, 75, 89
  - `src/toimi.tools.tietue.Tests/ClaimThenRunTests.cs` line 26

**Interfaces**
- Consumes: `OccurrenceRunner.RunAsync(Trigger, Entity, DateTimeOffset occurrence, DateTimeOffset now, ITickLock?, CancellationToken)`, `OccurrenceOutcome.ShouldAdvance`, `Schedules.NextAfter(string, DateTimeOffset)`, `ITickLock.TryAcquireAsync`.
- Produces: `public class SchedulerTick(TietueDbContext db, OccurrenceRunner runner, ILogger<SchedulerTick>? logger = null, ITickLock? tickLock = null)` with unchanged `public async Task RunDueAsync(DateTimeOffset now, CancellationToken ct)`.

This is a behavior-preserving refactor: the existing `SchedulerTickTests`, `SchedulerTickLockTests`, and `ClaimThenRunTests` (all 5 + 3 + 5 scenarios: one-shot disable, recurrence advance, complete suppression, throwing-handler isolation, unknown-kind advance, cap, deleted-entity skip, lock semantics, claim states) are the red/green harness. The "failing" step is the compile break from the constructor change; green is those suites passing again unmodified except for constructor call sites.

**Steps**

- [ ] Replace `src/toimi.tools.tietue/Scheduling/SchedulerTick.cs` entirely with:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Scheduling;

public class SchedulerTick(TietueDbContext db, OccurrenceRunner runner, ILogger<SchedulerTick>? logger = null, ITickLock? tickLock = null)
{
  private readonly ILogger<SchedulerTick> _logger = logger ?? NullLogger<SchedulerTick>.Instance;

  public async Task RunDueAsync(DateTimeOffset now, CancellationToken ct)
  {
    IAsyncDisposable? lease = null;
    if (tickLock is not null)
    {
      lease = await tickLock.TryAcquireAsync(ct);
      if (lease is null)
      {
        _logger.LogDebug("Scheduler tick skipped: another instance holds the tick lock.");
        return;
      }
    }
    await using var _ = lease;

    var due = await db.Triggers
      .Where(t => t.Enabled && t.NextFireAt != null && t.NextFireAt <= now)
      .OrderBy(t => t.NextFireAt)
      .ToListAsync(ct);

    foreach (var trigger in due)
    {
      if (ct.IsCancellationRequested)
      {
        break;
      }

      var occurrence = trigger.NextFireAt!.Value;
      var entity = await db.Entities.FirstOrDefaultAsync(e => e.Id == trigger.EntityId, ct);

      if (entity is not null)
      {
        // Already inside the tick lock, so the runner claims without re-acquiring it.
        var outcome = await runner.RunAsync(trigger, entity, occurrence, now, claimLock: null, ct);
        if (!outcome.ShouldAdvance)
        {
          // InProgress: leave the trigger un-advanced so it stays due for retry.
          // EntityDeleted: the trigger was cascade-deleted with its entity — don't touch it.
          continue;
        }
      }

      trigger.LastFiredAt = occurrence;
      trigger.NextFireAt = Schedules.NextAfter(trigger.Schedule, occurrence);
      trigger.Enabled = trigger.NextFireAt is not null;
      trigger.UpdatedAt = now;
      await db.SaveChangesAsync(ct);
    }
  }
}
```

  Note what got preserved: an entity-less trigger (entity row already gone) still falls through and advances, exactly as the old lines 43–45/105–116 did; `MaxErrorMessageChars` and the handler/error/no-handler branches now live in `OccurrenceRunner` (moved there in Task 1).

- [ ] Run the scheduler suites and see them fail to compile:
  `cd /Users/jari/private/toimi && export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH" && dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~SchedulerTick|FullyQualifiedName~ClaimThenRun"`
  Expected failure: `CS1503`/`CS1729` at the nine `new SchedulerTick(db, registry, new EntityEventStore(db)...)` call sites (a `HandlerRegistry` is not an `OccurrenceRunner`).

- [ ] Update the nine call sites, constructor expression only — assertions must not change:
  - `SchedulerTickTests.cs` lines 24, 96, 123, 156, 182 and `ClaimThenRunTests.cs` line 26, each from
    `var tick = new SchedulerTick(db, registry, new EntityEventStore(db));` to
    `var tick = new SchedulerTick(db, new OccurrenceRunner(db, registry, new EntityEventStore(db)));`
  - `SchedulerTickLockTests.cs` lines 51, 75, 89, each from
    `var tick = new SchedulerTick(db, registry, new EntityEventStore(db), tickLock: <lock>);` to
    `var tick = new SchedulerTick(db, new OccurrenceRunner(db, registry, new EntityEventStore(db)), tickLock: <lock>);`
    (keeping each test's own `<lock>` argument: `new DeniedTickLock()`, `new GrantedTickLock(lease)`, `new GrantedTickLock(lease)`).

- [ ] Register the runner in `src/toimi.tools.tietue/Program.cs` — between the existing `ITickLock` and `SchedulerTick` registrations (lines 55–56):

```csharp
builder.Services.AddScoped<toimi.tools.tietue.Scheduling.ITickLock, toimi.tools.tietue.Scheduling.PostgresTickLock>();
builder.Services.AddScoped<toimi.tools.tietue.Scheduling.OccurrenceRunner>();
builder.Services.AddScoped<toimi.tools.tietue.Scheduling.SchedulerTick>();
```

- [ ] Run the scheduler suites green:
  `cd /Users/jari/private/toimi && export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH" && dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~SchedulerTick|FullyQualifiedName~ClaimThenRun|FullyQualifiedName~OccurrenceRunner"`
  Expected: all passed (5 SchedulerTickTests + 2 more in that class, 3 SchedulerTickLockTests, 5 ClaimThenRunTests, 8 OccurrenceRunnerTests).

- [ ] Run the full suite:
  `cd /Users/jari/private/toimi && export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH" && dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`
  Expected: 302 passed, 0 failed.

- [ ] Format and verify (same command as Task 1). Expected: both verifies exit 0.

- [ ] Commit:
  `cd /Users/jari/private/toimi && git add src/toimi.tools.tietue/Scheduling/SchedulerTick.cs src/toimi.tools.tietue/Program.cs src/toimi.tools.tietue.Tests/SchedulerTickTests.cs src/toimi.tools.tietue.Tests/SchedulerTickLockTests.cs src/toimi.tools.tietue.Tests/ClaimThenRunTests.cs && git commit -m "refactor(tietue): SchedulerTick delegates occurrence execution to OccurrenceRunner" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"`

---

## Task 3: Rewire `RunTriggerTool` — tick lock on the claim, capped errors

**Files**
- Modify: `src/toimi.tools.tietue/Tools/RunTriggerTool.cs` (whole class; current lines 34–72 replaced by a runner call + outcome formatting)
- Modify: `src/toimi.tools.tietue/Events/EntityEventStore.cs` (comment at lines 85–88 only — update the safety contract to name both claimants)
- Test (modify): `src/toimi.tools.tietue.Tests/RunTriggerToolTests.cs` (SetupAsync + line 92 call site + new tests), `src/toimi.tools.tietue.Tests/JobEndToEndTests.cs` line 51 (call site only)

**Interfaces**
- Consumes: `OccurrenceRunner.RunAsync(...)`, `OccurrenceState`, `ITickLock` (DI-injected `PostgresTickLock`).
- Produces: `public class RunTriggerTool(TietueDbContext db, OccurrenceRunner runner, ITickLock? tickLock = null)` with unchanged tool method signature `public async Task<string> RunTrigger(string triggerId)`.

**Steps**

- [ ] Update `src/toimi.tools.tietue.Tests/RunTriggerToolTests.cs`. Replace the `SetupAsync` helper (lines 15–27) and the `Handler_exception_is_reported_not_thrown` construction (line 92), and add the new tests and fakes. Resulting file content:

```csharp
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
```

  (Note: `ThrowingHandler` gains a `message` constructor parameter — the existing `Handler_exception_is_reported_not_thrown` test passes `"kaboom"` and keeps its assertion. The `Unknown_handler_kind...` test asserts the NEW behavior — an error event is now recorded, per the Design Decisions section. `Handler_error_text_is_capped...` is the defect-(a) regression test; `Denied_tick_lock...`/`Injected_tick_lock...` are the defect-(b) regression tests.)

- [ ] Update `src/toimi.tools.tietue.Tests/JobEndToEndTests.cs` line 51 (call site only, assertions unchanged) from
  `var tool = new RunTriggerTool(db, new HandlerRegistry([handler]), new EntityEventStore(db));` to
  `var tool = new RunTriggerTool(db, new OccurrenceRunner(db, new HandlerRegistry([handler]), new EntityEventStore(db)));`
  and add `using toimi.tools.tietue.Scheduling;` to that file if not already imported.

- [ ] Run and see it fail:
  `cd /Users/jari/private/toimi && export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH" && dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~RunTriggerToolTests|FullyQualifiedName~JobEndToEnd"`
  Expected failure: build errors `CS1503`/`CS1729` — `RunTriggerTool` still has the old `(TietueDbContext, HandlerRegistry, EntityEventStore)` constructor.

- [ ] Replace `src/toimi.tools.tietue/Tools/RunTriggerTool.cs` entirely with:

```csharp
using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class RunTriggerTool(TietueDbContext db, OccurrenceRunner runner, ITickLock? tickLock = null)
{
  [McpServerTool, Description("Fire a trigger immediately, out of schedule, and return the handler result synchronously — including script logs. Use this to test a job or script right after creating or editing it instead of waiting for the scheduler. Does not change the trigger's schedule or NextFireAt. Returns a busy response if a scheduler tick holds the run lock — retry shortly. Note: a message-kind trigger runs a full agent synchronously and may take minutes — do not call run_trigger from within an agent run that was itself started by run_trigger.")]
  public async Task<string> RunTrigger([Description("Trigger id (GUID)")] string triggerId)
  {
    if (!Guid.TryParse(triggerId, out var id))
    {
      return "Invalid triggerId. Expected a GUID.";
    }

    var trigger = await db.Triggers.FirstOrDefaultAsync(t => t.Id == id);
    if (trigger is null)
    {
      return $"No trigger found with id {id}.";
    }

    var entity = await db.Entities.FirstOrDefaultAsync(e => e.Id == trigger.EntityId);
    if (entity is null)
    {
      return $"Trigger's entity {trigger.EntityId} no longer exists.";
    }

    // Accepted race: a manual run may interleave with a scheduled run of the same
    // trigger — both snapshot Data and the last writer wins (single-user, accepted).
    // A fresh 'now' occurrence never collides with scheduled occurrences, so the
    // normal claim/finalize idempotency machinery applies cleanly to manual runs.
    // The tick lock is handed to the runner so the claim itself is serialized
    // against scheduler ticks (see OccurrenceRunner.ClaimAsync).
    var occurrence = DateTimeOffset.UtcNow;
    var outcome = await runner.RunAsync(trigger, entity, occurrence, occurrence, claimLock: tickLock);

    return outcome.State switch
    {
      OccurrenceState.Busy => /*lang=json,strict*/ """{"status":"busy","error":"a scheduler tick holds the run lock; try again shortly"}""",
      OccurrenceState.InProgress or OccurrenceState.AlreadyHandled => "Could not claim a run for this occurrence; try again.",
      OccurrenceState.UnknownKind => $"No handler registered for kind '{trigger.HandlerKind}'. Recorded an error event for this occurrence.",
      _ => JsonSerializer.Serialize(new { status = outcome.Status, result = outcome.ResultJson }),
    };
  }
}
```

  Preserved behavior: invalid-guid / missing-trigger / missing-entity messages verbatim; `Ran`, `Errored`, and `EntityDeleted` all return the same `{status, result}` JSON shape as before (the old code also returned it after a handler deleted the entity, merely skipping finalize — the runner does that skip now). Changed deliberately: error text is capped (defect a), the claim runs under the tick lock when one is injected (defect b), and unknown-kind records an error event (Design Decisions).

- [ ] Update the stale-takeover comment in `src/toimi.tools.tietue/Events/EntityEventStore.cs` (currently lines 85–88) to name both serialized claimants — replace:

```csharp
    // Abandoned claim (crashed instance): take it over and refresh the window.
    // Plain read-modify-write: safe only because ticks are serialized by the Postgres
    // advisory tick lock (+ Recreate deploys). The claim table alone is NOT race-proof
    // for stale take-overs — do not remove the tick lock believing it is.
```

  with:

```csharp
    // Abandoned claim (crashed instance): take it over and refresh the window.
    // Plain read-modify-write: safe only because every claimant serializes on the
    // Postgres advisory tick lock — SchedulerTick holds it for the whole tick and
    // run_trigger's OccurrenceRunner acquires it around this claim (+ Recreate
    // deploys). The claim table alone is NOT race-proof for stale take-overs — do
    // not remove the tick lock believing it is.
```

- [ ] Run the tool suites green:
  `cd /Users/jari/private/toimi && export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH" && dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~RunTriggerToolTests|FullyQualifiedName~JobEndToEnd"`
  Expected: 10 RunTriggerToolTests + JobEndToEndTests passed.

- [ ] Run the full suite:
  `cd /Users/jari/private/toimi && export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH" && dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`
  Expected: 306 passed (302 from Task 2 + 4 new tool tests), 0 failed.

- [ ] Format and verify (same command as Task 1). Expected: both verifies exit 0.

- [ ] Commit:
  `cd /Users/jari/private/toimi && git add src/toimi.tools.tietue/Tools/RunTriggerTool.cs src/toimi.tools.tietue/Events/EntityEventStore.cs src/toimi.tools.tietue.Tests/RunTriggerToolTests.cs src/toimi.tools.tietue.Tests/JobEndToEndTests.cs && git commit -m "fix(tietue): run_trigger claims under the tick lock and caps handler error text" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"`

---

## Task 4: Docs touch + final verification

**Files**
- Modify: `CLAUDE.md` (the "Triggers + scheduler" bullet under Key Patterns)
- Test: full suite (no code changes in this task)

**Interfaces**
- Consumes: none (documentation only).
- Produces: none.

**Steps**

- [ ] In `/Users/jari/private/toimi/CLAUDE.md`, replace the "Triggers + scheduler" Key Patterns bullet:

```markdown
- **Triggers + scheduler** — `TriggerWorker` (1-min loop) → `SchedulerTick`
  scans due triggers (`Enabled && NextFireAt <= now`), dispatches the handler,
  records an `EntityEvent`, and recomputes `NextFireAt` (RFC 5545 via `Ical.Net`)
  or disables one-shots. Firing is idempotent (unique `(entity,occurrence,kind)`);
  a `complete` event suppresses an occurrence; a throwing handler is isolated
  (recorded as `error`, trigger still advances).
```

  with:

```markdown
- **Triggers + scheduler** — `TriggerWorker` (1-min loop) → `SchedulerTick`
  scans due triggers (`Enabled && NextFireAt <= now`), runs each occurrence via
  `OccurrenceRunner` (claim → dispatch → capped error capture → finalize; the
  same module backs `run_trigger`), and recomputes `NextFireAt` (RFC 5545 via
  `Ical.Net`) or disables one-shots. Firing is idempotent (unique
  `(entity,occurrence,kind)`); a `complete` event suppresses an occurrence; a
  throwing handler is isolated (recorded as `error`, trigger still advances);
  manual `run_trigger` claims serialize against ticks on the advisory tick lock.
```

- [ ] Run the full suite one final time:
  `cd /Users/jari/private/toimi && export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH" && dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`
  Expected: 306 passed, 0 failed.

- [ ] Verify formatting is still clean:
  `cd /Users/jari/private/toimi && export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH" && dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes && dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes`
  Expected: both exit 0.

- [ ] Commit:
  `cd /Users/jari/private/toimi && git add CLAUDE.md && git commit -m "docs: note OccurrenceRunner in the triggers + scheduler pattern" -m "Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"`

---

## Behavior-preservation checklist (verified by existing tests, unmodified)

- Idempotent firing via unique `(entity, occurrence, kind)`: `ClaimCollisionTests`, `ClaimThenRunTests.Fresh_started_claim_...`, `EntityEventStoreTests`.
- `complete` events suppress an occurrence but the trigger advances: `SchedulerTickTests.Does_not_fire_a_completed_occurrence`, `ClaimThenRunTests.Complete_event_suppresses_handler_but_advances_trigger`.
- Throwing handler recorded as error, trigger still advances: `SchedulerTickTests.Failing_handler_is_isolated_and_trigger_advances`, `Handler_error_text_is_capped_before_it_reaches_the_event_log`.
- One-shot disable semantics: `SchedulerTickTests.Fires_due_one_shot_then_disables_it`, `Recurring_reschedules_next_fire`.
- Stale-claim takeover + crash-window semantics: `ClaimThenRunTests.Stale_started_claim_is_retaken_...`, `Terminal_event_from_before_crash_...`.
- Deleted-entity guard: `SchedulerTickTests.Entity_deleted_by_handler_does_not_throw_and_removes_entity`.
- Tick lock acquire/skip/release: `SchedulerTickLockTests` (all three), `PostgresTickLockTests` (Docker-gated).
- run_trigger surface (result shape, no schedule advance, event recorded, invalid inputs): `RunTriggerToolTests` originals, `JobEndToEndTests`.
