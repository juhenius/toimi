# Week-2 Follow-ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the four items filed by the Week-2 reviews: the half-applied-mutation leak in `EntityRepository`, missing agent-run cost in the Usage view, `OutboxWorker` batch starvation + reconcile duplicate enqueues, and two documentation/comment corrections.

**Architecture:** Three small tasks, no new components. Task 1 restructures `EntityRepository` so no throw path leaves pending tracked state (pre-checks before mutation; change-tracker reset in the 23505 catch). Task 2 prices agent tokens in tietue's usage endpoint and totals cost in the UI. Task 3 widens the outbox worker's candidate window, dedupes reconcile enqueues, repositions the hub's budget anchor, and fixes the runbook.

**Tech Stack:** .NET 10, EF Core change-tracker APIs, xUnit + EF InMemory, React admin.

**Conventions for every task:** 2-space indent, file-scoped namespaces, block bodies (IDE0022 as error), CA1873 IsEnabled guards only if the build demands. `dotnet format <changed csproj> --verify-no-changes` before each commit. dotnet at `/Users/jari/.local/share/mise/installs/dotnet/10.0.301/` if not on PATH. Work from repo root; verify branch with `git branch --show-current` first.

---

## Task 1: EntityRepository mutation safety (tietue)

Context: handlers run inside the scheduler tick sharing a scoped `DbContext`. Today `UpdateAsync` mutates `entity.Data` (and `CreateAsync` calls `db.Entities.Add`) BEFORE the unique pre-check can throw — a caught `TietueValidationException` leaves poisoned tracked state that the tick's later `FinalizeAsync`/advance `SaveChangesAsync` silently commits. The DB-constraint path (`SaveGuardingUniqueAsync` catching 23505) has the same problem plus a poisoned pending `UniqueKey` that makes subsequent saves throw.

**Files:**
- Modify: `src/toimi.tools.tietue/Entities/EntityRepository.cs`
- Test: `src/toimi.tools.tietue.Tests/EntityRepositoryFailureTests.cs` (new)

- [x] **Step 1: Write the failing tests**

Create `src/toimi.tools.tietue.Tests/EntityRepositoryFailureTests.cs`. First check the real UniqueName behaviors JSON shape (grep existing tests / `TypeSeeder` for `"UniqueName"` — expected shape `[{"behavior":"UniqueName","config":{"field":"name"}}]`; adapt if different) and the `TestDb.New()` options pattern (see `ClaimCollisionTests.cs` for the throwing-context approach).

```csharp
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class EntityRepositoryFailureTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"},"note":{"type":"string"}},"required":["name"]}""";
  private const string Behaviors = /*lang=json,strict*/ """[{"behavior":"UniqueName","config":{"field":"name"}}]""";

  private static async Task<(TietueDbContext db, EntityRepository repo)> SetupAsync()
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("memory", Schema, Behaviors);
    var repo = new EntityRepository(db, new SchemaValidator());
    return (db, repo);
  }

  [Fact]
  public async Task Failed_update_unique_precheck_leaves_no_pending_changes()
  {
    var (db, repo) = await SetupAsync();
    using var _ = db;
    await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"a"}"""), []);
    var b = await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"b","note":"original"}"""), []);

    await Assert.ThrowsAsync<TietueValidationException>(() =>
      repo.UpdateAsync(b.Id, JsonNode.Parse("""{"name":"a","note":"poisoned"}"""), null));

    Assert.False(db.ChangeTracker.HasChanges());

    // Simulate the scheduler tick's later save flushing whatever is tracked.
    await db.SaveChangesAsync();
    using var fresh = TestDb.SameStore(db); // helper: new context on the same InMemory store; add it if absent
    var reloaded = await fresh.Entities.SingleAsync(e => e.Id == b.Id);
    Assert.Contains("original", reloaded.Data.RootElement.GetRawText());
    Assert.DoesNotContain("poisoned", reloaded.Data.RootElement.GetRawText());
  }

  [Fact]
  public async Task Failed_create_unique_precheck_leaves_no_pending_changes()
  {
    var (db, repo) = await SetupAsync();
    using var _ = db;
    await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"a"}"""), []);

    await Assert.ThrowsAsync<TietueValidationException>(() =>
      repo.CreateAsync("memory", JsonNode.Parse("""{"name":"a"}"""), []));

    Assert.False(db.ChangeTracker.HasChanges());
    Assert.Equal(1, await db.Entities.CountAsync());
    Assert.Empty(await db.IndexOutbox.ToListAsync());
  }

  [Fact]
  public async Task Unique_index_violation_resets_pending_changes()
  {
    // Drives the SaveGuardingUniqueAsync 23505 catch, unreachable under InMemory
    // (no unique enforcement) without a context that throws on demand.
    var db = TestDb.NewThrowingOnce(); // helper: context whose next SaveChangesAsync throws
                                       // DbUpdateException with inner PostgresException(SqlState 23505);
                                       // model it on ClaimCollisionTests.ThrowOnceDbContext.
    using var _ = db;
    await new TypeRepository(db).DefineAsync("memory", Schema, Behaviors);
    var repo = new EntityRepository(db, new SchemaValidator());
    var e = await repo.CreateAsync("memory", JsonNode.Parse("""{"name":"a","note":"original"}"""), []);

    db.ThrowNext = true;
    await Assert.ThrowsAsync<TietueValidationException>(() =>
      repo.UpdateAsync(e.Id, JsonNode.Parse("""{"name":"a","note":"poisoned"}"""), null));

    Assert.False(db.ChangeTracker.HasChanges());
    await db.SaveChangesAsync();
    using var fresh = TestDb.SameStore(db);
    var reloaded = await fresh.Entities.SingleAsync(x => x.Id == e.Id);
    Assert.Contains("original", reloaded.Data.RootElement.GetRawText());
  }
}
```

Notes for the implementer: `Npgsql.PostgresException` has a public constructor `(string messageText, string severity, string invariantSeverity, string sqlState)` — use SqlState `"23505"` so the existing `when` filter matches. If `TestDb` lacks `SameStore`/`NewThrowingOnce` helpers, add them to the test project (`SameStore` = new context over the same InMemory database name; `NewThrowingOnce` = a `TietueDbContext` subclass with a `ThrowNext` flag). Adapt names to what already exists — `ClaimCollisionTests` has a near-identical throwing subclass you may generalize instead of duplicating.

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/toimi.tools.tietue.Tests --filter EntityRepositoryFailureTests`
Expected: tests 1 and 3 FAIL (pending changes persist / poisoned data committed); test 2 may fail on `HasChanges` (the Added entity lingers).

- [x] **Step 3: Restructure `CreateAsync`**

Move `db.Entities.Add(entity)` to AFTER the unique pre-check so a throw leaves nothing tracked (the pre-check reads `entity.Data`/`Type` from the object, not the tracker; its own `UniqueKeys.Add` only happens when the check passes):

```csharp
    var entity = new Entity
    {
      Id = Guid.NewGuid(),
      Type = type,
      Data = JsonSerializer.SerializeToDocument(data),
      Tags = NormalizeTags(tags),
      CreatedAt = now,
      UpdatedAt = now,
    };
    await EnforceUniqueOnCreateAsync(entity, typeDef.Behaviors, ct);
    db.Entities.Add(entity);
    var indexOp = outbox?.Enqueue(entity, typeDef.Behaviors, "upsert");
    await SaveGuardingUniqueAsync(entity.Type, ct);
```

- [x] **Step 4: Restructure `UpdateAsync`**

Mutate `entity.Data` only after every throw path. Change `EnforceUniqueOnUpdateAsync` to take the NEW document explicitly, and drop the eager `previous.Dispose()` (an undisposed `JsonDocument` just misses the array pool — no leak — while eager disposal would poison the original-values snapshot the 23505 reset needs):

```csharp
    if (data is not null)
    {
      var typeDef = await GetTypeDefOrThrowAsync(entity.Type, ct);
      Validate(typeDef.JsonSchema.RootElement.GetRawText(), data);
      var newData = JsonSerializer.SerializeToDocument(data);
      await EnforceUniqueOnUpdateAsync(entity, newData, typeDef.Behaviors, ct);
      // Mutate only after all pre-checks: a caught validation failure inside a scheduler
      // tick must not leave half-applied tracked state for the tick's later saves to flush.
      entity.Data = newData;
      behaviorsForExpiry = typeDef.Behaviors;
      indexOp = outbox?.Enqueue(entity, typeDef.Behaviors, "upsert");
    }
```

and in `EnforceUniqueOnUpdateAsync`, add the `JsonDocument newData` parameter and read `var value = KeyValue(newData, cfg.Field);` instead of `entity.Data`.

- [x] **Step 5: Reset pending changes in the 23505 catch**

In `SaveGuardingUniqueAsync`:

```csharp
    catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
    {
      ResetPendingChanges();
      throw new TietueValidationException([$"A '{type}' with a duplicate unique field already exists."]);
    }
```

and add the private helper:

```csharp
  // Reverts everything the failed save was about to write, WITHOUT detaching unrelated
  // tracked entities (the scheduler tick's trigger batch shares this scoped context).
  private void ResetPendingChanges()
  {
    foreach (var entry in db.ChangeTracker.Entries()
      .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
      .ToList())
    {
      switch (entry.State)
      {
        case EntityState.Added:
          entry.State = EntityState.Detached;
          break;
        case EntityState.Modified:
          entry.CurrentValues.SetValues(entry.OriginalValues);
          entry.State = EntityState.Unchanged;
          break;
        case EntityState.Deleted:
          entry.State = EntityState.Unchanged;
          break;
      }
    }
  }
```

(`EntityState` comes with the existing `Microsoft.EntityFrameworkCore` using.)

- [x] **Step 6: Run the full tietue suite**

Run: `dotnet test src/toimi.tools.tietue.Tests`
Expected: all pass (169 pre-existing + 3 new = 172). If an existing test constructed data through the old `EnforceUniqueOnUpdateAsync` signature, fix the call site (it's private — only `UpdateAsync` calls it).

- [x] **Step 7: Format and commit**

```bash
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
git add src/toimi.tools.tietue src/toimi.tools.tietue.Tests
git commit -m "fix(tietue): keep failed entity mutations out of the shared change tracker"
```

---

## Task 2: Agent-run cost in the Usage view (tietue + web UI)

tietue already binds `ToimiConfiguration` (Program.cs `Toimi` section), so its usage endpoint can price tokens the same way the web one does.

**Files:**
- Modify: `src/toimi.tools.tietue/Admin/AdminEndpoints.cs` (`/usage` endpoint)
- Modify: `src/toimi.web/ClientApp/src/admin/UsagePage.tsx`
- Test: extend `src/toimi.tools.tietue.Tests/AdminEndpointsTests.cs`

- [x] **Step 1: Extend the tietue usage test (failing first)**

In the existing usage test in `AdminEndpointsTests.cs`, assert each returned row also carries `costUsd` computed from the seeded token counts at the test-host prices. Check how the test host builds services: if `ToimiConfiguration` isn't registered there, register one (`new ToimiConfiguration { OpenAI = new OpenAIOptions { ApiKey = "test" } }` — prices default to 2.50/10.00). Expected assertion for e.g. 1200 prompt + 340 completion tokens: `costUsd == 1200m / 1_000_000 * 2.50m + 340m / 1_000_000 * 10.00m`. Run → FAIL (no `costUsd` field).

- [x] **Step 2: Price the endpoint**

In tietue's `/usage` endpoint, add `Toimi.Core.Configuration.ToimiConfiguration config` to the handler parameters and extend the projection:

```csharp
        .Select(g => new
        {
          date = g.Key,
          promptTokens = g.Sum(r => r.Prompt),
          completionTokens = g.Sum(r => r.Completion),
          costUsd = (g.Sum(r => r.Prompt) / 1_000_000m * config.TokenPriceInputPer1M)
            + (g.Sum(r => r.Completion) / 1_000_000m * config.TokenPriceOutputPer1M),
        })
```

Run the test → PASS. Run the full tietue suite.

- [x] **Step 3: Total cost in the UI**

In `UsagePage.tsx`: add `costUsd: number` to `AgentUsageRow`; change the header `Est. cost (web)` to `Est. cost`; the cell becomes the sum with a dash when neither side has data:

```tsx
                <td className="px-3 py-2 border-t border-zinc-800">
                  {w || a ? `$${((w?.costUsd ?? 0) + (a?.costUsd ?? 0)).toFixed(2)}` : '—'}
                </td>
```

(match the file's existing cell classes exactly — read it first).

- [x] **Step 4: Verify, format, commit**

```bash
dotnet test toimi.sln
cd src/toimi.web/ClientApp && npm run lint && npm run build && cd -
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
git add src/toimi.tools.tietue src/toimi.tools.tietue.Tests src/toimi.web/ClientApp
git commit -m "feat(admin): price agent-run tokens in the usage view"
```

---

## Task 3: Worker window, reconcile dedupe, anchor placement, runbook note

**Files:**
- Modify: `src/toimi.tools.tietue/Semantic/OutboxWorker.cs`
- Modify: `src/toimi.tools.tietue/Semantic/SemanticReconciler.cs`
- Modify: `src/toimi.web/Hubs/ToimiHub.cs` (anchor position)
- Modify: `docs/ops/disaster-recovery.md`
- Test: extend `OutboxWorkerTests.cs` and `ReconcileTests.cs`

- [x] **Step 1: Failing test — starvation**

Add to `OutboxWorkerTests.cs` (reuse `Row(...)` and the index fakes):

```csharp
  [Fact]
  public async Task Due_row_behind_a_wall_of_backoff_rows_still_processes()
  {
    using var db = TestDb.New();
    var index = new FailingIndex { Fail = false };
    var now = DateTimeOffset.UtcNow;
    for (var i = 0; i < 25; i++)
    {
      // In backoff (attempts 3 → 8-min backoff, attempted 1 min ago), older than the due row.
      db.IndexOutbox.Add(Row(attempts: 3, lastAttempt: now.AddMinutes(-1), created: now.AddHours(-2)));
    }
    db.IndexOutbox.Add(Row(attempts: 1, lastAttempt: now.AddMinutes(-5), created: now.AddMinutes(-10)));
    await db.SaveChangesAsync();

    var processed = await OutboxWorker.RunOnceAsync(db, new SemanticOutbox(db, index), now, default);

    Assert.Equal(1, processed);
  }
```

Run → FAIL (the 20-oldest window is all backoff rows; processed == 0).

- [x] **Step 2: Widen the candidate window**

In `OutboxWorker.RunOnceAsync`, add `private const int CandidateWindow = 200;` next to `BatchSize` and change the query + loop:

```csharp
    var candidates = await db.IndexOutbox
      .Where(o => o.Attempts < SemanticOutbox.MaxAttempts)
      .OrderBy(o => o.CreatedAt)
      .Take(CandidateWindow) // wide fetch: due-ness (backoff math) isn't SQL-translatable,
                             // and a narrow window of purely-backoff rows would starve newer due rows
      .ToListAsync(ct);

    var processed = 0;
    foreach (var row in candidates.Where(r => IsDue(r, now)).Take(BatchSize))
```

Run the new test → PASS; full OutboxWorkerTests stay green.

- [x] **Step 3: Failing test — reconcile dedupe**

Add to `ReconcileTests.cs`:

```csharp
  [Fact]
  public async Task Reconcile_skips_entities_with_a_live_outbox_row()
  {
    // setup as in the existing diff test: type + one entity missing from Qdrant,
    // but ALSO seed a live (Attempts=1) upsert row for that entity.
    // After ReconcileAsync: assert MissingEnqueued == 0 and the outbox still has
    // exactly one row for that entity (no duplicate).
  }
```

Implement the comments with the file's real helpers. Run → FAIL (duplicate row enqueued).

- [x] **Step 4: Dedupe in `SemanticReconciler`**

After the dead-row purge and before the enqueue loops:

```csharp
    var live = (await db.IndexOutbox
      .Where(o => o.Type == type && o.Attempts < SemanticOutbox.MaxAttempts)
      .Select(o => new { o.EntityId, o.Op })
      .ToListAsync(ct))
      .Select(o => (o.EntityId, o.Op))
      .ToHashSet();
```

and guard each loop: `if (live.Contains((id, "upsert"))) { continue; }` (respectively `"delete"`), counting only rows actually enqueued (adjust the `missing.Count`/`orphans.Count` returns to the enqueued counts). Run → PASS.

- [x] **Step 5: Reposition the hub's budget anchor**

In `src/toimi.web/Hubs/ToimiHub.cs`, move the `RecordUsage` block to BEFORE `session.Messages.Add(new(ChatRole.Assistant, responseText));` and reword the comment:

```csharp
      // Anchor the budget to the real prompt-token count of the messages AS SENT.
      // The assistant response (appended below) then counts into the chars-delta,
      // keeping the estimate conservative rather than undercounting by one response.
      if (usage?.InputTokenCount is not null)
      {
        session.Budget.RecordUsage((int)usage.InputTokenCount.Value, session.Messages);
      }
```

(the `usage` variable is already populated by then — it's read after streaming completes).

- [x] **Step 6: Runbook note**

In `docs/ops/disaster-recovery.md`, in the verification-cadence (or a notes) section add: on the k3s server, `kubectl` means `sudo k3s kubectl` or `export KUBECONFIG=/etc/rancher/k3s/k3s.yaml` — `scripts/verify-backup.sh` assumes a working `kubectl` context on the machine that runs it.

- [x] **Step 7: Verify, format, commit**

```bash
dotnet test toimi.sln
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
dotnet format src/toimi.web/toimi.web.csproj --verify-no-changes
git add src/toimi.tools.tietue src/toimi.tools.tietue.Tests src/toimi.web/Hubs docs/ops
git commit -m "fix(tietue): widen outbox worker window, dedupe reconcile ops; anchor hub budget pre-append"
```

---

## Final verification

- [x] `bash scripts/lint.sh && dotnet test toimi.sln` — lint passed, ~315 tests green.
- [x] `git status` clean; commits follow convention.
