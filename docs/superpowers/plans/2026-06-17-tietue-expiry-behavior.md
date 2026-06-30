# Expiry Behavior Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Implement `Expiry` as a real declarative behavior: an entity of an `Expiry {field, prompt?}` type gets a one-shot trigger at `Data[field]` that, when it fires, either deletes the entity (deterministic) or — if a `prompt` is configured — runs an agent that decides to delete it or push the date forward. Editing the field re-provisions the trigger.

**Architecture:** Expiry rides the existing trigger/handler/scheduler machinery. New pieces: (1) a `delete` native handler (bottom of the cost ladder); (2) `SchedulerTick` made resilient to a handler deleting its own entity (skip event-record + trigger-advance when the entity is gone — its trigger is cascade-deleted); (3) a `Trigger.Source` marker so the per-entity expiry trigger can be found and replaced; (4) an `ExpiryReconciler` collaborator called from `EntityRepository` on create/update that removes the entity's old expiry trigger and provisions a fresh one from current `Data`. Agent-decided mode reuses the existing `message` handler; the agent pushes the date back simply by calling `update` on the entity (which re-runs reconcile).

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, xUnit + EF InMemory. Mirrors existing handlers (`NotifyHandler`/`SetFieldHandler`), `TriggerProvisioner`, `BehaviorSpec`.

**Conventions:** 2-space indent, file-scoped namespaces, block bodies (IDE0022), conditional expressions (IDE0046), no unused usings (IDE0005). After each task run `dotnet format <csproj> --verify-no-changes` for BOTH the src and test csproj and confirm exit 0. Build/test only via Docker: `docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 <cmd>`. Commit messages end with: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`. Keep the tree clean; if a format-apply dirties unrelated files, `git restore` them.

**Branch:** work on `feat/tietue-expiry` (branched from `feat/unified-model`). The plan file is committed there first.

---

## File Structure

- **Create** `src/toimi.tools.tietue/Handlers/DeleteHandler.cs` — native `delete` handler.
- **Modify** `src/toimi.tools.tietue/Scheduling/SchedulerTick.cs` — deletion-resilient bookkeeping.
- **Modify** `src/toimi.tools.tietue/Data/Trigger.cs` + `Data/TriggerConfiguration.cs` — `Source` column.
- **Modify** `src/toimi.tools.tietue/Scheduling/TriggerRepository.cs` — `source` param on `CreateAsync`.
- **Create** `src/toimi.tools.tietue/Migrations/<ts>_AddTriggerSource.cs` — via `dotnet ef`.
- **Modify** `src/toimi.tools.tietue/Behaviors/BehaviorSpec.cs` — `ExpiryConfig` + `ExpiryOf`.
- **Create** `src/toimi.tools.tietue/Provisioning/ExpiryReconciler.cs` — reconcile the expiry trigger.
- **Modify** `src/toimi.tools.tietue/Entities/EntityRepository.cs` — call the reconciler on create/update.
- **Modify** `src/toimi.tools.tietue/Program.cs` — register `DeleteHandler` + `ExpiryReconciler`.
- **Modify** `src/toimi.tools.tietue/Seed/TypeSeeder.cs` — `memory` gets an `expiresAt` field + `Expiry` behavior.
- **Modify** `CLAUDE.md` + the design study — mark `Expiry` implemented; note the "questions-for-user" backlog idea.
- **Tests:** `DeleteHandlerTests.cs`, additions to `SchedulerTickTests.cs`, `BehaviorSpecTests.cs`, new `ExpiryReconcilerTests.cs`, additions to `TypeSeederTests.cs`.

---

## Task 1: `delete` native handler

**Files:** Create `Handlers/DeleteHandler.cs`; Create `tests/DeleteHandlerTests.cs`; Modify `Program.cs`.

- [ ] **Step 1: Failing test** — create `src/toimi.tools.tietue.Tests/DeleteHandlerTests.cs`:

```csharp
using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class DeleteHandlerTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"}}}""";

  [Fact]
  public async Task Deletes_the_entity()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("task", Schema);
    var repo = new EntityRepository(db, new SchemaValidator());
    var e = await repo.CreateAsync("task", JsonNode.Parse("""{"name":"x"}"""), []);

    var handler = new DeleteHandler(repo);
    var result = await handler.HandleAsync(new HandlerContext(e, null, DateTimeOffset.UtcNow));

    Assert.Equal("deleted", result.Status);
    Assert.Null(await repo.GetAsync(e.Id));
  }

  [Fact]
  public async Task Reports_skipped_when_already_gone()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("task", Schema);
    var repo = new EntityRepository(db, new SchemaValidator());
    var e = await repo.CreateAsync("task", JsonNode.Parse("""{"name":"x"}"""), []);
    await repo.DeleteAsync(e.Id);

    var handler = new DeleteHandler(repo);
    var result = await handler.HandleAsync(new HandlerContext(e, null, DateTimeOffset.UtcNow));

    Assert.Equal("skipped", result.Status);
  }
}
```

- [ ] **Step 2: Run, confirm compile failure** (`DeleteHandler` missing).

Run: `docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet build src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`

- [ ] **Step 3: Implement** `src/toimi.tools.tietue/Handlers/DeleteHandler.cs`:

```csharp
using toimi.tools.tietue.Entities;

namespace toimi.tools.tietue.Handlers;

public class DeleteHandler(EntityRepository repository) : INativeHandler
{
  public string Kind => "delete";

  public async Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
  {
    var deleted = await repository.DeleteAsync(ctx.Entity.Id, ct);
    return deleted
      ? new HandlerResult("deleted")
      : new HandlerResult("skipped", /*lang=json,strict*/ """{"reason":"not found"}""");
  }
}
```

- [ ] **Step 4: Register** in `Program.cs`, next to the other `INativeHandler` registrations (after the `SetFieldHandler` line ~47):

```csharp
builder.Services.AddScoped<toimi.tools.tietue.Handlers.INativeHandler, toimi.tools.tietue.Handlers.DeleteHandler>();
```

- [ ] **Step 5: Run tests + lint:**

```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~DeleteHandlerTests" 2>&1 | grep -E "Passed!|Failed!|error"
  dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes >/dev/null 2>&1; echo "SRC=$?"
  dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes >/dev/null 2>&1; echo "TESTS=$?"'
```
Expected: PASS, `SRC=0`, `TESTS=0`.

- [ ] **Step 6: Commit** `git commit -m "feat(tietue): delete native handler"`

---

## Task 2: `SchedulerTick` resilient to handler-deleted entities

**Files:** Modify `Scheduling/SchedulerTick.cs`; add a test to `SchedulerTickTests.cs`.

**Why:** A `delete` handler (or an agent run) can remove the entity mid-tick. Its trigger is then cascade-deleted in Postgres, and an `entity_events` row would violate the FK. The tick must detect this and skip both the event-record and the trigger-advance.

- [ ] **Step 1: Failing test** — append to `SchedulerTickTests.cs` (match the file's existing setup helpers; this test uses a registered `delete` handler). Add:

```csharp
  [Fact]
  public async Task Entity_deleted_by_handler_does_not_throw_and_removes_entity()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("task", /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"}}}""");
    var repo = new EntityRepository(db, new SchemaValidator());
    var entity = await repo.CreateAsync("task", System.Text.Json.Nodes.JsonNode.Parse("""{"name":"x"}"""), []);

    var triggers = new TriggerRepository(db);
    var occurrence = DateTimeOffset.UtcNow.AddMinutes(-1);
    await triggers.CreateAsync(entity.Id, $$"""{"at":"{{occurrence:O}}"}""", "delete", null, DateTimeOffset.UtcNow.AddMinutes(-2));

    var registry = new HandlerRegistry([new DeleteHandler(repo)]);
    var tick = new SchedulerTick(db, registry, new EntityEventStore(db));

    await tick.RunDueAsync(DateTimeOffset.UtcNow, CancellationToken.None);

    Assert.Null(await repo.GetAsync(entity.Id));
  }
```

> Adjust the `using` directives and namespaces at the top of the test to match what's already imported in `SchedulerTickTests.cs` (it already references `TriggerRepository`, `SchedulerTick`, `EntityEventStore`, `HandlerRegistry`, `EntityRepository`, `TypeRepository`, `SchemaValidator`). Add any missing `using` for `toimi.tools.tietue.Handlers` / `Entities` / `Types` / `Validation` / `Scheduling` / `Events`.

- [ ] **Step 2: Run, confirm it FAILS** — currently the tick throws (`DbUpdateConcurrencyException`/FK) or leaves the entity. 

Run: `docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~SchedulerTickTests.Entity_deleted_by_handler"`

- [ ] **Step 3: Implement.** Replace the body of the `foreach (var trigger in due)` loop in `Scheduling/SchedulerTick.cs` with this (keeps existing behavior, adds the deleted-during-handling guard):

```csharp
    foreach (var trigger in due)
    {
      if (ct.IsCancellationRequested)
      {
        break;
      }

      var occurrence = trigger.NextFireAt!.Value;
      var entity = await db.Entities.FirstOrDefaultAsync(e => e.Id == trigger.EntityId, ct);

      var deletedDuringHandling = false;
      if (entity is not null && !await events.OccurrenceHandledAsync(trigger.EntityId, occurrence, trigger.HandlerKind, ct))
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
          }

          // The handler may have deleted the entity (delete handler, or an agent run).
          // Only record an event while the entity exists (the event FKs to it); if it is gone,
          // its trigger was cascade-deleted, so skip advancing the trigger too.
          if (await db.Entities.AnyAsync(e => e.Id == trigger.EntityId, ct))
          {
            await events.RecordAsync(trigger.EntityId, occurrence, trigger.HandlerKind, status, resultJson, ct);
          }
          else
          {
            deletedDuringHandling = true;
          }
        }
      }

      if (deletedDuringHandling)
      {
        continue;
      }

      trigger.LastFiredAt = occurrence;
      trigger.NextFireAt = Schedules.NextAfter(trigger.Schedule, occurrence);
      trigger.Enabled = trigger.NextFireAt is not null;
      trigger.UpdatedAt = now;
      await db.SaveChangesAsync(ct);
    }
```

- [ ] **Step 4: Run the new test + the full existing `SchedulerTickTests` + lint:**

```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~SchedulerTickTests" 2>&1 | grep -E "Passed!|Failed!|error"
  dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes >/dev/null 2>&1; echo "SRC=$?"
  dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes >/dev/null 2>&1; echo "TESTS=$?"'
```
Expected: all SchedulerTick tests pass, `SRC=0`, `TESTS=0`.

- [ ] **Step 5: Commit** `git commit -m "fix(tietue): scheduler tolerates a handler deleting its own entity"`

---

## Task 3: `Trigger.Source` column

**Files:** Modify `Data/Trigger.cs`, `Data/TriggerConfiguration.cs`, `Scheduling/TriggerRepository.cs`; generate migration; add a test to `TriggerRepositoryTests.cs`.

- [ ] **Step 1: Failing test** — append to `TriggerRepositoryTests.cs` (match its existing setup):

```csharp
  [Fact]
  public async Task Create_persists_source()
  {
    using var db = TestDb.New();
    var entityId = Guid.NewGuid();
    db.Entities.Add(new Data.Entity { Id = entityId, Type = "t", Data = System.Text.Json.JsonDocument.Parse("{}"), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
    await db.SaveChangesAsync();

    var repo = new TriggerRepository(db);
    var t = await repo.CreateAsync(entityId, """{"at":"2026-09-01T00:00:00Z"}""", "delete", null, DateTimeOffset.UtcNow, "expiry");

    Assert.Equal("expiry", t.Source);
  }
```

> Add/align `using` directives to the file's existing ones.

- [ ] **Step 2: Run, confirm compile failure** (no `Source`, `CreateAsync` arity).

- [ ] **Step 3: Implement.**

3a. `Data/Trigger.cs` — add after `HandlerConfig`:

```csharp
  public string? Source { get; set; }
```

3b. `Data/TriggerConfiguration.cs` — add inside `Configure` (after the `HandlerConfig` property line):

```csharp
    builder.Property(t => t.Source);
```

3c. `Scheduling/TriggerRepository.cs` — change `CreateAsync` signature to add a trailing optional `source` param **before** `ct`, and set it:

```csharp
  public async Task<Trigger> CreateAsync(Guid entityId, string scheduleJson, string handlerKind, string? handlerConfig, DateTimeOffset now, string? source = null, CancellationToken ct = default)
  {
    var trigger = new Trigger
    {
      Id = Guid.NewGuid(),
      EntityId = entityId,
      Schedule = scheduleJson,
      HandlerKind = handlerKind,
      HandlerConfig = handlerConfig,
      Source = source,
      Enabled = true,
      NextFireAt = Schedules.InitialNextFireAt(scheduleJson, now),
      CreatedAt = now,
      UpdatedAt = now,
    };
    db.Triggers.Add(trigger);
    await db.SaveChangesAsync(ct);
    return trigger;
  }
```

> Existing callers pass positional `(entityId, schedule, kind, config, now)` then optionally `ct` — those keep working because `source` defaults to null and callers that passed `ct` positionally are `TriggerProvisioner` (passes `ct` by name? verify). Check `TriggerProvisioner.ProvisionAsync` calls `triggers.CreateAsync(entity.Id, schedule, kind, config, now, ct)` — this now binds `ct` to the new `source` param! FIX: update that call to pass `ct: ct` by name: `await triggers.CreateAsync(entity.Id, schedule, kind, config, now, ct: ct);`. Search the whole solution for `CreateAsync(` on a `TriggerRepository`/`triggers` to ensure every call either names `ct:` or doesn't pass `ct` positionally.

- [ ] **Step 4: Generate migration:**

```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet tool install --global dotnet-ef --version "10.0.*" >/dev/null 2>&1
  export PATH="$PATH:/root/.dotnet/tools"
  dotnet restore src/toimi.tools.tietue/toimi.tools.tietue.csproj >/dev/null
  dotnet ef migrations add AddTriggerSource --project src/toimi.tools.tietue'
```
Verify `Up()` adds a nullable `source` text column to `triggers` and the snapshot updated.

- [ ] **Step 5: Run tests + lint** (full suite, since the signature change touches the provisioner):

```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj 2>&1 | grep -E "Passed!|Failed!|error"
  dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes >/dev/null 2>&1; echo "SRC=$?"
  dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes >/dev/null 2>&1; echo "TESTS=$?"'
```
Expected: all pass, `SRC=0`, `TESTS=0`.

- [ ] **Step 6: Commit** `git commit -m "feat(tietue): Trigger.Source marker for provisioned triggers"`

---

## Task 4: `BehaviorSpec.ExpiryOf`

**Files:** Modify `Behaviors/BehaviorSpec.cs`; add tests to `BehaviorSpecTests.cs`.

- [ ] **Step 1: Failing tests** — append to `BehaviorSpecTests.cs`:

```csharp
  [Fact]
  public void Parses_expiry_field_and_prompt()
  {
    var cfg = BehaviorSpec.ExpiryOf(/*lang=json,strict*/ """[{"behavior":"Expiry","config":{"field":"eol","prompt":"check first"}}]""");
    Assert.NotNull(cfg);
    Assert.Equal("eol", cfg!.Field);
    Assert.Equal("check first", cfg.Prompt);
  }

  [Fact]
  public void Expiry_defaults_field_and_null_prompt()
  {
    var cfg = BehaviorSpec.ExpiryOf(/*lang=json,strict*/ """[{"behavior":"Expiry"}]""");
    Assert.Equal("expiresAt", cfg!.Field);
    Assert.Null(cfg.Prompt);
  }

  [Fact]
  public void Null_when_no_expiry_behavior()
  {
    Assert.Null(BehaviorSpec.ExpiryOf(null));
    Assert.Null(BehaviorSpec.ExpiryOf("[]"));
    Assert.Null(BehaviorSpec.ExpiryOf(/*lang=json,strict*/ """[{"behavior":"SemanticIndex","config":{"fields":["a"]}}]"""));
    Assert.Null(BehaviorSpec.ExpiryOf("{ not json"));
  }
```

- [ ] **Step 2: Run, confirm compile failure.**

- [ ] **Step 3: Implement.** In `Behaviors/BehaviorSpec.cs`, add the record near the others:

```csharp
public record ExpiryConfig(string Field, string? Prompt);
```

and add (after `UniqueNameOf`):

```csharp
  // Returns the Expiry config from a type's Behaviors JSON, or null if absent/malformed.
  public static ExpiryConfig? ExpiryOf(string? behaviorsJson)
  {
    if (string.IsNullOrWhiteSpace(behaviorsJson))
    {
      return null;
    }

    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(behaviorsJson);
    }
    catch (JsonException)
    {
      return null;
    }

    using (doc)
    {
      if (doc.RootElement.ValueKind != JsonValueKind.Array)
      {
        return null;
      }

      foreach (var item in doc.RootElement.EnumerateArray())
      {
        if (!item.TryGetProperty("behavior", out var b) || b.GetString() != "Expiry")
        {
          continue;
        }

        var hasConfig = item.TryGetProperty("config", out var config);
        var field = hasConfig && config.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String
          ? f.GetString()!
          : "expiresAt";
        var prompt = hasConfig && config.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String
          ? p.GetString()
          : null;

        return new ExpiryConfig(field, prompt);
      }
    }

    return null;
  }
```

- [ ] **Step 4: Run scoped tests + lint** (filter `BehaviorSpecTests`). Expected PASS, lint 0.

- [ ] **Step 5: Commit** `git commit -m "feat(tietue): parse Expiry behavior config"`

---

## Task 5: `ExpiryReconciler` + wire into `EntityRepository`

**Files:** Create `Provisioning/ExpiryReconciler.cs`; Modify `Entities/EntityRepository.cs`, `Program.cs`; Create `tests/ExpiryReconcilerTests.cs`.

**Scene:** `EntityRepository` already takes optional collaborators (`dispatcher`, `provisioner`). Add `ExpiryReconciler? expiry = null` as a 5th optional ctor param and call it on create (after provisioning) and on update (when `data` changed), after the entity is persisted. The reconciler removes the entity's existing `Source == "expiry"` trigger(s) and, if the type has an `Expiry` behavior and the field holds a value, provisions a fresh one-shot trigger.

- [ ] **Step 1: Failing tests** — create `src/toimi.tools.tietue.Tests/ExpiryReconcilerTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Provisioning;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ExpiryReconcilerTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"name":{"type":"string"},"expiresAt":{"type":"string"}}}""";

  private static async Task<EntityRepository> SetupAsync(Data.TietueDbContext db, string? behaviors)
  {
    await new TypeRepository(db).DefineAsync("temp", Schema, behaviors);
    var triggers = new TriggerRepository(db);
    var reconciler = new ExpiryReconciler(db, triggers);
    return new EntityRepository(db, new SchemaValidator(), expiry: reconciler);
  }

  private const string DeleteExpiry = /*lang=json,strict*/ """[{"behavior":"Expiry","config":{"field":"expiresAt"}}]""";
  private const string AgentExpiry = /*lang=json,strict*/ """[{"behavior":"Expiry","config":{"field":"expiresAt","prompt":"check if still needed"}}]""";

  [Fact]
  public async Task Provisions_delete_trigger_on_create()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, DeleteExpiry);
    var e = await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x","expiresAt":"2026-09-01T00:00:00Z"}"""), []);

    var t = await db.Triggers.SingleAsync(x => x.EntityId == e.Id && x.Source == "expiry");
    Assert.Equal("delete", t.HandlerKind);
    Assert.NotNull(t.NextFireAt);
  }

  [Fact]
  public async Task Uses_message_handler_when_prompt_present()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, AgentExpiry);
    var e = await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x","expiresAt":"2026-09-01T00:00:00Z"}"""), []);

    var t = await db.Triggers.SingleAsync(x => x.EntityId == e.Id && x.Source == "expiry");
    Assert.Equal("message", t.HandlerKind);
    Assert.Contains("promptTemplate", t.HandlerConfig);
    Assert.Contains("check if still needed", t.HandlerConfig);
  }

  [Fact]
  public async Task No_trigger_when_field_absent()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, DeleteExpiry);
    var e = await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x"}"""), []);

    Assert.False(await db.Triggers.AnyAsync(x => x.EntityId == e.Id && x.Source == "expiry"));
  }

  [Fact]
  public async Task Update_moves_the_trigger()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, DeleteExpiry);
    var e = await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x","expiresAt":"2026-09-01T00:00:00Z"}"""), []);

    await repo.UpdateAsync(e.Id, JsonNode.Parse("""{"name":"x","expiresAt":"2027-01-01T00:00:00Z"}"""), null);

    var t = await db.Triggers.SingleAsync(x => x.EntityId == e.Id && x.Source == "expiry");
    Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), t.NextFireAt);
  }

  [Fact]
  public async Task Update_removing_field_drops_the_trigger()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, DeleteExpiry);
    var e = await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x","expiresAt":"2026-09-01T00:00:00Z"}"""), []);

    await repo.UpdateAsync(e.Id, JsonNode.Parse("""{"name":"x"}"""), null);

    Assert.False(await db.Triggers.AnyAsync(x => x.EntityId == e.Id && x.Source == "expiry"));
  }

  [Fact]
  public async Task Reconcile_does_not_duplicate_triggers()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, DeleteExpiry);
    var e = await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x","expiresAt":"2026-09-01T00:00:00Z"}"""), []);
    await repo.UpdateAsync(e.Id, JsonNode.Parse("""{"name":"y","expiresAt":"2026-09-01T00:00:00Z"}"""), null);

    Assert.Equal(1, await db.Triggers.CountAsync(x => x.EntityId == e.Id && x.Source == "expiry"));
  }
}
```

- [ ] **Step 2: Run, confirm failure** (compile: `ExpiryReconciler` missing; `expiry:` param missing).

- [ ] **Step 3: Implement `Provisioning/ExpiryReconciler.cs`:**

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Provisioning;

public class ExpiryReconciler(TietueDbContext db, TriggerRepository triggers)
{
  public const string SourceTag = "expiry";

  public async Task ReconcileAsync(Entity entity, string? behaviorsJson, DateTimeOffset now, CancellationToken ct = default)
  {
    var existing = await db.Triggers.Where(t => t.EntityId == entity.Id && t.Source == SourceTag).ToListAsync(ct);
    if (existing.Count > 0)
    {
      db.Triggers.RemoveRange(existing);
      await db.SaveChangesAsync(ct);
    }

    var cfg = BehaviorSpec.ExpiryOf(behaviorsJson);
    if (cfg is null)
    {
      return;
    }

    var at = ExpiryAt(entity.Data, cfg.Field);
    if (at is null)
    {
      return;
    }

    var schedule = new JsonObject { ["at"] = at }.ToJsonString();
    var (kind, config) = cfg.Prompt is null
      ? ("delete", (string?)null)
      : ("message", MessageConfig(entity.Type, cfg.Field, cfg.Prompt));

    await triggers.CreateAsync(entity.Id, schedule, kind, config, now, SourceTag, ct);
  }

  private static string? ExpiryAt(JsonDocument data, string field)
  {
    return data.RootElement.TryGetProperty(field, out var v)
      && v.ValueKind == JsonValueKind.String
      && !string.IsNullOrWhiteSpace(v.GetString())
        ? v.GetString()
        : null;
  }

  private static string MessageConfig(string type, string field, string prompt)
  {
    var instruction =
      $"The expiry time for this '{type}' entity has arrived. Decide whether it should be removed now. "
      + "If it is no longer needed, delete it using the delete tool. "
      + $"If it is still needed, update its '{field}' field to a later time using the update tool, which re-arms expiry. "
      + $"Guidance: {prompt}";
    return new JsonObject { ["promptTemplate"] = instruction }.ToJsonString();
  }
}
```

> Note: `MessageConfig` deliberately builds a literal instruction with no `{...}` placeholders (it uses C# interpolation), so the `message` handler's `TemplateRenderer` passes it through unchanged. The entity's current data is already given to the agent by `AgentRunner`.

- [ ] **Step 4: Wire into `Entities/EntityRepository.cs`.**

4a. Add the ctor param (5th optional):

```csharp
public class EntityRepository(TietueDbContext db, SchemaValidator validator, BehaviorDispatcher? dispatcher = null, TriggerProvisioner? provisioner = null, ExpiryReconciler? expiry = null)
```

4b. In `CreateAsync`, after the `provisioner` block (before `return entity;`):

```csharp
    if (expiry is not null)
    {
      await expiry.ReconcileAsync(entity, typeDef.Behaviors, entity.CreatedAt, ct);
    }
```

4c. In `UpdateAsync`: the reconcile must run after the entity is saved and only when `data` changed. The simplest correct edit — capture the type's behaviors when `data is not null`, then reconcile after the save. Change the `if (data is not null)` block to also stash behaviors, and add the reconcile after `await SaveGuardingUniqueAsync(entity.Type, ct);`:

Replace:

```csharp
    if (data is not null)
    {
      var typeDef = await GetTypeDefOrThrowAsync(entity.Type, ct);
      Validate(typeDef.JsonSchema.RootElement.GetRawText(), data);
      var previous = entity.Data;
      entity.Data = JsonSerializer.SerializeToDocument(data);
      previous.Dispose();
      await EnforceUniqueOnUpdateAsync(entity, typeDef.Behaviors, ct);
    }
```

with:

```csharp
    string? behaviorsForExpiry = null;
    if (data is not null)
    {
      var typeDef = await GetTypeDefOrThrowAsync(entity.Type, ct);
      Validate(typeDef.JsonSchema.RootElement.GetRawText(), data);
      var previous = entity.Data;
      entity.Data = JsonSerializer.SerializeToDocument(data);
      previous.Dispose();
      await EnforceUniqueOnUpdateAsync(entity, typeDef.Behaviors, ct);
      behaviorsForExpiry = typeDef.Behaviors;
    }
```

and after `await SaveGuardingUniqueAsync(entity.Type, ct);` (and before the `dispatcher` block):

```csharp
    if (expiry is not null && data is not null)
    {
      await expiry.ReconcileAsync(entity, behaviorsForExpiry, entity.UpdatedAt, ct);
    }
```

> `using toimi.tools.tietue.Provisioning;` is already imported in `EntityRepository.cs`.

- [ ] **Step 5: Register in `Program.cs`** (near the other scoped services, after `TriggerProvisioner` registration ~line 24):

```csharp
builder.Services.AddScoped<toimi.tools.tietue.Provisioning.ExpiryReconciler>();
```

- [ ] **Step 6: Run full suite + lint:**

```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj 2>&1 | grep -E "Passed!|Failed!|error"
  dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes >/dev/null 2>&1; echo "SRC=$?"
  dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes >/dev/null 2>&1; echo "TESTS=$?"'
```
Expected: all pass, both lint 0.

- [ ] **Step 7: Commit** `git commit -m "feat(tietue): ExpiryReconciler provisions/updates the expiry trigger"`

---

## Task 6: Seed Expiry on `memory` + docs

**Files:** Modify `Seed/TypeSeeder.cs`, `tests/TypeSeederTests.cs`, `CLAUDE.md`, the design study.

- [ ] **Step 1: Failing test** — in `TypeSeederTests.cs`, extend `Seeds_memory_and_skill_types_with_semantic_index` with:

```csharp
    Assert.Contains("Expiry", memory.Behaviors);
```

- [ ] **Step 2: Run, confirm failure.**

- [ ] **Step 3: Implement.** In `Seed/TypeSeeder.cs`, update the `memory` entry: add an `expiresAt` property to its schema and add `Expiry` to its behaviors. Replace the `memory` tuple's schema and behaviors with:

Schema (add the `expiresAt` property inside `properties`):
```
"expiresAt":{"type":"string","description":"optional ISO 8601 UTC time after which this memory is auto-deleted"}
```
so the schema becomes:
```csharp
      /*lang=json,strict*/
                           """
      {"type":"object","properties":{
        "content":{"type":"string","description":"the fact or observation to remember"},
        "category":{"type":"string","description":"optional category, e.g. preference/fact/context"},
        "source":{"type":"string","description":"user or inferred"},
        "confirmed":{"type":"boolean"},
        "expiresAt":{"type":"string","description":"optional ISO 8601 UTC time after which this memory is auto-deleted"}
      },"required":["content"]}
      """,
```
Behaviors (add Expiry alongside SemanticIndex):
```csharp
      /*lang=json,strict*/
                           """[{"behavior":"SemanticIndex","config":{"fields":["content"],"mode":"whole"}},{"behavior":"Expiry","config":{"field":"expiresAt"}}]""",
```

- [ ] **Step 4: Update docs.**

4a. `CLAUDE.md` — the behaviors bullet (updated in the UniqueName task) currently marks Expiry as not implemented. Replace it with:

```
- **Declarative behaviors** (passive, per-type): `SemanticIndex` (embed
  configured fields → Qdrant on save, semantic `search`), `UniqueName`
  (reject a second entity of the type sharing a keyed field — pre-check plus a
  `unique_keys` DB unique index; config `{"field":"<name>"}`, default `name`),
  and `Expiry` (`{"field":"<dateField>","prompt"?:"..."}` — provisions a
  one-shot trigger at that time that deletes the entity, or, when a `prompt` is
  set, runs an agent that deletes it or pushes the date forward).
```

4b. `docs/superpowers/specs/2026-06-14-generic-entity-component-engine-design.md` — at the `Expiry` row in the behaviors table (~line 176), append an implementation note, e.g. change the description to: `Provision a one-shot delete/message trigger at {field}; agent-decided mode via {prompt}. (Implemented.)` Keep it factual; do not rewrite the table.

4c. Add a backlog note for the deferred idea. Append to the design study a short "Future / backlog" bullet (create the section if absent):

```
## Future / backlog

- **User question inbox:** let a handler (or agent run) enqueue questions for
  the user; the web chat surfaces a list of pending questions to answer on next
  open. An inbound async-prompt surface, distinct from entity behaviors. Deferred.
```

- [ ] **Step 5: Run full suite + lint.** Expected: all pass, both lint 0.

- [ ] **Step 6: Commit** `git commit -m "feat(tietue): seed Expiry on memory; document Expiry + backlog"`

---

## Task 7: Full verification

- [ ] **Step 1:** Full suite + both lint checks:

```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj 2>&1 | grep -E "Passed!|Failed!"
  dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes >/dev/null 2>&1; echo "SRC=$?"
  dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes >/dev/null 2>&1; echo "TESTS=$?"'
```
Expected: all pass, `SRC=0`, `TESTS=0`.

- [ ] **Step 2:** Confirm both migrations present (`AddTriggerSource`) and the snapshot has `triggers.source`. Hand back to the controller for the finishing-a-development-branch step.

---

## Notes / out of scope

- **Read-time exclusion** (filter expired entities from `list`/`search` before the GC trigger fires) is NOT implemented — prompt GC (≤ scheduler tick, ~1 min) makes the staleness window small. Revisit if needed.
- **Agent pushback loop:** in `prompt` mode the agent reschedules by calling `update` on the entity, which re-runs `ExpiryReconciler` and moves the trigger — no special trigger-editing path needed.
- **Backfill:** adding `Expiry` to a type does not retro-provision triggers for pre-existing entities; it applies on next create/update of each entity.
