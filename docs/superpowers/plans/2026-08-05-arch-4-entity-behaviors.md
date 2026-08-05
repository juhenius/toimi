# IEntityBehavior Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give tietue's per-type behaviors a real seam: replace `EntityRepository`'s three optional nullable collaborators (`SemanticOutbox?`, `TriggerProvisioner?`, `ExpiryReconciler?`) with an `IEntityBehavior` hook pipeline, collapse `BehaviorSpec`'s three copy-pasted parsers into one `TypeBehaviors.Parse`, and close the two test gaps: no test runs all three behaviors together, and the create-transaction atomicity comment (`EntityRepository.cs:32-37`) is unverified because the InMemory provider never begins a transaction.

**Architecture:** A new `IEntityBehavior` (in `Behaviors/`) exposes three phase hooks — `OnSavingAsync` (joins the repository's pending change set, atomic with the entity save), `OnSavedAsync` (after the save; on create still inside the ambient transaction), `OnCommittedAsync` (after commit/dispose; must not fail the operation). A per-operation `BehaviorContext` carries the entity, the operation, the once-parsed `TypeBehaviors`, the type's `DefaultTriggers` JSON, and an `Items` bag for behavior-private state between hooks. Three adapters — `SemanticIndexBehavior`, `TriggerProvisioningBehavior`, `ExpiryBehavior` — wrap the existing `SemanticOutbox`, `TriggerProvisioner`, `ExpiryReconciler` (whose internals stay). `EntityRepository`'s ctor becomes `(TietueDbContext, SchemaValidator, IEnumerable<IEntityBehavior>? behaviors = null)`; `Program.cs` registers the three behaviors in pipeline order, so a fourth behavior is one class + one DI line. UniqueName enforcement stays inside `EntityRepository` (see Design Decisions).

**Tech Stack:** .NET 10, xUnit v2, EF Core (InMemory in unit tests), Npgsql + EFCore.NamingConventions, Testcontainers.PostgreSql (Docker-gated via `DockerFactAttribute`).

## Global Constraints

- dotnet is NOT on PATH: every command uses `export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"` first.
- Test command: `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj` (use `--filter` per task where possible; the final gate runs the whole project).
- Before the commit of each task: `dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj` and `dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`, then verify `--verify-no-changes` exits 0 on both. Enforced as errors: IDE0005 (unused usings), IDE0022 (block bodies — no expression-bodied members, including default interface methods), IDE0046, whitespace.
- Commit style: `<type>(<scope>): <subject>`, e.g. `refactor(tietue): ...`.
- 2-space indent, file-scoped namespaces; comments only for constraints the code can't show.
- tietue suite is currently 312 tests (including Docker-gated skips when Docker is absent); the count must never end a task below 312. Task 1 adds 12 before Task 2 removes the 10 `BehaviorSpecTests`; expected final count ≈ 325 (312 − 10 + 12 + 6 + 3 + 2). Core (93) and web (38) suites are untouched.
- MCP surface, `TypeSeeder` seeded types, `EntityEvent` shapes, and trigger semantics are unchanged. No observable behavior change anywhere (one deliberate robustness exception noted in Design Decisions).

## Design Decisions

**Hook set: phase hooks, not per-operation hooks.** The three collaborators differ by *phase relative to SaveChanges*, not by operation — read from the call sites in `EntityRepository.cs`: the outbox enqueues pre-save (:45, :119, :153) and drains post-commit (:86, :136, :158); the provisioner runs post-save inside the create transaction (:50); the expiry reconciler runs post-save on create (:55) and update (:131). A per-operation shape (`OnCreatingAsync`/`OnCreatedAsync`/`OnUpdatedAsync`/`OnDeletedAsync`) would need 9–12 methods, mostly empty, and *still* couldn't express the outbox's pre-save/post-commit pair inside any single "OnCreated". So the interface is `OnSavingAsync` / `OnSavedAsync` / `OnCommittedAsync` with `ctx.Operation` for dispatch, each with a default no-op body (block-bodied, per IDE0022) so adapters implement only the hooks they use.

**Outbox two-phase protocol (finding 4): absorbed by `SemanticIndexBehavior`, carried in `ctx.Items`.** `SemanticOutbox.Enqueue` currently returns an `IndexOutbox?` row the repository must hold across its own SaveChanges and hand back to `DrainAsync` — implemented three times with an explanatory comment (:80-83). The adapter enqueues in `OnSavingAsync` (row joins the entity's change set, so it commits or rolls back with the mutation — the durability property the current design bought) and drains in `OnCommittedAsync`. The row travels in `BehaviorContext.Items` (per-operation state), NOT in an adapter field: behaviors are DI-scoped and one scope (a scheduler tick, `ScriptEffectApplier`) performs many sequential operations. `Enqueue`'s signature narrows to `Enqueue(Entity, string op)` returning non-null `IndexOutbox` — the behavior-presence gate moves to the caller (the adapter), killing the null-return half of the protocol. `DrainAsync`/`ProcessAsync`/`OutboxWorker` internals are untouched.

**UniqueName stays inside `EntityRepository` (the honest answer).** Extracting it as an `IEntityBehavior` is dishonest three ways: (1) on update, the duplicate pre-check must run against `newData` *before* `entity.Data` is mutated (`EntityRepository.cs:113-117` — "Mutate only after all pre-checks"), which is earlier than any pipeline hook can run; (2) the 23505 save guard (`SaveGuardingUniqueAsync`) wraps the repository's *own* `SaveChangesAsync` call — a behavior can't intercept its host's save; (3) the recovery path (`ResetPendingChanges`) manipulates the entire tracked change set, including rows other behaviors just enqueued — that is save infrastructure, not a per-type module. The partial extraction that IS honest: parsing moves to the single `TypeBehaviors.Parse` (finding 1), and the enforcement helpers change signature to take the parsed `UniqueNameConfig?` instead of raw JSON. Enforcement logic (:204-297) is otherwise untouched.

**Ctor keeps 2-arg compatibility; no `TestRepo` helper.** `IEnumerable<IEntityBehavior>? behaviors = null` means the ~31 bare `new EntityRepository(db, new SchemaValidator())` test constructions compile unchanged — no helper needed to "kill duplication" because the duplication was the null-object matrix, which is gone. The 8 collaborator-passing sites become explicit behavior lists, which is exactly what the seam should make visible. Microsoft DI injects registered services into optional ctor parameters (this is how prod gets its collaborators *today* — `SemanticOutbox`/`TriggerProvisioner`/`ExpiryReconciler` are all registered scoped, so the "optional" params were always populated in prod); `IEnumerable<T>` is always resolvable, so the three registrations are injected in registration order. Pipeline order = registration order = `SemanticIndexBehavior`, `TriggerProvisioningBehavior`, `ExpiryBehavior`, matching today's call order (enqueue pre-save; provision, then expiry post-save).

**`TypeBehaviors` parse-once value object (finding 1).** One walk over the Behaviors JSON array dispatching on the `"behavior"` discriminator, filling three nullable config slots; per kind the first *parseable* item wins (preserving `SemanticIndexOf`'s skip-malformed-config-then-match-later semantics via `??=` with a null-returning item parser, and `UniqueNameOf`/`ExpiryOf`'s first-item-wins via never-null item parsers). The three config records (`SemanticIndexConfig`, `UniqueNameConfig`, `ExpiryConfig`) move into `TypeBehaviors.cs`; `BehaviorSpec.cs` is **deleted** — its callers (`SemanticOutbox` ×2, `SemanticReconciler`, `BehaviorDispatcher`, `ExpiryReconciler`, `EntityRepository` ×2) all migrate; `TypeSeeder` and `Admin/AdminEndpoints.cs` never call it (verified by grep). One deliberate robustness delta: a non-string `"behavior"` discriminator is now skipped instead of throwing `InvalidOperationException` from `JsonElement.GetString()` (unreachable via `define_type`, which validates JSON, but strictly more robust).

**`ExpiryReconciler` signature narrows to the parsed config.** `ReconcileAsync(Entity, ExpiryConfig?, DateTimeOffset, CancellationToken)` — it must still be *called* even when the config is null (its first act removes stale `Source == "expiry"` triggers, which is how removing the behavior or the field disarms expiry), so `ExpiryBehavior` gates only on operation/data-changed, never on config presence.

**Postgres transaction test (finding 3).** `EntityRepositoryPostgresTests` follows the `PostgresTickLockTests` pattern exactly: per-test `PostgreSqlContainer("postgres:17-alpine")` via `IAsyncLifetime` + `[DockerFact]` (a skipped DockerFact never constructs the class, so no container start on docker-less machines — do NOT convert to IClassFixture). The context must add `.UseSnakeCaseNamingConvention()` (prod parity; the extension resolves transitively through the project reference to EFCore.NamingConventions 10.0.1) and run `MigrateAsync()`. A behavior throwing in `OnSavedAsync` stands at the exact pipeline position provisioning occupies, proving the rollback claim of the 7-line comment; a second test proves the happy-path commit lands entity + unique key + provisioned trigger + drained outbox together.

**Per-test-file disposition (every file constructing `EntityRepository`, from grep):**

| File | Disposition |
|---|---|
| `BehaviorSpecTests.cs` (10 facts) | **Deleted** in Task 2; superseded by `TypeBehaviorsTests.cs` (12 facts, Task 1) |
| `SemanticOutboxTests.cs` (:66, :120) | Ctor edit → `[new SemanticIndexBehavior(outbox)]` (Task 4) |
| `EntityRepositoryIndexingTests.cs` (:19) | Ctor edit → `[new SemanticIndexBehavior(new SemanticOutbox(db, idx))]` (Task 4) |
| `SearchToolTests.cs` (:25) | Same ctor edit (Task 4) |
| `EntityRepositoryTests.cs` (:115) | Ctor edit → `[new TriggerProvisioningBehavior(...)]` (Task 4) |
| `JobEndToEndTests.cs` (:35) | Same ctor edit (Task 4) |
| `EntityRepositoryFailureTests.cs` (:84) | Ctor edit → `[new TriggerProvisioningBehavior(provisioner)]` (Task 4); sites :25/:98 unchanged |
| `ExpiryReconcilerTests.cs` (:21) | Ctor edit → `[new ExpiryBehavior(reconciler)]` (Task 4) |
| All other 21 files with bare `new EntityRepository(db, new SchemaValidator())` (`ScriptEffectApplierTests`, `UpdateTriggerToolTests`, `EntityToolsTests`, `RunTriggerToolTests`, `ClaimThenRunTests`, `ActivateToolTests`, `DeleteHandlerTests`, `SchedulerTickTests`, `OccurrenceRunnerTests`, `SetTriggerToolTests`, `ClaimCollisionTests`, `UniqueNameTests`, `TriggerToolsTests`, `ListSkillsToolTests`, `SkillSeederTests`, `ScriptHandlerTests`, `SchedulerTickLockTests`, `SetFieldHandlerTests`, `ReconcileTests`, `EntityRepositoryTests` :21, `EntityRepositoryFailureTests` :25/:98) | **Unchanged** — the 2-arg ctor still compiles |
| New: `TypeBehaviorsTests.cs`, `EntityBehaviorTests.cs`, `BehaviorPipelineTests.cs`, `EntityRepositoryPostgresTests.cs` | Added (Tasks 1, 3, 5) |

---

## Task 1: `TypeBehaviors` — the single parser (TDD)

**Files**
- Create: `src/toimi.tools.tietue/Behaviors/TypeBehaviors.cs`
- Test (create): `src/toimi.tools.tietue.Tests/TypeBehaviorsTests.cs`

**Interfaces**
- `public sealed record TypeBehaviors(SemanticIndexConfig? SemanticIndex, UniqueNameConfig? UniqueName, ExpiryConfig? Expiry)` with `public static readonly TypeBehaviors None` and `public static TypeBehaviors Parse(string? behaviorsJson)`.
- Reuses the existing records `SemanticIndexConfig(string[] Fields, string Mode)`, `UniqueNameConfig(string Field)`, `ExpiryConfig(string Field, string? Prompt)` (still declared in `BehaviorSpec.cs` until Task 2 — same namespace, no duplication).

**Steps**

- [ ] Write the failing test file `src/toimi.tools.tietue.Tests/TypeBehaviorsTests.cs` (red: `TypeBehaviors` doesn't exist yet, compile failure):

```csharp
using toimi.tools.tietue.Behaviors;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TypeBehaviorsTests
{
  [Fact]
  public void Parses_semantic_index_fields_and_mode()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"SemanticIndex","config":{"fields":["content"],"mode":"whole"}}]""");
    Assert.NotNull(b.SemanticIndex);
    Assert.Equal(["content"], b.SemanticIndex.Fields);
    Assert.Equal("whole", b.SemanticIndex.Mode);
  }

  [Fact]
  public void Defaults_semantic_mode_to_whole_when_absent()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"SemanticIndex","config":{"fields":["a","b"]}}]""");
    Assert.Equal("whole", b.SemanticIndex!.Mode);
    Assert.Equal(["a", "b"], b.SemanticIndex.Fields);
  }

  [Fact]
  public void Semantic_index_absent_when_missing_or_unmatched()
  {
    Assert.Null(TypeBehaviors.Parse(null).SemanticIndex);
    Assert.Null(TypeBehaviors.Parse("[]").SemanticIndex);
    Assert.Null(TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"Other","config":{}}]""").SemanticIndex);
  }

  [Fact]
  public void Semantic_index_without_fields_is_skipped_but_a_later_valid_item_wins()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/
      """[{"behavior":"SemanticIndex","config":{}},{"behavior":"SemanticIndex","config":{"fields":["late"]}}]""");
    Assert.Equal(["late"], b.SemanticIndex!.Fields);
  }

  [Fact]
  public void Malformed_json_yields_none()
  {
    Assert.Same(TypeBehaviors.None, TypeBehaviors.Parse("{ not json"));
    Assert.Same(TypeBehaviors.None, TypeBehaviors.Parse(/*lang=json,strict*/ """{"behavior":"SemanticIndex"}"""));
  }

  [Fact]
  public void Parses_unique_name_field()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"UniqueName","config":{"field":"url"}}]""");
    Assert.Equal("url", b.UniqueName!.Field);
  }

  [Fact]
  public void Unique_name_defaults_field_to_name()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"UniqueName"}]""");
    Assert.Equal("name", b.UniqueName!.Field);
  }

  [Fact]
  public void Unique_name_absent_when_missing_or_unmatched()
  {
    Assert.Null(TypeBehaviors.Parse(null).UniqueName);
    Assert.Null(TypeBehaviors.Parse("[]").UniqueName);
    Assert.Null(TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"SemanticIndex","config":{"fields":["a"]}}]""").UniqueName);
  }

  [Fact]
  public void Parses_expiry_field_and_prompt()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"Expiry","config":{"field":"eol","prompt":"check first"}}]""");
    Assert.Equal("eol", b.Expiry!.Field);
    Assert.Equal("check first", b.Expiry.Prompt);
  }

  [Fact]
  public void Expiry_defaults_field_and_leaves_prompt_null()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"Expiry"}]""");
    Assert.Equal("expiresAt", b.Expiry!.Field);
    Assert.Null(b.Expiry.Prompt);
  }

  [Fact]
  public void Parses_all_three_from_one_document()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/
      """[{"behavior":"SemanticIndex","config":{"fields":["content"]}},{"behavior":"UniqueName","config":{"field":"name"}},{"behavior":"Expiry","config":{"field":"expiresAt"}}]""");
    Assert.NotNull(b.SemanticIndex);
    Assert.NotNull(b.UniqueName);
    Assert.NotNull(b.Expiry);
  }

  [Fact]
  public void First_parseable_item_wins_per_kind()
  {
    var b = TypeBehaviors.Parse(/*lang=json,strict*/
      """[{"behavior":"UniqueName","config":{"field":"first"}},{"behavior":"UniqueName","config":{"field":"second"}}]""");
    Assert.Equal("first", b.UniqueName!.Field);
  }
}
```

- [ ] Run `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter TypeBehaviorsTests` — confirm it fails (compile error).
- [ ] Create `src/toimi.tools.tietue/Behaviors/TypeBehaviors.cs`:

```csharp
using System.Text.Json;

namespace toimi.tools.tietue.Behaviors;

/// <summary>
/// A type's Behaviors JSON parsed once into typed configs. Unknown behaviors are
/// ignored; malformed JSON yields <see cref="None"/>; per kind the first parseable
/// item wins (an item with an unusable config is skipped, so a later valid one applies).
/// </summary>
public sealed record TypeBehaviors(
  SemanticIndexConfig? SemanticIndex,
  UniqueNameConfig? UniqueName,
  ExpiryConfig? Expiry)
{
  public static readonly TypeBehaviors None = new(null, null, null);

  public static TypeBehaviors Parse(string? behaviorsJson)
  {
    if (string.IsNullOrWhiteSpace(behaviorsJson))
    {
      return None;
    }

    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(behaviorsJson);
    }
    catch (JsonException)
    {
      return None;
    }

    using (doc)
    {
      if (doc.RootElement.ValueKind != JsonValueKind.Array)
      {
        return None;
      }

      SemanticIndexConfig? semantic = null;
      UniqueNameConfig? unique = null;
      ExpiryConfig? expiry = null;

      foreach (var item in doc.RootElement.EnumerateArray())
      {
        if (!item.TryGetProperty("behavior", out var kind) || kind.ValueKind != JsonValueKind.String)
        {
          continue;
        }

        switch (kind.GetString())
        {
          case "SemanticIndex":
            semantic ??= ParseSemanticIndex(item);
            break;
          case "UniqueName":
            unique ??= ParseUniqueName(item);
            break;
          case "Expiry":
            expiry ??= ParseExpiry(item);
            break;
          default:
            break;
        }
      }

      return semantic is null && unique is null && expiry is null
        ? None
        : new TypeBehaviors(semantic, unique, expiry);
    }
  }

  private static SemanticIndexConfig? ParseSemanticIndex(JsonElement item)
  {
    if (!item.TryGetProperty("config", out var config)
      || !config.TryGetProperty("fields", out var fieldsEl)
      || fieldsEl.ValueKind != JsonValueKind.Array)
    {
      return null;
    }

    var fields = fieldsEl.EnumerateArray()
      .Where(f => f.ValueKind == JsonValueKind.String)
      .Select(f => f.GetString()!)
      .ToArray();

    var mode = config.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String
      ? m.GetString()!
      : "whole";

    return new SemanticIndexConfig(fields, mode);
  }

  private static UniqueNameConfig ParseUniqueName(JsonElement item)
  {
    var field = item.TryGetProperty("config", out var config)
      && config.TryGetProperty("field", out var f)
      && f.ValueKind == JsonValueKind.String
        ? f.GetString()!
        : "name";

    return new UniqueNameConfig(field);
  }

  private static ExpiryConfig ParseExpiry(JsonElement item)
  {
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
```

- [ ] Run `dotnet test ... --filter TypeBehaviorsTests` — all 12 green. Then run the full tietue suite — 324 (312 + 12), no regressions.
- [ ] Format both csproj (apply, then `--verify-no-changes`), commit: `refactor(tietue): add TypeBehaviors single-pass behavior parser`

---

## Task 2: Migrate every `BehaviorSpec` caller; delete `BehaviorSpec` (compile-break + suite harness — no new behavior, the existing 312-test suite is the safety net; state each edit, build, run suite)

**Files**
- Modify: `src/toimi.tools.tietue/Semantic/SemanticOutbox.cs`, `src/toimi.tools.tietue/Semantic/SemanticReconciler.cs`, `src/toimi.tools.tietue/Behaviors/BehaviorDispatcher.cs`, `src/toimi.tools.tietue/Provisioning/ExpiryReconciler.cs`, `src/toimi.tools.tietue/Entities/EntityRepository.cs`, `src/toimi.tools.tietue/Behaviors/TypeBehaviors.cs`
- Delete: `src/toimi.tools.tietue/Behaviors/BehaviorSpec.cs`, `src/toimi.tools.tietue.Tests/BehaviorSpecTests.cs`

**Interfaces (after this task)**
- `public IndexOutbox Enqueue(Entity entity, string op)` — non-null return; behavior-presence gating moves to callers (finding 4, first half).
- `public async Task ReconcileAsync(Entity entity, ExpiryConfig? cfg, DateTimeOffset now, CancellationToken ct = default)` on `ExpiryReconciler`.
- `private async Task EnforceUniqueOnCreateAsync(Entity entity, UniqueNameConfig? cfg, CancellationToken ct)` and `private async Task EnforceUniqueOnUpdateAsync(Entity entity, JsonDocument newData, UniqueNameConfig? cfg, CancellationToken ct)` on `EntityRepository`.

**Steps**

- [ ] Move the three config records: add to the top of `Behaviors/TypeBehaviors.cs` (below the `namespace` line, above the `TypeBehaviors` record):

```csharp
public record SemanticIndexConfig(string[] Fields, string Mode);

public record UniqueNameConfig(string Field);

public record ExpiryConfig(string Field, string? Prompt);
```

- [ ] Delete `src/toimi.tools.tietue/Behaviors/BehaviorSpec.cs` and `src/toimi.tools.tietue.Tests/BehaviorSpecTests.cs` entirely.
- [ ] `Semantic/SemanticOutbox.cs` — replace `Enqueue` (the gate leaves; the caller now decides):

```csharp
  /// <summary>Adds an outbox row to the current change set. Caller's SaveChanges commits it with the entity.</summary>
  public IndexOutbox Enqueue(Entity entity, string op)
  {
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
```

  and in `ProcessAsync` replace `var cfg = BehaviorSpec.SemanticIndexOf(typeDef?.Behaviors);` with `var cfg = TypeBehaviors.Parse(typeDef?.Behaviors).SemanticIndex;`.
- [ ] `Semantic/SemanticReconciler.cs:19` — replace `BehaviorSpec.SemanticIndexOf(typeDef?.Behaviors) is null` with `TypeBehaviors.Parse(typeDef?.Behaviors).SemanticIndex is null`.
- [ ] `Behaviors/BehaviorDispatcher.cs:35` — replace `BehaviorSpec.SemanticIndexOf(typeDef.Behaviors)` with `TypeBehaviors.Parse(typeDef.Behaviors).SemanticIndex`.
- [ ] `Provisioning/ExpiryReconciler.cs` — change the signature to `public async Task ReconcileAsync(Entity entity, ExpiryConfig? cfg, DateTimeOffset now, CancellationToken ct = default)` and delete the line `var cfg = BehaviorSpec.ExpiryOf(behaviorsJson);` (the null-check `if (cfg is null) { return; }` stays — it must run AFTER the stale-trigger removal, exactly as today).
- [ ] `Entities/EntityRepository.cs` — parse once per operation and pass configs (interim state; Task 4 replaces the collaborators themselves):
  - `CreateAsync`: after loading `typeDef`, add `var parsed = TypeBehaviors.Parse(typeDef.Behaviors);`. Replace the unique call with `await EnforceUniqueOnCreateAsync(entity, parsed.UniqueName, ct);`, the enqueue with `indexOp = parsed.SemanticIndex is not null ? outbox?.Enqueue(entity, "upsert") : null;`, and the expiry call with `await expiry.ReconcileAsync(entity, parsed.Expiry, entity.CreatedAt, ct);`.
  - `UpdateAsync`: replace `string? behaviorsForExpiry = null;` with `ExpiryConfig? expiryCfg = null;`. Inside the `data is not null` block: `var parsed = TypeBehaviors.Parse(typeDef.Behaviors);`, then `await EnforceUniqueOnUpdateAsync(entity, newData, parsed.UniqueName, ct);`, `expiryCfg = parsed.Expiry;`, `indexOp = parsed.SemanticIndex is not null ? outbox?.Enqueue(entity, "upsert") : null;`. The expiry call becomes `await expiry.ReconcileAsync(entity, expiryCfg, entity.UpdatedAt, ct);`.
  - `DeleteAsync`: replace the enqueue line with `var indexOp = TypeBehaviors.Parse(typeDef?.Behaviors).SemanticIndex is not null ? outbox?.Enqueue(entity, "delete") : null;`.
  - `EnforceUniqueOnCreateAsync` / `EnforceUniqueOnUpdateAsync`: parameter `string? behaviorsJson` → `UniqueNameConfig? cfg`; delete their `var cfg = BehaviorSpec.UniqueNameOf(behaviorsJson);` first lines (the `if (cfg is null) return;` guards stay).
- [ ] Build; fix any leftover `using` for the removed class (IDE0005 will flag). Run the full tietue suite: 314 tests (324 − 10 deleted `BehaviorSpecTests`), all green — `UniqueNameTests`, `ExpiryReconcilerTests`, `SemanticOutboxTests`, `ReconcileTests`, `SearchToolTests` are the harness proving the migration preserved semantics.
- [ ] Format both csproj (apply + verify), commit: `refactor(tietue): migrate all behavior-config callers to TypeBehaviors, delete BehaviorSpec`

---

## Task 3: `IEntityBehavior`, `BehaviorContext`, and the three adapters (TDD)

**Files**
- Create: `src/toimi.tools.tietue/Behaviors/IEntityBehavior.cs`, `src/toimi.tools.tietue/Behaviors/SemanticIndexBehavior.cs`, `src/toimi.tools.tietue/Behaviors/TriggerProvisioningBehavior.cs`, `src/toimi.tools.tietue/Behaviors/ExpiryBehavior.cs`
- Test (create): `src/toimi.tools.tietue.Tests/EntityBehaviorTests.cs`

**Interfaces**

```csharp
public enum EntityOperation { Create, Update, Delete }

public sealed class BehaviorContext
{
  public required Entity Entity { get; init; }
  public required EntityOperation Operation { get; init; }
  public required TypeBehaviors Behaviors { get; init; }
  public string? DefaultTriggersJson { get; init; }
  public required DateTimeOffset Now { get; init; }
  public bool DataChanged { get; init; } = true; // false only for a tags-only update
  public Dictionary<string, object?> Items { get; } = [];
}

public interface IEntityBehavior
{
  Task OnSavingAsync(BehaviorContext ctx, CancellationToken ct);   // default no-op
  Task OnSavedAsync(BehaviorContext ctx, CancellationToken ct);    // default no-op
  Task OnCommittedAsync(BehaviorContext ctx, CancellationToken ct); // default no-op
}

public sealed class SemanticIndexBehavior(SemanticOutbox outbox) : IEntityBehavior
public sealed class TriggerProvisioningBehavior(TriggerProvisioner provisioner) : IEntityBehavior
public sealed class ExpiryBehavior(ExpiryReconciler reconciler) : IEntityBehavior
```

**Steps**

- [ ] Write the failing test file `src/toimi.tools.tietue.Tests/EntityBehaviorTests.cs` (red: types don't exist):

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Provisioning;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Semantic;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class EntityBehaviorTests
{
  private static Entity NewEntity(TietueDbContext db, string type = "note", string json = /*lang=json,strict*/ """{"content":"hello"}""")
  {
    var e = new Entity
    {
      Id = Guid.NewGuid(),
      Type = type,
      Data = JsonDocument.Parse(json),
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow,
    };
    db.Entities.Add(e);
    return e;
  }

  private static BehaviorContext NewContext(Entity e, TypeBehaviors behaviors, EntityOperation op = EntityOperation.Create, string? defaultTriggers = null, bool dataChanged = true)
  {
    return new BehaviorContext
    {
      Entity = e,
      Operation = op,
      Behaviors = behaviors,
      DefaultTriggersJson = defaultTriggers,
      Now = e.CreatedAt,
      DataChanged = dataChanged,
    };
  }

  private const string SemanticBehaviors = /*lang=json,strict*/ """[{"behavior":"SemanticIndex","config":{"fields":["content"]}}]""";

  [Fact]
  public async Task Semantic_behavior_enqueues_on_saving_and_drains_on_committed()
  {
    using var db = TestDb.New();
    await new Types.TypeRepository(db).DefineAsync("note", /*lang=json,strict*/ """{"type":"object"}""", SemanticBehaviors);
    var idx = new FakeSemanticIndex();
    var behavior = new SemanticIndexBehavior(new SemanticOutbox(db, idx));
    var e = NewEntity(db);
    var ctx = NewContext(e, TypeBehaviors.Parse(SemanticBehaviors));

    await behavior.OnSavingAsync(ctx, default);
    await db.SaveChangesAsync();
    Assert.Single(await db.IndexOutbox.ToListAsync()); // row rode the entity's save

    await behavior.OnCommittedAsync(ctx, default);
    Assert.Equal("hello", idx.Store["note"][e.Id]);
    Assert.Empty(await db.IndexOutbox.ToListAsync()); // drained
  }

  [Fact]
  public async Task Semantic_behavior_skips_unindexed_types_and_tags_only_updates()
  {
    using var db = TestDb.New();
    var idx = new FakeSemanticIndex();
    var behavior = new SemanticIndexBehavior(new SemanticOutbox(db, idx));
    var e = NewEntity(db);

    await behavior.OnSavingAsync(NewContext(e, TypeBehaviors.None), default);
    await behavior.OnSavingAsync(NewContext(e, TypeBehaviors.Parse(SemanticBehaviors), EntityOperation.Update, dataChanged: false), default);
    await db.SaveChangesAsync();

    Assert.Empty(await db.IndexOutbox.ToListAsync());
  }

  [Fact]
  public async Task Semantic_behavior_enqueues_delete_op_on_delete()
  {
    using var db = TestDb.New();
    var idx = new FakeSemanticIndex();
    var behavior = new SemanticIndexBehavior(new SemanticOutbox(db, idx));
    var e = NewEntity(db);
    var ctx = NewContext(e, TypeBehaviors.Parse(SemanticBehaviors), EntityOperation.Delete);

    await behavior.OnSavingAsync(ctx, default);
    await db.SaveChangesAsync();

    Assert.Equal("delete", (await db.IndexOutbox.SingleAsync()).Op);
  }

  [Fact]
  public async Task Provisioning_behavior_provisions_on_create_only()
  {
    using var db = TestDb.New();
    var behavior = new TriggerProvisioningBehavior(new TriggerProvisioner(new TriggerRepository(db, TestConfig.Default)));
    var e = NewEntity(db, json: /*lang=json,strict*/ """{"dueAt":"2026-09-01T09:00:00Z"}""");
    await db.SaveChangesAsync();
    const string defaults = /*lang=json,strict*/ """[{"when":{"atField":"dueAt"},"handler":{"kind":"notify"}}]""";

    await behavior.OnSavedAsync(NewContext(e, TypeBehaviors.None, EntityOperation.Update, defaults), default);
    Assert.Empty(await db.Triggers.ToListAsync());

    await behavior.OnSavedAsync(NewContext(e, TypeBehaviors.None, EntityOperation.Create, defaults), default);
    Assert.Equal("notify", (await db.Triggers.SingleAsync()).HandlerKind);
  }

  [Fact]
  public async Task Expiry_behavior_arms_on_create_and_disarms_when_config_absent()
  {
    using var db = TestDb.New();
    var behavior = new ExpiryBehavior(new ExpiryReconciler(db, new TriggerRepository(db, TestConfig.Default)));
    var e = NewEntity(db, json: /*lang=json,strict*/ """{"expiresAt":"2026-09-01T00:00:00Z"}""");
    await db.SaveChangesAsync();
    var withExpiry = TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"Expiry","config":{"field":"expiresAt"}}]""");

    await behavior.OnSavedAsync(NewContext(e, withExpiry), default);
    Assert.Equal("delete", (await db.Triggers.SingleAsync(t => t.Source == "expiry")).HandlerKind);

    // Behavior removed from the type: reconcile must still run and remove the stale trigger.
    await behavior.OnSavedAsync(NewContext(e, TypeBehaviors.None, EntityOperation.Update), default);
    Assert.Empty(await db.Triggers.Where(t => t.Source == "expiry").ToListAsync());
  }

  [Fact]
  public async Task Expiry_behavior_skips_delete_and_tags_only_update()
  {
    using var db = TestDb.New();
    var behavior = new ExpiryBehavior(new ExpiryReconciler(db, new TriggerRepository(db, TestConfig.Default)));
    var e = NewEntity(db, json: /*lang=json,strict*/ """{"expiresAt":"2026-09-01T00:00:00Z"}""");
    await db.SaveChangesAsync();
    var withExpiry = TypeBehaviors.Parse(/*lang=json,strict*/ """[{"behavior":"Expiry","config":{"field":"expiresAt"}}]""");

    await behavior.OnSavedAsync(NewContext(e, withExpiry, EntityOperation.Delete), default);
    await behavior.OnSavedAsync(NewContext(e, withExpiry, EntityOperation.Update, dataChanged: false), default);

    Assert.Empty(await db.Triggers.ToListAsync());
  }
}
```

- [ ] Run `--filter EntityBehaviorTests` — confirm compile failure (red).
- [ ] Create `src/toimi.tools.tietue/Behaviors/IEntityBehavior.cs`:

```csharp
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Behaviors;

public enum EntityOperation
{
  Create,
  Update,
  Delete,
}

/// <summary>
/// Per-operation state handed to every behavior hook. One instance per repository
/// operation; <see cref="Items"/> carries behavior-private state between hooks
/// (e.g. the semantic outbox row from OnSaving to OnCommitted) — behaviors are
/// DI-scoped and a scope runs many sequential operations, so instance fields
/// would leak state across operations.
/// </summary>
public sealed class BehaviorContext
{
  public required Entity Entity { get; init; }
  public required EntityOperation Operation { get; init; }
  public required TypeBehaviors Behaviors { get; init; }
  public string? DefaultTriggersJson { get; init; }
  public required DateTimeOffset Now { get; init; }

  /// <summary>False only for a tags-only update; behaviors that react to Data skip those.</summary>
  public bool DataChanged { get; init; } = true;

  public Dictionary<string, object?> Items { get; } = [];
}

/// <summary>
/// A first-class per-type behavior. Hooks bracket EntityRepository's SaveChanges:
/// OnSaving joins the pending change set (same SaveChanges, atomic with the entity);
/// OnSaved runs after the save — on create still inside the ambient transaction, so
/// its own saves commit or roll back with the entity; OnCommitted runs after the
/// transaction is committed and disposed, and must not fail the operation.
/// </summary>
public interface IEntityBehavior
{
  Task OnSavingAsync(BehaviorContext ctx, CancellationToken ct)
  {
    return Task.CompletedTask;
  }

  Task OnSavedAsync(BehaviorContext ctx, CancellationToken ct)
  {
    return Task.CompletedTask;
  }

  Task OnCommittedAsync(BehaviorContext ctx, CancellationToken ct)
  {
    return Task.CompletedTask;
  }
}
```

- [ ] Create `src/toimi.tools.tietue/Behaviors/SemanticIndexBehavior.cs`:

```csharp
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Semantic;

namespace toimi.tools.tietue.Behaviors;

/// <summary>
/// Wraps SemanticOutbox's enqueue/drain pair: the row is enqueued into the entity's
/// change set (durable with the mutation) and drained only after commit, so a Qdrant
/// hiccup can never roll back — or be rolled back by — the entity write.
/// </summary>
public sealed class SemanticIndexBehavior(SemanticOutbox outbox) : IEntityBehavior
{
  private const string PendingRowKey = nameof(SemanticIndexBehavior);

  public Task OnSavingAsync(BehaviorContext ctx, CancellationToken ct)
  {
    if (ctx.Behaviors.SemanticIndex is null || !ctx.DataChanged)
    {
      return Task.CompletedTask;
    }

    var op = ctx.Operation == EntityOperation.Delete ? "delete" : "upsert";
    ctx.Items[PendingRowKey] = outbox.Enqueue(ctx.Entity, op);
    return Task.CompletedTask;
  }

  public async Task OnCommittedAsync(BehaviorContext ctx, CancellationToken ct)
  {
    if (ctx.Items.TryGetValue(PendingRowKey, out var row))
    {
      await outbox.DrainAsync((IndexOutbox?)row, ct);
    }
  }
}
```

- [ ] Create `src/toimi.tools.tietue/Behaviors/TriggerProvisioningBehavior.cs`:

```csharp
using toimi.tools.tietue.Provisioning;

namespace toimi.tools.tietue.Behaviors;

/// <summary>Copy-down default triggers: stamps the type's DefaultTriggers onto each new entity. Create-time only by design.</summary>
public sealed class TriggerProvisioningBehavior(TriggerProvisioner provisioner) : IEntityBehavior
{
  public async Task OnSavedAsync(BehaviorContext ctx, CancellationToken ct)
  {
    if (ctx.Operation == EntityOperation.Create)
    {
      await provisioner.ProvisionAsync(ctx.Entity, ctx.DefaultTriggersJson, ctx.Now, ct);
    }
  }
}
```

- [ ] Create `src/toimi.tools.tietue/Behaviors/ExpiryBehavior.cs`:

```csharp
using toimi.tools.tietue.Provisioning;

namespace toimi.tools.tietue.Behaviors;

/// <summary>
/// Re-arms the expiry trigger whenever Data changes. Runs even when the type no
/// longer has an Expiry config — the reconciler's first act removes stale expiry
/// triggers, which is how removing the behavior (or the field) disarms expiry.
/// </summary>
public sealed class ExpiryBehavior(ExpiryReconciler reconciler) : IEntityBehavior
{
  public async Task OnSavedAsync(BehaviorContext ctx, CancellationToken ct)
  {
    if (ctx.Operation == EntityOperation.Delete || !ctx.DataChanged)
    {
      return;
    }

    await reconciler.ReconcileAsync(ctx.Entity, ctx.Behaviors.Expiry, ctx.Now, ct);
  }
}
```

- [ ] Run `--filter EntityBehaviorTests` — 6 green. Full suite: 320, green (nothing else references the new types yet).
- [ ] Format both csproj (apply + verify), commit: `feat(tietue): IEntityBehavior hooks with semantic/provisioning/expiry adapters`

---

## Task 4: `EntityRepository` runs the pipeline; DI + test ctor adaptations (compile-break + suite harness — the full 320-test suite, especially `SemanticOutboxTests`, `ExpiryReconcilerTests`, `UniqueNameTests`, `EntityRepositoryFailureTests`, `JobEndToEndTests`, is the safety net)

**Files**
- Modify: `src/toimi.tools.tietue/Entities/EntityRepository.cs`, `src/toimi.tools.tietue/Program.cs`
- Modify (mechanical ctor edits only): `src/toimi.tools.tietue.Tests/SemanticOutboxTests.cs`, `EntityRepositoryIndexingTests.cs`, `SearchToolTests.cs`, `EntityRepositoryTests.cs`, `JobEndToEndTests.cs`, `EntityRepositoryFailureTests.cs`, `ExpiryReconcilerTests.cs`

**Interfaces**
- `public class EntityRepository(TietueDbContext db, SchemaValidator validator, IEnumerable<IEntityBehavior>? behaviors = null)` — the three optional collaborators are gone. All public method signatures (`CreateAsync`, `GetAsync`, `UpdateAsync`, `DeleteAsync`, `ListAsync`) unchanged.

**Steps**

- [ ] Rewrite `Entities/EntityRepository.cs`'s ctor, `CreateAsync`, `UpdateAsync`, and `DeleteAsync` (everything from `ListAsync` down — `GetTypeDefOrThrowAsync`, `KeyValue`, the unique enforcement, `SaveGuardingUniqueAsync`, `ResetPendingChanges`, `DuplicateError`, `NormalizeTags`, `Validate` — is unchanged from Task 2's state). Drop the now-unused `using toimi.tools.tietue.Provisioning;` and `using toimi.tools.tietue.Semantic;`:

```csharp
public class EntityRepository(TietueDbContext db, SchemaValidator validator, IEnumerable<IEntityBehavior>? behaviors = null)
{
  private readonly IReadOnlyList<IEntityBehavior> pipeline = [.. behaviors ?? []];

  public async Task<Entity> CreateAsync(string type, JsonNode? data, string[] tags, CancellationToken ct = default)
  {
    var typeDef = await db.TypeDefinitions.FirstOrDefaultAsync(t => t.Name == type, ct)
      ?? throw new TietueValidationException([$"Unknown type '{type}'. Define it first with define_type."]);
    Validate(typeDef.JsonSchema.RootElement.GetRawText(), data);

    var now = DateTimeOffset.UtcNow;
    var entity = new Entity
    {
      Id = Guid.NewGuid(),
      Type = type,
      Data = JsonSerializer.SerializeToDocument(data),
      Tags = NormalizeTags(tags),
      CreatedAt = now,
      UpdatedAt = now,
    };
    var ctx = new BehaviorContext
    {
      Entity = entity,
      Operation = EntityOperation.Create,
      Behaviors = TypeBehaviors.Parse(typeDef.Behaviors),
      DefaultTriggersJson = typeDef.DefaultTriggers,
      Now = now,
    };

    // Entity, unique-key, and every OnSaving/OnSaved behavior effect (outbox row, default
    // triggers, expiry trigger) must land together: a crash between the entity save and
    // OnSaved would otherwise leave a reminder with no trigger (never fires) or a
    // half-created entity a retry duplicates. Behaviors' own SaveChanges enlist in this
    // ambient transaction (they share this DbContext connection), so they commit or roll
    // back with the entity. InMemory can't begin a transaction, so guard on the relational
    // provider — the call sequence is identical.
    var useTx = db.Database.IsRelational();
    var tx = useTx ? await db.Database.BeginTransactionAsync(ct) : null;
    try
    {
      await EnforceUniqueOnCreateAsync(entity, ctx.Behaviors.UniqueName, ct);
      db.Entities.Add(entity);
      foreach (var behavior in pipeline)
      {
        await behavior.OnSavingAsync(ctx, ct);
      }

      await SaveGuardingUniqueAsync(entity.Type, ct);
      foreach (var behavior in pipeline)
      {
        await behavior.OnSavedAsync(ctx, ct);
      }

      if (tx is not null)
      {
        await tx.CommitAsync(ct);
      }
    }
    catch
    {
      if (tx is not null)
      {
        await tx.RollbackAsync(ct);
      }

      throw;
    }
    finally
    {
      if (tx is not null)
      {
        await tx.DisposeAsync();
      }
    }

    // OnCommitted runs AFTER the transaction is committed and disposed: the outbox row is
    // already durable, and a post-commit hiccup (e.g. Qdrant) must not roll back the entity
    // or trigger a rollback-after-commit.
    await RunCommittedAsync(ctx, ct);
    return entity;
  }

  public Task<Entity?> GetAsync(Guid id, CancellationToken ct = default)
  {
    return db.Entities.FirstOrDefaultAsync(e => e.Id == id, ct);
  }

  public async Task<Entity?> UpdateAsync(Guid id, JsonNode? data, string[]? tags, CancellationToken ct = default)
  {
    var entity = await db.Entities.FirstOrDefaultAsync(e => e.Id == id, ct);
    if (entity is null)
    {
      return null;
    }

    TypeDefinition? typeDef = null;
    var parsed = TypeBehaviors.None;
    if (data is not null)
    {
      typeDef = await GetTypeDefOrThrowAsync(entity.Type, ct);
      parsed = TypeBehaviors.Parse(typeDef.Behaviors);
      Validate(typeDef.JsonSchema.RootElement.GetRawText(), data);
      var newData = JsonSerializer.SerializeToDocument(data);
      await EnforceUniqueOnUpdateAsync(entity, newData, parsed.UniqueName, ct);
      // Mutate only after all pre-checks: a caught validation failure inside a scheduler
      // tick must not leave half-applied tracked state for the tick's later saves to flush.
      // The previous JsonDocument is intentionally NOT disposed — the change tracker's
      // original-values snapshot still references it (see ResetPendingChanges).
      entity.Data = newData;
    }

    if (tags is not null)
    {
      entity.Tags = NormalizeTags(tags);
    }

    entity.UpdatedAt = DateTimeOffset.UtcNow;
    var ctx = new BehaviorContext
    {
      Entity = entity,
      Operation = EntityOperation.Update,
      Behaviors = parsed,
      DefaultTriggersJson = typeDef?.DefaultTriggers,
      Now = entity.UpdatedAt,
      DataChanged = data is not null,
    };
    foreach (var behavior in pipeline)
    {
      await behavior.OnSavingAsync(ctx, ct);
    }

    await SaveGuardingUniqueAsync(entity.Type, ct);
    foreach (var behavior in pipeline)
    {
      await behavior.OnSavedAsync(ctx, ct);
    }

    await RunCommittedAsync(ctx, ct);
    return entity;
  }

  public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
  {
    var entity = await db.Entities.FirstOrDefaultAsync(e => e.Id == id, ct);
    if (entity is null)
    {
      return false;
    }

    var keys = await db.UniqueKeys.Where(k => k.EntityId == id).ToListAsync(ct);
    db.UniqueKeys.RemoveRange(keys);
    var typeDef = await db.TypeDefinitions.AsNoTracking().FirstOrDefaultAsync(t => t.Name == entity.Type, ct);
    var ctx = new BehaviorContext
    {
      Entity = entity,
      Operation = EntityOperation.Delete,
      Behaviors = TypeBehaviors.Parse(typeDef?.Behaviors),
      DefaultTriggersJson = typeDef?.DefaultTriggers,
      Now = DateTimeOffset.UtcNow,
    };
    db.Entities.Remove(entity);
    foreach (var behavior in pipeline)
    {
      await behavior.OnSavingAsync(ctx, ct);
    }

    await db.SaveChangesAsync(ct);
    foreach (var behavior in pipeline)
    {
      await behavior.OnSavedAsync(ctx, ct);
    }

    await RunCommittedAsync(ctx, ct);
    return true;
  }

  private async Task RunCommittedAsync(BehaviorContext ctx, CancellationToken ct)
  {
    foreach (var behavior in pipeline)
    {
      await behavior.OnCommittedAsync(ctx, ct);
    }
  }
```

  Ordering invariants preserved from today's code: unique pre-check before `db.Entities.Add` on create; pre-check before mutation on update; `OnSaving` (outbox enqueue) inside the change set; `OnSaved` (provision, expiry) after the guarded save, inside the create transaction; `OnCommitted` (drain) after `finally`. A throwing `OnSaving`/`OnSaved` on create rolls back and skips `OnCommitted` — same as today's `indexOp` never draining.
- [ ] `Program.cs` — register the pipeline (after the `SemanticOutbox` registration at line 42; order here IS pipeline order):

```csharp
builder.Services.AddScoped<toimi.tools.tietue.Behaviors.IEntityBehavior, toimi.tools.tietue.Behaviors.SemanticIndexBehavior>();
builder.Services.AddScoped<toimi.tools.tietue.Behaviors.IEntityBehavior, toimi.tools.tietue.Behaviors.TriggerProvisioningBehavior>();
builder.Services.AddScoped<toimi.tools.tietue.Behaviors.IEntityBehavior, toimi.tools.tietue.Behaviors.ExpiryBehavior>();
```

  Keep the existing `TriggerProvisioner`, `ExpiryReconciler`, and `SemanticOutbox` registrations — the adapters depend on them.
- [ ] Mechanical test ctor edits (add `using toimi.tools.tietue.Behaviors;` where missing):
  - `SemanticOutboxTests.cs:66`: `new EntityRepository(db, new SchemaValidator(), outbox)` → `new EntityRepository(db, new SchemaValidator(), [new SemanticIndexBehavior(outbox)])`
  - `SemanticOutboxTests.cs:120`: `new EntityRepository(db, new SchemaValidator(), new SemanticOutbox(db, index))` → `new EntityRepository(db, new SchemaValidator(), [new SemanticIndexBehavior(new SemanticOutbox(db, index))])`
  - `EntityRepositoryIndexingTests.cs:19` and `SearchToolTests.cs:25`: `..., new SemanticOutbox(db, idx))` → `..., [new SemanticIndexBehavior(new SemanticOutbox(db, idx))])`
  - `EntityRepositoryTests.cs:115`: `..., provisioner: new TriggerProvisioner(new TriggerRepository(db, TestConfig.Default)))` → `..., [new TriggerProvisioningBehavior(new TriggerProvisioner(new TriggerRepository(db, TestConfig.Default)))])`
  - `JobEndToEndTests.cs:35`: `..., provisioner: new TriggerProvisioner(triggers))` → `..., [new TriggerProvisioningBehavior(new TriggerProvisioner(triggers))])`
  - `EntityRepositoryFailureTests.cs:84`: `..., provisioner: provisioner)` → `..., [new TriggerProvisioningBehavior(provisioner)])`
  - `ExpiryReconcilerTests.cs:21`: `..., expiry: reconciler)` → `..., [new ExpiryBehavior(reconciler)])`
- [ ] Build; run the FULL tietue suite: 320 tests green. Pay attention to `SemanticOutboxTests.Tags_only_update_enqueues_nothing` (DataChanged gate), `Failed_create_validation_persists_no_outbox_row` (pipeline never reached), `ExpiryReconcilerTests.Update_removing_field_drops_the_trigger` (reconcile-with-null-config path), and `EntityRepositoryFailureTests` (unchanged inline unique + ResetPendingChanges).
- [ ] Format both csproj (apply + verify), commit: `refactor(tietue): EntityRepository behavior pipeline replaces optional collaborators`

---

## Task 5: New coverage — all three behaviors together + Postgres create atomicity

**Files**
- Test (create): `src/toimi.tools.tietue.Tests/BehaviorPipelineTests.cs`
- Test (create): `src/toimi.tools.tietue.Tests/EntityRepositoryPostgresTests.cs`

**Interfaces** — none new; consumes Task 3/4 output plus existing `FakeSemanticIndex`, `TestDb`, `TestConfig`, `DockerFactAttribute`, `Testcontainers.PostgreSql`.

**Steps**

- [ ] Create `src/toimi.tools.tietue.Tests/BehaviorPipelineTests.cs` (closes the "no test constructs all three collaborators together" gap on the InMemory provider, plus pins hook ordering and the failure path):

```csharp
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Provisioning;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Semantic;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class BehaviorPipelineTests
{
  private const string Schema = /*lang=json,strict*/
    """{"type":"object","properties":{"name":{"type":"string"},"content":{"type":"string"},"expiresAt":{"type":"string"},"dueAt":{"type":"string"}},"required":["name"]}""";
  private const string AllThreeBehaviors = /*lang=json,strict*/
    """[{"behavior":"SemanticIndex","config":{"fields":["content"]}},{"behavior":"UniqueName","config":{"field":"name"}},{"behavior":"Expiry","config":{"field":"expiresAt"}}]""";
  private const string DefaultTriggers = /*lang=json,strict*/
    """[{"when":{"atField":"dueAt"},"handler":{"kind":"notify"}}]""";

  private static async Task<(Data.TietueDbContext db, EntityRepository repo, FakeSemanticIndex idx)> SetupAsync()
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("wish", Schema, AllThreeBehaviors, DefaultTriggers);
    var idx = new FakeSemanticIndex();
    var triggers = new TriggerRepository(db, TestConfig.Default);
    var repo = new EntityRepository(db, new SchemaValidator(),
    [
      new SemanticIndexBehavior(new SemanticOutbox(db, idx)),
      new TriggerProvisioningBehavior(new TriggerProvisioner(triggers)),
      new ExpiryBehavior(new ExpiryReconciler(db, triggers)),
    ]);
    return (db, repo, idx);
  }

  [Fact]
  public async Task Create_runs_all_three_behaviors_and_unique_enforcement_together()
  {
    var (db, repo, idx) = await SetupAsync();
    using var _ = db;

    var e = await repo.CreateAsync("wish",
      JsonNode.Parse("""{"name":"n1","content":"a red bike","expiresAt":"2026-12-01T00:00:00Z","dueAt":"2026-09-01T09:00:00Z"}"""), []);

    Assert.Equal("a red bike", idx.Store["wish"][e.Id]);                       // SemanticIndex
    Assert.Empty(await db.IndexOutbox.ToListAsync());                          // drained post-commit
    Assert.Single(await db.UniqueKeys.Where(k => k.EntityId == e.Id).ToListAsync()); // UniqueName
    var kinds = (await db.Triggers.Where(t => t.EntityId == e.Id).ToListAsync())
      .Select(t => (t.HandlerKind, t.Source)).ToHashSet();
    Assert.Contains(("notify", (string?)null), kinds);                         // TriggerProvisioning
    Assert.Contains(("delete", (string?)"expiry"), kinds);                     // Expiry

    await Assert.ThrowsAsync<TietueValidationException>(() =>                  // UniqueName coexists
      repo.CreateAsync("wish", JsonNode.Parse("""{"name":"n1"}"""), []));
  }

  private sealed class RecordingBehavior(List<string> log, Data.TietueDbContext db) : IEntityBehavior
  {
    public async Task OnSavingAsync(BehaviorContext ctx, CancellationToken ct)
    {
      using var fresh = TestDb.SameStore(db);
      log.Add($"OnSaving(saved:{await fresh.Entities.AnyAsync(e => e.Id == ctx.Entity.Id, ct)})");
    }

    public async Task OnSavedAsync(BehaviorContext ctx, CancellationToken ct)
    {
      using var fresh = TestDb.SameStore(db);
      log.Add($"OnSaved(saved:{await fresh.Entities.AnyAsync(e => e.Id == ctx.Entity.Id, ct)})");
    }

    public Task OnCommittedAsync(BehaviorContext ctx, CancellationToken ct)
    {
      log.Add("OnCommitted");
      return Task.CompletedTask;
    }
  }

  private sealed class ThrowingOnSavedBehavior : IEntityBehavior
  {
    public Task OnSavedAsync(BehaviorContext ctx, CancellationToken ct)
    {
      throw new InvalidOperationException("simulated provisioning failure");
    }
  }

  [Fact]
  public async Task Hooks_run_saving_saved_committed_around_the_save()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("note", /*lang=json,strict*/ """{"type":"object"}""");
    var log = new List<string>();
    var repo = new EntityRepository(db, new SchemaValidator(), [new RecordingBehavior(log, db)]);

    await repo.CreateAsync("note", JsonNode.Parse("{}"), []);

    Assert.Equal(["OnSaving(saved:False)", "OnSaved(saved:True)", "OnCommitted"], log);
  }

  [Fact]
  public async Task Failing_OnSaved_propagates_and_skips_OnCommitted()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("note", /*lang=json,strict*/ """{"type":"object"}""");
    var log = new List<string>();
    var repo = new EntityRepository(db, new SchemaValidator(),
      [new RecordingBehavior(log, db), new ThrowingOnSavedBehavior()]);

    await Assert.ThrowsAsync<InvalidOperationException>(() =>
      repo.CreateAsync("note", JsonNode.Parse("{}"), []));

    Assert.DoesNotContain("OnCommitted", log); // rollback path never reaches post-commit hooks
  }
}
```

- [ ] Run `--filter BehaviorPipelineTests` — 3 green (these pass against Task 4's implementation; their value is pinning the composed pipeline, which no prior test did).
- [ ] Create `src/toimi.tools.tietue.Tests/EntityRepositoryPostgresTests.cs` (closes the "create transaction never runs under InMemory" gap — the 7-line atomicity comment finally has a test):

```csharp
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Provisioning;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

// Per-test container lifecycle, deliberately (see PostgresTickLockTests): a skipped
// [DockerFact] never constructs the class, so on a docker-less machine no container
// start is ever attempted. Do NOT "optimize" this into an IClassFixture.
public class EntityRepositoryPostgresTests : IAsyncLifetime
{
  private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
    .Build();

  public async Task InitializeAsync()
  {
    await _postgres.StartAsync();
    using var db = NewContext();
    await db.Database.MigrateAsync();
  }

  public Task DisposeAsync()
  {
    return _postgres.DisposeAsync().AsTask();
  }

  // Snake-case naming matches prod (Program.cs) and the checked-in migrations.
  private TietueDbContext NewContext()
  {
    return new TietueDbContext(new DbContextOptionsBuilder<TietueDbContext>()
      .UseNpgsql(_postgres.GetConnectionString())
      .UseSnakeCaseNamingConvention()
      .Options);
  }

  private const string Schema = /*lang=json,strict*/
    """{"type":"object","properties":{"name":{"type":"string"},"content":{"type":"string"},"dueAt":{"type":"string"}},"required":["name"]}""";
  private const string Behaviors = /*lang=json,strict*/
    """[{"behavior":"SemanticIndex","config":{"fields":["content"]}},{"behavior":"UniqueName","config":{"field":"name"}}]""";
  private const string DefaultTriggers = /*lang=json,strict*/
    """[{"when":{"atField":"dueAt"},"handler":{"kind":"notify"}}]""";

  private sealed class ThrowingOnSavedBehavior : IEntityBehavior
  {
    public Task OnSavedAsync(BehaviorContext ctx, CancellationToken ct)
    {
      // Stands at the exact pipeline position TriggerProvisioningBehavior occupies:
      // a provisioning failure after the entity save, inside the ambient transaction.
      throw new InvalidOperationException("simulated provisioning failure");
    }
  }

  [DockerFact]
  public async Task Failed_provisioning_stage_rolls_back_entity_unique_key_and_outbox()
  {
    using (var db = NewContext())
    {
      await new TypeRepository(db).DefineAsync("thing", Schema, Behaviors);
      var idx = new FakeSemanticIndex();
      var repo = new EntityRepository(db, new SchemaValidator(),
        [new SemanticIndexBehavior(new Semantic.SemanticOutbox(db, idx)), new ThrowingOnSavedBehavior()]);

      await Assert.ThrowsAsync<InvalidOperationException>(() =>
        repo.CreateAsync("thing", JsonNode.Parse("""{"name":"a","content":"x"}"""), []));
    }

    using var fresh = NewContext();
    Assert.Empty(await fresh.Entities.ToListAsync());   // the atomicity the comment promises
    Assert.Empty(await fresh.UniqueKeys.ToListAsync());
    Assert.Empty(await fresh.IndexOutbox.ToListAsync());
  }

  [DockerFact]
  public async Task Create_commits_entity_unique_key_trigger_and_drained_outbox_together()
  {
    Guid id;
    var idx = new FakeSemanticIndex();
    using (var db = NewContext())
    {
      await new TypeRepository(db).DefineAsync("thing", Schema, Behaviors, DefaultTriggers);
      var triggers = new TriggerRepository(db, TestConfig.Default);
      var repo = new EntityRepository(db, new SchemaValidator(),
      [
        new SemanticIndexBehavior(new Semantic.SemanticOutbox(db, idx)),
        new TriggerProvisioningBehavior(new TriggerProvisioner(triggers)),
      ]);

      var e = await repo.CreateAsync("thing",
        JsonNode.Parse("""{"name":"a","content":"hello","dueAt":"2026-09-01T09:00:00Z"}"""), []);
      id = e.Id;
    }

    using var fresh = NewContext();
    Assert.NotNull(await fresh.Entities.SingleOrDefaultAsync(e => e.Id == id));
    Assert.Single(await fresh.UniqueKeys.Where(k => k.EntityId == id).ToListAsync());
    Assert.Equal("notify", (await fresh.Triggers.SingleAsync(t => t.EntityId == id)).HandlerKind);
    Assert.Empty(await fresh.IndexOutbox.ToListAsync()); // drained after commit
    Assert.Equal("hello", idx.Store["thing"][id]);
  }
}
```

- [ ] Run `--filter EntityRepositoryPostgresTests`. With Docker present: 2 green (first run pulls `postgres:17-alpine`). Without Docker: 2 skips — both outcomes are acceptable; do not mark the task failed on skips.
- [ ] Run the full tietue suite: 325 total (320 + 3 + 2), green (Docker-gated tests skip where Docker is absent).
- [ ] Format both csproj (apply + verify), commit: `test(tietue): behavior-pipeline composition and Postgres create-atomicity coverage`

---

## Task 6: Final gate + CLAUDE.md

**Files**
- Modify: `CLAUDE.md`

**Steps**

- [ ] Full verification:

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet build toimi.sln
dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj
dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj        # 93, untouched
dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj          # 38, untouched
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
```

  tietue must report ≥ 325 total with 0 failures (Docker-gated tests may skip). If core/web test csproj paths differ, locate them with `ls src/*Tests*/*.csproj` and run what exists.
- [ ] `CLAUDE.md` — record the new seam. In the tietue block, edit the "Extend when" bullet:

```
- Extend when: adding a native handler, a declarative behavior (one
  `IEntityBehavior` class + one DI line in Program.cs — parsing lives in
  `TypeBehaviors.Parse`), a seeded type, or an MCP verb over
  entities/triggers. A new *capability* the agent needs is usually a new
  type + handler/behavior here, NOT a new pod.
```

  and in Key Patterns, extend the "Declarative semantic index" bullet's neighborhood by updating the first Key Pattern bullet ("Generic entity engine") — append one sentence:

```
  Per-type behaviors run as an `IEntityBehavior` pipeline inside
  `EntityRepository` (hooks: OnSaving/OnSaved/OnCommitted around the save;
  create is transactional on Postgres).
```

- [ ] Commit: `docs(claude): document IEntityBehavior pipeline seam`
- [ ] Do NOT merge or push; leave the branch for review per the wip-branch workflow.

## Self-review checklist (verified against the code while planning)

- Finding 1 (three copied parsers): Tasks 1–2 — `TypeBehaviors.Parse` single walk, `BehaviorSpec.cs` deleted, all seven caller sites migrated.
- Finding 2 (nullable-collaborator matrix, sprinkled calls): Task 4 — ctor `(db, validator, IEnumerable<IEntityBehavior>?)`, six behavior call sites become three uniform hook loops; UniqueName deliberately retained inline with justification.
- Finding 3 (no all-three test; unverified transaction): Task 5 — `BehaviorPipelineTests` composes all three + unique enforcement; `EntityRepositoryPostgresTests` proves rollback and commit atomicity via Testcontainers + `DockerFact`.
- Finding 4 (outbox two-phase leak): Task 2 narrows `Enqueue`; Task 3's `SemanticIndexBehavior` owns the enqueue/drain pair with per-operation state in `ctx.Items`.
- Hook set derived from real call sites (phase-based, not operation-based) with the outbox pair mapped to OnSaving/OnCommitted; per-test-file disposition table in Design Decisions; signatures consistent across tasks; no placeholders — all code is literal.
