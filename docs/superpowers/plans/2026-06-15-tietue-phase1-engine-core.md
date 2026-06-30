# Tietue Phase 1 — Engine Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the `tietue` tool server with a generic typed-entity store — define JSON-Schema types at runtime, validate entity data against them, and do generic CRUD over entities, all persisted in PostgreSQL `jsonb`.

**Architecture:** A new deployable .NET pod `src/toimi.tools.tietue` following the existing tool-server conventions (ASP.NET minimal host, MCP HTTP transport, EF Core + Npgsql with snake_case naming, `/admin` endpoints, `/health`). Two tables: `type_definitions` (name → JSON Schema) and `entities` (`type`, `data jsonb`, `tags text[]`). A `SchemaValidator` (JsonSchema.Net) gates every entity write. MCP tools expose `define_type`/`list_types`/`get_type`/`delete_type` and `create`/`get`/`update`/`delete`/`list`.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10 + `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions`, `ModelContextProtocol` 1.1.0, `JsonSchema.Net` (json-everything), xUnit + `Microsoft.AspNetCore.Mvc.Testing` + `Microsoft.EntityFrameworkCore.InMemory`.

**Scope boundary (this plan is Phase 1 of the §16 build order):**
- IN: the pod, `type_definitions` + `entities` tables, JSON Schema validation, generic type + entity CRUD MCP tools, `/admin` read/delete, deployment wiring.
- OUT (later phases): `SemanticIndex`/Qdrant (Phase 2); triggers, scheduler, handlers, `entity_events` (Phase 3); `message` handler + conversations (Phase 4); script sandbox (Phase 5). **System-prompt catalog injection is deferred to Phase 2** — `list_types` already returns injection-ready data here, but nothing meaningful exists to inject until the seeded types arrive with Phase 2. Generated-column promotion and `pg_jsonschema` (spec §15.1) are deferred; Phase 1 validates in-engine only.

---

## File Structure

**New project — `src/toimi.tools.tietue/`:**
- `toimi.tools.tietue.csproj` — project + package references
- `Program.cs` — host wiring (DI, migrate-on-start, MapMcp, admin, health)
- `appsettings.json`, `appsettings.Development.json`, `Properties/launchSettings.json`
- `Dockerfile` — multi-stage build (context = repo root)
- `Data/Entity.cs` — the generic entity model
- `Data/TypeDefinition.cs` — the type model (name + JSON Schema)
- `Data/EntityConfiguration.cs`, `Data/TypeDefinitionConfiguration.cs` — EF mappings
- `Data/TietueDbContext.cs`, `Data/TietueDbContextFactory.cs`
- `Migrations/` — generated EF migration
- `Validation/SchemaValidator.cs` + `Validation/ValidationResult.cs` — JSON Schema gate
- `Validation/TietueValidationException.cs` — thrown on invalid data / unknown type
- `Types/TypeRepository.cs` — CRUD over `type_definitions`
- `Entities/EntityRepository.cs` — validated CRUD + list over `entities`
- `Tools/DefineTypeTool.cs`, `ListTypesTool.cs`, `GetTypeTool.cs`, `DeleteTypeTool.cs`
- `Tools/CreateEntityTool.cs`, `GetEntityTool.cs`, `UpdateEntityTool.cs`, `DeleteEntityTool.cs`, `ListEntitiesTool.cs`
- `Admin/AdminEndpoints.cs` — `/admin/summary`, `/items`, `/items/{id}`, delete

**New test project — `src/toimi.tools.tietue.Tests/`:**
- `toimi.tools.tietue.Tests.csproj`
- `SchemaValidatorTests.cs`, `TypeRepositoryTests.cs`, `EntityRepositoryTests.cs`, `AdminEndpointsTests.cs`

**Modified (deployment wiring):**
- `toimi.sln` — add both projects
- `scripts/dev-setup.sh:131` — add `tietue` to the DB-creation loop
- `infrastructure/base/helm/postgresql-values.yaml` — add `CREATE DATABASE tietue;`
- `k8s/base/tools-tietue/` — new `deployment.yaml`, `service.yaml`, `kustomization.yaml`
- `k8s/overlays/*/secrets.env.example` — add `tietue-connection-string`
- `src/toimi.web/appsettings.json` — register the tietue MCP server URL

---

## Task 1: Scaffold the `tietue` project and a minimal host

**Files:**
- Create: `src/toimi.tools.tietue/toimi.tools.tietue.csproj`
- Create: `src/toimi.tools.tietue/Program.cs`
- Create: `src/toimi.tools.tietue/appsettings.json`
- Create: `src/toimi.tools.tietue/appsettings.Development.json`
- Create: `src/toimi.tools.tietue/Properties/launchSettings.json`
- Modify: `toimi.sln`

- [ ] **Step 1: Create the project file**

`src/toimi.tools.tietue/toimi.tools.tietue.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../toimi.core/toimi.core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="EFCore.NamingConventions" Version="10.0.1" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.1">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="ModelContextProtocol" Version="1.1.0" />
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.1.0" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.1" />
    <PackageReference Include="JsonSchema.Net" Version="7.3.4" />
  </ItemGroup>

</Project>
```

> If NuGet restore reports `JsonSchema.Net 7.3.4` is unavailable, use the latest stable `7.x` (`dotnet add src/toimi.tools.tietue package JsonSchema.Net`) and keep the resolved version.

- [ ] **Step 2: Create minimal `Program.cs`** (DB + tools added in later tasks)

`src/toimi.tools.tietue/Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services
  .AddMcpServer(options =>
  {
    options.ServerInfo = new()
    {
      Name = "tietue",
      Version = "1.0.0"
    };
  })
  .WithHttpTransport()
  .WithToolsFromAssembly();

var app = builder.Build();

app.MapMcp();
app.MapGet("/health", () => Results.Ok());

app.Run();

public partial class Program;
```

> `public partial class Program;` makes `Program` visible to `WebApplicationFactory<Program>` in the test project (the other servers rely on the implicit `Program`; declaring it explicitly is required because the test project references this assembly).

- [ ] **Step 3: Create `appsettings.json`**

`src/toimi.tools.tietue/appsettings.json`:

```json
{
  "ConnectionStrings": {
    "Tietue": ""
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

- [ ] **Step 4: Create `appsettings.Development.json`**

`src/toimi.tools.tietue/appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "Tietue": "Host=localhost;Database=tietue;Username=postgres;Password=postgres"
  }
}
```

- [ ] **Step 5: Create `Properties/launchSettings.json`**

`src/toimi.tools.tietue/Properties/launchSettings.json`:

```json
{
  "profiles": {
    "http": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "applicationUrl": "http://localhost:5210",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  }
}
```

- [ ] **Step 6: Add both projects to the solution**

Run:
```bash
dotnet sln toimi.sln add src/toimi.tools.tietue/toimi.tools.tietue.csproj
```
Expected: `Project ... added to the solution.` (The test project is added in Task 9.)

- [ ] **Step 7: Build to verify the skeleton compiles**

Run: `dotnet build src/toimi.tools.tietue/toimi.tools.tietue.csproj`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src/toimi.tools.tietue toimi.sln
git commit -m "feat(tietue): scaffold engine pod with minimal MCP host"
```

---

## Task 2: Data models, DbContext, and EF configurations

**Files:**
- Create: `src/toimi.tools.tietue/Data/Entity.cs`
- Create: `src/toimi.tools.tietue/Data/TypeDefinition.cs`
- Create: `src/toimi.tools.tietue/Data/EntityConfiguration.cs`
- Create: `src/toimi.tools.tietue/Data/TypeDefinitionConfiguration.cs`
- Create: `src/toimi.tools.tietue/Data/TietueDbContext.cs`
- Create: `src/toimi.tools.tietue/Data/TietueDbContextFactory.cs`

- [ ] **Step 1: Create the `Entity` model**

`src/toimi.tools.tietue/Data/Entity.cs`:

```csharp
using System.Text.Json;

namespace toimi.tools.tietue.Data;

public class Entity
{
  public Guid Id { get; set; }
  public required string Type { get; set; }
  public required JsonDocument Data { get; set; }
  public string[] Tags { get; set; } = [];
  public DateTimeOffset CreatedAt { get; set; }
  public DateTimeOffset UpdatedAt { get; set; }
}
```

- [ ] **Step 2: Create the `TypeDefinition` model**

`src/toimi.tools.tietue/Data/TypeDefinition.cs`:

```csharp
using System.Text.Json;

namespace toimi.tools.tietue.Data;

public class TypeDefinition
{
  // Name is the primary key — define_type upserts by name.
  public required string Name { get; set; }
  public required JsonDocument JsonSchema { get; set; }
  public DateTimeOffset CreatedAt { get; set; }
  public DateTimeOffset UpdatedAt { get; set; }
}
```

- [ ] **Step 3: Create `EntityConfiguration`**

`src/toimi.tools.tietue/Data/EntityConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.tietue.Data;

public class EntityConfiguration : IEntityTypeConfiguration<Entity>
{
  public void Configure(EntityTypeBuilder<Entity> builder)
  {
    builder.ToTable("entities");
    builder.HasKey(e => e.Id);

    builder.Property(e => e.Id)
      .HasDefaultValueSql("gen_random_uuid()");

    builder.Property(e => e.Type)
      .IsRequired();

    builder.Property(e => e.Data)
      .HasColumnType("jsonb")
      .IsRequired();

    builder.Property(e => e.Tags)
      .HasColumnType("text[]");

    builder.Property(e => e.CreatedAt)
      .HasDefaultValueSql("now()");

    builder.Property(e => e.UpdatedAt)
      .HasDefaultValueSql("now()");

    builder.HasIndex(e => e.Type);
  }
}
```

- [ ] **Step 4: Create `TypeDefinitionConfiguration`**

`src/toimi.tools.tietue/Data/TypeDefinitionConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.tietue.Data;

public class TypeDefinitionConfiguration : IEntityTypeConfiguration<TypeDefinition>
{
  public void Configure(EntityTypeBuilder<TypeDefinition> builder)
  {
    builder.ToTable("type_definitions");
    builder.HasKey(t => t.Name);

    builder.Property(t => t.Name)
      .IsRequired();

    builder.Property(t => t.JsonSchema)
      .HasColumnType("jsonb")
      .IsRequired();

    builder.Property(t => t.CreatedAt)
      .HasDefaultValueSql("now()");

    builder.Property(t => t.UpdatedAt)
      .HasDefaultValueSql("now()");
  }
}
```

- [ ] **Step 5: Create `TietueDbContext`**

`src/toimi.tools.tietue/Data/TietueDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace toimi.tools.tietue.Data;

public class TietueDbContext(DbContextOptions<TietueDbContext> options) : DbContext(options)
{
  public DbSet<Entity> Entities => Set<Entity>();
  public DbSet<TypeDefinition> TypeDefinitions => Set<TypeDefinition>();

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.ApplyConfigurationsFromAssembly(typeof(TietueDbContext).Assembly);
  }
}
```

- [ ] **Step 6: Create the design-time factory** (needed for `dotnet ef`)

`src/toimi.tools.tietue/Data/TietueDbContextFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace toimi.tools.tietue.Data;

public class TietueDbContextFactory : IDesignTimeDbContextFactory<TietueDbContext>
{
  public TietueDbContext CreateDbContext(string[] args)
  {
    var optionsBuilder = new DbContextOptionsBuilder<TietueDbContext>();
    optionsBuilder.UseNpgsql("Host=localhost;Database=tietue")
      .UseSnakeCaseNamingConvention();

    return new TietueDbContext(optionsBuilder.Options);
  }
}
```

- [ ] **Step 7: Build to verify**

Run: `dotnet build src/toimi.tools.tietue/toimi.tools.tietue.csproj`
Expected: `Build succeeded.`

- [ ] **Step 8: Commit**

```bash
git add src/toimi.tools.tietue/Data
git commit -m "feat(tietue): add entity + type definition models and dbcontext"
```

---

## Task 3: Create the test project and verify in-memory persistence

**Files:**
- Create: `src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`
- Create: `src/toimi.tools.tietue.Tests/DbContextTests.cs`
- Modify: `toimi.sln`

- [ ] **Step 1: Create the test project file**

`src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`:

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
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" Version="10.0.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../toimi.tools.tietue/toimi.tools.tietue.csproj" />
    <ProjectReference Include="../toimi.core/toimi.core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add the test project to the solution**

Run:
```bash
dotnet sln toimi.sln add src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj
```
Expected: `Project ... added to the solution.`

- [ ] **Step 3: Write a failing test for round-tripping an entity through the DbContext**

`src/toimi.tools.tietue.Tests/DbContextTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using Xunit;

namespace toimi.tools.tietue.Tests;

public static class TestDb
{
  public static TietueDbContext New() =>
    new(new DbContextOptionsBuilder<TietueDbContext>()
      .UseInMemoryDatabase($"tietue-{Guid.NewGuid()}")
      .Options);
}

public class DbContextTests
{
  [Fact]
  public async Task Entity_round_trips_with_jsonb_data()
  {
    using var db = TestDb.New();
    var id = Guid.NewGuid();
    db.Entities.Add(new Entity
    {
      Id = id,
      Type = "note",
      Data = JsonDocument.Parse("""{"title":"hello"}"""),
      Tags = ["a", "b"],
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow,
    });
    await db.SaveChangesAsync();

    var loaded = await db.Entities.FindAsync(id);
    Assert.NotNull(loaded);
    Assert.Equal("note", loaded!.Type);
    Assert.Equal("hello", loaded.Data.RootElement.GetProperty("title").GetString());
    Assert.Equal(["a", "b"], loaded.Tags);
  }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~DbContextTests"`
Expected: PASS (1 passed). This confirms the models, configurations, and test harness wire up correctly.

- [ ] **Step 5: Commit**

```bash
git add src/toimi.tools.tietue.Tests toimi.sln
git commit -m "test(tietue): add test project and dbcontext round-trip test"
```

---

## Task 4: `SchemaValidator` — validate entity data against a JSON Schema

**Files:**
- Create: `src/toimi.tools.tietue/Validation/ValidationResult.cs`
- Create: `src/toimi.tools.tietue/Validation/SchemaValidator.cs`
- Test: `src/toimi.tools.tietue.Tests/SchemaValidatorTests.cs`

- [ ] **Step 1: Write the failing tests**

`src/toimi.tools.tietue.Tests/SchemaValidatorTests.cs`:

```csharp
using System.Text.Json.Nodes;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SchemaValidatorTests
{
  private const string Schema = """
  {
    "type": "object",
    "properties": { "title": { "type": "string" }, "count": { "type": "integer" } },
    "required": ["title"]
  }
  """;

  private readonly SchemaValidator _validator = new();

  [Fact]
  public void Valid_data_passes()
  {
    var data = JsonNode.Parse("""{"title":"hi","count":3}""");
    var result = _validator.Validate(Schema, data);
    Assert.True(result.IsValid);
    Assert.Empty(result.Errors);
  }

  [Fact]
  public void Missing_required_field_fails()
  {
    var data = JsonNode.Parse("""{"count":3}""");
    var result = _validator.Validate(Schema, data);
    Assert.False(result.IsValid);
    Assert.NotEmpty(result.Errors);
  }

  [Fact]
  public void Wrong_type_fails()
  {
    var data = JsonNode.Parse("""{"title":"hi","count":"three"}""");
    var result = _validator.Validate(Schema, data);
    Assert.False(result.IsValid);
  }

  [Fact]
  public void Malformed_schema_reports_invalid_schema()
  {
    var result = _validator.Validate("{ not json", JsonNode.Parse("{}"));
    Assert.False(result.IsValid);
    Assert.Contains(result.Errors, e => e.Contains("schema", StringComparison.OrdinalIgnoreCase));
  }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~SchemaValidatorTests"`
Expected: FAIL — `SchemaValidator` / `ValidationResult` do not exist (compile error).

- [ ] **Step 3: Create `ValidationResult`**

`src/toimi.tools.tietue/Validation/ValidationResult.cs`:

```csharp
namespace toimi.tools.tietue.Validation;

public record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
  public static ValidationResult Valid() => new(true, []);
  public static ValidationResult Invalid(IReadOnlyList<string> errors) => new(false, errors);
  public static ValidationResult Invalid(string error) => new(false, [error]);
}
```

- [ ] **Step 4: Implement `SchemaValidator`**

`src/toimi.tools.tietue/Validation/SchemaValidator.cs`:

```csharp
using System.Text.Json.Nodes;
using Json.Schema;

namespace toimi.tools.tietue.Validation;

public class SchemaValidator
{
  private static readonly EvaluationOptions Options = new()
  {
    OutputFormat = OutputFormat.List,
  };

  public ValidationResult Validate(string schemaJson, JsonNode? data)
  {
    JsonSchema schema;
    try
    {
      schema = JsonSchema.FromText(schemaJson);
    }
    catch (Exception ex)
    {
      return ValidationResult.Invalid($"Invalid schema: {ex.Message}");
    }

    var results = schema.Evaluate(data, Options);
    if (results.IsValid)
    {
      return ValidationResult.Valid();
    }

    var errors = results.Details
      .Where(d => d.HasErrors)
      .SelectMany(d => d.Errors!.Select(e =>
        string.IsNullOrEmpty(d.InstanceLocation.ToString())
          ? e.Value
          : $"{d.InstanceLocation}: {e.Value}"))
      .Distinct()
      .ToList();

    if (errors.Count == 0)
    {
      errors.Add("Data does not match the type schema.");
    }

    return ValidationResult.Invalid(errors);
  }
}
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~SchemaValidatorTests"`
Expected: PASS (4 passed).

> If the `results.Details`/`d.Errors` shape differs in the resolved JsonSchema.Net version, adjust the error-collection to that version's API — the contract is: return distinct human-readable error strings when `results.IsValid` is false. Keep the tests unchanged.

- [ ] **Step 6: Commit**

```bash
git add src/toimi.tools.tietue/Validation src/toimi.tools.tietue.Tests/SchemaValidatorTests.cs
git commit -m "feat(tietue): add JSON Schema validator"
```

---

## Task 5: `TypeRepository` — CRUD over `type_definitions`

**Files:**
- Create: `src/toimi.tools.tietue/Types/TypeRepository.cs`
- Test: `src/toimi.tools.tietue.Tests/TypeRepositoryTests.cs`

- [ ] **Step 1: Write the failing tests**

`src/toimi.tools.tietue.Tests/TypeRepositoryTests.cs`:

```csharp
using System.Text.Json.Nodes;
using toimi.tools.tietue.Types;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TypeRepositoryTests
{
  private const string Schema = """{"type":"object","properties":{"title":{"type":"string"}}}""";

  [Fact]
  public async Task Define_then_get_returns_type()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    await repo.DefineAsync("note", Schema);
    var t = await repo.GetAsync("note");

    Assert.NotNull(t);
    Assert.Equal("note", t!.Name);
    Assert.Equal("string",
      t.JsonSchema.RootElement.GetProperty("properties").GetProperty("title").GetProperty("type").GetString());
  }

  [Fact]
  public async Task Define_is_upsert_by_name()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    await repo.DefineAsync("note", Schema);
    await repo.DefineAsync("note", """{"type":"object","properties":{"body":{"type":"string"}}}""");

    var t = await repo.GetAsync("note");
    Assert.True(t!.JsonSchema.RootElement.GetProperty("properties").TryGetProperty("body", out _));
    Assert.Single(await repo.ListAsync());
  }

  [Fact]
  public async Task Define_rejects_malformed_schema()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);
    await Assert.ThrowsAsync<toimi.tools.tietue.Validation.TietueValidationException>(
      () => repo.DefineAsync("note", "{ not json"));
  }

  [Fact]
  public async Task Delete_removes_type()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);
    await repo.DefineAsync("note", Schema);

    var deleted = await repo.DeleteAsync("note");

    Assert.True(deleted);
    Assert.Null(await repo.GetAsync("note"));
  }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~TypeRepositoryTests"`
Expected: FAIL — `TypeRepository` / `TietueValidationException` do not exist.

- [ ] **Step 3: Create `TietueValidationException`**

`src/toimi.tools.tietue/Validation/TietueValidationException.cs`:

```csharp
namespace toimi.tools.tietue.Validation;

public class TietueValidationException(IReadOnlyList<string> errors)
  : Exception(string.Join("; ", errors))
{
  public IReadOnlyList<string> Errors { get; } = errors;
}
```

- [ ] **Step 4: Implement `TypeRepository`**

`src/toimi.tools.tietue/Types/TypeRepository.cs`:

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Types;

public class TypeRepository(TietueDbContext db)
{
  public async Task<TypeDefinition> DefineAsync(string name, string schemaJson, CancellationToken ct = default)
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

    var now = DateTimeOffset.UtcNow;
    var existing = await db.TypeDefinitions.FirstOrDefaultAsync(t => t.Name == name, ct);
    if (existing is null)
    {
      existing = new TypeDefinition { Name = name, JsonSchema = schema, CreatedAt = now, UpdatedAt = now };
      db.TypeDefinitions.Add(existing);
    }
    else
    {
      existing.JsonSchema = schema;
      existing.UpdatedAt = now;
    }

    await db.SaveChangesAsync(ct);
    return existing;
  }

  public Task<TypeDefinition?> GetAsync(string name, CancellationToken ct = default) =>
    db.TypeDefinitions.FirstOrDefaultAsync(t => t.Name == name, ct);

  public async Task<IReadOnlyList<TypeDefinition>> ListAsync(CancellationToken ct = default) =>
    await db.TypeDefinitions.OrderBy(t => t.Name).ToListAsync(ct);

  public async Task<bool> DeleteAsync(string name, CancellationToken ct = default)
  {
    var t = await db.TypeDefinitions.FirstOrDefaultAsync(x => x.Name == name, ct);
    if (t is null)
    {
      return false;
    }

    db.TypeDefinitions.Remove(t);
    await db.SaveChangesAsync(ct);
    return true;
  }
}
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~TypeRepositoryTests"`
Expected: PASS (4 passed).

- [ ] **Step 6: Commit**

```bash
git add src/toimi.tools.tietue/Types src/toimi.tools.tietue/Validation/TietueValidationException.cs src/toimi.tools.tietue.Tests/TypeRepositoryTests.cs
git commit -m "feat(tietue): add type repository with upsert-by-name"
```

---

## Task 6: `EntityRepository` — validated CRUD + list over `entities`

**Files:**
- Create: `src/toimi.tools.tietue/Entities/EntityRepository.cs`
- Test: `src/toimi.tools.tietue.Tests/EntityRepositoryTests.cs`

- [ ] **Step 1: Write the failing tests**

`src/toimi.tools.tietue.Tests/EntityRepositoryTests.cs`:

```csharp
using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class EntityRepositoryTests
{
  private const string Schema = """
  {"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}
  """;

  private static async Task<(toimi.tools.tietue.Data.TietueDbContext db, EntityRepository repo)> SetupAsync()
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("note", Schema);
    return (db, new EntityRepository(db, new SchemaValidator()));
  }

  [Fact]
  public async Task Create_valid_entity_persists()
  {
    var (db, repo) = await SetupAsync();
    using var _ = db;

    var e = await repo.CreateAsync("note", JsonNode.Parse("""{"title":"hi"}"""), ["x"]);

    Assert.NotEqual(Guid.Empty, e.Id);
    Assert.Equal("note", e.Type);
    Assert.Equal("hi", e.Data.RootElement.GetProperty("title").GetString());
    Assert.Equal(["x"], e.Tags);
  }

  [Fact]
  public async Task Create_with_unknown_type_throws()
  {
    var (db, repo) = await SetupAsync();
    using var _ = db;
    var ex = await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.CreateAsync("ghost", JsonNode.Parse("""{"title":"hi"}"""), []));
    Assert.Contains(ex.Errors, m => m.Contains("ghost"));
  }

  [Fact]
  public async Task Create_invalid_data_throws_with_errors()
  {
    var (db, repo) = await SetupAsync();
    using var _ = db;
    await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.CreateAsync("note", JsonNode.Parse("""{"count":3}"""), []));
  }

  [Fact]
  public async Task Update_revalidates_and_persists()
  {
    var (db, repo) = await SetupAsync();
    using var _ = db;
    var e = await repo.CreateAsync("note", JsonNode.Parse("""{"title":"hi"}"""), []);

    var updated = await repo.UpdateAsync(e.Id, JsonNode.Parse("""{"title":"bye"}"""), ["t"]);

    Assert.Equal("bye", updated!.Data.RootElement.GetProperty("title").GetString());
    Assert.Equal(["t"], updated.Tags);
    await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.UpdateAsync(e.Id, JsonNode.Parse("""{"count":1}"""), null));
  }

  [Fact]
  public async Task List_filters_by_type_and_tag()
  {
    var (db, repo) = await SetupAsync();
    using var _ = db;
    await repo.CreateAsync("note", JsonNode.Parse("""{"title":"a"}"""), ["keep"]);
    await repo.CreateAsync("note", JsonNode.Parse("""{"title":"b"}"""), ["drop"]);

    var keep = await repo.ListAsync("note", tag: "keep", page: 1, size: 20);

    Assert.Single(keep.Items);
    Assert.Equal(2, (await repo.ListAsync("note", tag: null, page: 1, size: 20)).Total);
  }

  [Fact]
  public async Task Delete_removes_entity()
  {
    var (db, repo) = await SetupAsync();
    using var _ = db;
    var e = await repo.CreateAsync("note", JsonNode.Parse("""{"title":"a"}"""), []);

    Assert.True(await repo.DeleteAsync(e.Id));
    Assert.Null(await repo.GetAsync(e.Id));
  }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~EntityRepositoryTests"`
Expected: FAIL — `EntityRepository` and `PagedEntities` do not exist.

- [ ] **Step 3: Implement `EntityRepository`**

`src/toimi.tools.tietue/Entities/EntityRepository.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Entities;

public record PagedEntities(IReadOnlyList<Entity> Items, int Page, int Size, int Total);

public class EntityRepository(TietueDbContext db, SchemaValidator validator)
{
  public async Task<Entity> CreateAsync(string type, JsonNode? data, string[] tags, CancellationToken ct = default)
  {
    var schemaJson = await GetSchemaOrThrowAsync(type, ct);
    Validate(schemaJson, data);

    var now = DateTimeOffset.UtcNow;
    var entity = new Entity
    {
      Id = Guid.NewGuid(),
      Type = type,
      Data = JsonSerializer.SerializeToDocument(data),
      Tags = tags,
      CreatedAt = now,
      UpdatedAt = now,
    };
    db.Entities.Add(entity);
    await db.SaveChangesAsync(ct);
    return entity;
  }

  public Task<Entity?> GetAsync(Guid id, CancellationToken ct = default) =>
    db.Entities.FirstOrDefaultAsync(e => e.Id == id, ct);

  public async Task<Entity?> UpdateAsync(Guid id, JsonNode? data, string[]? tags, CancellationToken ct = default)
  {
    var entity = await db.Entities.FirstOrDefaultAsync(e => e.Id == id, ct);
    if (entity is null)
    {
      return null;
    }

    if (data is not null)
    {
      var schemaJson = await GetSchemaOrThrowAsync(entity.Type, ct);
      Validate(schemaJson, data);
      entity.Data = JsonSerializer.SerializeToDocument(data);
    }

    if (tags is not null)
    {
      entity.Tags = tags;
    }

    entity.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return entity;
  }

  public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
  {
    var entity = await db.Entities.FirstOrDefaultAsync(e => e.Id == id, ct);
    if (entity is null)
    {
      return false;
    }

    db.Entities.Remove(entity);
    await db.SaveChangesAsync(ct);
    return true;
  }

  public async Task<PagedEntities> ListAsync(string? type, string? tag, int page, int size, CancellationToken ct = default)
  {
    page = page <= 0 ? 1 : page;
    size = size <= 0 ? 20 : Math.Clamp(size, 1, 100);

    var query = db.Entities.AsQueryable();
    if (!string.IsNullOrWhiteSpace(type))
    {
      query = query.Where(e => e.Type == type);
    }

    if (!string.IsNullOrWhiteSpace(tag))
    {
      query = query.Where(e => e.Tags.Contains(tag));
    }

    var total = await query.CountAsync(ct);
    var items = await query
      .OrderByDescending(e => e.UpdatedAt)
      .Skip((page - 1) * size)
      .Take(size)
      .ToListAsync(ct);

    return new PagedEntities(items, page, size, total);
  }

  private async Task<string> GetSchemaOrThrowAsync(string type, CancellationToken ct)
  {
    var typeDef = await db.TypeDefinitions.FirstOrDefaultAsync(t => t.Name == type, ct)
      ?? throw new TietueValidationException([$"Unknown type '{type}'. Define it first with define_type."]);
    return typeDef.JsonSchema.RootElement.GetRawText();
  }

  private void Validate(string schemaJson, JsonNode? data)
  {
    var result = validator.Validate(schemaJson, data);
    if (!result.IsValid)
    {
      throw new TietueValidationException(result.Errors);
    }
  }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~EntityRepositoryTests"`
Expected: PASS (6 passed).

- [ ] **Step 5: Commit**

```bash
git add src/toimi.tools.tietue/Entities src/toimi.tools.tietue.Tests/EntityRepositoryTests.cs
git commit -m "feat(tietue): add validated entity repository with list/paging"
```

---

## Task 7: MCP tools for type definitions

**Files:**
- Create: `src/toimi.tools.tietue/Tools/DefineTypeTool.cs`
- Create: `src/toimi.tools.tietue/Tools/ListTypesTool.cs`
- Create: `src/toimi.tools.tietue/Tools/GetTypeTool.cs`
- Create: `src/toimi.tools.tietue/Tools/DeleteTypeTool.cs`
- Test: `src/toimi.tools.tietue.Tests/TypeToolsTests.cs`

- [ ] **Step 1: Write the failing tests** (tools are plain methods — construct with a repo over in-memory DB)

`src/toimi.tools.tietue.Tests/TypeToolsTests.cs`:

```csharp
using System.Text.Json;
using toimi.tools.tietue.Tools;
using toimi.tools.tietue.Types;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TypeToolsTests
{
  private const string Schema = """{"type":"object","properties":{"title":{"type":"string"}}}""";

  [Fact]
  public async Task DefineType_then_ListTypes_includes_it()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    var define = await new DefineTypeTool(repo).DefineType("note", Schema);
    Assert.Contains("note", define);

    var list = await new ListTypesTool(repo).ListTypes();
    using var doc = JsonDocument.Parse(list);
    Assert.Equal("note", doc.RootElement[0].GetProperty("name").GetString());
    // schema is included for catalog injection
    Assert.True(doc.RootElement[0].TryGetProperty("schema", out _));
  }

  [Fact]
  public async Task DefineType_rejects_bad_schema_with_message()
  {
    using var db = TestDb.New();
    var result = await new DefineTypeTool(new TypeRepository(db)).DefineType("note", "{ not json");
    Assert.Contains("Invalid schema", result);
  }

  [Fact]
  public async Task GetType_and_DeleteType_work()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);
    await new DefineTypeTool(repo).DefineType("note", Schema);

    Assert.Contains("title", await new GetTypeTool(repo).GetType("note"));
    Assert.Contains("deleted", await new DeleteTypeTool(repo).DeleteType("note"));
    Assert.Contains("not found", await new GetTypeTool(repo).GetType("note"));
  }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~TypeToolsTests"`
Expected: FAIL — the tool classes do not exist.

- [ ] **Step 3: Implement `DefineTypeTool`**

`src/toimi.tools.tietue/Tools/DefineTypeTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class DefineTypeTool(TypeRepository repository)
{
  [McpServerTool, Description("Define or replace a data type by name. The schema is a JSON Schema (draft 2020-12) describing the shape of entities of this type. Upserts by name.")]
  public async Task<string> DefineType(
      [Description("Unique type name, e.g. 'wishlist_item'")] string name,
      [Description("JSON Schema (draft 2020-12) for entities of this type")] string schema)
  {
    try
    {
      var t = await repository.DefineAsync(name, schema);
      return JsonSerializer.Serialize(new { t.Name, defined = true });
    }
    catch (TietueValidationException ex)
    {
      return string.Join("; ", ex.Errors);
    }
  }
}
```

- [ ] **Step 4: Implement `ListTypesTool`** (returns catalog-injection-ready data: name + schema)

`src/toimi.tools.tietue/Tools/ListTypesTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Types;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class ListTypesTool(TypeRepository repository)
{
  [McpServerTool, Description("List all defined data types with their JSON Schemas. Use this to discover what types exist and how to shape their data before creating entities.")]
  public async Task<string> ListTypes()
  {
    var types = await repository.ListAsync();
    var rows = types.Select(t => new JsonObject
    {
      ["name"] = t.Name,
      ["schema"] = JsonNode.Parse(t.JsonSchema.RootElement.GetRawText()),
    }).ToArray();
    return JsonSerializer.Serialize(new JsonArray(rows));
  }
}
```

- [ ] **Step 5: Implement `GetTypeTool`**

`src/toimi.tools.tietue/Tools/GetTypeTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Types;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class GetTypeTool(TypeRepository repository)
{
  [McpServerTool, Description("Get a single data type and its JSON Schema by name.")]
  public async Task<string> GetType(
      [Description("The type name")] string name)
  {
    var t = await repository.GetAsync(name);
    if (t is null)
    {
      return $"Type '{name}' not found.";
    }

    return JsonSerializer.Serialize(new
    {
      t.Name,
      Schema = JsonDocument.Parse(t.JsonSchema.RootElement.GetRawText()),
    });
  }
}
```

- [ ] **Step 6: Implement `DeleteTypeTool`**

`src/toimi.tools.tietue/Tools/DeleteTypeTool.cs`:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Types;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class DeleteTypeTool(TypeRepository repository)
{
  [McpServerTool, Description("Delete a data type by name. Does not delete existing entities of that type.")]
  public async Task<string> DeleteType(
      [Description("The type name")] string name)
  {
    var deleted = await repository.DeleteAsync(name);
    return deleted ? $"Type '{name}' deleted." : $"Type '{name}' not found.";
  }
}
```

- [ ] **Step 7: Run to verify they pass**

Run: `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~TypeToolsTests"`
Expected: PASS (3 passed).

- [ ] **Step 8: Commit**

```bash
git add src/toimi.tools.tietue/Tools src/toimi.tools.tietue.Tests/TypeToolsTests.cs
git commit -m "feat(tietue): add MCP tools for type definitions"
```

---

## Task 8: MCP tools for entities

**Files:**
- Create: `src/toimi.tools.tietue/Tools/CreateEntityTool.cs`
- Create: `src/toimi.tools.tietue/Tools/GetEntityTool.cs`
- Create: `src/toimi.tools.tietue/Tools/UpdateEntityTool.cs`
- Create: `src/toimi.tools.tietue/Tools/DeleteEntityTool.cs`
- Create: `src/toimi.tools.tietue/Tools/ListEntitiesTool.cs`
- Test: `src/toimi.tools.tietue.Tests/EntityToolsTests.cs`

- [ ] **Step 1: Write the failing tests**

`src/toimi.tools.tietue.Tests/EntityToolsTests.cs`:

```csharp
using System.Text.Json;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Tools;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class EntityToolsTests
{
  private const string Schema = """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""";

  private static async Task<EntityRepository> RepoWithNoteTypeAsync(toimi.tools.tietue.Data.TietueDbContext db)
  {
    await new TypeRepository(db).DefineAsync("note", Schema);
    return new EntityRepository(db, new SchemaValidator());
  }

  [Fact]
  public async Task Create_get_update_delete_round_trip()
  {
    using var db = TestDb.New();
    var repo = await RepoWithNoteTypeAsync(db);

    var created = await new CreateEntityTool(repo).Create("note", """{"title":"hi"}""", null);
    using var createdDoc = JsonDocument.Parse(created);
    var id = createdDoc.RootElement.GetProperty("id").GetString()!;

    Assert.Contains("hi", await new GetEntityTool(repo).Get(id));
    Assert.Contains("bye", await new UpdateEntityTool(repo).Update(id, """{"title":"bye"}""", null));
    Assert.Contains("deleted", await new DeleteEntityTool(repo).Delete(id));
    Assert.Contains("not found", await new GetEntityTool(repo).Get(id));
  }

  [Fact]
  public async Task Create_invalid_data_returns_validation_message()
  {
    using var db = TestDb.New();
    var repo = await RepoWithNoteTypeAsync(db);
    var result = await new CreateEntityTool(repo).Create("note", """{"count":3}""", null);
    Assert.Contains("title", result, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Create_with_malformed_json_returns_message()
  {
    using var db = TestDb.New();
    var repo = await RepoWithNoteTypeAsync(db);
    var result = await new CreateEntityTool(repo).Create("note", "{ not json", null);
    Assert.Contains("Invalid data JSON", result);
  }

  [Fact]
  public async Task List_returns_entities_of_type()
  {
    using var db = TestDb.New();
    var repo = await RepoWithNoteTypeAsync(db);
    await new CreateEntityTool(repo).Create("note", """{"title":"a"}""", "x,y");

    var list = await new ListEntitiesTool(repo).List("note", null, 1, 20);
    using var doc = JsonDocument.Parse(list);
    Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
  }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~EntityToolsTests"`
Expected: FAIL — the tool classes do not exist.

- [ ] **Step 3: Implement `CreateEntityTool`**

`src/toimi.tools.tietue/Tools/CreateEntityTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class CreateEntityTool(EntityRepository repository)
{
  [McpServerTool, Description("Create an entity of a defined type. 'data' is a JSON object validated against the type's schema. Use list_types first to learn the schema.")]
  public async Task<string> Create(
      [Description("The type name (must be defined)")] string type,
      [Description("JSON object with the entity's fields")] string data,
      [Description("Optional comma-separated tags")] string? tags = null)
  {
    JsonNode? node;
    try
    {
      node = JsonNode.Parse(data);
    }
    catch (JsonException ex)
    {
      return $"Invalid data JSON: {ex.Message}";
    }

    try
    {
      var e = await repository.CreateAsync(type, node, ToolHelpers.ParseTags(tags));
      return ToolHelpers.Render(e);
    }
    catch (TietueValidationException ex)
    {
      return string.Join("; ", ex.Errors);
    }
  }
}
```

- [ ] **Step 4: Create the shared `ToolHelpers`**

`src/toimi.tools.tietue/Tools/ToolHelpers.cs`:

```csharp
using System.Text.Json;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Tools;

internal static class ToolHelpers
{
  public static string[] ParseTags(string? tags) =>
    string.IsNullOrWhiteSpace(tags)
      ? []
      : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

  public static string Render(Entity e) =>
    JsonSerializer.Serialize(new
    {
      id = e.Id.ToString(),
      type = e.Type,
      data = JsonDocument.Parse(e.Data.RootElement.GetRawText()),
      tags = e.Tags,
      createdAt = e.CreatedAt.ToString("o"),
      updatedAt = e.UpdatedAt.ToString("o"),
    });
}
```

- [ ] **Step 5: Implement `GetEntityTool`**

`src/toimi.tools.tietue/Tools/GetEntityTool.cs`:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Entities;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class GetEntityTool(EntityRepository repository)
{
  [McpServerTool, Description("Get a single entity by id.")]
  public async Task<string> Get(
      [Description("The entity id (GUID)")] string id)
  {
    if (!Guid.TryParse(id, out var guid))
    {
      return "Invalid id format. Expected a GUID.";
    }

    var e = await repository.GetAsync(guid);
    return e is null ? $"Entity '{id}' not found." : ToolHelpers.Render(e);
  }
}
```

- [ ] **Step 6: Implement `UpdateEntityTool`**

`src/toimi.tools.tietue/Tools/UpdateEntityTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class UpdateEntityTool(EntityRepository repository)
{
  [McpServerTool, Description("Update an entity's data and/or tags. 'data' (if provided) replaces the entity's fields and is re-validated against the type schema.")]
  public async Task<string> Update(
      [Description("The entity id (GUID)")] string id,
      [Description("Optional new JSON object for the entity's fields")] string? data = null,
      [Description("Optional comma-separated tags (replaces existing)")] string? tags = null)
  {
    if (!Guid.TryParse(id, out var guid))
    {
      return "Invalid id format. Expected a GUID.";
    }

    JsonNode? node = null;
    if (data is not null)
    {
      try
      {
        node = JsonNode.Parse(data);
      }
      catch (JsonException ex)
      {
        return $"Invalid data JSON: {ex.Message}";
      }
    }

    try
    {
      var e = await repository.UpdateAsync(guid, node, tags is null ? null : ToolHelpers.ParseTags(tags));
      return e is null ? $"Entity '{id}' not found." : ToolHelpers.Render(e);
    }
    catch (TietueValidationException ex)
    {
      return string.Join("; ", ex.Errors);
    }
  }
}
```

- [ ] **Step 7: Implement `DeleteEntityTool`**

`src/toimi.tools.tietue/Tools/DeleteEntityTool.cs`:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Entities;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class DeleteEntityTool(EntityRepository repository)
{
  [McpServerTool, Description("Delete an entity by id.")]
  public async Task<string> Delete(
      [Description("The entity id (GUID)")] string id)
  {
    if (!Guid.TryParse(id, out var guid))
    {
      return "Invalid id format. Expected a GUID.";
    }

    var deleted = await repository.DeleteAsync(guid);
    return deleted ? $"Entity '{id}' deleted." : $"Entity '{id}' not found.";
  }
}
```

- [ ] **Step 8: Implement `ListEntitiesTool`**

`src/toimi.tools.tietue/Tools/ListEntitiesTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Entities;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class ListEntitiesTool(EntityRepository repository)
{
  [McpServerTool, Description("List entities, optionally filtered by type and/or a single tag, with paging.")]
  public async Task<string> List(
      [Description("Optional type name to filter by")] string? type = null,
      [Description("Optional single tag to filter by")] string? tag = null,
      [Description("Page number (1-based, default 1)")] int page = 1,
      [Description("Page size (default 20, max 100)")] int size = 20)
  {
    var result = await repository.ListAsync(type, tag, page, size);
    var items = result.Items.Select(e => new JsonObject
    {
      ["id"] = e.Id.ToString(),
      ["type"] = e.Type,
      ["data"] = JsonNode.Parse(e.Data.RootElement.GetRawText()),
      ["tags"] = new JsonArray(e.Tags.Select(t => (JsonNode)t!).ToArray()),
      ["updatedAt"] = e.UpdatedAt.ToString("o"),
    }).ToArray();

    return JsonSerializer.Serialize(new JsonObject
    {
      ["items"] = new JsonArray(items),
      ["page"] = result.Page,
      ["size"] = result.Size,
      ["total"] = result.Total,
    });
  }
}
```

- [ ] **Step 9: Run to verify they pass**

Run: `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~EntityToolsTests"`
Expected: PASS (4 passed).

- [ ] **Step 10: Commit**

```bash
git add src/toimi.tools.tietue/Tools src/toimi.tools.tietue.Tests/EntityToolsTests.cs
git commit -m "feat(tietue): add MCP tools for entity CRUD and list"
```

---

## Task 9: Admin endpoints

**Files:**
- Create: `src/toimi.tools.tietue/Admin/AdminEndpoints.cs`
- Test: `src/toimi.tools.tietue.Tests/AdminEndpointsTests.cs`

> The admin surface is read + delete only for Phase 1 (entities are generic; edits go through the schema-validating MCP `update`). This mirrors the read-heavy admin panels of the other servers while keeping validation centralized.

- [ ] **Step 1: Implement `AdminEndpoints`**

`src/toimi.tools.tietue/Admin/AdminEndpoints.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Toimi.Core.Admin;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Admin;

public static class AdminEndpoints
{
  public record EntityItem(
      Guid Id, string Type, string Data, string[] Tags,
      DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

  public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Size, int Total);

  public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
  {
    var admin = app.MapAdmin();

    admin.MapGet("/summary", async (TietueDbContext db, string? q, int limit = 0) =>
    {
      limit = limit <= 0 ? 50 : Math.Clamp(limit, 1, 200);
      var query = db.Entities.AsQueryable();
      if (!string.IsNullOrWhiteSpace(q))
      {
        query = query.Where(e => e.Type == q);
      }

      var rows = await query
        .OrderByDescending(e => e.UpdatedAt)
        .Take(limit)
        .Select(e => new AdminSummaryDto(
          e.Id.ToString(),
          e.Type,
          e.Type,
          $"{e.Tags.Length} tag(s)",
          e.CreatedAt,
          e.UpdatedAt))
        .ToListAsync();
      return Results.Ok(rows);
    });

    admin.MapGet("/items", async (TietueDbContext db, string? q, int page = 0, int size = 0) =>
    {
      page = page <= 0 ? 1 : page;
      size = size <= 0 ? 20 : Math.Clamp(size, 1, 100);
      var query = db.Entities.AsQueryable();
      if (!string.IsNullOrWhiteSpace(q))
      {
        query = query.Where(e => e.Type == q);
      }

      var total = await query.CountAsync();
      var items = await query
        .OrderByDescending(e => e.UpdatedAt)
        .Skip((page - 1) * size).Take(size)
        .Select(e => new EntityItem(e.Id, e.Type, e.Data.RootElement.GetRawText(), e.Tags, e.CreatedAt, e.UpdatedAt))
        .ToListAsync();
      return Results.Ok(new PagedResult<EntityItem>(items, page, size, total));
    });

    admin.MapGet("/items/{id:guid}", async (TietueDbContext db, Guid id) =>
    {
      var e = await db.Entities.FindAsync(id);
      return e is null
        ? Results.NotFound()
        : Results.Ok(new EntityItem(e.Id, e.Type, e.Data.RootElement.GetRawText(), e.Tags, e.CreatedAt, e.UpdatedAt));
    });

    admin.MapDelete("/items/{id:guid}", async (TietueDbContext db, Guid id) =>
    {
      var e = await db.Entities.FindAsync(id);
      if (e is null)
      {
        return Results.NotFound();
      }

      db.Entities.Remove(e);
      await db.SaveChangesAsync();
      return Results.NoContent();
    });
  }
}
```

- [ ] **Step 2: Write the failing test** (full host via `WebApplicationFactory`, in-memory DB)

`src/toimi.tools.tietue.Tests/AdminEndpointsTests.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Toimi.Core.Admin;
using toimi.tools.tietue.Data;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class AdminEndpointsTests : IDisposable
{
  private readonly TietueTestFactory _factory = new();

  [Fact]
  public async Task Summary_returns_entity_summaries()
  {
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<TietueDbContext>();
      db.Entities.Add(new Entity
      {
        Id = Guid.NewGuid(),
        Type = "note",
        Data = JsonDocument.Parse("""{"title":"x"}"""),
        Tags = ["a"],
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
      });
      await db.SaveChangesAsync();
    }

    var client = _factory.CreateClient();
    var summary = await client.GetFromJsonAsync<AdminSummaryDto[]>("/admin/summary");
    var item = Assert.Single(summary!);
    Assert.Equal("note", item.Kind);
  }

  [Fact]
  public async Task Delete_removes_entity()
  {
    var id = Guid.NewGuid();
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<TietueDbContext>();
      db.Entities.Add(new Entity
      {
        Id = id, Type = "note", Data = JsonDocument.Parse("{}"),
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
      });
      await db.SaveChangesAsync();
    }

    var client = _factory.CreateClient();
    var resp = await client.DeleteAsync($"/admin/items/{id}");
    resp.EnsureSuccessStatusCode();

    using var scope2 = _factory.Services.CreateScope();
    var db2 = scope2.ServiceProvider.GetRequiredService<TietueDbContext>();
    Assert.Null(await db2.Entities.FindAsync(id));
  }

  public void Dispose()
  {
    _factory.Dispose();
    GC.SuppressFinalize(this);
  }
}

public class TietueTestFactory : WebApplicationFactory<Program>
{
  private readonly string _dbName = $"tietue-{Guid.NewGuid()}";

  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    builder.UseSetting("ConnectionStrings:Tietue", "Server=ignored");
    builder.ConfigureServices(services =>
    {
      var configOptType = typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<TietueDbContext>);
      var toRemove = services.Where(d =>
        d.ServiceType == typeof(DbContextOptions<TietueDbContext>)
        || d.ServiceType == typeof(DbContextOptions)
        || d.ServiceType == configOptType
        || d.ServiceType == typeof(TietueDbContext)).ToArray();
      foreach (var d in toRemove) services.Remove(d);

      services.AddDbContext<TietueDbContext>(o => o.UseInMemoryDatabase(_dbName));
    });
  }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~AdminEndpointsTests"`
Expected: FAIL — `/admin` routes return 404 because `Program.cs` does not yet register the DbContext or map admin endpoints (fixed in Task 11).

- [ ] **Step 4: Commit (test + endpoint code; wiring lands in Task 11)**

```bash
git add src/toimi.tools.tietue/Admin src/toimi.tools.tietue.Tests/AdminEndpointsTests.cs
git commit -m "feat(tietue): add admin endpoints (read + delete)"
```

---

## Task 10: Generate the initial EF migration

**Files:**
- Create: `src/toimi.tools.tietue/Migrations/*` (generated)

- [ ] **Step 1: Ensure the EF CLI is available**

Run: `dotnet ef --version`
Expected: a version prints. If "command not found", run `dotnet tool install --global dotnet-ef` first.

- [ ] **Step 2: Add the migration**

Run:
```bash
dotnet ef migrations add InitialCreate --project src/toimi.tools.tietue --startup-project src/toimi.tools.tietue
```
Expected: `Done.` and a new `src/toimi.tools.tietue/Migrations/` folder containing `<timestamp>_InitialCreate.cs`, `<timestamp>_InitialCreate.Designer.cs`, and `TietueDbContextModelSnapshot.cs`.

- [ ] **Step 3: Verify the migration created both tables with jsonb columns**

Run: `grep -n "jsonb\|type_definitions\|entities" src/toimi.tools.tietue/Migrations/*_InitialCreate.cs`
Expected: matches showing `type_definitions` and `entities` tables and `jsonb` column type for `data` and `json_schema`.

- [ ] **Step 4: Build to confirm the generated code compiles**

Run: `dotnet build src/toimi.tools.tietue/toimi.tools.tietue.csproj`
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add src/toimi.tools.tietue/Migrations
git commit -m "feat(tietue): add initial EF migration"
```

---

## Task 11: Wire up `Program.cs` (DI, migrate-on-start, admin)

**Files:**
- Modify: `src/toimi.tools.tietue/Program.cs`

- [ ] **Step 1: Replace `Program.cs` with the fully wired host**

`src/toimi.tools.tietue/Program.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Tietue")
  ?? throw new InvalidOperationException("ConnectionStrings:Tietue is required");

builder.Services.AddDbContext<TietueDbContext>(options =>
  options.UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton<SchemaValidator>();
builder.Services.AddScoped<TypeRepository>();
builder.Services.AddScoped<EntityRepository>();

builder.Services
  .AddMcpServer(options =>
  {
    options.ServerInfo = new()
    {
      Name = "tietue",
      Version = "1.0.0"
    };
  })
  .WithHttpTransport()
  .WithToolsFromAssembly();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
  var dbContext = scope.ServiceProvider.GetRequiredService<TietueDbContext>();
  if (dbContext.Database.IsRelational())
  {
    await dbContext.Database.MigrateAsync();
  }
}

app.MapMcp();
app.MapGet("/health", () => Results.Ok());
toimi.tools.tietue.Admin.AdminEndpoints.MapAdminEndpoints(app);

app.Run();

public partial class Program;
```

> `IsRelational()` is false under the in-memory test provider, so `MigrateAsync()` is skipped in tests — the same guard the other servers use.

- [ ] **Step 2: Run the full test suite (admin tests now pass with wiring in place)**

Run: `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`
Expected: PASS — all tests green (DbContext, SchemaValidator, TypeRepository, EntityRepository, TypeTools, EntityTools, AdminEndpoints).

- [ ] **Step 3: Commit**

```bash
git add src/toimi.tools.tietue/Program.cs
git commit -m "feat(tietue): wire DI, migrate-on-start, and admin endpoints"
```

---

## Task 12: Deployment wiring (Docker, k8s, DB creation, web registration)

**Files:**
- Create: `src/toimi.tools.tietue/Dockerfile`
- Create: `k8s/base/tools-tietue/deployment.yaml`
- Create: `k8s/base/tools-tietue/service.yaml`
- Create: `k8s/base/tools-tietue/kustomization.yaml`
- Modify: `scripts/dev-setup.sh`
- Modify: `infrastructure/base/helm/postgresql-values.yaml`
- Modify: `k8s/overlays/dev/secrets.env.example` and `k8s/overlays/server/secrets.env.example` (whichever exist)
- Modify: `src/toimi.web/appsettings.json`

- [ ] **Step 1: Create the Dockerfile**

`src/toimi.tools.tietue/Dockerfile`:

```dockerfile
# Build context = REPO ROOT (this file COPYs toimi.sln and src/).
# Build: docker build -f src/toimi.tools.tietue/Dockerfile -t <registry>/<image>:latest .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY toimi.sln .
COPY src/toimi.core/toimi.core.csproj src/toimi.core/
COPY src/toimi.notifications/toimi.notifications.csproj src/toimi.notifications/
COPY src/toimi.tools.ajastin/toimi.tools.ajastin.csproj src/toimi.tools.ajastin/
COPY src/toimi.tools.verkko/toimi.tools.verkko.csproj src/toimi.tools.verkko/
COPY src/toimi.tools.koti/toimi.tools.koti.csproj src/toimi.tools.koti/
COPY src/toimi.tools.muistio/toimi.tools.muistio.csproj src/toimi.tools.muistio/
COPY src/toimi.tools.muistutin/toimi.tools.muistutin.csproj src/toimi.tools.muistutin/
COPY src/toimi.tools.taidot/toimi.tools.taidot.csproj src/toimi.tools.taidot/
COPY src/toimi.tools.ruutu/toimi.tools.ruutu.csproj src/toimi.tools.ruutu/
COPY src/toimi.tools.tietue/toimi.tools.tietue.csproj src/toimi.tools.tietue/
COPY src/toimi.web/toimi.web.csproj src/toimi.web/
RUN dotnet restore src/toimi.tools.tietue/toimi.tools.tietue.csproj

COPY src/ src/
RUN dotnet publish src/toimi.tools.tietue/toimi.tools.tietue.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "toimi.tools.tietue.dll"]
```

> The `COPY ... .csproj` lines mirror the other Dockerfiles. If a referenced project file does not exist at build time, drop that line — only `toimi.sln`, `toimi.core`, and `toimi.tools.tietue` are strictly required for this image's restore.

- [ ] **Step 2: Create the k8s deployment**

`k8s/base/tools-tietue/deployment.yaml`:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: toimi-tools-tietue
  namespace: apps
  labels:
    app: toimi-tools-tietue
spec:
  replicas: 1
  selector:
    matchLabels:
      app: toimi-tools-tietue
  template:
    metadata:
      labels:
        app: toimi-tools-tietue
    spec:
      containers:
        - name: toimi-tools-tietue
          image: ${IMAGE_REGISTRY}/toimi-tools-tietue:latest
          ports:
            - containerPort: 8080
          env:
            - name: ConnectionStrings__Tietue
              valueFrom:
                secretKeyRef:
                  name: toimi-secrets
                  key: tietue-connection-string
          livenessProbe:
            httpGet:
              path: /health
              port: 8080
            initialDelaySeconds: 5
            periodSeconds: 10
          readinessProbe:
            httpGet:
              path: /health
              port: 8080
            initialDelaySeconds: 3
            periodSeconds: 5
```

- [ ] **Step 3: Create the k8s service**

`k8s/base/tools-tietue/service.yaml`:

```yaml
apiVersion: v1
kind: Service
metadata:
  name: toimi-tools-tietue
  namespace: apps
spec:
  selector:
    app: toimi-tools-tietue
  ports:
    - port: 80
      targetPort: 8080
```

- [ ] **Step 4: Create the k8s kustomization**

`k8s/base/tools-tietue/kustomization.yaml`:

```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization

resources:
  - deployment.yaml
  - service.yaml
```

- [ ] **Step 5: Add `tietue` to the dev DB-creation loop**

In `scripts/dev-setup.sh`, line ~131, change:
```bash
for DB_NAME in muistio muistutin ajastin toimi ruutu; do
```
to:
```bash
for DB_NAME in muistio muistutin ajastin toimi ruutu tietue; do
```

- [ ] **Step 6: Add `tietue` to the server DB-creation SQL**

In `infrastructure/base/helm/postgresql-values.yaml`, in the `create-databases.sql` block (after the `CREATE DATABASE ajastin;` line), add:
```sql
    CREATE DATABASE tietue;
```
(Match the existing indentation in that block.)

- [ ] **Step 7: Add the connection-string secret key to both overlay templates**

Add this line to `k8s/overlays/dev/secrets.env.example` **and** `k8s/overlays/server/secrets.env.example` (matching the format of the existing `*-connection-string` lines in each file; value is set in the real, gitignored `secrets.env`):
```
tietue-connection-string=Host=postgresql.data.svc.cluster.local;Database=tietue;Username=postgres;Password=CHANGEME
```

- [ ] **Step 8: Register the tietue MCP server and admin tool in the web app**

In `src/toimi.web/appsettings.json`, make two edits.

First, add `"tietue"` to the `Toimi:Admin:Tools` array (line 7):
```json
      "Tools": ["muistio", "muistutin", "ajastin", "taidot", "tietue"]
```

Second, append a new entry to the `Toimi:McpServers` array (after the `verkko` entry, before the closing `]` on line 49). Note the `/sse` URL suffix the other entries use:
```json
      },
      {
        "Name": "tietue",
        "Transport": "Http",
        "Url": "http://toimi-tools-tietue.apps.svc.cluster.local/sse"
      }
```
(Add a comma after the `verkko` entry's closing `}` so the array stays valid JSON.)

- [ ] **Step 9: Validate the k8s base renders**

Run: `kubectl kustomize k8s/base/tools-tietue`
Expected: prints the Deployment and Service YAML with no errors. (If `kubectl` is unavailable in this environment, skip and rely on CI.)

- [ ] **Step 10: Lint changed shell/yaml**

Run: `scripts/lint.sh`
Expected: passes (or only pre-existing unrelated warnings). Fix any new yamllint/shellcheck issues introduced by the edits.

- [ ] **Step 11: Commit**

```bash
git add src/toimi.tools.tietue/Dockerfile k8s/base/tools-tietue scripts/dev-setup.sh infrastructure/base/helm/postgresql-values.yaml k8s/overlays src/toimi.web/appsettings.json
git commit -m "feat(tietue): add Dockerfile, k8s base, DB creation, and web MCP registration"
```

---

## Task 13: Full-suite verification

**Files:** none (verification only)

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build toimi.sln`
Expected: `Build succeeded.` 0 errors.

- [ ] **Step 2: Run the whole test suite**

Run: `dotnet test toimi.sln`
Expected: all test projects pass, including `toimi.tools.tietue.Tests` (DbContext, SchemaValidator, TypeRepository, EntityRepository, TypeTools, EntityTools, AdminEndpoints).

- [ ] **Step 3: Manual smoke test against a real Postgres (optional but recommended)**

Start a local Postgres with a `tietue` database, then:
```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/toimi.tools.tietue
```
In another shell:
```bash
curl -s localhost:5210/health        # expect 200
```
Stop the process. Migration-on-start should have created the `entities` and `type_definitions` tables. Confirm with `psql -d tietue -c '\dt'`.

- [ ] **Step 4: Final commit if anything changed**

```bash
git add -A
git commit -m "chore(tietue): phase 1 engine core complete" --allow-empty
```

---

## Phase 1 Done — what exists now

A deployable `tietue` pod that lets the AI **define JSON-Schema types at runtime**, **validate** entity data against them, and **CRUD + list** generic entities in PostgreSQL `jsonb`, with `/admin` read/delete and `/health`. This is the substrate the later phases build on.

**Next phases (separate plans):**
- **Phase 2** — `SemanticIndex` declarative behavior (Qdrant), seed `memory` + `skill` types, wire system-prompt catalog injection. Retires muistio + taidot.
- **Phase 3** — triggers, scheduler, native handlers (`notify`, `set-field`, `poll-diff`), `entity_events`; seed `reminder`. Retires muistutin.
- **Phase 4** — `message` handler + lazy conversations + self-scheduling; seed `schedule`. Retires ajastin.
- **Phase 5** — script sandbox + escalation.
- **Phase 6** — cutover: delete the four old pods/DBs/bases.
