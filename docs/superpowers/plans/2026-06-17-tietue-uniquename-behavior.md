# UniqueName Behavior (side-table) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement `UniqueName` as a real declarative behavior so the engine rejects a second entity of a type that shares a keyed field value (e.g. a `wishlist` keyed on `url`), backed by a dedicated `unique_keys` side table with a DB unique index plus an app-level pre-check.

**Architecture:** A `UniqueKey { Type, Field, Value, EntityId }` row is maintained per uniquely-keyed entity. `EntityRepository` enforces uniqueness on create/update by (a) an explicit pre-check query against `unique_keys` (portable + unit-testable on EF InMemory, which does **not** enforce unique indexes) and (b) a DB unique index on `(type, field, value)` as the production race backstop (Postgres `23505` → `TietueValidationException`). Rows are removed on entity update-away/delete.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, xUnit + EF InMemory. Behavior config mirrors the existing `SemanticIndex` shape parsed in `Behaviors/BehaviorSpec.cs`.

**Conventions reminder:** 2-space indent, file-scoped namespaces, block bodies (IDE0022), conditional expressions (IDE0046), no unused usings (IDE0005). After each task run `dotnet format <csproj> --verify-no-changes` and confirm it exits 0. Build/test only via Docker SDK: `docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 <cmd>`.

---

## File Structure

- **Create** `src/toimi.tools.tietue/Data/UniqueKey.cs` — the side-table entity.
- **Create** `src/toimi.tools.tietue/Data/UniqueKeyConfiguration.cs` — table `unique_keys`, unique index `(type, field, value)`, FK to `entities` (cascade).
- **Modify** `src/toimi.tools.tietue/Data/TietueDbContext.cs` — add `DbSet<UniqueKey>`.
- **Modify** `src/toimi.tools.tietue/Behaviors/BehaviorSpec.cs` — add `UniqueNameConfig` + `UniqueNameOf(...)`.
- **Modify** `src/toimi.tools.tietue/Entities/EntityRepository.cs` — enforce + maintain unique keys on create/update/delete.
- **Create** `src/toimi.tools.tietue/Migrations/<ts>_AddUniqueKeys.cs` (+ Designer + snapshot) — via `dotnet ef`.
- **Modify** `src/toimi.tools.tietue/Seed/TypeSeeder.cs` — add `UniqueName` (field `name`) to the seeded `skill` type.
- **Modify** `src/toimi.tools.tietue.Tests/BehaviorSpecTests.cs` — `UniqueNameOf` tests.
- **Create** `src/toimi.tools.tietue.Tests/UniqueNameTests.cs` — enforcement tests.
- **Modify** `src/toimi.tools.tietue.Tests/TypeSeederTests.cs` — assert `UniqueName` on `skill`.
- **Modify** `CLAUDE.md` — make the behaviors line accurate (UniqueName implemented; Expiry not yet).

---

## Task 1: `UniqueKey` entity, configuration, DbSet, migration

**Files:**
- Create: `src/toimi.tools.tietue/Data/UniqueKey.cs`
- Create: `src/toimi.tools.tietue/Data/UniqueKeyConfiguration.cs`
- Modify: `src/toimi.tools.tietue/Data/TietueDbContext.cs`
- Create: `src/toimi.tools.tietue/Migrations/<ts>_AddUniqueKeys.cs` (generated)
- Test: `src/toimi.tools.tietue.Tests/DbContextTests.cs` (add one round-trip test)

- [ ] **Step 1: Write the failing test** (append to `DbContextTests.cs`, in `DbContextTests`):

```csharp
  [Fact]
  public async Task UniqueKey_round_trips()
  {
    using var db = TestDb.New();
    var entityId = Guid.NewGuid();
    db.Entities.Add(new Entity
    {
      Id = entityId,
      Type = "wishlist",
      Data = JsonDocument.Parse("""{"url":"x"}"""),
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow,
    });
    db.UniqueKeys.Add(new UniqueKey { Type = "wishlist", Field = "url", Value = "x", EntityId = entityId });
    await db.SaveChangesAsync();

    var loaded = await db.UniqueKeys.SingleAsync();
    Assert.Equal("wishlist", loaded.Type);
    Assert.Equal("url", loaded.Field);
    Assert.Equal("x", loaded.Value);
    Assert.Equal(entityId, loaded.EntityId);
  }
```

- [ ] **Step 2: Run it to verify it fails to compile** (`UniqueKey` / `db.UniqueKeys` don't exist).

Run: `docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet build src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`
Expected: compile error (missing `UniqueKey`).

- [ ] **Step 3: Create `Data/UniqueKey.cs`:**

```csharp
namespace toimi.tools.tietue.Data;

public class UniqueKey
{
  public Guid Id { get; set; }
  public required string Type { get; set; }
  public required string Field { get; set; }
  public required string Value { get; set; }
  public Guid EntityId { get; set; }
}
```

- [ ] **Step 4: Create `Data/UniqueKeyConfiguration.cs`:**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.tietue.Data;

public class UniqueKeyConfiguration : IEntityTypeConfiguration<UniqueKey>
{
  public void Configure(EntityTypeBuilder<UniqueKey> builder)
  {
    builder.ToTable("unique_keys");
    builder.HasKey(k => k.Id);
    builder.Property(k => k.Id).HasDefaultValueSql("gen_random_uuid()");
    builder.Property(k => k.Type).IsRequired();
    builder.Property(k => k.Field).IsRequired();
    builder.Property(k => k.Value).IsRequired();
    builder.HasIndex(k => new { k.Type, k.Field, k.Value }).IsUnique();
    builder.HasIndex(k => k.EntityId);
    builder.HasOne<Entity>()
      .WithMany()
      .HasForeignKey(k => k.EntityId)
      .OnDelete(DeleteBehavior.Cascade);
  }
}
```

- [ ] **Step 5: Add the DbSet** to `Data/TietueDbContext.cs` (after the `EntityEvents` line):

```csharp
  public DbSet<UniqueKey> UniqueKeys => Set<UniqueKey>();
```

- [ ] **Step 6: Generate the migration** (the project has a design-time `TietueDbContextFactory`, so no app config is needed):

Run:
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet tool install --global dotnet-ef --version "10.0.*" >/dev/null 2>&1
  export PATH="$PATH:/root/.dotnet/tools"
  dotnet ef migrations add AddUniqueKeys --project src/toimi.tools.tietue'
```
Expected: a new `Migrations/<ts>_AddUniqueKeys.cs` whose `Up` calls `CreateTable("unique_keys", ...)` with a unique index `ix_unique_keys_type_field_value` and an FK to `entities` (cascade), plus an updated `TietueDbContextModelSnapshot.cs`. **Verify** the unique index is present and `unique: true`. If `dotnet ef` is unavailable, hand-write the migration + Designer + snapshot edit following `Migrations/20260615075919_AddTriggersAndEvents.cs` as the pattern (table with `id`/`type`/`field`/`value`/`entity_id`, unique index on `type,field,value`, FK cascade to `entities`).

- [ ] **Step 7: Run the test + lint:**

Run:
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~DbContextTests" 2>&1 | grep -E "Passed!|Failed!|error"
  dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes >/dev/null 2>&1; echo "LINT=$?"'
```
Expected: PASS, `LINT=0`.

- [ ] **Step 8: Commit**

```bash
git add src/toimi.tools.tietue/Data/UniqueKey.cs src/toimi.tools.tietue/Data/UniqueKeyConfiguration.cs src/toimi.tools.tietue/Data/TietueDbContext.cs src/toimi.tools.tietue/Migrations src/toimi.tools.tietue.Tests/DbContextTests.cs
git commit -m "feat(tietue): unique_keys side table for UniqueName behavior"
```

---

## Task 2: `BehaviorSpec.UniqueNameOf`

**Files:**
- Modify: `src/toimi.tools.tietue/Behaviors/BehaviorSpec.cs`
- Test: `src/toimi.tools.tietue.Tests/BehaviorSpecTests.cs`

- [ ] **Step 1: Write failing tests** (append to `BehaviorSpecTests.cs`):

```csharp
  [Fact]
  public void Parses_unique_name_field()
  {
    var cfg = BehaviorSpec.UniqueNameOf(
                           /*lang=json,strict*/
                           """[{"behavior":"UniqueName","config":{"field":"url"}}]""");
    Assert.NotNull(cfg);
    Assert.Equal("url", cfg!.Field);
  }

  [Fact]
  public void Unique_name_defaults_field_to_name()
  {
    var cfg = BehaviorSpec.UniqueNameOf(/*lang=json,strict*/ """[{"behavior":"UniqueName"}]""");
    Assert.Equal("name", cfg!.Field);
  }

  [Fact]
  public void Null_when_no_unique_name_behavior()
  {
    Assert.Null(BehaviorSpec.UniqueNameOf(null));
    Assert.Null(BehaviorSpec.UniqueNameOf("[]"));
    Assert.Null(BehaviorSpec.UniqueNameOf(/*lang=json,strict*/ """[{"behavior":"SemanticIndex","config":{"fields":["a"]}}]"""));
    Assert.Null(BehaviorSpec.UniqueNameOf("{ not json"));
  }
```

- [ ] **Step 2: Run to verify failure** (`UniqueNameOf` missing).

Run: `docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet build src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`
Expected: compile error.

- [ ] **Step 3: Implement.** In `Behaviors/BehaviorSpec.cs`, add the record next to `SemanticIndexConfig`:

```csharp
public record UniqueNameConfig(string Field);
```

and add this method inside `BehaviorSpec` (after `SemanticIndexOf`):

```csharp
  // Returns the UniqueName config from a type's Behaviors JSON, or null if absent/malformed.
  public static UniqueNameConfig? UniqueNameOf(string? behaviorsJson)
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
        if (!item.TryGetProperty("behavior", out var b) || b.GetString() != "UniqueName")
        {
          continue;
        }

        var field = item.TryGetProperty("config", out var config)
          && config.TryGetProperty("field", out var f)
          && f.ValueKind == JsonValueKind.String
            ? f.GetString()!
            : "name";

        return new UniqueNameConfig(field);
      }
    }

    return null;
  }
```

- [ ] **Step 4: Run tests + lint:**

Run:
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~BehaviorSpecTests" 2>&1 | grep -E "Passed!|Failed!|error"
  dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes >/dev/null 2>&1; echo "LINT=$?"'
```
Expected: PASS, `LINT=0`.

- [ ] **Step 5: Commit**

```bash
git add src/toimi.tools.tietue/Behaviors/BehaviorSpec.cs src/toimi.tools.tietue.Tests/BehaviorSpecTests.cs
git commit -m "feat(tietue): parse UniqueName behavior config"
```

---

## Task 3: Enforce uniqueness in `EntityRepository`

**Files:**
- Modify: `src/toimi.tools.tietue/Entities/EntityRepository.cs`
- Test: `src/toimi.tools.tietue.Tests/UniqueNameTests.cs` (create)

**Scene:** `EntityRepository` already loads the `TypeDefinition` in `CreateAsync` (it has `.Behaviors`). `UpdateAsync` currently fetches only the schema via `GetSchemaOrThrowAsync`; refactor it to fetch the whole `TypeDefinition` so behaviors are available. Delete must drop the entity's unique keys explicitly (EF InMemory does not cascade unrelated tables).

- [ ] **Step 1: Write failing tests.** Create `src/toimi.tools.tietue.Tests/UniqueNameTests.cs`:

```csharp
using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class UniqueNameTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"url":{"type":"string"},"title":{"type":"string"}}}""";
  private const string UniqueOnUrl = /*lang=json,strict*/ """[{"behavior":"UniqueName","config":{"field":"url"}}]""";

  private static async Task<EntityRepository> SetupAsync(Data.TietueDbContext db, string? behaviors)
  {
    await new TypeRepository(db).DefineAsync("wishlist", Schema, behaviors);
    return new EntityRepository(db, new SchemaValidator());
  }

  [Fact]
  public async Task Rejects_second_entity_with_same_keyed_value()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, UniqueOnUrl);
    await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a","title":"one"}"""), []);

    await Assert.ThrowsAsync<TietueValidationException>(() =>
      repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a","title":"two"}"""), []));
  }

  [Fact]
  public async Task Allows_distinct_keyed_values()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, UniqueOnUrl);
    await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a"}"""), []);
    var second = await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"b"}"""), []);
    Assert.NotNull(second);
  }

  [Fact]
  public async Task No_constraint_without_unique_name_behavior()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, null);
    await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a"}"""), []);
    var second = await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a"}"""), []);
    Assert.NotNull(second);
  }

  [Fact]
  public async Task Missing_keyed_field_is_not_enforced()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, UniqueOnUrl);
    await repo.CreateAsync("wishlist", JsonNode.Parse("""{"title":"one"}"""), []);
    var second = await repo.CreateAsync("wishlist", JsonNode.Parse("""{"title":"two"}"""), []);
    Assert.NotNull(second);
  }

  [Fact]
  public async Task Update_into_existing_value_is_rejected()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, UniqueOnUrl);
    await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a"}"""), []);
    var b = await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"b"}"""), []);

    await Assert.ThrowsAsync<TietueValidationException>(() =>
      repo.UpdateAsync(b.Id, JsonNode.Parse("""{"url":"a"}"""), null));
  }

  [Fact]
  public async Task Updating_own_value_frees_the_old_one()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, UniqueOnUrl);
    var a = await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a"}"""), []);

    await repo.UpdateAsync(a.Id, JsonNode.Parse("""{"url":"c"}"""), null);

    // "a" is now free to reuse
    var reused = await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a"}"""), []);
    Assert.NotNull(reused);
  }

  [Fact]
  public async Task Delete_frees_the_keyed_value()
  {
    using var db = TestDb.New();
    var repo = await SetupAsync(db, UniqueOnUrl);
    var a = await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a"}"""), []);

    await repo.DeleteAsync(a.Id);

    var recreated = await repo.CreateAsync("wishlist", JsonNode.Parse("""{"url":"a"}"""), []);
    Assert.NotNull(recreated);
  }
}
```

- [ ] **Step 2: Run to verify failure** (enforcement not implemented — `Rejects_*` and `Update_into_*` fail).

Run: `docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~UniqueNameTests"`
Expected: FAIL (2 rejection tests fail; others pass).

- [ ] **Step 3: Implement enforcement in `Entities/EntityRepository.cs`.**

3a. In `CreateAsync`, after `db.Entities.Add(entity);` and **before** `await db.SaveChangesAsync(ct);`, insert:

```csharp
    await EnforceUniqueOnCreateAsync(entity, typeDef.Behaviors, ct);
```

and replace the `await db.SaveChangesAsync(ct);` in `CreateAsync` with the guarded helper:

```csharp
    await SaveGuardingUniqueAsync(entity.Type, ct);
```

3b. Refactor `UpdateAsync` to load the full type definition and enforce. Replace the block:

```csharp
    if (data is not null)
    {
      var schemaJson = await GetSchemaOrThrowAsync(entity.Type, ct);
      Validate(schemaJson, data);
      var previous = entity.Data;
      entity.Data = JsonSerializer.SerializeToDocument(data);
      previous.Dispose();
    }
```

with:

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

and replace `UpdateAsync`'s `await db.SaveChangesAsync(ct);` with:

```csharp
    await SaveGuardingUniqueAsync(entity.Type, ct);
```

3c. In `DeleteAsync`, before `db.Entities.Remove(entity);`, insert:

```csharp
    var keys = await db.UniqueKeys.Where(k => k.EntityId == id).ToListAsync(ct);
    db.UniqueKeys.RemoveRange(keys);
```

3d. Replace the now-unused `GetSchemaOrThrowAsync` with `GetTypeDefOrThrowAsync` and add the helpers. In `CreateAsync` it already uses `typeDef`; keep it. Add these private members (and `using System.Text.Json;` is already present; add `using toimi.tools.tietue.Behaviors;` — already present):

```csharp
  private async Task<TypeDefinition> GetTypeDefOrThrowAsync(string type, CancellationToken ct)
  {
    return await db.TypeDefinitions.FirstOrDefaultAsync(t => t.Name == type, ct)
      ?? throw new TietueValidationException([$"Unknown type '{type}'. Define it first with define_type."]);
  }

  private static string? KeyValue(JsonDocument data, string field)
  {
    if (!data.RootElement.TryGetProperty(field, out var v)
      || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
    {
      return null;
    }

    return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
  }

  private async Task EnforceUniqueOnCreateAsync(Entity entity, string? behaviorsJson, CancellationToken ct)
  {
    var cfg = BehaviorSpec.UniqueNameOf(behaviorsJson);
    if (cfg is null)
    {
      return;
    }

    var value = KeyValue(entity.Data, cfg.Field);
    if (value is null)
    {
      return;
    }

    if (await db.UniqueKeys.AnyAsync(k => k.Type == entity.Type && k.Field == cfg.Field && k.Value == value, ct))
    {
      throw DuplicateError(entity.Type, cfg.Field, value);
    }

    db.UniqueKeys.Add(new UniqueKey { Type = entity.Type, Field = cfg.Field, Value = value, EntityId = entity.Id });
  }

  private async Task EnforceUniqueOnUpdateAsync(Entity entity, string? behaviorsJson, CancellationToken ct)
  {
    var cfg = BehaviorSpec.UniqueNameOf(behaviorsJson);
    if (cfg is null)
    {
      return;
    }

    var value = KeyValue(entity.Data, cfg.Field);
    var existing = await db.UniqueKeys.FirstOrDefaultAsync(k => k.EntityId == entity.Id && k.Field == cfg.Field, ct);

    if (value is null)
    {
      if (existing is not null)
      {
        db.UniqueKeys.Remove(existing);
      }

      return;
    }

    if (await db.UniqueKeys.AnyAsync(k => k.Type == entity.Type && k.Field == cfg.Field && k.Value == value && k.EntityId != entity.Id, ct))
    {
      throw DuplicateError(entity.Type, cfg.Field, value);
    }

    if (existing is null)
    {
      db.UniqueKeys.Add(new UniqueKey { Type = entity.Type, Field = cfg.Field, Value = value, EntityId = entity.Id });
    }
    else
    {
      existing.Value = value;
    }
  }

  private async Task SaveGuardingUniqueAsync(string type, CancellationToken ct)
  {
    try
    {
      await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
    {
      throw new TietueValidationException([$"A '{type}' with a duplicate unique field already exists."]);
    }
  }

  private static TietueValidationException DuplicateError(string type, string field, string value)
  {
    return new TietueValidationException([$"A '{type}' with {field}='{value}' already exists."]);
  }
```

> Note: remove the old `GetSchemaOrThrowAsync` method only if nothing else references it (it was used solely by `UpdateAsync`). If `dotnet format` flags it as unused-after-refactor, delete it.

- [ ] **Step 4: Run the new tests + full suite + lint:**

Run:
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj 2>&1 | grep -E "Passed!|Failed!|error"
  dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes >/dev/null 2>&1; echo "LINT=$?"'
```
Expected: all pass (113 prior + 7 new = 120), `LINT=0`.

- [ ] **Step 5: Commit**

```bash
git add src/toimi.tools.tietue/Entities/EntityRepository.cs src/toimi.tools.tietue.Tests/UniqueNameTests.cs
git commit -m "feat(tietue): enforce UniqueName on create/update via unique_keys"
```

---

## Task 4: Seed UniqueName on `skill` + docs

**Files:**
- Modify: `src/toimi.tools.tietue/Seed/TypeSeeder.cs`
- Modify: `src/toimi.tools.tietue.Tests/TypeSeederTests.cs`
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update the failing test** (in `TypeSeederTests.cs`, extend the existing skill assertion):

In `Seeds_memory_and_skill_types_with_semantic_index`, after `Assert.Contains("SemanticIndex", skill.Behaviors);` add:

```csharp
    Assert.Contains("UniqueName", skill.Behaviors);
```

- [ ] **Step 2: Run to verify failure.**

Run: `docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~TypeSeederTests"`
Expected: FAIL (skill lacks UniqueName).

- [ ] **Step 3: Implement.** In `Seed/TypeSeeder.cs`, change the `skill` entry's behaviors string to include both behaviors:

```csharp
      /*lang=json,strict*/
                           """[{"behavior":"SemanticIndex","config":{"fields":["description","instructions"],"mode":"whole"}},{"behavior":"UniqueName","config":{"field":"name"}}]""",
```

- [ ] **Step 4: Update `CLAUDE.md`.** Replace the behaviors bullet (currently lines ~46–48):

```
- **Declarative behaviors** (passive, per-type): `SemanticIndex` (embed
  configured fields → Qdrant on save, semantic `search`), `Expiry`,
  `UniqueName`.
```

with:

```
- **Declarative behaviors** (passive, per-type): `SemanticIndex` (embed
  configured fields → Qdrant on save, semantic `search`) and `UniqueName`
  (reject a second entity of the type sharing a keyed field value — pre-check
  plus a `unique_keys` DB unique index; config `{"field":"<name>"}`, default
  `name`). (`Expiry` is designed in the spec but not yet implemented.)
```

- [ ] **Step 5: Run tests + lint:**

Run:
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj 2>&1 | grep -E "Passed!|Failed!|error"
  dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes >/dev/null 2>&1; echo "LINT=$?"'
```
Expected: all pass, `LINT=0`.

- [ ] **Step 6: Commit**

```bash
git add src/toimi.tools.tietue/Seed/TypeSeeder.cs src/toimi.tools.tietue.Tests/TypeSeederTests.cs CLAUDE.md
git commit -m "feat(tietue): seed UniqueName on skill; document behavior status"
```

---

## Task 5: Full verification

- [ ] **Step 1:** Full tietue suite + lint + a web build sanity (no web changes, but confirm nothing transitively broke):

Run:
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj 2>&1 | grep -E "Passed!|Failed!"
  dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes >/dev/null 2>&1; echo "TIETUE_LINT=$?"
  dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes >/dev/null 2>&1; echo "TESTS_LINT=$?"'
```
Expected: 120 passed, both `LINT=0`.

- [ ] **Step 2:** Confirm migration is present and the model snapshot has `unique_keys` with a unique index. Then this plan is complete; hand back to the controller for the finishing-a-development-branch step.

---

## Notes / out of scope

- **Backfill:** adding `UniqueName` to a type does **not** retroactively create `unique_keys` rows for entities saved before the behavior existed; enforcement applies to creates/updates from that point on. Fine for a fresh DB; a backfill pass is out of scope.
- **Case sensitivity:** keys match by exact (ordinal) value. Case-insensitive keying is a future option.
- **`wishlist` is user-defined** (not seeded). To dedup it, the type must be redefined with `{"behavior":"UniqueName","config":{"field":"url"}}` via `define_type` (the agent or admin) — no code change.
