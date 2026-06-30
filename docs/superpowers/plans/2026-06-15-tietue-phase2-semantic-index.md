# Tietue Phase 2 — Semantic Index & Type Seeding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the `tietue` engine a `SemanticIndex` declarative behavior — entities of a type that declares it get their configured fields embedded into Qdrant on write and removed on delete, with a semantic `search` MCP tool — then seed `memory` and `skill` standard types and inject the type catalog into the system prompt. This makes `tietue` functionally able to replace `muistio` (memory recall) and `taidot` (skill search); the old pods are not deleted until the Phase 6 cutover.

**Architecture:** Qdrant + OpenAI embeddings are added to the `tietue` pod (same wiring muistio/taidot use). Qdrant I/O sits behind an `ISemanticIndex` interface (real `QdrantSemanticIndex`, plus an in-memory fake for tests — the repo never unit-tests Qdrant directly). A `BehaviorDispatcher` reads a type's declared behaviors and drives indexing on entity save/delete and semantic search; it is wired into `EntityRepository` as an optional dependency so Phase 1's DB-only tests are unaffected. One Qdrant collection per semantically-indexed type (collection name = type name); search rolls up by `entity_id` and returns entities ordered by score. A `TypeSeeder` upserts the `memory` and `skill` standard types on startup (idempotent, like taidot's `SkillSeeder`). Finally `list_types` is injected into the system prompt by `ToimiHub`, reusing the existing skill-injection mechanism.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, `Qdrant.Client` 1.17.0, `Microsoft.Extensions.AI.OpenAI` 10.4.1, `ModelContextProtocol` 1.1.0, xUnit + EF InMemory. Run all dotnet commands inside the cached .NET 10 SDK Docker image (dotnet is not on PATH): `docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 <cmd>`. The repo enforces `dotnet format` (IDE0005 unused-usings, IDE0022 block bodies, whitespace) as errors — run `dotnet format <csproj>` before committing.

**Scope boundary (Phase 2 of the §16 build order):**
- IN: `SemanticIndex` behavior (embed-on-save, remove-on-delete), `ISemanticIndex`/`QdrantSemanticIndex`, `BehaviorDispatcher`, `search` MCP tool (single type), `TypeDefinition.Behaviors` column + migration, `memory` + `skill` standard-type seeding, type-catalog injection into the system prompt, Qdrant/OpenAI config + deployment env.
- OUT (later phases): cross-type `search("*")` (single-type only here — note it in the tool description); chunking / `mode: chunk`/`per-item` (only `mode: whole`); triggers/scheduler/handlers (Phase 3); message handler/conversations (Phase 4); script sandbox (Phase 5); deleting the muistio/taidot pods/DBs (Phase 6 cutover). Phase 2 adds the replacement capability only.

**Assumes Phase 1 is merged** (the `tietue` server with `Entity`/`TypeDefinition`, `TietueDbContext`, `SchemaValidator`, `TypeRepository`, `EntityRepository`, MCP CRUD tools, `/admin`, migration `InitialCreate`).

---

## File Structure

**New in `src/toimi.tools.tietue/`:**
- `Semantic/ISemanticIndex.cs` — Qdrant abstraction + `ScoredId` record
- `Semantic/QdrantSemanticIndex.cs` — real Qdrant+embeddings implementation
- `Semantic/EmbeddingService.cs` — OpenAI embedding wrapper (copied from muistio)
- `Semantic/SemanticText.cs` — pure field-extraction helper
- `Behaviors/BehaviorSpec.cs` — parse `TypeDefinition.Behaviors` JSON → `SemanticIndexConfig`
- `Behaviors/BehaviorDispatcher.cs` — drives index on save/delete; semantic search
- `Seed/TypeSeeder.cs` — seeds `memory` + `skill` standard types
- `Tools/SearchEntitiesTool.cs` — semantic search MCP tool

**Modified in `src/toimi.tools.tietue/`:**
- `toimi.tools.tietue.csproj` — add Qdrant + OpenAI packages
- `appsettings.json` — add `Qdrant` + `OpenAI` sections
- `Data/TypeDefinition.cs` + `Data/TypeDefinitionConfiguration.cs` — add `Behaviors` jsonb column
- `Types/TypeRepository.cs` — `DefineAsync` accepts optional behaviors JSON
- `Tools/DefineTypeTool.cs` — add `behaviors` param
- `Entities/EntityRepository.cs` — optional `BehaviorDispatcher` dispatch on save/delete
- `Admin/AdminEndpoints.cs` — delete routes through `EntityRepository` (so it de-indexes)
- `Program.cs` — register Qdrant, embeddings, `ISemanticIndex`, `BehaviorDispatcher`; ensure collections + seed on startup
- `Migrations/` — new migration `AddTypeBehaviors`

**Modified in `src/toimi.tools.tietue.Tests/`:**
- `FakeSemanticIndex.cs` — in-memory `ISemanticIndex` test double
- new test files per task

**Modified for catalog injection:**
- `src/toimi.core/ToimiClientFactory.cs` — `CreateInitialMessages` accepts a type catalog
- `src/toimi.web/Hubs/ToimiHub.cs` — call `list_types`, pass to `CreateInitialMessages`

**Deployment:**
- `k8s/base/tools-tietue/deployment.yaml` — add `OpenAI__ApiKey` (secret) + `Qdrant__Host`/`Qdrant__Port` env

---

## Task 1: Add Qdrant + OpenAI to the tietue project (packages, config, embeddings, DI)

**Files:**
- Modify: `src/toimi.tools.tietue/toimi.tools.tietue.csproj`
- Modify: `src/toimi.tools.tietue/appsettings.json`
- Create: `src/toimi.tools.tietue/Semantic/EmbeddingService.cs`
- Modify: `src/toimi.tools.tietue/Program.cs`

- [ ] **Step 1: Add package references.** In `src/toimi.tools.tietue/toimi.tools.tietue.csproj`, add to the existing `<ItemGroup>` of `PackageReference`s:

```xml
    <PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.4.1" />
    <PackageReference Include="Qdrant.Client" Version="1.17.0" />
```

- [ ] **Step 2: Add config sections.** In `src/toimi.tools.tietue/appsettings.json`, add `Qdrant` and `OpenAI` sections (alongside the existing `ConnectionStrings`/`Logging`/`AllowedHosts`):

```json
  "Qdrant": {
    "Host": "qdrant.data.svc.cluster.local",
    "Port": 6334
  },
  "OpenAI": {
    "ApiKey": "",
    "EmbeddingModel": "text-embedding-3-small"
  }
```

- [ ] **Step 3: Create the embedding service** (copied from muistio).

`src/toimi.tools.tietue/Semantic/EmbeddingService.cs`:
```csharp
using Microsoft.Extensions.AI;

namespace toimi.tools.tietue.Semantic;

public class EmbeddingService(IEmbeddingGenerator<string, Embedding<float>> generator)
{
  public async Task<float[]> GenerateEmbeddingAsync(string text)
  {
    var vector = await generator.GenerateVectorAsync(text);
    return vector.ToArray();
  }
}
```

- [ ] **Step 4: Register Qdrant + embeddings in DI.** In `src/toimi.tools.tietue/Program.cs`, add the registrations after the existing `AddDbContext`/repository registrations and before `AddMcpServer` (mirror muistio's Program.cs). Add the needed `using`s at the top (`using OpenAI;`, `using Qdrant.Client;`, `using Microsoft.Extensions.AI;`, `using toimi.tools.tietue.Semantic;`):

```csharp
var qdrantHost = builder.Configuration["Qdrant:Host"] ?? "localhost";
var qdrantPort = int.Parse(builder.Configuration["Qdrant:Port"] ?? "6334", System.Globalization.CultureInfo.InvariantCulture);
builder.Services.AddSingleton(new QdrantClient(qdrantHost, qdrantPort));

var openAiApiKey = builder.Configuration["OpenAI:ApiKey"]
  ?? throw new InvalidOperationException("OpenAI:ApiKey is required");
var embeddingModel = builder.Configuration["OpenAI:EmbeddingModel"] ?? "text-embedding-3-small";
var openAiClient = new OpenAIClient(openAiApiKey);
var embeddingClient = openAiClient.GetEmbeddingClient(embeddingModel);
builder.Services.AddSingleton(embeddingClient.AsIEmbeddingGenerator());
builder.Services.AddSingleton<EmbeddingService>();
```

> Note: the test factory (`TietueTestFactory`) sets `ConnectionStrings:Tietue` but NOT `OpenAI:ApiKey`. Adding the required-`OpenAI:ApiKey` throw means the existing admin tests would fail to boot. To prevent that, in this step ALSO update `src/toimi.tools.tietue.Tests/AdminEndpointsTests.cs`'s `TietueTestFactory.ConfigureWebHost` to add `builder.UseSetting("OpenAI:ApiKey", "test-key");` right after the existing `builder.UseSetting("ConnectionStrings:Tietue", ...)` line. A fake `OpenAIClient` is never called in those tests (no embedding happens), so a dummy key is fine.

- [ ] **Step 5: Build + run the full suite** (the OpenAI key throw is the risky bit):

Run: `docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`
Expected: PASS — all 26 prior tests still green (with the dummy `OpenAI:ApiKey` set in the test factory).

- [ ] **Step 6: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj
git add src/toimi.tools.tietue/toimi.tools.tietue.csproj src/toimi.tools.tietue/appsettings.json src/toimi.tools.tietue/Semantic/EmbeddingService.cs src/toimi.tools.tietue/Program.cs src/toimi.tools.tietue.Tests/AdminEndpointsTests.cs
git commit -m "feat(tietue): add Qdrant + OpenAI embeddings wiring"
```

---

## Task 2: `SemanticText` — pure field-extraction helper

**Files:**
- Create: `src/toimi.tools.tietue/Semantic/SemanticText.cs`
- Test: `src/toimi.tools.tietue.Tests/SemanticTextTests.cs`

- [ ] **Step 1: Write the failing tests.**

`src/toimi.tools.tietue.Tests/SemanticTextTests.cs`:
```csharp
using System.Text.Json;
using toimi.tools.tietue.Semantic;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SemanticTextTests
{
  private static JsonDocument Doc(string json) => JsonDocument.Parse(json);

  [Fact]
  public void Extracts_and_joins_named_string_fields()
  {
    var text = SemanticText.Extract(Doc("""{"title":"hi","body":"there"}"""), ["title", "body"]);
    Assert.Equal("hi there", text);
  }

  [Fact]
  public void Skips_missing_fields()
  {
    var text = SemanticText.Extract(Doc("""{"title":"hi"}"""), ["title", "missing"]);
    Assert.Equal("hi", text);
  }

  [Fact]
  public void Renders_non_string_fields_as_raw_json()
  {
    var text = SemanticText.Extract(Doc("""{"count":3,"tags":["a","b"]}"""), ["count", "tags"]);
    Assert.Equal("""3 ["a","b"]""", text);
  }

  [Fact]
  public void Empty_fields_yields_empty_string()
  {
    Assert.Equal("", SemanticText.Extract(Doc("""{"title":"hi"}"""), []));
  }
}
```

- [ ] **Step 2: Run, confirm FAIL** (`SemanticText` doesn't exist):
`docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~SemanticTextTests"`

- [ ] **Step 3: Implement.**

`src/toimi.tools.tietue/Semantic/SemanticText.cs`:
```csharp
using System.Text.Json;

namespace toimi.tools.tietue.Semantic;

public static class SemanticText
{
  // Concatenates the named fields of an entity's Data into one string for embedding.
  // String fields contribute their value; non-string fields contribute their raw JSON.
  public static string Extract(JsonDocument data, string[] fields)
  {
    var parts = new List<string>();
    foreach (var field in fields)
    {
      if (!data.RootElement.TryGetProperty(field, out var value))
      {
        continue;
      }

      parts.Add(value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? ""
        : value.GetRawText());
    }

    return string.Join(' ', parts);
  }
}
```

- [ ] **Step 4: Run, confirm 4 PASS.**

- [ ] **Step 5: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj
git add src/toimi.tools.tietue/Semantic/SemanticText.cs src/toimi.tools.tietue.Tests/SemanticTextTests.cs
git commit -m "feat(tietue): add semantic field-extraction helper"
```

---

## Task 3: `TypeDefinition.Behaviors` column + behavior parsing + migration

**Files:**
- Modify: `src/toimi.tools.tietue/Data/TypeDefinition.cs`
- Modify: `src/toimi.tools.tietue/Data/TypeDefinitionConfiguration.cs`
- Create: `src/toimi.tools.tietue/Behaviors/BehaviorSpec.cs`
- Modify: `src/toimi.tools.tietue/Types/TypeRepository.cs`
- Modify: `src/toimi.tools.tietue/Tools/DefineTypeTool.cs`
- Test: `src/toimi.tools.tietue.Tests/BehaviorSpecTests.cs`
- Migration: `src/toimi.tools.tietue/Migrations/*`

- [ ] **Step 1: Add the `Behaviors` property** to `src/toimi.tools.tietue/Data/TypeDefinition.cs` (a nullable jsonb-as-string column — kept as `string?` so it maps to `jsonb` natively without a value converter and works under the in-memory provider):
```csharp
  public string? Behaviors { get; set; }
```
(Add it after `JsonSchema`.)

- [ ] **Step 2: Map it as jsonb.** In `src/toimi.tools.tietue/Data/TypeDefinitionConfiguration.cs`, add inside `Configure`:
```csharp
    builder.Property(t => t.Behaviors)
      .HasColumnType("jsonb");
```

- [ ] **Step 3: Write failing tests for behavior parsing.**

`src/toimi.tools.tietue.Tests/BehaviorSpecTests.cs`:
```csharp
using toimi.tools.tietue.Behaviors;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class BehaviorSpecTests
{
  [Fact]
  public void Parses_semantic_index_fields_and_mode()
  {
    var cfg = BehaviorSpec.SemanticIndexOf(
      """[{"behavior":"SemanticIndex","config":{"fields":["content"],"mode":"whole"}}]""");
    Assert.NotNull(cfg);
    Assert.Equal(["content"], cfg!.Fields);
    Assert.Equal("whole", cfg.Mode);
  }

  [Fact]
  public void Defaults_mode_to_whole_when_absent()
  {
    var cfg = BehaviorSpec.SemanticIndexOf(
      """[{"behavior":"SemanticIndex","config":{"fields":["a","b"]}}]""");
    Assert.Equal("whole", cfg!.Mode);
  }

  [Fact]
  public void Null_when_no_semantic_index_behavior()
  {
    Assert.Null(BehaviorSpec.SemanticIndexOf(null));
    Assert.Null(BehaviorSpec.SemanticIndexOf("[]"));
    Assert.Null(BehaviorSpec.SemanticIndexOf("""[{"behavior":"Other","config":{}}]"""));
  }

  [Fact]
  public void Null_on_malformed_json()
  {
    Assert.Null(BehaviorSpec.SemanticIndexOf("{ not json"));
  }
}
```

- [ ] **Step 4: Run, confirm FAIL.**

- [ ] **Step 5: Implement behavior parsing.**

`src/toimi.tools.tietue/Behaviors/BehaviorSpec.cs`:
```csharp
using System.Text.Json;

namespace toimi.tools.tietue.Behaviors;

public record SemanticIndexConfig(string[] Fields, string Mode);

public static class BehaviorSpec
{
  // Returns the SemanticIndex config from a type's Behaviors JSON, or null if absent/malformed.
  public static SemanticIndexConfig? SemanticIndexOf(string? behaviorsJson)
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
        if (!item.TryGetProperty("behavior", out var b) || b.GetString() != "SemanticIndex")
        {
          continue;
        }

        if (!item.TryGetProperty("config", out var config)
          || !config.TryGetProperty("fields", out var fieldsEl)
          || fieldsEl.ValueKind != JsonValueKind.Array)
        {
          continue;
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
    }

    return null;
  }
}
```

- [ ] **Step 6: Run, confirm 4 PASS.**

- [ ] **Step 7: Extend `TypeRepository.DefineAsync`** to accept optional behaviors. In `src/toimi.tools.tietue/Types/TypeRepository.cs`, change the signature and body:
```csharp
  public async Task<TypeDefinition> DefineAsync(string name, string schemaJson, string? behaviorsJson = null, CancellationToken ct = default)
  {
    JsonDocument schema;
    try
    {
      schema = JsonDocument.Parse(schemaJson);
    }
    catch (JsonException ex)
    {
      throw new TietueValidationException([$"Invalid schema JSON: {ex.Message}"]);
    }

    if (behaviorsJson is not null)
    {
      try
      {
        using var _ = JsonDocument.Parse(behaviorsJson);
      }
      catch (JsonException ex)
      {
        throw new TietueValidationException([$"Invalid behaviors JSON: {ex.Message}"]);
      }
    }

    var now = DateTimeOffset.UtcNow;
    var existing = await db.TypeDefinitions.FirstOrDefaultAsync(t => t.Name == name, ct);
    if (existing is null)
    {
      existing = new TypeDefinition { Name = name, JsonSchema = schema, Behaviors = behaviorsJson, CreatedAt = now, UpdatedAt = now };
      db.TypeDefinitions.Add(existing);
    }
    else
    {
      existing.JsonSchema = schema;
      existing.Behaviors = behaviorsJson;
      existing.UpdatedAt = now;
    }

    await db.SaveChangesAsync(ct);
    return existing;
  }
```
(The existing `using System.Text.Json;` already covers `JsonDocument`.)

- [ ] **Step 8: Extend `DefineTypeTool`** with a behaviors param. In `src/toimi.tools.tietue/Tools/DefineTypeTool.cs`, update the method:
```csharp
  [McpServerTool, Description("Define or replace a data type by name. The schema is a JSON Schema (draft 2020-12) describing the shape of entities of this type. 'behaviors' is an optional JSON array of declarative behaviors, e.g. [{\"behavior\":\"SemanticIndex\",\"config\":{\"fields\":[\"content\"]}}]. Upserts by name.")]
  public async Task<string> DefineType(
      [Description("Unique type name, e.g. 'wishlist_item'")] string name,
      [Description("JSON Schema (draft 2020-12) for entities of this type")] string schema,
      [Description("Optional JSON array of behaviors (e.g. SemanticIndex)")] string? behaviors = null)
  {
    try
    {
      var t = await repository.DefineAsync(name, schema, behaviors);
      return JsonSerializer.Serialize(new { t.Name, defined = true });
    }
    catch (TietueValidationException ex)
    {
      return string.Join("; ", ex.Errors);
    }
  }
```

- [ ] **Step 9: Generate the migration.**
```
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet tool install --global dotnet-ef >/dev/null 2>&1 || true;
  export PATH="$PATH:/root/.dotnet/tools";
  dotnet ef migrations add AddTypeBehaviors --project src/toimi.tools.tietue --startup-project src/toimi.tools.tietue
'
```
Verify it adds a `behaviors` jsonb column to `type_definitions`:
`grep -n "behaviors\|json" src/toimi.tools.tietue/Migrations/*_AddTypeBehaviors.cs`

- [ ] **Step 10: Run the full suite + format; confirm green** (the `ListTypes`/`GetType` tools still work; existing tests unaffected since `behaviors` defaults null). Then commit:
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj'
git add src/toimi.tools.tietue/Data src/toimi.tools.tietue/Behaviors src/toimi.tools.tietue/Types/TypeRepository.cs src/toimi.tools.tietue/Tools/DefineTypeTool.cs src/toimi.tools.tietue/Migrations src/toimi.tools.tietue.Tests/BehaviorSpecTests.cs
git commit -m "feat(tietue): add type behaviors column, parsing, and define_type behaviors arg"
```

---

## Task 4: `ISemanticIndex` interface + in-memory fake test double

**Files:**
- Create: `src/toimi.tools.tietue/Semantic/ISemanticIndex.cs`
- Create: `src/toimi.tools.tietue.Tests/FakeSemanticIndex.cs`

- [ ] **Step 1: Define the interface + result record.**

`src/toimi.tools.tietue/Semantic/ISemanticIndex.cs`:
```csharp
namespace toimi.tools.tietue.Semantic;

public record ScoredId(Guid EntityId, float Score);

public interface ISemanticIndex
{
  Task EnsureCollectionAsync(string collection, CancellationToken ct = default);

  Task IndexAsync(string collection, Guid entityId, string text, CancellationToken ct = default);

  Task RemoveAsync(string collection, Guid entityId, CancellationToken ct = default);

  // Embeds the query internally and returns entity ids ranked by similarity, deduped by entity (best score wins).
  Task<IReadOnlyList<ScoredId>> SearchAsync(string collection, string query, int limit, CancellationToken ct = default);
}
```

- [ ] **Step 2: Create the test double** (used by Tasks 6, 7, 8). It ranks by substring overlap so tests are deterministic without real embeddings.

`src/toimi.tools.tietue.Tests/FakeSemanticIndex.cs`:
```csharp
using toimi.tools.tietue.Semantic;

namespace toimi.tools.tietue.Tests;

public class FakeSemanticIndex : ISemanticIndex
{
  // collection -> (entityId -> indexed text)
  public Dictionary<string, Dictionary<Guid, string>> Store { get; } = [];
  public HashSet<string> EnsuredCollections { get; } = [];

  public Task EnsureCollectionAsync(string collection, CancellationToken ct = default)
  {
    EnsuredCollections.Add(collection);
    if (!Store.ContainsKey(collection))
    {
      Store[collection] = [];
    }

    return Task.CompletedTask;
  }

  public Task IndexAsync(string collection, Guid entityId, string text, CancellationToken ct = default)
  {
    if (!Store.TryGetValue(collection, out var c))
    {
      c = Store[collection] = [];
    }

    c[entityId] = text;
    return Task.CompletedTask;
  }

  public Task RemoveAsync(string collection, Guid entityId, CancellationToken ct = default)
  {
    if (Store.TryGetValue(collection, out var c))
    {
      c.Remove(entityId);
    }

    return Task.CompletedTask;
  }

  public Task<IReadOnlyList<ScoredId>> SearchAsync(string collection, string query, int limit, CancellationToken ct = default)
  {
    if (!Store.TryGetValue(collection, out var c))
    {
      return Task.FromResult<IReadOnlyList<ScoredId>>([]);
    }

    var ranked = c
      .Select(kvp => new ScoredId(kvp.Key, Overlap(kvp.Value, query)))
      .Where(s => s.Score > 0)
      .OrderByDescending(s => s.Score)
      .Take(limit)
      .ToList();

    return Task.FromResult<IReadOnlyList<ScoredId>>(ranked);
  }

  private static float Overlap(string text, string query)
  {
    var t = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var q = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    return q.Count(t.Contains);
  }
}
```

- [ ] **Step 3: Build the test project to confirm both compile.**
`docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet build src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`
Expected: `Build succeeded.`

- [ ] **Step 4: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj
git add src/toimi.tools.tietue/Semantic/ISemanticIndex.cs src/toimi.tools.tietue.Tests/FakeSemanticIndex.cs
git commit -m "feat(tietue): add ISemanticIndex abstraction and in-memory test fake"
```

---

## Task 5: `QdrantSemanticIndex` — real Qdrant implementation

**Files:**
- Create: `src/toimi.tools.tietue/Semantic/QdrantSemanticIndex.cs`

> Like muistio's/taidot's Qdrant code, this is NOT unit-tested (no Qdrant in CI); correctness is covered by the manual smoke test in the final task and by the fake-backed logic tests. Mirror muistio's `MemoryRepository` exactly for the Qdrant calls.

- [ ] **Step 1: Implement.**

`src/toimi.tools.tietue/Semantic/QdrantSemanticIndex.cs`:
```csharp
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace toimi.tools.tietue.Semantic;

public class QdrantSemanticIndex(QdrantClient qdrant, EmbeddingService embeddings) : ISemanticIndex
{
  private const uint VectorSize = 1536;

  public async Task EnsureCollectionAsync(string collection, CancellationToken ct = default)
  {
    if (await qdrant.CollectionExistsAsync(collection, ct))
    {
      return;
    }

    await qdrant.CreateCollectionAsync(
      collection,
      new VectorParams { Size = VectorSize, Distance = Distance.Cosine },
      cancellationToken: ct);
  }

  public async Task IndexAsync(string collection, Guid entityId, string text, CancellationToken ct = default)
  {
    var embedding = await embeddings.GenerateEmbeddingAsync(text);
    var point = new PointStruct { Id = entityId, Vectors = embedding };
    point.Payload["entity_id"] = entityId.ToString();
    await qdrant.UpsertAsync(collection, [point], cancellationToken: ct);
  }

  public async Task RemoveAsync(string collection, Guid entityId, CancellationToken ct = default)
  {
    await qdrant.DeleteAsync(collection, entityId, cancellationToken: ct);
  }

  public async Task<IReadOnlyList<ScoredId>> SearchAsync(string collection, string query, int limit, CancellationToken ct = default)
  {
    if (!await qdrant.CollectionExistsAsync(collection, ct))
    {
      return [];
    }

    var embedding = await embeddings.GenerateEmbeddingAsync(query);
    var results = await qdrant.SearchAsync(collection, embedding, limit: (ulong)limit, cancellationToken: ct);

    // Roll up by entity id (best score wins) — one point per entity today, but keeps the contract stable for future chunking.
    return [.. results
      .GroupBy(r => Guid.Parse(r.Id.Uuid))
      .Select(g => new ScoredId(g.Key, g.Max(r => r.Score)))
      .OrderByDescending(s => s.Score)];
  }
}
```

- [ ] **Step 2: Build the main project; confirm it compiles.**
`docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet build src/toimi.tools.tietue/toimi.tools.tietue.csproj`
Expected: `Build succeeded.` (If a Qdrant API name differs from muistio's usage, align it with `src/toimi.tools.muistio/Memory/MemoryRepository.cs`, which uses the same `Qdrant.Client` 1.17.0.)

- [ ] **Step 3: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj
git add src/toimi.tools.tietue/Semantic/QdrantSemanticIndex.cs
git commit -m "feat(tietue): add Qdrant-backed semantic index implementation"
```

---

## Task 6: `BehaviorDispatcher` — index on save/delete, semantic search

**Files:**
- Create: `src/toimi.tools.tietue/Behaviors/BehaviorDispatcher.cs`
- Test: `src/toimi.tools.tietue.Tests/BehaviorDispatcherTests.cs`

- [ ] **Step 1: Write the failing tests** (using the in-memory DB + `FakeSemanticIndex`).

`src/toimi.tools.tietue.Tests/BehaviorDispatcherTests.cs`:
```csharp
using System.Text.Json;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Types;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class BehaviorDispatcherTests
{
  private const string Schema = """{"type":"object","properties":{"content":{"type":"string"}}}""";
  private const string Behaviors = """[{"behavior":"SemanticIndex","config":{"fields":["content"]}}]""";

  private static async Task<(TietueDbContext db, FakeSemanticIndex idx, BehaviorDispatcher disp)> SetupAsync(string? behaviors)
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("note", Schema, behaviors);
    var idx = new FakeSemanticIndex();
    return (db, idx, new BehaviorDispatcher(db, idx));
  }

  private static Entity NewEntity(string content) => new()
  {
    Id = Guid.NewGuid(),
    Type = "note",
    Data = JsonDocument.Parse($$"""{"content":"{{content}}"}"""),
    CreatedAt = DateTimeOffset.UtcNow,
    UpdatedAt = DateTimeOffset.UtcNow,
  };

  [Fact]
  public async Task OnSaved_indexes_configured_fields_for_semantic_type()
  {
    var (db, idx, disp) = await SetupAsync(Behaviors);
    using var _ = db;
    var e = NewEntity("hello world");

    await disp.OnEntitySavedAsync(e);

    Assert.Equal("hello world", idx.Store["note"][e.Id]);
    Assert.Contains("note", idx.EnsuredCollections);
  }

  [Fact]
  public async Task OnSaved_does_nothing_for_non_semantic_type()
  {
    var (db, idx, disp) = await SetupAsync(behaviors: null);
    using var _ = db;

    await disp.OnEntitySavedAsync(NewEntity("hi"));

    Assert.Empty(idx.Store);
  }

  [Fact]
  public async Task OnDeleted_removes_from_index()
  {
    var (db, idx, disp) = await SetupAsync(Behaviors);
    using var _ = db;
    var e = NewEntity("bye");
    await disp.OnEntitySavedAsync(e);

    await disp.OnEntityDeletedAsync(e);

    Assert.False(idx.Store["note"].ContainsKey(e.Id));
  }

  [Fact]
  public async Task Search_returns_matching_entities_ordered_by_score()
  {
    var (db, idx, disp) = await SetupAsync(Behaviors);
    using var _ = db;
    var match = NewEntity("apple banana");
    var other = NewEntity("zebra");
    db.Entities.AddRange(match, other);
    await db.SaveChangesAsync();
    await disp.OnEntitySavedAsync(match);
    await disp.OnEntitySavedAsync(other);

    var results = await disp.SearchAsync("note", "apple", 10);

    var hit = Assert.Single(results);
    Assert.Equal(match.Id, hit.Entity.Id);
  }

  [Fact]
  public async Task Search_throws_for_type_without_semantic_index()
  {
    var (db, idx, disp) = await SetupAsync(behaviors: null);
    using var _ = db;
    await Assert.ThrowsAsync<toimi.tools.tietue.Validation.TietueValidationException>(
      () => disp.SearchAsync("note", "x", 10));
  }
}
```

- [ ] **Step 2: Run, confirm FAIL.**

- [ ] **Step 3: Implement.**

`src/toimi.tools.tietue/Behaviors/BehaviorDispatcher.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Semantic;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Behaviors;

public record ScoredEntity(Entity Entity, float Score);

public class BehaviorDispatcher(TietueDbContext db, ISemanticIndex index)
{
  public async Task OnEntitySavedAsync(Entity entity, CancellationToken ct = default)
  {
    var cfg = await SemanticConfigAsync(entity.Type, ct);
    if (cfg is null)
    {
      return;
    }

    await index.EnsureCollectionAsync(entity.Type, ct);
    var text = SemanticText.Extract(entity.Data, cfg.Fields);
    await index.IndexAsync(entity.Type, entity.Id, text, ct);
  }

  public async Task OnEntityDeletedAsync(Entity entity, CancellationToken ct = default)
  {
    var cfg = await SemanticConfigAsync(entity.Type, ct);
    if (cfg is null)
    {
      return;
    }

    await index.RemoveAsync(entity.Type, entity.Id, ct);
  }

  public async Task<IReadOnlyList<ScoredEntity>> SearchAsync(string type, string query, int limit, CancellationToken ct = default)
  {
    var cfg = await SemanticConfigAsync(type, ct)
      ?? throw new TietueValidationException([$"Type '{type}' is not semantically indexed (no SemanticIndex behavior)."]);

    var scored = await index.SearchAsync(type, query, limit, ct);
    if (scored.Count == 0)
    {
      return [];
    }

    var scoreById = scored.ToDictionary(s => s.EntityId, s => s.Score);
    var ids = scoreById.Keys.ToList();
    var entities = await db.Entities.Where(e => ids.Contains(e.Id)).ToListAsync(ct);

    return [.. entities
      .Select(e => new ScoredEntity(e, scoreById.GetValueOrDefault(e.Id)))
      .OrderByDescending(r => r.Score)];
  }

  private async Task<SemanticIndexConfig?> SemanticConfigAsync(string type, CancellationToken ct)
  {
    var typeDef = await db.TypeDefinitions.FirstOrDefaultAsync(t => t.Name == type, ct);
    return typeDef is null ? null : BehaviorSpec.SemanticIndexOf(typeDef.Behaviors);
  }
}
```

- [ ] **Step 4: Run, confirm 5 PASS.**

- [ ] **Step 5: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj'
git add src/toimi.tools.tietue/Behaviors/BehaviorDispatcher.cs src/toimi.tools.tietue.Tests/BehaviorDispatcherTests.cs
git commit -m "feat(tietue): add behavior dispatcher for semantic index + search"
```

---

## Task 7: Wire the dispatcher into `EntityRepository` write path

**Files:**
- Modify: `src/toimi.tools.tietue/Entities/EntityRepository.cs`
- Test: `src/toimi.tools.tietue.Tests/EntityRepositoryIndexingTests.cs`

- [ ] **Step 1: Add an optional dispatcher dependency.** In `src/toimi.tools.tietue/Entities/EntityRepository.cs`, change the class declaration to take an optional `BehaviorDispatcher?` (defaulting null keeps all existing Phase 1 tests — `new EntityRepository(db, validator)` — compiling and behaving identically):
```csharp
using toimi.tools.tietue.Behaviors;
// ... existing usings ...

public class EntityRepository(TietueDbContext db, SchemaValidator validator, BehaviorDispatcher? dispatcher = null)
```
Then dispatch after each successful write:
- At the end of `CreateAsync`, before `return entity;`, add:
```csharp
    if (dispatcher is not null)
    {
      await dispatcher.OnEntitySavedAsync(entity, ct);
    }
```
- In `UpdateAsync`, after `await db.SaveChangesAsync(ct);` and before `return entity;`, add the same `OnEntitySavedAsync(entity, ct)` dispatch.
- In `DeleteAsync`, the entity is loaded before removal — after `await db.SaveChangesAsync(ct);` and before `return true;`, add:
```csharp
    if (dispatcher is not null)
    {
      await dispatcher.OnEntityDeletedAsync(entity, ct);
    }
```

- [ ] **Step 2: Write failing tests** proving the repo drives indexing.

`src/toimi.tools.tietue.Tests/EntityRepositoryIndexingTests.cs`:
```csharp
using System.Text.Json.Nodes;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class EntityRepositoryIndexingTests
{
  private const string Schema = """{"type":"object","properties":{"content":{"type":"string"}},"required":["content"]}""";
  private const string Behaviors = """[{"behavior":"SemanticIndex","config":{"fields":["content"]}}]""";

  private static async Task<(EntityRepository repo, FakeSemanticIndex idx)> SetupAsync(toimi.tools.tietue.Data.TietueDbContext db)
  {
    await new TypeRepository(db).DefineAsync("note", Schema, Behaviors);
    var idx = new FakeSemanticIndex();
    var repo = new EntityRepository(db, new SchemaValidator(), new BehaviorDispatcher(db, idx));
    return (repo, idx);
  }

  [Fact]
  public async Task Create_indexes_entity()
  {
    using var db = TestDb.New();
    var (repo, idx) = await SetupAsync(db);

    var e = await repo.CreateAsync("note", JsonNode.Parse("""{"content":"hello"}"""), []);

    Assert.Equal("hello", idx.Store["note"][e.Id]);
  }

  [Fact]
  public async Task Update_reindexes_entity()
  {
    using var db = TestDb.New();
    var (repo, idx) = await SetupAsync(db);
    var e = await repo.CreateAsync("note", JsonNode.Parse("""{"content":"hello"}"""), []);

    await repo.UpdateAsync(e.Id, JsonNode.Parse("""{"content":"goodbye"}"""), null);

    Assert.Equal("goodbye", idx.Store["note"][e.Id]);
  }

  [Fact]
  public async Task Delete_deindexes_entity()
  {
    using var db = TestDb.New();
    var (repo, idx) = await SetupAsync(db);
    var e = await repo.CreateAsync("note", JsonNode.Parse("""{"content":"hello"}"""), []);

    await repo.DeleteAsync(e.Id);

    Assert.False(idx.Store["note"].ContainsKey(e.Id));
  }
}
```

- [ ] **Step 3: Run, confirm 3 PASS, and re-run the FULL suite** to confirm the Phase 1 `EntityRepositoryTests` (which construct `EntityRepository` without a dispatcher) still pass.

- [ ] **Step 4: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj'
git add src/toimi.tools.tietue/Entities/EntityRepository.cs src/toimi.tools.tietue.Tests/EntityRepositoryIndexingTests.cs
git commit -m "feat(tietue): dispatch semantic indexing from entity write path"
```

---

## Task 8: `search` MCP tool

**Files:**
- Create: `src/toimi.tools.tietue/Tools/SearchEntitiesTool.cs`
- Test: `src/toimi.tools.tietue.Tests/SearchToolTests.cs`

- [ ] **Step 1: Write the failing test.**

`src/toimi.tools.tietue.Tests/SearchToolTests.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Tools;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SearchToolTests
{
  private const string Schema = """{"type":"object","properties":{"content":{"type":"string"}},"required":["content"]}""";
  private const string Behaviors = """[{"behavior":"SemanticIndex","config":{"fields":["content"]}}]""";

  [Fact]
  public async Task Search_returns_matching_entities()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("note", Schema, Behaviors);
    var idx = new FakeSemanticIndex();
    var dispatcher = new BehaviorDispatcher(db, idx);
    var repo = new EntityRepository(db, new SchemaValidator(), dispatcher);
    await repo.CreateAsync("note", JsonNode.Parse("""{"content":"apple pie"}"""), []);
    await repo.CreateAsync("note", JsonNode.Parse("""{"content":"zebra"}"""), []);

    var json = await new SearchEntitiesTool(dispatcher).Search("note", "apple", 10);

    using var doc = JsonDocument.Parse(json);
    var items = doc.RootElement.GetProperty("results");
    Assert.Equal(1, items.GetArrayLength());
    Assert.Contains("apple", items[0].GetProperty("data").GetProperty("content").GetString());
  }

  [Fact]
  public async Task Search_unindexed_type_returns_message()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("plain", Schema);
    var dispatcher = new BehaviorDispatcher(db, new FakeSemanticIndex());

    var result = await new SearchEntitiesTool(dispatcher).Search("plain", "x", 10);

    Assert.Contains("not semantically indexed", result);
  }
}
```

- [ ] **Step 2: Run, confirm FAIL.**

- [ ] **Step 3: Implement.**

`src/toimi.tools.tietue/Tools/SearchEntitiesTool.cs`:
```csharp
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class SearchEntitiesTool(BehaviorDispatcher dispatcher)
{
  [McpServerTool, Description("Semantic search over entities of a type that has a SemanticIndex behavior. Returns the best-matching entities ranked by similarity. The type must be semantically indexed.")]
  public async Task<string> Search(
      [Description("The type name to search within (must have a SemanticIndex behavior)")] string type,
      [Description("Natural-language query")] string query,
      [Description("Max results (default 10)")] int limit = 10)
  {
    limit = Math.Clamp(limit, 1, 100);
    try
    {
      var results = await dispatcher.SearchAsync(type, query, limit);
      var items = results.Select(r => new JsonObject
      {
        ["id"] = r.Entity.Id.ToString(),
        ["type"] = r.Entity.Type,
        ["data"] = JsonNode.Parse(r.Entity.Data.RootElement.GetRawText()),
        ["tags"] = new JsonArray(r.Entity.Tags.Select(t => (JsonNode)t!).ToArray()),
        ["score"] = r.Score,
      }).ToArray();

      return JsonSerializer.Serialize(new JsonObject { ["results"] = new JsonArray(items) });
    }
    catch (TietueValidationException ex)
    {
      return string.Join("; ", ex.Errors);
    }
  }
}
```

- [ ] **Step 4: Run, confirm 2 PASS.**

- [ ] **Step 5: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj'
git add src/toimi.tools.tietue/Tools/SearchEntitiesTool.cs src/toimi.tools.tietue.Tests/SearchToolTests.cs
git commit -m "feat(tietue): add semantic search MCP tool"
```

---

## Task 9: Route admin delete through the repository (so it de-indexes)

**Files:**
- Modify: `src/toimi.tools.tietue/Admin/AdminEndpoints.cs`

> Phase 1's admin `DELETE /items/{id}` removes the row directly via the DbContext, which would leave a stale Qdrant point. Route it through `EntityRepository.DeleteAsync` (which the DI container builds with the dispatcher) so deletes de-index.

- [ ] **Step 1: Change the delete handler.** In `src/toimi.tools.tietue/Admin/AdminEndpoints.cs`, replace the `DELETE /items/{id:guid}` handler so it depends on `EntityRepository` instead of doing `db.Entities.Remove`:
```csharp
    admin.MapDelete("/items/{id:guid}", async (toimi.tools.tietue.Entities.EntityRepository repo, Guid id) =>
    {
      var deleted = await repo.DeleteAsync(id);
      return deleted ? Results.NoContent() : Results.NotFound();
    });
```
Leave the `GET` handlers untouched (they can keep using `TietueDbContext`).

- [ ] **Step 2: Run the admin tests** — `AdminEndpointsTests.Delete_removes_entity` still passes (the test factory's DI builds `EntityRepository`; with no `BehaviorDispatcher` registered yet it resolves the `dispatcher`-less constructor — see note). Run the full suite:
`docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`

> If `EntityRepository` is not yet registered in DI for the admin endpoint to resolve, that registration is added in Task 11 (Program wiring). If the admin delete test fails here because `EntityRepository` can't be resolved, proceed to Task 11 first and then re-run — OR add `builder.Services.AddScoped<EntityRepository>();` now if it isn't already present (Phase 1 registered it). Confirm `EntityRepository` is registered (it was, in Phase 1's Program.cs) so the endpoint resolves it.

- [ ] **Step 3: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj
git add src/toimi.tools.tietue/Admin/AdminEndpoints.cs
git commit -m "feat(tietue): route admin delete through repository to de-index"
```

---

## Task 10: `TypeSeeder` — seed `memory` and `skill` standard types

**Files:**
- Create: `src/toimi.tools.tietue/Seed/TypeSeeder.cs`
- Test: `src/toimi.tools.tietue.Tests/TypeSeederTests.cs`

- [ ] **Step 1: Write failing tests** (idempotent seed of two semantic types).

`src/toimi.tools.tietue.Tests/TypeSeederTests.cs`:
```csharp
using toimi.tools.tietue.Seed;
using toimi.tools.tietue.Types;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TypeSeederTests
{
  [Fact]
  public async Task Seeds_memory_and_skill_types_with_semantic_index()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    await new TypeSeeder(repo).SeedAsync();

    var memory = await repo.GetAsync("memory");
    var skill = await repo.GetAsync("skill");
    Assert.NotNull(memory);
    Assert.NotNull(skill);
    Assert.Contains("SemanticIndex", memory!.Behaviors!);
    Assert.Contains("SemanticIndex", skill!.Behaviors!);
  }

  [Fact]
  public async Task Seeding_twice_is_idempotent()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    await new TypeSeeder(repo).SeedAsync();
    await new TypeSeeder(repo).SeedAsync();

    Assert.Equal(2, (await repo.ListAsync()).Count);
  }
}
```

- [ ] **Step 2: Run, confirm FAIL.**

- [ ] **Step 3: Implement** the seeder. Schemas mirror the muistio `Memory` and taidot `SkillEntry` shapes; both declare a `SemanticIndex` behavior over their text fields.

`src/toimi.tools.tietue/Seed/TypeSeeder.cs`:
```csharp
using toimi.tools.tietue.Types;

namespace toimi.tools.tietue.Seed;

public class TypeSeeder(TypeRepository repository)
{
  private static readonly (string Name, string Schema, string Behaviors)[] StandardTypes =
  [
    (
      "memory",
      """
      {"type":"object","properties":{
        "content":{"type":"string","description":"the fact or observation to remember"},
        "category":{"type":"string","description":"optional category, e.g. preference/fact/context"},
        "source":{"type":"string","description":"user or inferred"},
        "confirmed":{"type":"boolean"}
      },"required":["content"]}
      """,
      """[{"behavior":"SemanticIndex","config":{"fields":["content"],"mode":"whole"}}]"""
    ),
    (
      "skill",
      """
      {"type":"object","properties":{
        "name":{"type":"string","description":"short unique skill name"},
        "description":{"type":"string","description":"what the skill does"},
        "instructions":{"type":"string","description":"full step-by-step instructions"}
      },"required":["name","description","instructions"]}
      """,
      """[{"behavior":"SemanticIndex","config":{"fields":["description","instructions"],"mode":"whole"}}]"""
    ),
  ];

  public async Task SeedAsync(CancellationToken ct = default)
  {
    foreach (var (name, schema, behaviors) in StandardTypes)
    {
      await repository.DefineAsync(name, schema, behaviors, ct);
    }
  }
}
```
(`DefineAsync` already upserts by name, so re-seeding is idempotent.)

- [ ] **Step 4: Run, confirm 2 PASS.**

- [ ] **Step 5: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj'
git add src/toimi.tools.tietue/Seed/TypeSeeder.cs src/toimi.tools.tietue.Tests/TypeSeederTests.cs
git commit -m "feat(tietue): seed memory and skill standard types"
```

---

## Task 11: Program wiring — register index/dispatcher, seed + ensure collections on startup

**Files:**
- Modify: `src/toimi.tools.tietue/Program.cs`

- [ ] **Step 1: Register the semantic services + dispatcher.** In `src/toimi.tools.tietue/Program.cs`, after the `EmbeddingService` registration (Task 1) and the existing repository registrations, add:
```csharp
builder.Services.AddSingleton<toimi.tools.tietue.Semantic.ISemanticIndex, toimi.tools.tietue.Semantic.QdrantSemanticIndex>();
builder.Services.AddScoped<toimi.tools.tietue.Behaviors.BehaviorDispatcher>();
builder.Services.AddScoped<toimi.tools.tietue.Seed.TypeSeeder>();
```
(`EntityRepository` is already registered from Phase 1; now that `BehaviorDispatcher` is registered, the DI container injects it into `EntityRepository`'s optional parameter automatically.)

- [ ] **Step 2: Seed types + ensure Qdrant collections on startup.** In the startup scope block (where `MigrateAsync` runs), after the migration, add seeding + collection creation for each semantically-indexed seeded type:
```csharp
using (var scope = app.Services.CreateScope())
{
  var dbContext = scope.ServiceProvider.GetRequiredService<TietueDbContext>();
  if (dbContext.Database.IsRelational())
  {
    await dbContext.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<toimi.tools.tietue.Seed.TypeSeeder>().SeedAsync();

    var index = scope.ServiceProvider.GetRequiredService<toimi.tools.tietue.Semantic.ISemanticIndex>();
    foreach (var name in new[] { "memory", "skill" })
    {
      await index.EnsureCollectionAsync(name);
    }
  }
}
```

- [ ] **Step 3: Run the full suite** (in-memory tests skip this block via `IsRelational()`):
`docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`
Expected: all tests pass.

- [ ] **Step 4: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj
git add src/toimi.tools.tietue/Program.cs
git commit -m "feat(tietue): wire semantic index, dispatcher, and startup seeding"
```

---

## Task 12: Type-catalog injection into the system prompt

**Files:**
- Modify: `src/toimi.core/ToimiClientFactory.cs`
- Modify: `src/toimi.web/Hubs/ToimiHub.cs`
- Test: `src/toimi.web.Tests/` (new test for `CreateInitialMessages`)

> Reuses the existing skill-injection mechanism: `ToimiHub` already calls `aggregator.CallToolAsync("list_skills")` and passes the result to `ToimiClientFactory.CreateInitialMessages`. We add a parallel `list_types` call and a catalog section.

- [ ] **Step 1: Extend `CreateInitialMessages`.** In `src/toimi.core/ToimiClientFactory.cs`, change the signature to accept an optional type catalog and append it to the dynamic context message:
```csharp
  public static List<ChatMessage> CreateInitialMessages(string? skillSummary = null, string? typeCatalog = null)
  {
    var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt) };

    var context = new System.Text.StringBuilder();
    context.AppendLine($"Current time: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC (Europe/Helsinki is UTC+2 or UTC+3 during DST)");

    if (!string.IsNullOrEmpty(skillSummary))
    {
      context.AppendLine();
      context.AppendLine("Available skills (use GetSkill for full instructions):");
      context.AppendLine(skillSummary);
    }

    if (!string.IsNullOrEmpty(typeCatalog))
    {
      context.AppendLine();
      context.AppendLine("Available data types (use create/search/list with these type names; data must match the JSON schema):");
      context.AppendLine(typeCatalog);
    }

    messages.Add(new(ChatRole.System, context.ToString()));

    return messages;
  }
```

- [ ] **Step 2: Call `list_types` in the hub.** In `src/toimi.web/Hubs/ToimiHub.cs`, where it currently does `var skillSummary = await aggregator.CallToolAsync("list_skills");` and then `ToimiClientFactory.CreateInitialMessages(skillSummary)`, add a `list_types` call and pass it through:
```csharp
    var skillSummary = await aggregator.CallToolAsync("list_skills");
    var typeCatalog = await aggregator.CallToolAsync("list_types");
    // ... existing code, updated:
    var messages = ToimiClientFactory.CreateInitialMessages(skillSummary, typeCatalog);
```
(Match the exact existing variable names / call sites in that file; only add the one call and the extra argument.)

- [ ] **Step 3: Write a test for the catalog injection** in the web test project (`src/toimi.web.Tests`). Add `src/toimi.web.Tests/InitialMessagesTests.cs`:
```csharp
using Microsoft.Extensions.AI;
using Toimi.Core; // adjust to the actual namespace of ToimiClientFactory
using Xunit;

namespace toimi.web.Tests;

public class InitialMessagesTests
{
  [Fact]
  public void Includes_type_catalog_when_provided()
  {
    var messages = ToimiClientFactory.CreateInitialMessages(skillSummary: null, typeCatalog: """[{"name":"memory"}]""");
    var context = string.Join("\n", messages.Select(m => m.Text));
    Assert.Contains("Available data types", context);
    Assert.Contains("memory", context);
  }

  [Fact]
  public void Omits_type_catalog_when_absent()
  {
    var messages = ToimiClientFactory.CreateInitialMessages();
    var context = string.Join("\n", messages.Select(m => m.Text));
    Assert.DoesNotContain("Available data types", context);
  }
}
```
> Before writing, confirm the namespace of `ToimiClientFactory` (open `src/toimi.core/ToimiClientFactory.cs` — use its actual `namespace`) and that `ChatMessage.Text` is the right accessor in this `Microsoft.Extensions.AI` version; adjust the `using`/accessor if needed so it compiles.

- [ ] **Step 4: Run the web test project + build the solution’s affected projects.**
`docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj --filter "FullyQualifiedName~InitialMessagesTests"`
Expected: 2 pass. Also build `toimi.web` to confirm the hub change compiles:
`docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet build src/toimi.web/toimi.web.csproj`

- [ ] **Step 5: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.core/toimi.core.csproj; dotnet format src/toimi.web/toimi.web.csproj; dotnet format src/toimi.web.Tests/toimi.web.Tests.csproj'
git add src/toimi.core/ToimiClientFactory.cs src/toimi.web/Hubs/ToimiHub.cs src/toimi.web.Tests/InitialMessagesTests.cs
git commit -m "feat(tietue): inject type catalog into the system prompt"
```

---

## Task 13: Deployment env — OpenAI key + Qdrant host for the tietue pod

**Files:**
- Modify: `k8s/base/tools-tietue/deployment.yaml`

- [ ] **Step 1: Add the env vars** to the container in `k8s/base/tools-tietue/deployment.yaml`, mirroring muistio's deployment (the `openai-api-key` secret already exists in the cluster; Qdrant host/port are plain values):
```yaml
            - name: OpenAI__ApiKey
              valueFrom:
                secretKeyRef:
                  name: toimi-secrets
                  key: openai-api-key
            - name: Qdrant__Host
              value: qdrant.data.svc.cluster.local
            - name: Qdrant__Port
              value: "6334"
```
Add these under the existing `env:` list (which already has `ConnectionStrings__Tietue`).

- [ ] **Step 2: Validate YAML** (render if kubectl available, else confirm well-formed):
`docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -lc "python3 -c 'import yaml,sys; yaml.safe_load(open(\"k8s/base/tools-tietue/deployment.yaml\"))' && echo YAML_OK"`
(If python3/yaml is unavailable, visually confirm 2-space indentation matches the muistio deployment.)

- [ ] **Step 3: Commit.**
```bash
git add k8s/base/tools-tietue/deployment.yaml
git commit -m "feat(tietue): add OpenAI + Qdrant env to deployment"
```

---

## Task 14: Full-suite verification

**Files:** none (verification only)

- [ ] **Step 1: Run the full tietue test suite + lint, with real exit codes.**
```
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj 2>&1 | grep -E "Passed!|Failed!"
  dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes; echo "MAIN_EXIT=$?"
  dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes; echo "TESTS_EXIT=$?"
'
```
Expected: all tests pass (Phase 1's 26 + the new Phase 2 tests), `MAIN_EXIT=0`, `TESTS_EXIT=0`.

- [ ] **Step 2: Build the affected web/core projects** to confirm the catalog-injection change is intact:
`docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet build src/toimi.web/toimi.web.csproj`

- [ ] **Step 3: Manual smoke test against real Postgres + Qdrant (optional but recommended).** With a local `tietue` Postgres DB and a Qdrant instance reachable, set `OpenAI:ApiKey`, run the server, then via the MCP/admin surface: define is auto-seeded → create a `memory` entity → `search` for related text → confirm the matching entity returns with a score, and that deleting it removes it from results. (This is the only end-to-end exercise of the real `QdrantSemanticIndex`, which has no unit tests by design.)

- [ ] **Step 4: Final commit if anything changed.**
```bash
git add -A && git commit -m "chore(tietue): phase 2 semantic index complete" --allow-empty
```

---

## Phase 2 Done — what exists now

`tietue` can now host semantically-searchable types: a type declaring a `SemanticIndex` behavior gets its fields embedded to Qdrant on write (and removed on delete), with a `search` MCP tool returning entities ranked by similarity. The `memory` and `skill` standard types are seeded on startup, and the type catalog is injected into the system prompt — so the assistant can store/recall memories and skills through `tietue`. This functionally covers `muistio` (memory recall) and `taidot` (skill search), though those pods remain running until the Phase 6 cutover.

**Deferred (noted inline):** cross-type `search("*")`; chunking (`mode: chunk`/`per-item`); a real-Postgres+Qdrant integration test.

**Next phases (separate plans):**
- **Phase 3** — triggers, scheduler, native handlers (`notify`, `set-field`, `poll-diff`), `entity_events`; seed `reminder`. Retires muistutin.
- **Phase 4** — `message` handler + lazy conversations + self-scheduling; seed `schedule`. Retires ajastin.
- **Phase 5** — script sandbox + escalation.
- **Phase 6** — cutover: delete the four old pods/DBs/bases, update standard-skill seeds + MCP URLs.
