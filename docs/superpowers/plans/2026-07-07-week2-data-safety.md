# Week 2: Data Safety & Accounting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the approved design in `docs/superpowers/specs/2026-07-07-week2-data-safety-design.md`: claim-then-run scheduling, a Qdrant index outbox, real token accounting with an admin usage view, ContextManager fidelity, the Scriban upgrade, and local-PVC backups with restore verification.

**Architecture:** Six independent workstreams, ordered code-first so CI protects everything before infra lands. tietue gains an `index_outbox` table + worker and claim-then-run event semantics; toimi.core gains a `ContextBudget` anchor + its first test project; toimi.web captures real usage and grows a Usage admin page; ruutu bumps Scriban; `infrastructure/base/backup/` adds a PVC + two CronJobs plus a verify script and runbook.

**Tech Stack:** .NET 10, EF Core + Npgsql (migrations), Qdrant.Client, Microsoft.Extensions.AI (`UsageContent`/`UsageDetails`), xUnit + EF InMemory, React admin, Kubernetes CronJobs.

**Conventions for every task:** 2-space indent, file-scoped namespaces, block bodies (IDE0022 enforced as error — no expression-bodied methods). CA1873 (as error) may require `if (logger.IsEnabled(LogLevel.Information))` around `LogInformation` calls — apply only when the build demands. After each task run `dotnet format <changed csproj> --verify-no-changes` (fix + re-verify if dirty). dotnet lives at `/Users/jari/.local/share/mise/installs/dotnet/10.0.301/` if not on PATH. Work from repo root. Never commit files the task doesn't name.

---

## Task 1: Claim-then-run scheduling (tietue)

Today `SchedulerTick` runs the handler and only afterwards records the `EntityEvent`; a crash in between re-fires the occurrence. Change to: claim (insert `started` event) → run → finalize (update to terminal status). A fresh `started` row suppresses duplicates; a stale one (>15 min) is re-claimed.

**Files:**
- Modify: `src/toimi.tools.tietue/Events/EntityEventStore.cs`
- Modify: `src/toimi.tools.tietue/Scheduling/SchedulerTick.cs` (the handler-dispatch block)
- Test: `src/toimi.tools.tietue.Tests/ClaimThenRunTests.cs` (new)

- [ ] **Step 1: Write the failing tests**

Create `src/toimi.tools.tietue.Tests/ClaimThenRunTests.cs`. Mirror the setup helpers used by `SchedulerTickTests.cs` in the same directory (`TestDb`, `FakeNotifier`, `TypeRepository`, `EntityRepository`, `TriggerRepository`, `HandlerRegistry`); check that file first and match signatures exactly.

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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/toimi.tools.tietue.Tests --filter ClaimThenRunTests`
Expected: `Fresh_started_claim_...` and `Stale_started_claim_...` FAIL against current behavior (the first because today a pre-existing event advances the trigger AND is treated as handled with no distinction; the stale test because there's no re-claim). The other two may pass — that's fine; they pin behavior that must survive.

- [ ] **Step 3: Add claim/finalize to `EntityEventStore`**

In `src/toimi.tools.tietue/Events/EntityEventStore.cs`, keep all existing members and add:

```csharp
public enum ClaimResult
{
  Claimed,        // caller owns the occurrence and must run the handler + finalize
  InProgress,     // another instance holds a fresh claim — skip, do NOT advance the trigger
  AlreadyHandled  // terminal event or 'complete' exists — skip handler, advance the trigger
}
```

(top-level in the same file, inside the namespace) and these methods on `EntityEventStore`:

```csharp
  // How long a 'started' claim suppresses duplicates before being considered abandoned.
  public static readonly TimeSpan StaleClaimAfter = TimeSpan.FromMinutes(15);

  public async Task<ClaimResult> TryClaimAsync(Guid entityId, DateTimeOffset occurrenceUtc, string kind, DateTimeOffset now, CancellationToken ct = default)
  {
    if (await db.EntityEvents.AnyAsync(e => e.EntityId == entityId && e.OccurrenceUtc == occurrenceUtc && e.Kind == "complete", ct))
    {
      return ClaimResult.AlreadyHandled;
    }

    var existing = await db.EntityEvents
      .FirstOrDefaultAsync(e => e.EntityId == entityId && e.OccurrenceUtc == occurrenceUtc && e.Kind == kind, ct);

    if (existing is null)
    {
      db.EntityEvents.Add(new EntityEvent
      {
        Id = Guid.NewGuid(),
        EntityId = entityId,
        OccurrenceUtc = occurrenceUtc,
        Kind = kind,
        Status = "started",
        CreatedAt = now,
      });
      try
      {
        await db.SaveChangesAsync(ct);
        return ClaimResult.Claimed;
      }
      catch (DbUpdateException)
      {
        // Unique (entity, occurrence, kind) index: another instance claimed concurrently.
        db.ChangeTracker.Clear();
        return ClaimResult.InProgress;
      }
    }

    if (existing.Status != "started")
    {
      return ClaimResult.AlreadyHandled;
    }

    if (existing.CreatedAt > now - StaleClaimAfter)
    {
      return ClaimResult.InProgress;
    }

    // Abandoned claim (crashed instance): take it over and refresh the window.
    existing.CreatedAt = now;
    await db.SaveChangesAsync(ct);
    return ClaimResult.Claimed;
  }

  public async Task FinalizeAsync(Guid entityId, DateTimeOffset occurrenceUtc, string kind, string status, string? result, CancellationToken ct = default)
  {
    var evt = await db.EntityEvents
      .FirstOrDefaultAsync(e => e.EntityId == entityId && e.OccurrenceUtc == occurrenceUtc && e.Kind == kind, ct);
    if (evt is null)
    {
      return; // entity deleted during handling — the claim row was cascade-deleted
    }

    evt.Status = status;
    evt.Result = result;
    await db.SaveChangesAsync(ct);
  }
```

`OccurrenceHandledAsync` becomes unused by the tick after Step 4 — grep the repo; if nothing else references it, delete it (its behavior is subsumed by `TryClaimAsync`). Keep `RecordAsync`/`HasEventAsync`/`CompleteAsync` (used by `complete_occurrence` and tests).

- [ ] **Step 4: Rework the dispatch block in `SchedulerTick.RunDueAsync`**

Replace the body of the `foreach (var trigger in due)` loop's handling section. The current block (from `var deletedDuringHandling = false;` through the end of the `if (entity is not null && ...)` block) becomes:

```csharp
      var deletedDuringHandling = false;
      if (entity is not null)
      {
        var claim = await events.TryClaimAsync(trigger.EntityId, occurrence, trigger.HandlerKind, now, ct);
        if (claim == ClaimResult.InProgress)
        {
          // Another instance (or a crashed one, within the stale window) owns this
          // occurrence. Leave the trigger un-advanced so it stays due for retry.
          continue;
        }

        if (claim == ClaimResult.Claimed)
        {
          var handler = handlers.Resolve(trigger.HandlerKind);
          if (handler is not null)
          {
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
              status = "error";
              resultJson = System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message });
              _logger.LogError(ex, "Handler {HandlerKind} failed for trigger {TriggerId} (entity {EntityId}).",
                trigger.HandlerKind, trigger.Id, trigger.EntityId);
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
              _logger.LogInformation("Trigger {TriggerId} ({HandlerKind}) fired for entity {EntityId}: {Status}",
                trigger.Id, trigger.HandlerKind, trigger.EntityId, status);
            }

            // The handler may have deleted the entity (delete handler, or an agent run).
            // FinalizeAsync no-ops if the claim row was cascade-deleted with it; skip
            // advancing the trigger too — it was cascade-deleted as well.
            if (await db.Entities.AnyAsync(e => e.Id == trigger.EntityId, ct))
            {
              await events.FinalizeAsync(trigger.EntityId, occurrence, trigger.HandlerKind, status, resultJson, ct);
            }
            else
            {
              deletedDuringHandling = true;
            }
          }
          else
          {
            _logger.LogWarning("No handler registered for kind {HandlerKind} (trigger {TriggerId}, entity {EntityId}); trigger advances without firing.",
              trigger.HandlerKind, trigger.Id, trigger.EntityId);
            await events.FinalizeAsync(trigger.EntityId, occurrence, trigger.HandlerKind, "error", /*lang=json,strict*/ """{"error":"no handler registered"}""", ct);
          }
        }
        // ClaimResult.AlreadyHandled falls through: advance the trigger without firing.
      }
```

Add `using toimi.tools.tietue.Events;` if not already present (it is). Leave everything after this block (the `deletedDuringHandling` continue + trigger advancement) exactly as-is.

- [ ] **Step 5: Run the full tietue suite**

Run: `dotnet test src/toimi.tools.tietue.Tests`
Expected: all pass, including the pre-existing `SchedulerTickTests` (fires-once semantics are preserved: a terminal event → `AlreadyHandled`) and `SchedulerTickLockTests`. If an existing test asserted `OccurrenceHandledAsync` behavior directly, update it to use `TryClaimAsync` semantics instead — behavior pinned must stay equivalent.

- [ ] **Step 6: Format check and commit**

```bash
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
git add src/toimi.tools.tietue src/toimi.tools.tietue.Tests
git commit -m "feat(tietue): claim-then-run occurrence handling so crashes delay instead of duplicate"
```

---

## Task 2: Index outbox — table, enqueue, inline drain (tietue)

**Files:**
- Create: `src/toimi.tools.tietue/Data/IndexOutbox.cs`
- Create: `src/toimi.tools.tietue/Data/IndexOutboxConfiguration.cs`
- Modify: `src/toimi.tools.tietue/Data/TietueDbContext.cs` (add DbSet)
- Create: `src/toimi.tools.tietue/Semantic/SemanticOutbox.cs`
- Modify: `src/toimi.tools.tietue/Entities/EntityRepository.cs` (replace dispatcher calls)
- Modify: `src/toimi.tools.tietue/Behaviors/BehaviorDispatcher.cs` (remove save/delete hooks)
- Modify: `src/toimi.tools.tietue/Program.cs` (register SemanticOutbox)
- Create: EF migration `AddIndexOutbox`
- Test: `src/toimi.tools.tietue.Tests/SemanticOutboxTests.cs` (new)

- [ ] **Step 1: Write the failing tests**

First read `src/toimi.tools.tietue.Tests/` for how semantic-index behavior is currently tested (there is likely a fake `ISemanticIndex` — reuse it; if not, the fake below). Create `src/toimi.tools.tietue.Tests/SemanticOutboxTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Semantic;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SemanticOutboxTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"},"content":{"type":"string"}},"required":["name"]}""";
  private const string Behaviors = /*lang=json,strict*/ """{"semanticIndex":{"fields":["name","content"]}}""";

  private sealed class RecordingIndex : ISemanticIndex
  {
    public List<(string Collection, Guid Id, string Text)> Indexed { get; } = [];
    public List<(string Collection, Guid Id)> Removed { get; } = [];
    public bool Fail { get; set; }

    public Task EnsureCollectionAsync(string collection, CancellationToken ct = default)
    {
      return Task.CompletedTask;
    }

    public Task IndexAsync(string collection, Guid entityId, string text, CancellationToken ct = default)
    {
      if (Fail)
      {
        throw new InvalidOperationException("qdrant down");
      }
      Indexed.Add((collection, entityId, text));
      return Task.CompletedTask;
    }

    public Task RemoveAsync(string collection, Guid entityId, CancellationToken ct = default)
    {
      if (Fail)
      {
        throw new InvalidOperationException("qdrant down");
      }
      Removed.Add((collection, entityId));
      return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScoredId>> SearchAsync(string collection, string query, int limit, CancellationToken ct = default)
    {
      return Task.FromResult<IReadOnlyList<ScoredId>>([]);
    }
  }

  private static async Task<(Data.TietueDbContext db, RecordingIndex index, EntityRepository repo)> SetupAsync()
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("memory", Schema, Behaviors);
    var index = new RecordingIndex();
    var outbox = new SemanticOutbox(db, index);
    var repo = new EntityRepository(db, new SchemaValidator(), outbox);
    return (db, index, repo);
  }

  [Fact]
  public async Task Create_indexes_inline_and_leaves_no_outbox_row()
  {
    var (db, index, repo) = await SetupAsync();
    using var _ = db;

    var e = await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"n1","content":"hello"}"""), []);

    Assert.Single(index.Indexed);
    Assert.Equal(e.Id, index.Indexed[0].Id);
    Assert.Contains("hello", index.Indexed[0].Text);
    Assert.Empty(await db.IndexOutbox.ToListAsync());
  }

  [Fact]
  public async Task Failed_inline_index_leaves_retryable_outbox_row()
  {
    var (db, index, repo) = await SetupAsync();
    using var _ = db;
    index.Fail = true;

    var e = await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"n1"}"""), []);

    var row = await db.IndexOutbox.SingleAsync();
    Assert.Equal(e.Id, row.EntityId);
    Assert.Equal("upsert", row.Op);
    Assert.Equal(1, row.Attempts);
    Assert.Contains("qdrant down", row.LastError);
  }

  [Fact]
  public async Task Delete_enqueues_and_drains_a_delete_op()
  {
    var (db, index, repo) = await SetupAsync();
    using var _ = db;
    var e = await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"n1"}"""), []);

    await repo.DeleteAsync(e.Id);

    Assert.Single(index.Removed);
    Assert.Equal(e.Id, index.Removed[0].Id);
    Assert.Empty(await db.IndexOutbox.ToListAsync());
  }

  [Fact]
  public async Task Unindexed_type_enqueues_nothing()
  {
    var db = TestDb.New();
    using var _ = db;
    await new TypeRepository(db).DefineAsync("plain", Schema); // no behaviors
    var index = new RecordingIndex();
    var repo = new EntityRepository(db, new SchemaValidator(), new SemanticOutbox(db, index));

    await repo.CreateAsync("plain", JsonNode.Parse("""{"name":"n1"}"""), []);

    Assert.Empty(index.Indexed);
    Assert.Empty(await db.IndexOutbox.ToListAsync());
  }

  [Fact]
  public async Task Processing_upsert_for_deleted_entity_is_dropped_as_success()
  {
    var (db, index, _) = await SetupAsync();
    using var _2 = db;
    var outbox = new SemanticOutbox(db, index);
    var row = new Data.IndexOutbox { Id = Guid.NewGuid(), EntityId = Guid.NewGuid(), Type = "memory", Op = "upsert", CreatedAt = DateTimeOffset.UtcNow };

    await outbox.ProcessAsync(row); // must not throw

    Assert.Empty(index.Indexed);
  }
}
```

Note: `TypeRepository.DefineAsync` signature — check the real one (it accepted `(name, schema)` in older tests; behaviors may be a further parameter or a separate call). Adapt the setup to reality; the asserted behavior stays. Same for the exact `Behaviors` JSON shape — copy the shape used by `TypeSeeder`/existing semantic tests.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/toimi.tools.tietue.Tests --filter SemanticOutboxTests`
Expected: FAIL to compile (`SemanticOutbox`, `db.IndexOutbox` don't exist).

- [ ] **Step 3: Add the entity, configuration, and DbSet**

Create `src/toimi.tools.tietue/Data/IndexOutbox.cs`:

```csharp
namespace toimi.tools.tietue.Data;

public class IndexOutbox
{
  public Guid Id { get; set; }
  public Guid EntityId { get; set; } // intentionally no FK: delete ops must outlive the entity
  public required string Type { get; set; }
  public required string Op { get; set; } // "upsert" | "delete"
  public int Attempts { get; set; }
  public string? LastError { get; set; }
  public DateTimeOffset? LastAttemptAt { get; set; }
  public DateTimeOffset CreatedAt { get; set; }
}
```

Create `src/toimi.tools.tietue/Data/IndexOutboxConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.tietue.Data;

public class IndexOutboxConfiguration : IEntityTypeConfiguration<IndexOutbox>
{
  public void Configure(EntityTypeBuilder<IndexOutbox> builder)
  {
    builder.ToTable("index_outbox");
    builder.HasKey(o => o.Id);
    builder.Property(o => o.Type).IsRequired();
    builder.Property(o => o.Op).IsRequired();
    builder.HasIndex(o => o.CreatedAt);
  }
}
```

In `src/toimi.tools.tietue/Data/TietueDbContext.cs` add:

```csharp
  public DbSet<IndexOutbox> IndexOutbox => Set<IndexOutbox>();
```

- [ ] **Step 4: Create `SemanticOutbox`**

Create `src/toimi.tools.tietue/Semantic/SemanticOutbox.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Semantic;

/// <summary>
/// Durable Qdrant-indexing intent. Rows are enqueued in the same SaveChanges as the
/// entity mutation (atomic), drained inline on the happy path for freshness, and
/// retried by OutboxWorker on failure.
/// </summary>
public class SemanticOutbox(TietueDbContext db, ISemanticIndex index, ILogger<SemanticOutbox>? logger = null)
{
  public const int MaxAttempts = 8;

  /// <summary>Adds an outbox row to the current change set. Caller's SaveChanges commits it with the entity.</summary>
  public IndexOutbox? Enqueue(Entity entity, string? behaviorsJson, string op)
  {
    if (BehaviorSpec.SemanticIndexOf(behaviorsJson) is null)
    {
      return null;
    }

    var row = new IndexOutbox
    {
      Id = Guid.NewGuid(),
      EntityId = entity.Id,
      Type = entity.Type,
      Op = op,
      CreatedAt = DateTimeOffset.UtcNow,
    };
    db.IndexOutbox.Add(row);
    return row;
  }

  /// <summary>Post-commit fast path: process once; on failure leave the row for the worker.</summary>
  public async Task DrainAsync(IndexOutbox? row, CancellationToken ct = default)
  {
    if (row is null)
    {
      return;
    }

    try
    {
      await ProcessAsync(row, ct);
      db.IndexOutbox.Remove(row);
      await db.SaveChangesAsync(ct);
    }
    catch (Exception ex)
    {
      row.Attempts = 1;
      row.LastError = ex.Message;
      row.LastAttemptAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync(CancellationToken.None);
      logger?.LogWarning(ex, "Inline {Op} index for entity {EntityId} failed; queued for retry.", row.Op, row.EntityId);
    }
  }

  /// <summary>Idempotent op execution: upsert re-reads current entity state (newest wins); missing entity = success.</summary>
  public async Task ProcessAsync(IndexOutbox row, CancellationToken ct = default)
  {
    if (row.Op == "delete")
    {
      await index.RemoveAsync(row.Type, row.EntityId, ct);
      return;
    }

    var entity = await db.Entities.AsNoTracking().FirstOrDefaultAsync(e => e.Id == row.EntityId, ct);
    if (entity is null)
    {
      return; // deleted since enqueue; the delete op owns the vector removal
    }

    var typeDef = await db.TypeDefinitions.AsNoTracking().FirstOrDefaultAsync(t => t.Name == row.Type, ct);
    var cfg = BehaviorSpec.SemanticIndexOf(typeDef?.Behaviors);
    if (cfg is null)
    {
      return; // behavior removed since enqueue
    }

    await index.EnsureCollectionAsync(row.Type, ct);
    await index.IndexAsync(row.Type, row.EntityId, SemanticText.Extract(entity.Data, cfg.Fields), ct);
  }
}
```

(`TypeDefinition.Behaviors` — check the property's actual type; if it's a `string?` this compiles as-is, adapt if it's a JsonDocument.)

- [ ] **Step 5: Rewire `EntityRepository`**

In `src/toimi.tools.tietue/Entities/EntityRepository.cs`:

Constructor: replace the `BehaviorDispatcher? dispatcher = null` parameter with `SemanticOutbox? outbox = null` (add `using toimi.tools.tietue.Semantic;`).

`CreateAsync`: after `db.Entities.Add(entity);` and before `await EnforceUniqueOnCreateAsync(...)`, add:

```csharp
    var indexOp = outbox?.Enqueue(entity, typeDef.Behaviors, "upsert");
```

Replace the post-save `if (dispatcher is not null) { await dispatcher.OnEntitySavedAsync(entity, ct); }` block with:

```csharp
    if (outbox is not null)
    {
      await outbox.DrainAsync(indexOp, ct);
    }
```

(keep it in the same position relative to provisioner/expiry as the dispatcher call was).

`UpdateAsync`: inside the `if (data is not null)` block, after `behaviorsForExpiry = typeDef.Behaviors;`, add:

```csharp
      indexOp = outbox?.Enqueue(entity, typeDef.Behaviors, "upsert");
```

with `IndexOutbox? indexOp = null;` declared before the block (add `using toimi.tools.tietue.Data;` if missing). Replace the trailing dispatcher block with the same `DrainAsync` block as in Create. Tags-only updates enqueue nothing — tags are not embedded (`SemanticText` reads Data fields only).

`DeleteAsync`: before `db.Entities.Remove(entity);` add:

```csharp
    var typeDef = await db.TypeDefinitions.AsNoTracking().FirstOrDefaultAsync(t => t.Name == entity.Type, ct);
    var indexOp = outbox?.Enqueue(entity, typeDef?.Behaviors, "delete");
```

Replace the post-save dispatcher block with the `DrainAsync` block.

- [ ] **Step 6: Trim `BehaviorDispatcher` and update registration**

In `src/toimi.tools.tietue/Behaviors/BehaviorDispatcher.cs`: delete `OnEntitySavedAsync` and `OnEntityDeletedAsync` (search stays). First `grep -rn "OnEntitySavedAsync\|OnEntityDeletedAsync" src/` — if seeders or tools call them, route those callers through `SemanticOutbox.Enqueue` + `DrainAsync` instead (same pattern as the repository) and note it in your report.

In `src/toimi.tools.tietue/Program.cs`, next to the `BehaviorDispatcher` registration add:

```csharp
builder.Services.AddScoped<toimi.tools.tietue.Semantic.SemanticOutbox>();
```

- [ ] **Step 7: Generate the migration**

```bash
dotnet tool install --global dotnet-ef 2>/dev/null || true
dotnet ef migrations add AddIndexOutbox --project src/toimi.tools.tietue
```

Inspect the generated migration: it must create only the `index_outbox` table (+ index on `created_at`). If it contains unrelated changes, STOP and report — the model snapshot may have drifted.

- [ ] **Step 8: Run the full tietue suite**

Run: `dotnet test src/toimi.tools.tietue.Tests`
Expected: all pass. Existing tests that constructed `EntityRepository(db, validator, dispatcher, ...)` with a `BehaviorDispatcher` for indexing assertions must be updated to pass a `SemanticOutbox` instead; the semantic-search tests (via `BehaviorDispatcher.SearchAsync`) are unaffected.

- [ ] **Step 9: Format check and commit**

```bash
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
git add src/toimi.tools.tietue src/toimi.tools.tietue.Tests
git commit -m "feat(tietue): enqueue Qdrant index ops in an outbox committed atomically with the entity"
```

---

## Task 3: OutboxWorker (tietue)

**Files:**
- Create: `src/toimi.tools.tietue/Semantic/OutboxWorker.cs`
- Modify: `src/toimi.tools.tietue/Program.cs` (hosted service)
- Test: `src/toimi.tools.tietue.Tests/OutboxWorkerTests.cs` (new)

- [ ] **Step 1: Write the failing tests**

The worker loop itself is a thin `BackgroundService` shell (mirroring `TriggerWorker`); put the drainable logic in a testable `RunOnceAsync`. Create `src/toimi.tools.tietue.Tests/OutboxWorkerTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Semantic;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class OutboxWorkerTests
{
  private sealed class FailingIndex : ISemanticIndex
  {
    public int Calls { get; private set; }
    public bool Fail { get; set; } = true;

    public Task EnsureCollectionAsync(string collection, CancellationToken ct = default)
    {
      return Task.CompletedTask;
    }

    public Task IndexAsync(string collection, Guid entityId, string text, CancellationToken ct = default)
    {
      Calls++;
      return Fail ? throw new InvalidOperationException("down") : Task.CompletedTask;
    }

    public Task RemoveAsync(string collection, Guid entityId, CancellationToken ct = default)
    {
      Calls++;
      return Fail ? throw new InvalidOperationException("down") : Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScoredId>> SearchAsync(string collection, string query, int limit, CancellationToken ct = default)
    {
      return Task.FromResult<IReadOnlyList<ScoredId>>([]);
    }
  }

  private static IndexOutbox Row(int attempts, DateTimeOffset? lastAttempt, DateTimeOffset created)
  {
    return new IndexOutbox
    {
      Id = Guid.NewGuid(),
      EntityId = Guid.NewGuid(),
      Type = "memory",
      Op = "delete", // delete ops need no entity/typedef rows — simplest to drive the worker with
      Attempts = attempts,
      LastAttemptAt = lastAttempt,
      CreatedAt = created,
    };
  }

  [Fact]
  public async Task Retries_due_row_and_removes_on_success()
  {
    using var db = TestDb.New();
    var index = new FailingIndex { Fail = false };
    var now = DateTimeOffset.UtcNow;
    db.IndexOutbox.Add(Row(attempts: 1, lastAttempt: now.AddMinutes(-5), created: now.AddMinutes(-6)));
    await db.SaveChangesAsync();

    await OutboxWorker.RunOnceAsync(db, new SemanticOutbox(db, index), now, default);

    Assert.Equal(1, index.Calls);
    Assert.Empty(await db.IndexOutbox.ToListAsync());
  }

  [Fact]
  public async Task Backoff_skips_rows_attempted_too_recently()
  {
    using var db = TestDb.New();
    var index = new FailingIndex();
    var now = DateTimeOffset.UtcNow;
    // Attempts=3 → backoff 2^3 = 8 minutes; last attempt 1 minute ago → not due.
    db.IndexOutbox.Add(Row(attempts: 3, lastAttempt: now.AddMinutes(-1), created: now.AddHours(-1)));
    await db.SaveChangesAsync();

    await OutboxWorker.RunOnceAsync(db, new SemanticOutbox(db, index), now, default);

    Assert.Equal(0, index.Calls);
  }

  [Fact]
  public async Task Failure_increments_attempts_and_records_error()
  {
    using var db = TestDb.New();
    var index = new FailingIndex();
    var now = DateTimeOffset.UtcNow;
    db.IndexOutbox.Add(Row(attempts: 1, lastAttempt: now.AddMinutes(-5), created: now.AddMinutes(-6)));
    await db.SaveChangesAsync();

    await OutboxWorker.RunOnceAsync(db, new SemanticOutbox(db, index), now, default);

    var row = await db.IndexOutbox.SingleAsync();
    Assert.Equal(2, row.Attempts);
    Assert.Contains("down", row.LastError);
  }

  [Fact]
  public async Task Dead_rows_are_left_alone()
  {
    using var db = TestDb.New();
    var index = new FailingIndex();
    var now = DateTimeOffset.UtcNow;
    db.IndexOutbox.Add(Row(attempts: SemanticOutbox.MaxAttempts, lastAttempt: now.AddDays(-1), created: now.AddDays(-2)));
    await db.SaveChangesAsync();

    await OutboxWorker.RunOnceAsync(db, new SemanticOutbox(db, index), now, default);

    Assert.Equal(0, index.Calls);
    Assert.Equal(SemanticOutbox.MaxAttempts, (await db.IndexOutbox.SingleAsync()).Attempts);
  }

  [Fact]
  public async Task Undrained_fresh_row_waits_for_grace_period()
  {
    using var db = TestDb.New();
    var index = new FailingIndex { Fail = false };
    var now = DateTimeOffset.UtcNow;
    // Attempts=0 (inline drain never ran — e.g. pod died post-commit): picked up only after 2 min grace.
    db.IndexOutbox.Add(Row(attempts: 0, lastAttempt: null, created: now.AddSeconds(-30)));
    db.IndexOutbox.Add(Row(attempts: 0, lastAttempt: null, created: now.AddMinutes(-5)));
    await db.SaveChangesAsync();

    await OutboxWorker.RunOnceAsync(db, new SemanticOutbox(db, index), now, default);

    Assert.Equal(1, index.Calls); // only the 5-minute-old row
    Assert.Single(await db.IndexOutbox.ToListAsync());
  }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/toimi.tools.tietue.Tests --filter OutboxWorkerTests`
Expected: FAIL to compile (`OutboxWorker` doesn't exist).

- [ ] **Step 3: Implement `OutboxWorker`**

Create `src/toimi.tools.tietue/Semantic/OutboxWorker.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Semantic;

public class OutboxWorker(IServiceScopeFactory scopeFactory, ILogger<OutboxWorker> logger) : BackgroundService
{
  private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
  private static readonly TimeSpan UndrainedGrace = TimeSpan.FromMinutes(2);
  private const int BatchSize = 20;

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    logger.LogInformation("Tietue index outbox worker started.");
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TietueDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<SemanticOutbox>();
        await RunOnceAsync(db, outbox, DateTimeOffset.UtcNow, stoppingToken, logger);
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "Error in index outbox worker loop.");
      }

      await Task.Delay(Interval, stoppingToken);
    }
  }

  public static async Task<int> RunOnceAsync(
    TietueDbContext db, SemanticOutbox outbox, DateTimeOffset now,
    CancellationToken ct, ILogger? logger = null)
  {
    var candidates = await db.IndexOutbox
      .Where(o => o.Attempts < SemanticOutbox.MaxAttempts)
      .OrderBy(o => o.CreatedAt)
      .Take(BatchSize)
      .ToListAsync(ct);

    var processed = 0;
    foreach (var row in candidates.Where(r => IsDue(r, now)))
    {
      try
      {
        await outbox.ProcessAsync(row, ct);
        db.IndexOutbox.Remove(row);
        processed++;
      }
      catch (Exception ex)
      {
        row.Attempts++;
        row.LastError = ex.Message;
        row.LastAttemptAt = now;
        if (row.Attempts >= SemanticOutbox.MaxAttempts)
        {
          logger?.LogError(ex, "Index op {Op} for entity {EntityId} is dead after {Attempts} attempts.", row.Op, row.EntityId, row.Attempts);
        }
      }

      await db.SaveChangesAsync(ct);
    }

    return processed;
  }

  private static bool IsDue(IndexOutbox row, DateTimeOffset now)
  {
    if (row.Attempts == 0)
    {
      // Never drained inline (crash between commit and drain, or reconcile-enqueued):
      // give the inline path a grace window before the worker takes over.
      return row.CreatedAt + UndrainedGrace <= now;
    }

    return row.LastAttemptAt is null
      || row.LastAttemptAt + TimeSpan.FromMinutes(Math.Pow(2, row.Attempts)) <= now;
  }
}
```

- [ ] **Step 4: Register the worker**

In `src/toimi.tools.tietue/Program.cs`, next to the `TriggerWorker` registration:

```csharp
builder.Services.AddHostedService<toimi.tools.tietue.Semantic.OutboxWorker>();
```

- [ ] **Step 5: Run tests, format, commit**

```bash
dotnet test src/toimi.tools.tietue.Tests
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
git add src/toimi.tools.tietue src/toimi.tools.tietue.Tests
git commit -m "feat(tietue): retry failed index ops with a background outbox worker"
```

---

## Task 4: Reconcile endpoint + outbox admin (tietue)

**Files:**
- Modify: `src/toimi.tools.tietue/Semantic/ISemanticIndex.cs` (add `ListIdsAsync`)
- Modify: `src/toimi.tools.tietue/Semantic/QdrantSemanticIndex.cs` (implement)
- Modify: `src/toimi.tools.tietue/Admin/AdminEndpoints.cs` (outbox + reconcile endpoints)
- Test: `src/toimi.tools.tietue.Tests/ReconcileTests.cs` (new)

- [ ] **Step 1: Write the failing tests**

Reconcile logic goes in a testable static helper so the endpoint stays thin. Create `src/toimi.tools.tietue.Tests/ReconcileTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Semantic;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ReconcileTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""";
  private const string Behaviors = /*lang=json,strict*/ """{"semanticIndex":{"fields":["name"]}}""";

  private sealed class StubIndex : ISemanticIndex
  {
    public List<Guid> Ids { get; init; } = [];

    public Task EnsureCollectionAsync(string collection, CancellationToken ct = default)
    {
      return Task.CompletedTask;
    }

    public Task IndexAsync(string collection, Guid entityId, string text, CancellationToken ct = default)
    {
      return Task.CompletedTask;
    }

    public Task RemoveAsync(string collection, Guid entityId, CancellationToken ct = default)
    {
      return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScoredId>> SearchAsync(string collection, string query, int limit, CancellationToken ct = default)
    {
      return Task.FromResult<IReadOnlyList<ScoredId>>([]);
    }

    public Task<IReadOnlyList<Guid>> ListIdsAsync(string collection, CancellationToken ct = default)
    {
      return Task.FromResult<IReadOnlyList<Guid>>(Ids);
    }
  }

  [Fact]
  public async Task Enqueues_upserts_for_missing_and_deletes_for_orphans()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("memory", Schema, Behaviors);
    var repo = new EntityRepository(db, new SchemaValidator()); // no outbox: entities exist but were never indexed
    var e1 = await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"a"}"""), []);
    var e2 = await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"b"}"""), []);
    var orphan = Guid.NewGuid();
    var index = new StubIndex { Ids = [e2.Id, orphan] };

    var result = await SemanticReconciler.ReconcileAsync(db, index, "memory", default);

    Assert.Equal(1, result.MissingEnqueued);   // e1
    Assert.Equal(1, result.OrphansEnqueued);   // orphan
    var rows = await db.IndexOutbox.ToListAsync();
    Assert.Contains(rows, r => r.EntityId == e1.Id && r.Op == "upsert");
    Assert.Contains(rows, r => r.EntityId == orphan && r.Op == "delete");
    Assert.Equal(2, rows.Count);
  }
}
```

(Adapt `DefineAsync` signature as in Task 2.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/toimi.tools.tietue.Tests --filter ReconcileTests`
Expected: FAIL to compile (`ListIdsAsync`, `SemanticReconciler` don't exist).

- [ ] **Step 3: Extend `ISemanticIndex` and `QdrantSemanticIndex`**

Add to `src/toimi.tools.tietue/Semantic/ISemanticIndex.cs`:

```csharp
  // Returns every point id in the collection (for reconciliation). Empty if the collection doesn't exist.
  Task<IReadOnlyList<Guid>> ListIdsAsync(string collection, CancellationToken ct = default);
```

Implement in `src/toimi.tools.tietue/Semantic/QdrantSemanticIndex.cs`:

```csharp
  public async Task<IReadOnlyList<Guid>> ListIdsAsync(string collection, CancellationToken ct = default)
  {
    if (!await qdrant.CollectionExistsAsync(collection, ct))
    {
      return [];
    }

    var ids = new List<Guid>();
    PointId? offset = null;
    while (true)
    {
      var page = await qdrant.ScrollAsync(collection, limit: 256, offset: offset, cancellationToken: ct);
      ids.AddRange(page.Result.Select(p => Guid.Parse(p.Id.Uuid)));
      if (page.NextPageOffset is null || page.Result.Count == 0)
      {
        break;
      }

      offset = page.NextPageOffset;
    }

    return ids;
  }
```

The exact `ScrollAsync` signature/response shape depends on the `Qdrant.Client` version in the csproj — check it and adapt (the response carries a result list and a next-page offset; the loop shape stays). Any test fake of `ISemanticIndex` elsewhere in the suite now needs the new member — add a trivial `Task.FromResult<IReadOnlyList<Guid>>([])` implementation there.

- [ ] **Step 4: Create `SemanticReconciler` and the endpoints**

Create `src/toimi.tools.tietue/Semantic/SemanticReconciler.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Semantic;

public record ReconcileResult(int MissingEnqueued, int OrphansEnqueued);

public static class SemanticReconciler
{
  /// <summary>
  /// Diffs Postgres entities of a type against the Qdrant collection and enqueues
  /// outbox ops to repair the difference. Content mismatches are undetectable
  /// without stored hashes; this covers missing vectors and orphaned points.
  /// </summary>
  public static async Task<ReconcileResult> ReconcileAsync(TietueDbContext db, ISemanticIndex index, string type, CancellationToken ct)
  {
    var typeDef = await db.TypeDefinitions.AsNoTracking().FirstOrDefaultAsync(t => t.Name == type, ct);
    if (BehaviorSpec.SemanticIndexOf(typeDef?.Behaviors) is null)
    {
      throw new InvalidOperationException($"Type '{type}' is not semantically indexed.");
    }

    var dbIds = await db.Entities.Where(e => e.Type == type).Select(e => e.Id).ToListAsync(ct);
    var pointIds = await index.ListIdsAsync(type, ct);
    var now = DateTimeOffset.UtcNow;

    var missing = dbIds.Except(pointIds).ToList();
    var orphans = pointIds.Except(dbIds).ToList();

    foreach (var id in missing)
    {
      db.IndexOutbox.Add(new IndexOutbox { Id = Guid.NewGuid(), EntityId = id, Type = type, Op = "upsert", CreatedAt = now });
    }

    foreach (var id in orphans)
    {
      db.IndexOutbox.Add(new IndexOutbox { Id = Guid.NewGuid(), EntityId = id, Type = type, Op = "delete", CreatedAt = now });
    }

    await db.SaveChangesAsync(ct);
    return new ReconcileResult(missing.Count, orphans.Count);
  }
}
```

In `src/toimi.tools.tietue/Admin/AdminEndpoints.cs`, inside `MapAdminEndpoints` (follow the existing `admin.MapGet(...)` style):

```csharp
    admin.MapGet("/outbox", async (TietueDbContext db) =>
    {
      var rows = await db.IndexOutbox.OrderBy(o => o.CreatedAt).ToListAsync();
      return Results.Ok(new
      {
        pending = rows.Count(r => r.Attempts == 0),
        failing = rows.Count(r => r.Attempts > 0 && r.Attempts < Semantic.SemanticOutbox.MaxAttempts),
        dead = rows.Count(r => r.Attempts >= Semantic.SemanticOutbox.MaxAttempts),
        deadRows = rows.Where(r => r.Attempts >= Semantic.SemanticOutbox.MaxAttempts)
          .Select(r => new { r.Id, r.EntityId, r.Type, r.Op, r.Attempts, r.LastError, r.LastAttemptAt })
          .ToList(),
      });
    });

    admin.MapPost("/semantic/reconcile/{type}", async (TietueDbContext db, Semantic.ISemanticIndex index, string type) =>
    {
      try
      {
        var result = await SemanticReconciler.ReconcileAsync(db, index, type, CancellationToken.None);
        return Results.Ok(result);
      }
      catch (InvalidOperationException ex)
      {
        return Results.BadRequest(new { error = ex.Message });
      }
    });
```

(Adjust namespace qualifiers/usings to match the file's existing imports.)

- [ ] **Step 5: Run tests, format, commit**

```bash
dotnet test src/toimi.tools.tietue.Tests
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
git add src/toimi.tools.tietue src/toimi.tools.tietue.Tests
git commit -m "feat(tietue): admin outbox status and Qdrant reconcile endpoint"
```

---

## Task 5: Real token capture (core + tietue + web)

**Files:**
- Modify: `src/toimi.core/Configuration/ToimiOptions.cs` (ToimiConfiguration: prices + MaxContextTokens)
- Modify: `src/toimi.tools.tietue/Agents/IAgentRunner.cs` (usage on AgentRunResult)
- Modify: `src/toimi.tools.tietue/Agents/AgentRunner.cs` (populate from response.Usage)
- Modify: `src/toimi.tools.tietue/Handlers/MessageHandler.cs` (serialize usage)
- Modify: `src/toimi.web/Hubs/ToimiHub.cs` (capture UsageContent)
- Test: `src/toimi.tools.tietue.Tests/MessageHandlerTests.cs` (extend), `src/toimi.tools.tietue.Tests/FakeAgentRunner.cs` (extend)

- [ ] **Step 1: Extend `AgentRunResult` and the fake (failing test first)**

In `src/toimi.tools.tietue.Tests/MessageHandlerTests.cs`, add a test (adapt to the file's existing setup helpers — read it first):

```csharp
  [Fact]
  public async Task Serializes_usage_into_result_json()
  {
    var runner = new FakeAgentRunner { NextResult = new AgentRunResult(true, "done", null, null, PromptTokens: 1200, CompletionTokens: 340) };
    var handler = new MessageHandler(runner);
    var entity = new Data.Entity
    {
      Id = Guid.NewGuid(),
      Type = "schedule",
      Data = System.Text.Json.JsonDocument.Parse("{}"),
      Tags = [],
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow,
    };
    // NOTE: if MessageHandlerTests already has an entity-builder helper, use that instead —
    // match the file's existing style; the assertion below is what matters.

    var result = await handler.HandleAsync(new HandlerContext(entity, null, DateTimeOffset.UtcNow));

    Assert.Contains("\"promptTokens\":1200", result.Result);
    Assert.Contains("\"completionTokens\":340", result.Result);
  }
```

Run `dotnet test src/toimi.tools.tietue.Tests --filter MessageHandlerTests` — FAIL to compile (no such constructor parameters).

- [ ] **Step 2: Extend the result record and producers**

`src/toimi.tools.tietue/Agents/IAgentRunner.cs`:

```csharp
public record AgentRunResult(bool Success, string Response, string? ToolCallsJson, string? Error, int? PromptTokens = null, int? CompletionTokens = null);
```

`src/toimi.tools.tietue/Agents/AgentRunner.cs` — in the success path, after `var responseText = response.Text ?? "";`:

```csharp
      var promptTokens = (int?)response.Usage?.InputTokenCount;
      var completionTokens = (int?)response.Usage?.OutputTokenCount;
```

and return `new AgentRunResult(true, responseText, toolCallsJson, null, promptTokens, completionTokens);`. (`response.Usage` is `UsageDetails?` on `ChatResponse`; the token counts are `long?`.) Failure paths keep the defaults.

`src/toimi.tools.tietue/Handlers/MessageHandler.cs` — the result serialization becomes:

```csharp
    var result = JsonSerializer.Serialize(new
    {
      run.Response,
      run.Success,
      run.Error,
      promptTokens = run.PromptTokens,
      completionTokens = run.CompletionTokens,
    });
```

Update `FakeAgentRunner` (in tests) so callers can set a canned result including usage (give it a `NextResult` property if it doesn't have one; keep its existing behavior for other tests).

- [ ] **Step 3: Capture real usage in `ToimiHub`**

In `src/toimi.web/Hubs/ToimiHub.cs` `SendMessage`, inside the streaming loop's `foreach (var content in update.Contents)`, add a branch (with `UsageDetails? usage = null;` declared before the `await foreach`):

```csharp
          if (content is UsageContent usageContent)
          {
            usage = usageContent.Details;
          }
```

Then replace the estimate block:

```csharp
      // Estimate token usage (streaming doesn't provide exact counts)
      var estimatedPromptTokens = session.Messages.Sum(m => m.Text?.Length ?? 0) / 4;
      var estimatedCompletionTokens = responseText.Length / 4;
      var estimatedTotalTokens = estimatedPromptTokens + estimatedCompletionTokens;
```

with:

```csharp
      // Prefer real usage from the final streaming update; fall back to a rough estimate.
      var promptTokens = (int?)usage?.InputTokenCount ?? session.Messages.Sum(m => m.Text?.Length ?? 0) / 4;
      var completionTokens = (int?)usage?.OutputTokenCount ?? responseText.Length / 4;
      var totalTokens = (int?)usage?.TotalTokenCount ?? promptTokens + completionTokens;
```

and pass `promptTokens`/`completionTokens`/`totalTokens` to `AddMessageAsync`. (`UsageContent`/`UsageDetails` are in `Microsoft.Extensions.AI`, already imported.)

- [ ] **Step 4: Add config properties**

In `src/toimi.core/Configuration/ToimiOptions.cs`, add to `ToimiConfiguration`:

```csharp
  /// <summary>Context-window budget used by ContextManager before summarizing older messages.</summary>
  public int MaxContextTokens { get; set; } = 100_000;

  /// <summary>USD per 1M input tokens, for the admin usage view. Defaults track gpt-4o.</summary>
  public decimal TokenPriceInputPer1M { get; set; } = 2.50m;

  /// <summary>USD per 1M output tokens, for the admin usage view.</summary>
  public decimal TokenPriceOutputPer1M { get; set; } = 10.00m;
```

- [ ] **Step 5: Run all tests, format, commit**

```bash
dotnet build toimi.sln && dotnet test toimi.sln
dotnet format src/toimi.core/toimi.core.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
dotnet format src/toimi.web/toimi.web.csproj --verify-no-changes
git add src/toimi.core src/toimi.tools.tietue src/toimi.tools.tietue.Tests src/toimi.web
git commit -m "feat: record real token usage from LLM responses in web and agent runs"
```

---

## Task 6: Usage endpoints + admin Usage page (web + tietue)

**Files:**
- Modify: `src/toimi.web/Admin/AdminEndpoints.cs` (local `/api/admin/usage`)
- Modify: `src/toimi.tools.tietue/Admin/AdminEndpoints.cs` (`/admin/usage`)
- Create: `src/toimi.web/ClientApp/src/admin/UsagePage.tsx`
- Modify: `src/toimi.web/ClientApp/src/App.tsx` (route), `src/toimi.web/ClientApp/src/admin/AdminLayout.tsx` (nav link)
- Test: `src/toimi.web.Tests/UsageEndpointTests.cs` (new), extend `src/toimi.tools.tietue.Tests/AdminEndpointsTests.cs`

Both endpoints load the last 30 days of rows and aggregate in C# — provider-agnostic (works under EF InMemory in tests) and trivially fast at single-user volume. Do NOT use raw SQL or jsonb operators here.

- [ ] **Step 1: Write the failing web test**

Read `src/toimi.web.Tests/` for the existing test style (`AggregatorTests` / `InitialMessagesTests`), then create `src/toimi.web.Tests/UsageEndpointTests.cs`. Test the aggregation logic directly (extract it into a testable static, `UsageReport.Build`):

```csharp
using Toimi.Core.Data;
using Toimi.Web.Admin;
using Xunit;

namespace Toimi.Web.Tests;

public class UsageEndpointTests
{
  [Fact]
  public void Aggregates_by_day_and_prices_tokens()
  {
    var day1 = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
    var messages = new List<ConversationMessage>
    {
      new() { ConversationId = Guid.NewGuid(), Role = "assistant", Content = "a", CreatedAt = day1, PromptTokens = 1000, CompletionTokens = 500 },
      new() { ConversationId = Guid.NewGuid(), Role = "assistant", Content = "b", CreatedAt = day1.AddHours(2), PromptTokens = 2000, CompletionTokens = 1000 },
      new() { ConversationId = Guid.NewGuid(), Role = "assistant", Content = "c", CreatedAt = day1.AddDays(1), PromptTokens = 100, CompletionTokens = 50 },
    };

    var rows = UsageReport.Build(messages, inputPricePer1M: 2.50m, outputPricePer1M: 10.00m);

    Assert.Equal(2, rows.Count);
    var d1 = rows.Single(r => r.Date == new DateOnly(2026, 7, 1));
    Assert.Equal(3000, d1.PromptTokens);
    Assert.Equal(1500, d1.CompletionTokens);
    Assert.Equal(3000m / 1_000_000 * 2.50m + 1500m / 1_000_000 * 10.00m, d1.CostUsd);
  }
}
```

Check `ConversationMessage`'s required members (`Role`, `Content` are required; `CreatedAt` may be set by default — adapt the object initializers to compile). Run: FAIL (no `UsageReport`).

- [ ] **Step 2: Implement `UsageReport` + the web endpoint**

In `src/toimi.web/Admin/AdminEndpoints.cs` (same file, new types at the bottom):

```csharp
public record UsageRow(DateOnly Date, long PromptTokens, long CompletionTokens, decimal CostUsd);

public static class UsageReport
{
  public static List<UsageRow> Build(IEnumerable<Toimi.Core.Data.ConversationMessage> messages, decimal inputPricePer1M, decimal outputPricePer1M)
  {
    return [.. messages
      .GroupBy(m => DateOnly.FromDateTime(m.CreatedAt.UtcDateTime))
      .Select(g =>
      {
        long prompt = g.Sum(m => (long)(m.PromptTokens ?? 0));
        long completion = g.Sum(m => (long)(m.CompletionTokens ?? 0));
        var cost = prompt / 1_000_000m * inputPricePer1M + completion / 1_000_000m * outputPricePer1M;
        return new UsageRow(g.Key, prompt, completion, cost);
      })
      .OrderBy(r => r.Date)];
  }
}
```

And in `MapAdminEndpoints`, before the catch-all forward route:

```csharp
    app.MapGet("/api/admin/usage", async (Toimi.Core.Data.ToimiDbContext db, Toimi.Core.Configuration.ToimiConfiguration config) =>
    {
      var since = DateTimeOffset.UtcNow.AddDays(-30);
      var messages = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
        db.ConversationMessages.Where(m => m.CreatedAt >= since));
      return Results.Ok(UsageReport.Build(messages, config.TokenPriceInputPer1M, config.TokenPriceOutputPer1M));
    });
```

(Use normal `using` imports instead of fully-qualified names — shown qualified here only for unambiguity. Route note: the literal `/api/admin/usage` outranks the `/api/admin/{tool}/{**path}` template in ASP.NET routing, same as the existing `/api/admin/summary`.)

- [ ] **Step 3: tietue agent-usage endpoint (failing test first)**

Extend `src/toimi.tools.tietue.Tests/AdminEndpointsTests.cs` (read it first; follow its harness) with a test seeding `EntityEvent` rows of kind `message` whose `Result` JSON includes `promptTokens`/`completionTokens`, calling `/admin/usage`, and asserting the daily sums. Then in `src/toimi.tools.tietue/Admin/AdminEndpoints.cs` add:

```csharp
    admin.MapGet("/usage", async (TietueDbContext db) =>
    {
      var since = DateTimeOffset.UtcNow.AddDays(-30);
      var events = await db.EntityEvents
        .Where(e => e.Kind == "message" && e.CreatedAt >= since && e.Result != null)
        .Select(e => new { e.CreatedAt, e.Result })
        .ToListAsync();

      var rows = events
        .Select(e =>
        {
          using var doc = System.Text.Json.JsonDocument.Parse(e.Result!);
          var prompt = doc.RootElement.TryGetProperty("promptTokens", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.Number ? p.GetInt64() : 0L;
          var completion = doc.RootElement.TryGetProperty("completionTokens", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number ? c.GetInt64() : 0L;
          return (Date: DateOnly.FromDateTime(e.CreatedAt.UtcDateTime), Prompt: prompt, Completion: completion);
        })
        .GroupBy(r => r.Date)
        .Select(g => new { date = g.Key, promptTokens = g.Sum(r => r.Prompt), completionTokens = g.Sum(r => r.Completion) })
        .OrderBy(r => r.date)
        .ToList();

      return Results.Ok(rows);
    });
```

- [ ] **Step 4: React Usage page**

Create `src/toimi.web/ClientApp/src/admin/UsagePage.tsx` (follow the visual/style conventions of `DashboardPage.tsx` — read it first and reuse its table/card classes):

```tsx
import { useEffect, useState } from 'react'

interface WebUsageRow { date: string; promptTokens: number; completionTokens: number; costUsd: number }
interface AgentUsageRow { date: string; promptTokens: number; completionTokens: number }

export default function UsagePage() {
  const [web, setWeb] = useState<WebUsageRow[] | null>(null)
  const [agent, setAgent] = useState<AgentUsageRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    void (async () => {
      try {
        const [webResp, agentResp] = await Promise.all([
          fetch('/api/admin/usage'),
          fetch('/api/admin/tietue/usage'),
        ])
        if (cancelled) return
        if (webResp.ok) setWeb(await webResp.json() as WebUsageRow[])
        if (agentResp.ok) setAgent(await agentResp.json() as AgentUsageRow[])
        if (!webResp.ok && !agentResp.ok) setError('Failed to load usage data')
      } catch {
        if (!cancelled) setError('Failed to load usage data')
      }
    })()
    return () => { cancelled = true }
  }, [])

  const days = [...new Set([...(web ?? []).map(r => r.date), ...(agent ?? []).map(r => r.date)])].sort().reverse()
  const webBy = new Map((web ?? []).map(r => [r.date, r]))
  const agentBy = new Map((agent ?? []).map(r => [r.date, r]))

  return (
    <div>
      <h1>Usage (last 30 days)</h1>
      {error && <p>{error}</p>}
      <table>
        <thead>
          <tr><th>Day</th><th>Web tokens (in/out)</th><th>Agent tokens (in/out)</th><th>Est. cost (web)</th></tr>
        </thead>
        <tbody>
          {days.map(d => {
            const w = webBy.get(d); const a = agentBy.get(d)
            return (
              <tr key={d}>
                <td>{d}</td>
                <td>{w ? `${w.promptTokens.toLocaleString()} / ${w.completionTokens.toLocaleString()}` : '—'}</td>
                <td>{a ? `${a.promptTokens.toLocaleString()} / ${a.completionTokens.toLocaleString()}` : '—'}</td>
                <td>{w ? `$${w.costUsd.toFixed(2)}` : '—'}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
```

Adapt markup/classNames to the existing admin pages' Tailwind styling so it doesn't look alien; JSON property casing: the web endpoint returns camelCase via ASP.NET defaults — verify against the actual response shape (`date` serializes `DateOnly` as `"2026-07-01"`).

Wire the route in `src/toimi.web/ClientApp/src/App.tsx` under the `/admin` layout:

```tsx
<Route path="usage" element={<UsagePage />} />
```

and add a nav link in `AdminLayout.tsx` following its existing nav-item pattern.

- [ ] **Step 5: Verify, format, commit**

```bash
dotnet test toimi.sln
cd src/toimi.web/ClientApp && npm run lint && npm run build && cd -
dotnet format src/toimi.web/toimi.web.csproj --verify-no-changes
dotnet format src/toimi.web.Tests/toimi.web.Tests.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
git add src/toimi.web src/toimi.web.Tests src/toimi.tools.tietue src/toimi.tools.tietue.Tests
git commit -m "feat(admin): usage dashboard with real token counts and estimated cost"
```

---

## Task 7: ContextManager fidelity + toimi.core.Tests

**Files:**
- Create: `src/toimi.core.Tests/toimi.core.Tests.csproj`, `src/toimi.core.Tests/ContextManagerTests.cs`, `src/toimi.core.Tests/FakeChatClient.cs`
- Create: `src/toimi.core/ContextBudget.cs`
- Modify: `src/toimi.core/ContextManager.cs`
- Modify: `src/toimi.web/Hubs/ToimiHub.cs` + the `ToimiSession` type (find it near the hub) — budget wiring
- Modify: `src/toimi.tools.tietue/Agents/AgentRunner.cs` — budget wiring
- Modify: `toimi.sln`

- [ ] **Step 1: Create the test project**

Create `src/toimi.core.Tests/toimi.core.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../toimi.core/toimi.core.csproj" />
  </ItemGroup>

</Project>
```

```bash
dotnet sln toimi.sln add src/toimi.core.Tests/toimi.core.Tests.csproj
```

Create `src/toimi.core.Tests/FakeChatClient.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Toimi.Core.Tests;

public sealed class FakeChatClient : IChatClient
{
  public List<IEnumerable<ChatMessage>> Requests { get; } = [];
  public string NextResponseText { get; set; } = "summary text";

  public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
  {
    Requests.Add(messages.ToList());
    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, NextResponseText)));
  }

  public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
  {
    throw new NotSupportedException();
  }

  public object? GetService(Type serviceType, object? serviceKey = null)
  {
    return null;
  }

  public void Dispose()
  {
  }
}
```

(Adapt to the exact `IChatClient` members of the referenced Microsoft.Extensions.AI version — the compiler will tell you.)

- [ ] **Step 2: Write the failing tests**

Create `src/toimi.core.Tests/ContextManagerTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Xunit;

namespace Toimi.Core.Tests;

public class ContextManagerTests
{
  private static ChatMessage Text(ChatRole role, int chars)
  {
    return new ChatMessage(role, new string('x', chars));
  }

  [Fact]
  public void Estimate_without_anchor_falls_back_to_chars_over_4()
  {
    var budget = new ContextBudget();
    var messages = new List<ChatMessage> { Text(ChatRole.User, 4000) };

    Assert.Equal(1000, budget.Estimate(messages));
  }

  [Fact]
  public void Estimate_with_anchor_uses_real_tokens_plus_conservative_delta()
  {
    var budget = new ContextBudget();
    var messages = new List<ChatMessage> { Text(ChatRole.User, 4000) };
    budget.RecordUsage(2500, messages); // reality: denser than 4 chars/token

    messages.Add(Text(ChatRole.Assistant, 300));

    Assert.Equal(2500 + 300 / 3, budget.Estimate(messages));
  }

  [Fact]
  public async Task Compaction_includes_tool_calls_and_results_in_summary_input()
  {
    var client = new FakeChatClient();
    var messages = new List<ChatMessage> { new(ChatRole.System, "sys") };
    for (var i = 0; i < 20; i++)
    {
      messages.Add(Text(ChatRole.User, 10));
      var withTool = new ChatMessage(ChatRole.Assistant, [
        new FunctionCallContent($"call{i}", "search", new Dictionary<string, object?> { ["query"] = "milk" }),
      ]);
      messages.Add(withTool);
      messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call{i}", "found 3 items")]));
    }

    var compacted = await ContextManager.CompactIfNeeded(messages, client, budget: null, maxTokens: 1, ct: default);

    Assert.True(compacted);
    var summaryRequest = Assert.Single(client.Requests);
    var payload = string.Join("\n", summaryRequest.Select(m => m.Text));
    Assert.Contains("search", payload);       // tool call name present
    Assert.Contains("found 3 items", payload); // tool result present
  }

  [Fact]
  public async Task Compaction_resets_the_budget_anchor()
  {
    var client = new FakeChatClient();
    var budget = new ContextBudget();
    var messages = new List<ChatMessage>();
    for (var i = 0; i < 30; i++)
    {
      messages.Add(Text(ChatRole.User, 100));
    }
    budget.RecordUsage(999_999, messages); // absurd anchor forces compaction and must then be discarded

    var compacted = await ContextManager.CompactIfNeeded(messages, client, budget, maxTokens: 100_000, ct: default);

    Assert.True(compacted);
    // Anchor gone: estimate is chars/4 of the compacted list, far below the old anchor.
    Assert.True(budget.Estimate(messages) < 999_999);
  }

  [Fact]
  public async Task No_compaction_below_the_limit()
  {
    var client = new FakeChatClient();
    var messages = new List<ChatMessage> { Text(ChatRole.User, 100) };

    var compacted = await ContextManager.CompactIfNeeded(messages, client, budget: null, maxTokens: 100_000, ct: default);

    Assert.False(compacted);
    Assert.Empty(client.Requests);
  }
}
```

(Check `FunctionCallContent`/`FunctionResultContent` constructor signatures in the installed Microsoft.Extensions.AI and adapt.) Run: FAIL to compile (`ContextBudget`, new `CompactIfNeeded` signature).

- [ ] **Step 3: Implement `ContextBudget`**

Create `src/toimi.core/ContextBudget.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Toimi.Core;

/// <summary>
/// Per-session/run token estimator anchored to real usage. After each LLM call the
/// host records the actual prompt-token count; estimates become
/// anchor + charsAddedSince/3 (conservative) instead of blind chars/4.
/// </summary>
public class ContextBudget
{
  private int? _anchorPromptTokens;
  private int _charsAtAnchor;

  public void RecordUsage(int promptTokens, List<ChatMessage> messages)
  {
    _anchorPromptTokens = promptTokens;
    _charsAtAnchor = TotalChars(messages);
  }

  public int Estimate(List<ChatMessage> messages)
  {
    var chars = TotalChars(messages);
    if (_anchorPromptTokens is null)
    {
      return chars / 4;
    }

    var delta = Math.Max(0, chars - _charsAtAnchor);
    return _anchorPromptTokens.Value + delta / 3;
  }

  /// <summary>Call after compaction: the message list changed shape, the anchor is invalid.</summary>
  public void Reset()
  {
    _anchorPromptTokens = null;
    _charsAtAnchor = 0;
  }

  internal static int TotalChars(List<ChatMessage> messages)
  {
    return messages.Sum(m => m.Text?.Length ?? 0);
  }
}
```

- [ ] **Step 4: Rework `ContextManager`**

Replace `src/toimi.core/ContextManager.cs` with:

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Toimi.Core;

public static class ContextManager
{
  private const int RecentMessagesToKeep = 10;
  private const int MaxToolResultCharsInSummary = 500;
  private const int MaxSummaryInputChars = 300_000;

  public static async Task<bool> CompactIfNeeded(
    List<ChatMessage> messages,
    IChatClient client,
    ContextBudget? budget = null,
    int maxTokens = 100_000,
    CancellationToken ct = default)
  {
    var estimated = budget?.Estimate(messages) ?? messages.Sum(m => (m.Text?.Length ?? 0) / 4);
    if (estimated < maxTokens)
    {
      return false;
    }

    // Count system messages at the start (keep them all)
    var systemCount = 0;
    for (var i = 0; i < messages.Count; i++)
    {
      if (messages[i].Role == ChatRole.System)
      {
        systemCount++;
      }
      else
      {
        break;
      }
    }

    var nonSystemCount = messages.Count - systemCount;
    if (nonSystemCount <= RecentMessagesToKeep)
    {
      return false;
    }

    var summarizeCount = nonSystemCount - RecentMessagesToKeep;
    if (summarizeCount < 2)
    {
      return false;
    }

    var toSummarize = messages.GetRange(systemCount, summarizeCount);
    var conversationText = string.Join("\n\n", toSummarize.Select(MessageAsText));
    if (conversationText.Length > MaxSummaryInputChars)
    {
      conversationText = conversationText[..MaxSummaryInputChars] + "\n\n[remainder truncated]";
    }

    var summaryMessages = new List<ChatMessage>
    {
      new(ChatRole.System, "Summarize the following conversation concisely. Preserve key facts, decisions, user preferences, action items, and the outcomes of tool calls. Be brief but complete."),
      new(ChatRole.User, conversationText)
    };

    var response = await client.GetResponseAsync(summaryMessages, cancellationToken: ct);
    var summary = response.Text ?? "Earlier conversation summary unavailable.";

    messages.RemoveRange(systemCount, summarizeCount);
    messages.Insert(systemCount, new(ChatRole.System, $"Summary of earlier conversation:\n{summary}"));
    budget?.Reset();

    return true;
  }

  private static string MessageAsText(ChatMessage m)
  {
    var parts = new List<string>();
    foreach (var content in m.Contents)
    {
      switch (content)
      {
        case TextContent t when !string.IsNullOrEmpty(t.Text):
          parts.Add(t.Text);
          break;
        case FunctionCallContent fc:
          parts.Add($"[tool call: {fc.Name}({JsonSerializer.Serialize(fc.Arguments)})]");
          break;
        case FunctionResultContent fr:
          var result = fr.Result?.ToString() ?? "";
          if (result.Length > MaxToolResultCharsInSummary)
          {
            result = result[..MaxToolResultCharsInSummary] + "…";
          }
          parts.Add($"[tool result: {result}]");
          break;
      }
    }

    return $"{m.Role}: {string.Join("\n", parts)}";
  }
}
```

Note the old public members `EstimateTokens`/const `MaxEstimatedTokens` are gone — grep for callers (`ToimiHub`, `AgentRunner` call only `CompactIfNeeded`; fix any others).

- [ ] **Step 5: Wire the budget into both hosts**

`ToimiSession` (find its definition; it's constructed in `ToimiHub.OnConnectedAsync`): add a `ContextBudget Budget` member initialized to `new()`.

`src/toimi.web/Hubs/ToimiHub.cs`:
- The compaction call becomes:
  `await ContextManager.CompactIfNeeded(session.Messages, session.ChatClient, session.Budget, _config.MaxContextTokens, Context.ConnectionAborted);`
- In `SendMessage`, after real usage is captured (Task 5's `usage` variable), anchor the budget:

```csharp
      if (usage?.InputTokenCount is not null)
      {
        session.Budget.RecordUsage((int)usage.InputTokenCount.Value, session.Messages);
      }
```

(place it after `session.Messages.Add(new(ChatRole.Assistant, responseText));` so the anchor covers the list as sent plus the response).

`src/toimi.tools.tietue/Agents/AgentRunner.cs`: the compaction call becomes:

```csharp
      await ContextManager.CompactIfNeeded(messages, client, budget: null, maxTokens: config.MaxContextTokens, ct: token);
```

(a fresh single-call run has no anchor to exploit; passing null keeps the chars/4 fallback).

- [ ] **Step 6: Run everything, format, commit**

```bash
dotnet build toimi.sln && dotnet test toimi.sln
dotnet format src/toimi.core/toimi.core.csproj --verify-no-changes
dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj --verify-no-changes
dotnet format src/toimi.web/toimi.web.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
git add toimi.sln src/toimi.core src/toimi.core.Tests src/toimi.web src/toimi.tools.tietue
git commit -m "feat(core): anchor context estimates to real usage and keep tool content in summaries"
```

---

## Task 8: Scriban upgrade (ruutu)

**Files:**
- Modify: `src/toimi.tools.ruutu/toimi.tools.ruutu.csproj:18`

- [ ] **Step 1: Bump the package**

Change `<PackageReference Include="Scriban" Version="5.12.1" />` to `Version="7.2.5"`.

- [ ] **Step 2: Build and test**

```bash
dotnet build src/toimi.tools.ruutu/toimi.tools.ruutu.csproj
dotnet test src/toimi.tools.ruutu.Tests
```

Expected: build clean (fix compile breaks in `ScribanRenderer.cs` if the 6.x/7.x API moved things — the used surface is `Template.Parse`, `template.HasErrors/Messages`, `template.Render(TemplateContext)`, `ScriptObject`, `TemplateContext.PushGlobal`, `scriptObj.Import(name, delegate)`, all stable across 6/7 per release notes, but verify). All 91 ruutu tests green — they render every seed template in both tiers and pin `SafeUrl`.

**If any test fails with changed rendering OUTPUT (not a compile error): STOP and report BLOCKED with the diff — template-visible behavior changes must be surfaced, not papered over.**

- [ ] **Step 3: Confirm the advisories are gone**

```bash
dotnet list src/toimi.tools.ruutu/toimi.tools.ruutu.csproj package --vulnerable
```

Expected: no Scriban entries.

- [ ] **Step 4: Format, commit**

```bash
dotnet format src/toimi.tools.ruutu/toimi.tools.ruutu.csproj --verify-no-changes
git add src/toimi.tools.ruutu
git commit -m "chore(ruutu): upgrade Scriban to 7.2.5 to clear security advisories"
```

(If code changes were needed in ScribanRenderer, include them and mention in the commit body.)

---

## Task 9: Backup infrastructure (PVC + CronJobs)

**Files:**
- Create: `infrastructure/base/backup/kustomization.yaml`
- Create: `infrastructure/base/backup/pvc.yaml`
- Create: `infrastructure/base/backup/postgres-backup-cronjob.yaml`
- Create: `infrastructure/base/backup/qdrant-backup-cronjob.yaml`
- Modify: `infrastructure/base/kustomization.yaml`

Context: Postgres runs as Helm release `postgresql` in namespace `data`; its password lives in the chart-created Secret `postgresql`, key `postgres-password`. Qdrant REST is `qdrant.data.svc.cluster.local:6333`. yamllint rules: 2-space indent, 200-char lines.

- [ ] **Step 1: Create the manifests**

`infrastructure/base/backup/pvc.yaml`:

```yaml
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: backups
  namespace: data
spec:
  accessModes:
    - ReadWriteOnce
  resources:
    requests:
      storage: 5Gi
```

`infrastructure/base/backup/postgres-backup-cronjob.yaml`:

```yaml
apiVersion: batch/v1
kind: CronJob
metadata:
  name: postgres-backup
  namespace: data
spec:
  schedule: "0 2 * * *"
  timeZone: Europe/Helsinki
  concurrencyPolicy: Forbid
  successfulJobsHistoryLimit: 3
  failedJobsHistoryLimit: 3
  jobTemplate:
    spec:
      backoffLimit: 1
      template:
        spec:
          restartPolicy: Never
          containers:
            - name: pg-dump
              image: postgres:17-alpine
              env:
                - name: PGHOST
                  value: postgresql.data.svc.cluster.local
                - name: PGUSER
                  value: postgres
                - name: PGPASSWORD
                  valueFrom:
                    secretKeyRef:
                      name: postgresql
                      key: postgres-password
              command:
                - /bin/sh
                - -c
                - |
                  set -eu
                  mkdir -p /backups/postgres
                  DATE=$(date +%F)
                  for DB in tietue toimi ruutu; do
                    echo "Dumping $DB..."
                    pg_dump -Fc -d "$DB" -f "/backups/postgres/$DB-$DATE.dump"
                  done
                  find /backups/postgres -name '*.dump' -mtime +14 -delete
                  echo "Done. Current backups:"
                  ls -lh /backups/postgres
              volumeMounts:
                - name: backups
                  mountPath: /backups
          volumes:
            - name: backups
              persistentVolumeClaim:
                claimName: backups
```

`infrastructure/base/backup/qdrant-backup-cronjob.yaml`:

```yaml
apiVersion: batch/v1
kind: CronJob
metadata:
  name: qdrant-backup
  namespace: data
spec:
  schedule: "30 2 * * *"
  timeZone: Europe/Helsinki
  concurrencyPolicy: Forbid
  successfulJobsHistoryLimit: 3
  failedJobsHistoryLimit: 3
  jobTemplate:
    spec:
      backoffLimit: 1
      template:
        spec:
          restartPolicy: Never
          containers:
            - name: qdrant-snapshot
              # alpine + apk keeps us on an official image; the k3s node has egress.
              image: alpine:3.20
              command:
                - /bin/sh
                - -c
                - |
                  set -eu
                  apk add --no-cache curl jq >/dev/null
                  QDRANT=http://qdrant.data.svc.cluster.local:6333
                  DATE=$(date +%F)
                  mkdir -p /backups/qdrant
                  for COLLECTION in $(curl -sf "$QDRANT/collections" | jq -r '.result.collections[].name'); do
                    echo "Snapshotting $COLLECTION..."
                    NAME=$(curl -sf -X POST "$QDRANT/collections/$COLLECTION/snapshots" | jq -r '.result.name')
                    curl -sf "$QDRANT/collections/$COLLECTION/snapshots/$NAME" \
                      -o "/backups/qdrant/$COLLECTION-$DATE.snapshot"
                    curl -sf -X DELETE "$QDRANT/collections/$COLLECTION/snapshots/$NAME" >/dev/null
                  done
                  find /backups/qdrant -name '*.snapshot' -mtime +7 -delete
                  echo "Done. Current snapshots:"
                  ls -lh /backups/qdrant
              volumeMounts:
                - name: backups
                  mountPath: /backups
          volumes:
            - name: backups
              persistentVolumeClaim:
                claimName: backups
```

`infrastructure/base/backup/kustomization.yaml`:

```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization

resources:
  - pvc.yaml
  - postgres-backup-cronjob.yaml
  - qdrant-backup-cronjob.yaml
```

In `infrastructure/base/kustomization.yaml`, add `- backup` to `resources`.

- [ ] **Step 2: Lint**

```bash
yamllint -c .yamllint.yaml infrastructure/base/backup/
```

Expected: clean. (`kubectl kustomize` is unavailable locally — the CI yaml job and the next real deploy validate structure; note this in your report.)

- [ ] **Step 3: Commit**

```bash
git add infrastructure/base/backup infrastructure/base/kustomization.yaml
git commit -m "feat(infra): nightly Postgres dumps and Qdrant snapshots to a local backups PVC"
```

---

## Task 10: Restore verification script + disaster-recovery runbook

**Files:**
- Create: `scripts/verify-backup.sh`
- Create: `docs/ops/disaster-recovery.md`

- [ ] **Step 1: Write `scripts/verify-backup.sh`**

Follow the conventions of the existing scripts (`set -euo pipefail`, `SCRIPT_DIR`/`ROOT_DIR` preamble, reads `infrastructure/overlays/<env>/secrets.env` — see `scripts/dev-setup.sh:103` for the password-sourcing pattern):

```bash
#!/usr/bin/env bash
# Restores the newest backup of each database into a scratch <db>_verify database,
# sanity-checks it, and drops it. Run monthly (see docs/ops/disaster-recovery.md).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

ENV="${1:-dev}"
INFRA_SECRETS="$ROOT_DIR/infrastructure/overlays/$ENV/secrets.env"
if [ ! -f "$INFRA_SECRETS" ]; then
  echo "Missing $INFRA_SECRETS (copy infrastructure/secrets.env.example)" >&2
  exit 1
fi
PG_PASSWORD=$(grep '^postgres-password=' "$INFRA_SECRETS" | cut -d= -f2-)

PSQL="kubectl exec -n data svc/postgresql -- env PGPASSWORD=$PG_PASSWORD psql -U postgres"
FAILURES=0

for DB in tietue toimi ruutu; do
  echo "=== $DB ==="
  LATEST=$(kubectl exec -n data deploy/qdrant -- true 2>/dev/null; \
    kubectl run backup-reader-"$RANDOM" --rm -i --restart=Never -n data \
      --image=postgres:17-alpine \
      --overrides='{"spec":{"containers":[{"name":"backup-reader","image":"postgres:17-alpine","command":["sh","-c","ls -1 /backups/postgres/'"$DB"'-*.dump 2>/dev/null | sort | tail -1"],"volumeMounts":[{"name":"backups","mountPath":"/backups"}]}],"volumes":[{"name":"backups","persistentVolumeClaim":{"claimName":"backups"}}]}}' \
      2>/dev/null | tail -1)
  if [ -z "$LATEST" ]; then
    echo "FAIL: no backup found for $DB"
    FAILURES=$((FAILURES + 1))
    continue
  fi
  echo "Newest dump: $LATEST"

  $PSQL -c "DROP DATABASE IF EXISTS ${DB}_verify;" >/dev/null
  $PSQL -c "CREATE DATABASE ${DB}_verify;" >/dev/null

  kubectl run backup-restore-"$RANDOM" --rm -i --restart=Never -n data \
    --image=postgres:17-alpine \
    --env="PGPASSWORD=$PG_PASSWORD" \
    --overrides='{"spec":{"containers":[{"name":"backup-restore","image":"postgres:17-alpine","env":[{"name":"PGPASSWORD","value":"'"$PG_PASSWORD"'"}],"command":["sh","-c","pg_restore -h postgresql.data.svc.cluster.local -U postgres -d '"${DB}_verify"' '"$LATEST"'"],"volumeMounts":[{"name":"backups","mountPath":"/backups"}]}],"volumes":[{"name":"backups","persistentVolumeClaim":{"claimName":"backups"}}]}}'

  TABLES=$($PSQL -tA -d "${DB}_verify" -c "SELECT count(*) FROM information_schema.tables WHERE table_schema='public';")
  if [ "$TABLES" -gt 0 ]; then
    echo "PASS: ${DB}_verify restored with $TABLES table(s)"
  else
    echo "FAIL: ${DB}_verify has no tables"
    FAILURES=$((FAILURES + 1))
  fi
  $PSQL -c "DROP DATABASE ${DB}_verify;" >/dev/null
done

if [ "$FAILURES" -gt 0 ]; then
  echo "=== verify-backup FAILED ($FAILURES) ==="
  exit 1
fi
echo "=== verify-backup PASSED ==="
```

This script cannot be run in this dev environment (no kubectl/cluster) — the deliverable is shellcheck-clean code plus the runbook. Simplify the pod-exec plumbing if you find a cleaner shape (e.g. one helper function creating the PVC-mounted pod), but keep: newest-dump discovery, restore into `<db>_verify`, table-count assertion, cleanup, non-zero exit on failure.

Make it executable: `chmod +x scripts/verify-backup.sh`.

- [ ] **Step 2: Verify with shellcheck (via docker if not installed, else skip note)**

```bash
shellcheck scripts/verify-backup.sh || echo "shellcheck unavailable locally — CI will check"
```

If shellcheck is unavailable locally, rely on CI — but re-read the script carefully for quoting bugs first (the JSON `--overrides` quoting is the risky part).

- [ ] **Step 3: Write the runbook**

Create `docs/ops/disaster-recovery.md`:

```markdown
# Disaster recovery

Nightly backups land on the `backups` PVC in the `data` namespace:
`/backups/postgres/<db>-<date>.dump` (14 days) and `/backups/qdrant/<collection>-<date>.snapshot` (7 days),
produced by the `postgres-backup` (02:00) and `qdrant-backup` (02:30) CronJobs.

> **Limitation:** these backups live on the same node disk as the databases.
> They protect against dropped tables, bad migrations, and corruption — NOT
> against disk failure. Off-site replication (S3 or rsync to another machine)
> is the planned upgrade; until then, copy dumps off the node manually after
> significant data changes: `kubectl cp` from any pod mounting the PVC.

## Restore PostgreSQL

1. Find the dump: run a pod with the PVC mounted and `ls /backups/postgres/`.
2. Stop writers: `kubectl scale deploy -n apps toimi-tools-tietue toimi-web toimi-tools-ruutu --replicas=0`.
3. Recreate the DB and restore:
   `dropdb`/`createdb` (or `DROP/CREATE DATABASE` via psql), then
   `pg_restore -h postgresql.data.svc.cluster.local -U postgres -d <db> /backups/postgres/<db>-<date>.dump`.
4. Scale the services back up. EF migrations run on startup and are no-ops on a current dump.

## Restore Qdrant

Per collection: `PUT /collections/<name>/snapshots/upload` with the snapshot file
(see Qdrant snapshot docs for the exact multipart form), or copy the snapshot into
the qdrant pod and use `POST /collections/<name>/snapshots/recover`.

## Rebuild Qdrant without snapshots

Qdrant is derived data. For each semantically-indexed type (memory, skill, ...):
`POST /api/admin/tietue/semantic/reconcile/<type>` from the /admin panel host.
This enqueues re-embedding of every missing entity (OpenAI cost applies) and
prunes orphaned vectors. The outbox worker drains the queue within minutes.

## Verification cadence

Run `scripts/verify-backup.sh <env>` monthly. It restores the newest dump of each
database into a scratch `<db>_verify` database, asserts tables exist, and drops it.
```

- [ ] **Step 4: Lint and commit**

```bash
bash scripts/lint.sh
git add scripts/verify-backup.sh docs/ops/disaster-recovery.md
git commit -m "docs(ops): restore-verification script and disaster-recovery runbook"
```

---

## Final verification (after all tasks)

- [ ] Run the full suite:

```bash
bash scripts/lint.sh && dotnet test toimi.sln
cd src/toimi.web/ClientApp && npm run lint && npm run build
```
Expected: lint passed, all tests green (roughly 300+ across 5 test projects now), frontend clean.

- [ ] `git status` clean; commits follow `<type>(<scope>): <subject>`.
- [ ] Report the deploy note to the user: tietue needs `scripts/deploy.sh <env> toimi.tools.tietue` (new migration + workers), web and infra need their deploys, and the first backup run can be forced with `kubectl create job --from=cronjob/postgres-backup manual-backup-test -n data`.
