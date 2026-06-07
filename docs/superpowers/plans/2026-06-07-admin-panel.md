# Admin Panel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a single-user admin panel that lists and manages all stored items (memories, reminders, schedules, skills) across Toimi's tool servers, with a unified search/recents dashboard plus per-store CRUD pages.

**Architecture:** Each tool server exposes a uniform `/admin/*` REST surface (summary + items + domain actions). `toimi.web` hosts (a) a fan-out aggregator that merges `/admin/summary` from every tool for the dashboard, and (b) a single generic forwarder `/api/admin/{tool}/{**path}` that proxies to `toimi-tools-{tool}` based on a config allowlist — adding a new tool server requires only a config entry. A `react-router-dom` route tree under `/admin` in the existing SPA renders the dashboard and four domain pages.

**Tech Stack:** .NET 10 minimal APIs, EF Core 10 + Npgsql, Qdrant.Client (taidot only), Microsoft.EntityFrameworkCore.InMemory (test provider), xUnit + WebApplicationFactory, React 19 + Vite + react-router-dom, Tailwind CSS.

**Spec reference:** `docs/superpowers/specs/2026-06-07-admin-panel-design.md`

**Field name reconciliation (overrides spec where it disagrees with real code):**
- Memory: `Content`, `Category`, `Tags[]`, `Source`, `Confirmed` (bool), `ExpiresAt?` — has `UpdatedAt` already.
- Reminder: `Title`, `Description`, `DateTimeUtc`, `TimeZone`, `RecurrenceRule?`, `DisplayEndUtc?`, `IsCompleted`, `NotifiedAt?` — needs new `UpdatedAt` column.
- Schedule: `Name`, `CronExpression?`, `RunAt?`, `Prompt`, `Enabled`, `LastRunAt?` — needs new `UpdatedAt` column.
- Skill: `Name`, `Description`, `Instructions`, `Tags[]` — stored in Qdrant; needs `updated_at` added to payload (read defaults from `created_at` when absent).

---

## File Structure

### Created

| Path | Responsibility |
|---|---|
| `src/toimi.core/Admin/AdminSummaryDto.cs` | Shared summary DTO POCO |
| `src/toimi.core/Admin/AdminError.cs` | Aggregator per-tool error POCO + `AggregatedSummary` wrapper |
| `src/toimi.core/Admin/AdminEndpointBuilder.cs` | `MapAdmin()` route-group helper |
| `src/toimi.tools.muistio/Admin/AdminEndpoints.cs` | muistio `/admin/*` minimal API endpoints |
| `src/toimi.tools.muistio.Tests/` (new project) | xUnit tests using WebApplicationFactory + EF InMemory |
| `src/toimi.tools.muistutin/Admin/AdminEndpoints.cs` | muistutin `/admin/*` |
| `src/toimi.tools.muistutin/Migrations/<ts>_AddReminderUpdatedAt.cs` | EF migration adding `updated_at` |
| `src/toimi.tools.muistutin.Tests/` (new project) | xUnit tests |
| `src/toimi.tools.ajastin/Admin/AdminEndpoints.cs` | ajastin `/admin/*` |
| `src/toimi.tools.ajastin/Migrations/<ts>_AddScheduleUpdatedAt.cs` | EF migration adding `updated_at` |
| `src/toimi.tools.ajastin.Tests/` (new project) | xUnit tests |
| `src/toimi.tools.taidot/Admin/AdminEndpoints.cs` | taidot `/admin/*` |
| `src/toimi.tools.taidot/Skills/SkillAdminRepository.cs` | Thin admin facade over `SkillRepository` (paged list, update, delete by id, includes `UpdatedAt`) |
| `src/toimi.web/Admin/AdminToolsOptions.cs` | `Toimi:Admin:Tools` config binding |
| `src/toimi.web/Admin/AdminEndpoints.cs` | `/api/admin/summary` aggregator + `/api/admin/{tool}/{**path}` forwarder |
| `src/toimi.web/ClientApp/src/admin/AdminLayout.tsx` | Sidebar + outlet shell |
| `src/toimi.web/ClientApp/src/admin/useAdmin.ts` | Typed fetch hooks + `If-Unmodified-Since` plumbing |
| `src/toimi.web/ClientApp/src/admin/useAdminSummary.ts` | Aggregator hook |
| `src/toimi.web/ClientApp/src/admin/DataTable.tsx` | Shared table primitive |
| `src/toimi.web/ClientApp/src/admin/ConfirmDelete.tsx` | Delete confirmation modal |
| `src/toimi.web/ClientApp/src/admin/EmptyState.tsx` | Empty list placeholder |
| `src/toimi.web/ClientApp/src/admin/ErrorBanner.tsx` | Aggregator partial-failure banner |
| `src/toimi.web/ClientApp/src/admin/StaleConflictModal.tsx` | 409 conflict prompt |
| `src/toimi.web/ClientApp/src/admin/DashboardPage.tsx` | Global search + recents |
| `src/toimi.web/ClientApp/src/admin/MemoriesPage.tsx` | muistio CRUD |
| `src/toimi.web/ClientApp/src/admin/RemindersPage.tsx` | muistutin CRUD + complete action |
| `src/toimi.web/ClientApp/src/admin/SchedulesPage.tsx` | ajastin list |
| `src/toimi.web/ClientApp/src/admin/ScheduleDetailPage.tsx` | schedule detail + runs |
| `src/toimi.web/ClientApp/src/admin/SkillsPage.tsx` | taidot CRUD |

### Modified

| Path | Change |
|---|---|
| `src/toimi.tools.muistio/Program.cs` | Add `MapAdminEndpoints()`; remove obsolete `/api/memories` group |
| `src/toimi.tools.muistutin/Program.cs` | Add `MapAdminEndpoints()` |
| `src/toimi.tools.muistutin/Data/Reminder.cs` | Add `UpdatedAt` property |
| `src/toimi.tools.muistutin/Data/ReminderConfiguration.cs` | Configure `UpdatedAt` |
| `src/toimi.tools.muistutin/Data/ReminderRepository.cs` | Update `UpdatedAt` on writes |
| `src/toimi.tools.ajastin/Program.cs` | Add `MapAdminEndpoints()`; replace `AddHttpClient("ajastin")` with named-loop registration — N/A here, that change is in `toimi.web` |
| `src/toimi.tools.ajastin/Data/Schedule.cs` | Add `UpdatedAt` property |
| `src/toimi.tools.ajastin/Data/ScheduleConfiguration.cs` | Configure `UpdatedAt` |
| `src/toimi.tools.ajastin/Data/ScheduleRepository.cs` | Update `UpdatedAt` on writes |
| `src/toimi.tools.taidot/Program.cs` | Add `MapAdminEndpoints()`; register `SkillAdminRepository` |
| `src/toimi.tools.taidot/Skills/SkillRepository.cs` | Write `updated_at` payload field on upserts; read it on projection |
| `src/toimi.web/Program.cs` | Replace single `AddHttpClient("ajastin")` with loop over `AdminToolsOptions.Tools`; add admin endpoints map |
| `src/toimi.web/appsettings.json` | Add `Toimi:Admin:Tools` array |
| `src/toimi.web/ClientApp/package.json` | Add `react-router-dom` |
| `src/toimi.web/ClientApp/src/App.tsx` | Add `BrowserRouter` with chat + admin routes |
| `src/toimi.web/ClientApp/src/components/ToimiView.tsx` | Add "Admin" link in header |
| `toimi.sln` | Add the four new `*.Tests` projects |

---

## Task 1: Shared admin contract in `toimi.core`

**Files:**
- Create: `src/toimi.core/Admin/AdminSummaryDto.cs`
- Create: `src/toimi.core/Admin/AdminError.cs`
- Create: `src/toimi.core/Admin/AdminEndpointBuilder.cs`

- [ ] **Step 1: Create `AdminSummaryDto.cs`**

```csharp
namespace Toimi.Core.Admin;

public record AdminSummaryDto(
    string Id,
    string Kind,
    string Title,
    string? Subtitle,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
```

- [ ] **Step 2: Create `AdminError.cs`**

```csharp
namespace Toimi.Core.Admin;

public record AdminError(string Tool, string Message);

public record AggregatedSummary(
    IReadOnlyList<AdminSummaryDto> Items,
    IReadOnlyList<AdminError> Errors);
```

- [ ] **Step 3: Create `AdminEndpointBuilder.cs`**

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Toimi.Core.Admin;

public static class AdminEndpointBuilder
{
  public static RouteGroupBuilder MapAdmin(this IEndpointRouteBuilder app)
    => app.MapGroup("/admin");
}
```

- [ ] **Step 4: Verify the solution still builds**

Run: `dotnet build toimi.sln -nologo -v q`
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add src/toimi.core/Admin/
git commit -m "feat(core): add shared admin DTOs and MapAdmin helper"
```

---

## Task 2: muistio admin endpoints + tests

**Files:**
- Create: `src/toimi.tools.muistio.Tests/toimi.tools.muistio.Tests.csproj`
- Create: `src/toimi.tools.muistio.Tests/AdminEndpointsTests.cs`
- Create: `src/toimi.tools.muistio/Admin/AdminEndpoints.cs`
- Modify: `src/toimi.tools.muistio/Program.cs` (lines 56-103 — replace the `/api/memories` block)
- Modify: `toimi.sln`

- [ ] **Step 1: Create the test project file**

`src/toimi.tools.muistio.Tests/toimi.tools.muistio.Tests.csproj`:

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
    <ProjectReference Include="../toimi.tools.muistio/toimi.tools.muistio.csproj" />
    <ProjectReference Include="../toimi.core/toimi.core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add the project to the solution**

Run: `dotnet sln toimi.sln add src/toimi.tools.muistio.Tests/toimi.tools.muistio.Tests.csproj`
Expected: `Project ... added to the solution.`

- [ ] **Step 3: Write a failing summary-endpoint test**

`src/toimi.tools.muistio.Tests/AdminEndpointsTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Toimi.Core.Admin;
using toimi.tools.muistio.Data;
using Xunit;

namespace toimi.tools.muistio.Tests;

public class AdminEndpointsTests : IClassFixture<MuistioTestFactory>
{
  private readonly MuistioTestFactory _factory;
  public AdminEndpointsTests(MuistioTestFactory factory) => _factory = factory;

  [Fact]
  public async Task Summary_returns_memory_summaries()
  {
    using var scope = _factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MuistioDbContext>();
    db.Memories.Add(new Memory
    {
      Id = Guid.NewGuid(),
      Content = "User likes oat milk",
      Source = "user",
      Confirmed = true,
      CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
      UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
    });
    await db.SaveChangesAsync();

    var client = _factory.CreateClient();
    var summary = await client.GetFromJsonAsync<AdminSummaryDto[]>("/admin/summary");

    Assert.NotNull(summary);
    var item = Assert.Single(summary!);
    Assert.Equal("memory", item.Kind);
    Assert.Equal("User likes oat milk", item.Title);
  }
}

public class MuistioTestFactory : WebApplicationFactory<Program>
{
  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    builder.UseSetting("ConnectionStrings:Muistio", "Server=ignored");
    builder.UseSetting("OpenAI:ApiKey", "test-key");
    builder.ConfigureServices(services =>
    {
      var ctxDescriptor = services.Single(d => d.ServiceType == typeof(DbContextOptions<MuistioDbContext>));
      services.Remove(ctxDescriptor);
      services.AddDbContext<MuistioDbContext>(o => o.UseInMemoryDatabase($"muistio-{Guid.NewGuid()}"));
    });
  }
}
```

- [ ] **Step 4: Run the test, expect failure**

Run: `dotnet test src/toimi.tools.muistio.Tests/ --nologo -v q`
Expected: FAIL — `/admin/summary` returns 404 (endpoint not yet registered).

- [ ] **Step 5: Create `AdminEndpoints.cs` with summary + items endpoints**

`src/toimi.tools.muistio/Admin/AdminEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Toimi.Core.Admin;
using toimi.tools.muistio.Data;

namespace toimi.tools.muistio.Admin;

public static class AdminEndpoints
{
  public record MemoryItem(
      Guid Id, string Content, string? Category, string[] Tags, string Source,
      bool Confirmed, DateTimeOffset? ExpiresAt,
      DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

  public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Size, int Total);

  public record MemoryUpdate(string? Content, string? Category, string[]? Tags, bool? Confirmed, DateTimeOffset? ExpiresAt);

  public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
  {
    var admin = app.MapAdmin();

    admin.MapGet("/summary", async (MuistioDbContext db, string? q, int limit) =>
    {
      limit = limit <= 0 ? 50 : Math.Clamp(limit, 1, 200);
      var query = db.Memories.AsQueryable();
      if (!string.IsNullOrWhiteSpace(q))
      {
        var qLower = q.ToLowerInvariant();
        query = query.Where(m => m.Content.ToLower().Contains(qLower));
      }
      var rows = await query
        .OrderByDescending(m => m.UpdatedAt)
        .Take(limit)
        .Select(m => new AdminSummaryDto(
          m.Id.ToString(),
          "memory",
          m.Content.Length > 60 ? m.Content.Substring(0, 60) : m.Content,
          $"from {m.Source}" + (m.Confirmed ? "" : " (unconfirmed)"),
          m.CreatedAt,
          m.UpdatedAt))
        .ToListAsync();
      return Results.Ok(rows);
    });

    admin.MapGet("/items", async (MuistioDbContext db, string? q, int page, int size) =>
    {
      page = page <= 0 ? 1 : page;
      size = size <= 0 ? 20 : Math.Clamp(size, 1, 100);
      var query = db.Memories.AsQueryable();
      if (!string.IsNullOrWhiteSpace(q))
      {
        var qLower = q.ToLowerInvariant();
        query = query.Where(m => m.Content.ToLower().Contains(qLower));
      }
      var total = await query.CountAsync();
      var items = await query
        .OrderByDescending(m => m.UpdatedAt)
        .Skip((page - 1) * size).Take(size)
        .Select(m => new MemoryItem(m.Id, m.Content, m.Category, m.Tags, m.Source, m.Confirmed, m.ExpiresAt, m.CreatedAt, m.UpdatedAt))
        .ToListAsync();
      return Results.Ok(new PagedResult<MemoryItem>(items, page, size, total));
    });

    admin.MapGet("/items/{id:guid}", async (MuistioDbContext db, Guid id) =>
    {
      var m = await db.Memories.FindAsync(id);
      return m is null
        ? Results.NotFound()
        : Results.Ok(new MemoryItem(m.Id, m.Content, m.Category, m.Tags, m.Source, m.Confirmed, m.ExpiresAt, m.CreatedAt, m.UpdatedAt));
    });

    admin.MapPut("/items/{id:guid}", async (HttpContext ctx, MuistioDbContext db, Guid id, MemoryUpdate body) =>
    {
      if (!ctx.Request.Headers.TryGetValue("If-Unmodified-Since", out var ius))
        return Results.StatusCode((int)System.Net.HttpStatusCode.PreconditionRequired);
      if (!DateTimeOffset.TryParse(ius.ToString(), out var clientUpdatedAt))
        return Results.BadRequest(new { error = "invalid-If-Unmodified-Since" });

      var m = await db.Memories.FindAsync(id);
      if (m is null) return Results.NotFound();
      if (Math.Abs((m.UpdatedAt - clientUpdatedAt).TotalSeconds) > 1)
        return Results.Conflict(new { error = "stale", currentUpdatedAt = m.UpdatedAt });

      if (body.Content is not null) m.Content = body.Content;
      if (body.Category is not null) m.Category = body.Category;
      if (body.Tags is not null) m.Tags = body.Tags;
      if (body.Confirmed is not null) m.Confirmed = body.Confirmed.Value;
      if (body.ExpiresAt is not null) m.ExpiresAt = body.ExpiresAt;
      m.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();

      return Results.Ok(new MemoryItem(m.Id, m.Content, m.Category, m.Tags, m.Source, m.Confirmed, m.ExpiresAt, m.CreatedAt, m.UpdatedAt));
    });

    admin.MapDelete("/items/{id:guid}", async (MuistioDbContext db, Guid id) =>
    {
      var m = await db.Memories.FindAsync(id);
      if (m is null) return Results.NotFound();
      db.Memories.Remove(m);
      await db.SaveChangesAsync();
      return Results.NoContent();
    });
  }
}
```

- [ ] **Step 6: Wire endpoints into `Program.cs` and drop `/api/memories`**

Edit `src/toimi.tools.muistio/Program.cs`: replace the entire block from `app.MapMcp();` through the end (lines starting at `app.MapMcp();`) with:

```csharp
app.MapMcp();
app.MapGet("/health", () => Results.Ok());
toimi.tools.muistio.Admin.AdminEndpoints.MapAdminEndpoints(app);

app.Run();
```

Also delete the trailing `MemoryUpdateRequest` record (no longer used).

- [ ] **Step 7: Run the summary test, expect pass**

Run: `dotnet test src/toimi.tools.muistio.Tests/ --nologo -v q`
Expected: PASS — `Summary_returns_memory_summaries`.

- [ ] **Step 8: Add list-pagination test**

Append to `AdminEndpointsTests.cs`:

```csharp
[Fact]
public async Task Items_paginates()
{
  using var scope = _factory.Services.CreateScope();
  var db = scope.ServiceProvider.GetRequiredService<MuistioDbContext>();
  for (var i = 0; i < 25; i++)
  {
    db.Memories.Add(new Memory
    {
      Id = Guid.NewGuid(),
      Content = $"Memory {i}",
      Source = "user",
      Confirmed = true,
      CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
      UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
    });
  }
  await db.SaveChangesAsync();

  var client = _factory.CreateClient();
  var page1 = await client.GetFromJsonAsync<AdminEndpoints.PagedResult<AdminEndpoints.MemoryItem>>("/admin/items?page=1&size=10");
  Assert.Equal(10, page1!.Items.Count);
  Assert.Equal(25, page1.Total);
}
```

> Note: `AdminEndpoints` is in the `toimi.tools.muistio.Admin` namespace; add `using toimi.tools.muistio.Admin;` to the test file.

- [ ] **Step 9: Run all tests, expect pass**

Run: `dotnet test src/toimi.tools.muistio.Tests/ --nologo -v q`
Expected: 2 passed.

- [ ] **Step 10: Add an optimistic-concurrency test**

Append to `AdminEndpointsTests.cs`:

```csharp
[Fact]
public async Task Put_with_stale_If_Unmodified_Since_returns_409()
{
  var id = Guid.NewGuid();
  using (var scope = _factory.Services.CreateScope())
  {
    var db = scope.ServiceProvider.GetRequiredService<MuistioDbContext>();
    db.Memories.Add(new Memory
    {
      Id = id, Content = "old", Source = "user", Confirmed = true,
      CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    });
    await db.SaveChangesAsync();
  }
  var client = _factory.CreateClient();
  var req = new HttpRequestMessage(HttpMethod.Put, $"/admin/items/{id}")
  {
    Content = JsonContent.Create(new { content = "new" }),
  };
  req.Headers.IfUnmodifiedSince = DateTimeOffset.UtcNow.AddDays(-1).UtcDateTime;
  var resp = await client.SendAsync(req);
  Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
}
```

- [ ] **Step 11: Run, expect pass**

Run: `dotnet test src/toimi.tools.muistio.Tests/ --nologo -v q`
Expected: 3 passed.

- [ ] **Step 12: Add a delete test**

Append to `AdminEndpointsTests.cs`:

```csharp
[Fact]
public async Task Delete_returns_204_and_removes_row()
{
  var id = Guid.NewGuid();
  using (var scope = _factory.Services.CreateScope())
  {
    var db = scope.ServiceProvider.GetRequiredService<MuistioDbContext>();
    db.Memories.Add(new Memory
    {
      Id = id, Content = "x", Source = "user", Confirmed = true,
      CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    });
    await db.SaveChangesAsync();
  }
  var client = _factory.CreateClient();
  var resp = await client.DeleteAsync($"/admin/items/{id}");
  Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
  using var scope2 = _factory.Services.CreateScope();
  var db2 = scope2.ServiceProvider.GetRequiredService<MuistioDbContext>();
  Assert.Null(await db2.Memories.FindAsync(id));
}
```

- [ ] **Step 13: Run, expect pass**

Run: `dotnet test src/toimi.tools.muistio.Tests/ --nologo -v q`
Expected: 4 passed.

- [ ] **Step 14: Commit**

```bash
git add src/toimi.tools.muistio.Tests/ \
        src/toimi.tools.muistio/Admin/ \
        src/toimi.tools.muistio/Program.cs \
        toimi.sln
git commit -m "feat(muistio): add /admin REST surface; drop /api/memories"
```

---

## Task 3: muistutin admin endpoints + `UpdatedAt` migration

**Files:**
- Modify: `src/toimi.tools.muistutin/Data/Reminder.cs`
- Modify: `src/toimi.tools.muistutin/Data/ReminderConfiguration.cs`
- Modify: `src/toimi.tools.muistutin/Data/ReminderRepository.cs` (write `UpdatedAt`)
- Create: `src/toimi.tools.muistutin/Migrations/<auto>_AddReminderUpdatedAt.cs`
- Create: `src/toimi.tools.muistutin/Admin/AdminEndpoints.cs`
- Modify: `src/toimi.tools.muistutin/Program.cs`
- Create: `src/toimi.tools.muistutin.Tests/toimi.tools.muistutin.Tests.csproj`
- Create: `src/toimi.tools.muistutin.Tests/AdminEndpointsTests.cs`
- Modify: `toimi.sln`

- [ ] **Step 1: Add `UpdatedAt` to `Reminder` entity**

Edit `src/toimi.tools.muistutin/Data/Reminder.cs` — append before the closing brace:

```csharp
  public DateTimeOffset UpdatedAt { get; set; }
```

- [ ] **Step 2: Configure `UpdatedAt` in `ReminderConfiguration.cs`**

Edit `src/toimi.tools.muistutin/Data/ReminderConfiguration.cs` — after the existing `CreatedAt` property config block (lines 27-28), add:

```csharp
    builder.Property(r => r.UpdatedAt)
      .HasDefaultValueSql("now()");
```

- [ ] **Step 3: Update repository writes in `ReminderRepository.cs`**

Edit `src/toimi.tools.muistutin/Data/ReminderRepository.cs`:

1. In `CreateAsync` (line 9-11), add `UpdatedAt`:

```csharp
    reminder.Id = Guid.NewGuid();
    reminder.CreatedAt = DateTimeOffset.UtcNow;
    reminder.UpdatedAt = DateTimeOffset.UtcNow;
```

2. In `CompleteAsync` (line 41-49), set `UpdatedAt` before save:

```csharp
  public async Task CompleteAsync(Guid id)
  {
    var reminder = await dbContext.Reminders.FindAsync(id);
    if (reminder != null)
    {
      reminder.IsCompleted = true;
      reminder.UpdatedAt = DateTimeOffset.UtcNow;
      await dbContext.SaveChangesAsync();
    }
  }
```

The `CompleteOccurrenceAsync` method only writes to `CompletedOccurrences`, not the parent `Reminder`, so no change there.

- [ ] **Step 4: Generate the migration**

Run: `dotnet ef migrations add AddReminderUpdatedAt -p src/toimi.tools.muistutin/toimi.tools.muistutin.csproj`
Expected: a new file appears under `src/toimi.tools.muistutin/Migrations/`.

- [ ] **Step 5: Create the test project**

`src/toimi.tools.muistutin.Tests/toimi.tools.muistutin.Tests.csproj`:

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
    <ProjectReference Include="../toimi.tools.muistutin/toimi.tools.muistutin.csproj" />
    <ProjectReference Include="../toimi.core/toimi.core.csproj" />
  </ItemGroup>
</Project>
```

Then: `dotnet sln toimi.sln add src/toimi.tools.muistutin.Tests/toimi.tools.muistutin.Tests.csproj`

- [ ] **Step 6: Write a failing summary test**

`src/toimi.tools.muistutin.Tests/AdminEndpointsTests.cs`:

```csharp
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Toimi.Core.Admin;
using toimi.tools.muistutin.Data;
using Xunit;

namespace toimi.tools.muistutin.Tests;

public class AdminEndpointsTests : IClassFixture<MuistutinTestFactory>
{
  private readonly MuistutinTestFactory _factory;
  public AdminEndpointsTests(MuistutinTestFactory f) => _factory = f;

  [Fact]
  public async Task Summary_returns_reminder_summaries()
  {
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<MuistutinDbContext>();
      db.Reminders.Add(new Reminder
      {
        Id = Guid.NewGuid(),
        Title = "Buy milk",
        Description = null,
        DateTimeUtc = DateTimeOffset.UtcNow.AddHours(2),
        TimeZone = "Europe/Helsinki",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
      });
      await db.SaveChangesAsync();
    }
    var client = _factory.CreateClient();
    var summary = await client.GetFromJsonAsync<AdminSummaryDto[]>("/admin/summary");
    var item = Assert.Single(summary!);
    Assert.Equal("reminder", item.Kind);
    Assert.Equal("Buy milk", item.Title);
  }
}

public class MuistutinTestFactory : WebApplicationFactory<Program>
{
  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    builder.UseSetting("ConnectionStrings:Muistutin", "Server=ignored");
    builder.ConfigureServices(services =>
    {
      var ctx = services.Single(d => d.ServiceType == typeof(DbContextOptions<MuistutinDbContext>));
      services.Remove(ctx);
      services.AddDbContext<MuistutinDbContext>(o => o.UseInMemoryDatabase($"muistutin-{Guid.NewGuid()}"));
      // Remove the hosted notifier so it doesn't try to wake up under test.
      var hosted = services.Where(d => d.ImplementationType?.Name == "ReminderNotifier").ToArray();
      foreach (var h in hosted) services.Remove(h);
    });
  }
}
```

- [ ] **Step 7: Run the test, expect failure**

Run: `dotnet test src/toimi.tools.muistutin.Tests/ --nologo -v q`
Expected: FAIL (no `/admin/summary` yet).

- [ ] **Step 8: Create `Admin/AdminEndpoints.cs`**

`src/toimi.tools.muistutin/Admin/AdminEndpoints.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Toimi.Core.Admin;
using toimi.tools.muistutin.Data;

namespace toimi.tools.muistutin.Admin;

public static class AdminEndpoints
{
  public record ReminderItem(
      Guid Id, string Title, string? Description,
      DateTimeOffset DateTimeUtc, string TimeZone,
      string? RecurrenceRule, DateTimeOffset? DisplayEndUtc,
      bool IsCompleted, DateTimeOffset? NotifiedAt,
      DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

  public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Size, int Total);

  public record ReminderUpdate(string? Title, string? Description,
      DateTimeOffset? DateTimeUtc, string? TimeZone,
      string? RecurrenceRule, DateTimeOffset? DisplayEndUtc, bool? IsCompleted);

  public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
  {
    var admin = app.MapAdmin();

    admin.MapGet("/summary", async (MuistutinDbContext db, string? q, int limit) =>
    {
      limit = limit <= 0 ? 50 : Math.Clamp(limit, 1, 200);
      var query = db.Reminders.AsQueryable();
      if (!string.IsNullOrWhiteSpace(q))
      {
        var qLower = q.ToLowerInvariant();
        query = query.Where(r => r.Title.ToLower().Contains(qLower));
      }
      var rows = await query
        .OrderByDescending(r => r.UpdatedAt)
        .Take(limit)
        .Select(r => new AdminSummaryDto(
          r.Id.ToString(),
          "reminder",
          r.Title,
          (r.IsCompleted ? "completed — " : "") + r.DateTimeUtc.ToString("u")
            + (r.RecurrenceRule != null ? " (recurring)" : ""),
          r.CreatedAt,
          r.UpdatedAt))
        .ToListAsync();
      return Results.Ok(rows);
    });

    admin.MapGet("/items", async (MuistutinDbContext db, string? q, int page, int size) =>
    {
      page = page <= 0 ? 1 : page;
      size = size <= 0 ? 20 : Math.Clamp(size, 1, 100);
      var query = db.Reminders.AsQueryable();
      if (!string.IsNullOrWhiteSpace(q))
      {
        var qLower = q.ToLowerInvariant();
        query = query.Where(r => r.Title.ToLower().Contains(qLower));
      }
      var total = await query.CountAsync();
      var items = await query
        .OrderByDescending(r => r.UpdatedAt)
        .Skip((page - 1) * size).Take(size)
        .Select(r => new ReminderItem(r.Id, r.Title, r.Description, r.DateTimeUtc, r.TimeZone,
          r.RecurrenceRule, r.DisplayEndUtc, r.IsCompleted, r.NotifiedAt, r.CreatedAt, r.UpdatedAt))
        .ToListAsync();
      return Results.Ok(new PagedResult<ReminderItem>(items, page, size, total));
    });

    admin.MapGet("/items/{id:guid}", async (MuistutinDbContext db, Guid id) =>
    {
      var r = await db.Reminders.FindAsync(id);
      return r is null
        ? Results.NotFound()
        : Results.Ok(new ReminderItem(r.Id, r.Title, r.Description, r.DateTimeUtc, r.TimeZone,
          r.RecurrenceRule, r.DisplayEndUtc, r.IsCompleted, r.NotifiedAt, r.CreatedAt, r.UpdatedAt));
    });

    admin.MapPut("/items/{id:guid}", async (HttpContext ctx, MuistutinDbContext db, Guid id, ReminderUpdate body) =>
    {
      if (!ctx.Request.Headers.TryGetValue("If-Unmodified-Since", out var ius))
        return Results.StatusCode(428);
      if (!DateTimeOffset.TryParse(ius.ToString(), out var clientUpdatedAt))
        return Results.BadRequest(new { error = "invalid-If-Unmodified-Since" });
      var r = await db.Reminders.FindAsync(id);
      if (r is null) return Results.NotFound();
      if (Math.Abs((r.UpdatedAt - clientUpdatedAt).TotalSeconds) > 1)
        return Results.Conflict(new { error = "stale", currentUpdatedAt = r.UpdatedAt });

      if (body.Title is not null) r.Title = body.Title;
      if (body.Description is not null) r.Description = body.Description;
      if (body.DateTimeUtc is not null) r.DateTimeUtc = body.DateTimeUtc.Value;
      if (body.TimeZone is not null) r.TimeZone = body.TimeZone;
      if (body.RecurrenceRule is not null) r.RecurrenceRule = body.RecurrenceRule;
      if (body.DisplayEndUtc is not null) r.DisplayEndUtc = body.DisplayEndUtc;
      if (body.IsCompleted is not null) r.IsCompleted = body.IsCompleted.Value;
      r.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();

      return Results.Ok(new ReminderItem(r.Id, r.Title, r.Description, r.DateTimeUtc, r.TimeZone,
        r.RecurrenceRule, r.DisplayEndUtc, r.IsCompleted, r.NotifiedAt, r.CreatedAt, r.UpdatedAt));
    });

    admin.MapDelete("/items/{id:guid}", async (MuistutinDbContext db, Guid id) =>
    {
      var r = await db.Reminders.FindAsync(id);
      if (r is null) return Results.NotFound();
      db.Reminders.Remove(r);
      await db.SaveChangesAsync();
      return Results.NoContent();
    });

    admin.MapPost("/items/{id:guid}/complete", async (MuistutinDbContext db, Guid id) =>
    {
      var r = await db.Reminders.FindAsync(id);
      if (r is null) return Results.NotFound();
      r.IsCompleted = true;
      r.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();
      return Results.NoContent();
    });
  }
}
```

- [ ] **Step 9: Wire into `Program.cs`**

In `src/toimi.tools.muistutin/Program.cs`, immediately after `app.MapGet("/health", () => Results.Ok());` add:

```csharp
toimi.tools.muistutin.Admin.AdminEndpoints.MapAdminEndpoints(app);
```

- [ ] **Step 10: Run summary test, expect pass**

Run: `dotnet test src/toimi.tools.muistutin.Tests/ --nologo -v q`
Expected: PASS.

- [ ] **Step 11: Add complete-action test**

Append to `AdminEndpointsTests.cs`:

```csharp
[Fact]
public async Task Complete_marks_reminder_completed()
{
  var id = Guid.NewGuid();
  using (var scope = _factory.Services.CreateScope())
  {
    var db = scope.ServiceProvider.GetRequiredService<MuistutinDbContext>();
    db.Reminders.Add(new Reminder
    {
      Id = id, Title = "x", DateTimeUtc = DateTimeOffset.UtcNow.AddHours(1),
      TimeZone = "UTC", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    });
    await db.SaveChangesAsync();
  }
  var client = _factory.CreateClient();
  var resp = await client.PostAsync($"/admin/items/{id}/complete", null);
  resp.EnsureSuccessStatusCode();
  using var scope2 = _factory.Services.CreateScope();
  var db2 = scope2.ServiceProvider.GetRequiredService<MuistutinDbContext>();
  Assert.True((await db2.Reminders.FindAsync(id))!.IsCompleted);
}
```

- [ ] **Step 12: Run, expect pass**

Run: `dotnet test src/toimi.tools.muistutin.Tests/ --nologo -v q`
Expected: 2 passed.

- [ ] **Step 13: Commit**

```bash
git add src/toimi.tools.muistutin/ \
        src/toimi.tools.muistutin.Tests/ \
        toimi.sln
git commit -m "feat(muistutin): add /admin REST surface, UpdatedAt column"
```

---

## Task 4: ajastin admin endpoints + `UpdatedAt` migration + runs sub-endpoint

**Files:**
- Modify: `src/toimi.tools.ajastin/Data/Schedule.cs`
- Modify: `src/toimi.tools.ajastin/Data/ScheduleConfiguration.cs`
- Modify: `src/toimi.tools.ajastin/Data/ScheduleRepository.cs` (set `UpdatedAt` on writes)
- Create: `src/toimi.tools.ajastin/Migrations/<auto>_AddScheduleUpdatedAt.cs`
- Create: `src/toimi.tools.ajastin/Admin/AdminEndpoints.cs`
- Modify: `src/toimi.tools.ajastin/Program.cs`
- Create: `src/toimi.tools.ajastin.Tests/` project + tests
- Modify: `toimi.sln`

- [ ] **Step 1: Add `UpdatedAt` to `Schedule`**

Edit `src/toimi.tools.ajastin/Data/Schedule.cs` — add before the `Runs` collection:

```csharp
  public DateTimeOffset UpdatedAt { get; set; }
```

- [ ] **Step 2: Configure `UpdatedAt`**

Edit `src/toimi.tools.ajastin/Data/ScheduleConfiguration.cs` — after the `CreatedAt` block (lines 29-30), add:

```csharp
    builder.Property(s => s.UpdatedAt)
      .HasDefaultValueSql("now()");
```

- [ ] **Step 3: Update repo writes in `ScheduleRepository.cs`**

Edit `src/toimi.tools.ajastin/Data/ScheduleRepository.cs`:

1. In `CreateAsync` (lines 7-16), add `UpdatedAt`:

```csharp
  public async Task<Schedule> CreateAsync(Schedule schedule)
  {
    schedule.Id = Guid.NewGuid();
    schedule.CreatedAt = DateTimeOffset.UtcNow;
    schedule.UpdatedAt = DateTimeOffset.UtcNow;

    dbContext.Schedules.Add(schedule);
    await dbContext.SaveChangesAsync();
    return schedule;
  }
```

2. In `UpdateAsync` (lines 56-62), set `UpdatedAt`:

```csharp
  public async Task<Schedule> UpdateAsync(Schedule schedule)
  {
    schedule.UpdatedAt = DateTimeOffset.UtcNow;
    dbContext.Schedules.Update(schedule);
    await dbContext.SaveChangesAsync();
    return schedule;
  }
```

Run-related methods (`AddRunAsync`, `UpdateRunAsync`) only touch `ScheduleRun`, not `Schedule`. The `ScheduleWorker` (separate file) updates `Schedule.LastRunAt` after each run — it must also set `UpdatedAt`. Find that call site and add `schedule.UpdatedAt = DateTimeOffset.UtcNow;` next to the `LastRunAt` assignment.

- [ ] **Step 4: Generate the EF migration**

Run: `dotnet ef migrations add AddScheduleUpdatedAt -p src/toimi.tools.ajastin/toimi.tools.ajastin.csproj`
Expected: new migration file under `Migrations/`.

- [ ] **Step 5: Create test project**

`src/toimi.tools.ajastin.Tests/toimi.tools.ajastin.Tests.csproj` (same skeleton as Task 3 step 5, swap project ref to `../toimi.tools.ajastin/toimi.tools.ajastin.csproj`).

Run: `dotnet sln toimi.sln add src/toimi.tools.ajastin.Tests/toimi.tools.ajastin.Tests.csproj`

- [ ] **Step 6: Create `Admin/AdminEndpoints.cs`**

`src/toimi.tools.ajastin/Admin/AdminEndpoints.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Toimi.Core.Admin;
using toimi.tools.ajastin.Data;

namespace toimi.tools.ajastin.Admin;

public static class AdminEndpoints
{
  public record ScheduleItem(
      Guid Id, string Name, string? CronExpression, DateTimeOffset? RunAt,
      string Prompt, bool Enabled, DateTimeOffset? LastRunAt,
      DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

  public record RunItem(
      Guid Id, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
      bool Success, string? Response, string? Error);

  public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Size, int Total);

  public record ScheduleUpdate(string? Name, string? CronExpression, DateTimeOffset? RunAt,
      string? Prompt, bool? Enabled);

  public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
  {
    var admin = app.MapAdmin();

    admin.MapGet("/summary", async (AjastinDbContext db, string? q, int limit) =>
    {
      limit = limit <= 0 ? 50 : Math.Clamp(limit, 1, 200);
      var query = db.Schedules.AsQueryable();
      if (!string.IsNullOrWhiteSpace(q))
      {
        var qLower = q.ToLowerInvariant();
        query = query.Where(s => s.Name.ToLower().Contains(qLower));
      }
      var rows = await query
        .OrderByDescending(s => s.UpdatedAt)
        .Take(limit)
        .Select(s => new AdminSummaryDto(
          s.Id.ToString(),
          "schedule",
          s.Name,
          (s.CronExpression ?? (s.RunAt != null ? "one-shot " + s.RunAt.Value.ToString("u") : "no trigger"))
            + (s.Enabled ? "" : " (disabled)"),
          s.CreatedAt,
          s.UpdatedAt))
        .ToListAsync();
      return Results.Ok(rows);
    });

    admin.MapGet("/items", async (AjastinDbContext db, string? q, int page, int size) =>
    {
      page = page <= 0 ? 1 : page;
      size = size <= 0 ? 20 : Math.Clamp(size, 1, 100);
      var query = db.Schedules.AsQueryable();
      if (!string.IsNullOrWhiteSpace(q))
      {
        var qLower = q.ToLowerInvariant();
        query = query.Where(s => s.Name.ToLower().Contains(qLower));
      }
      var total = await query.CountAsync();
      var items = await query
        .OrderByDescending(s => s.UpdatedAt)
        .Skip((page - 1) * size).Take(size)
        .Select(s => new ScheduleItem(s.Id, s.Name, s.CronExpression, s.RunAt, s.Prompt,
          s.Enabled, s.LastRunAt, s.CreatedAt, s.UpdatedAt))
        .ToListAsync();
      return Results.Ok(new PagedResult<ScheduleItem>(items, page, size, total));
    });

    admin.MapGet("/items/{id:guid}", async (AjastinDbContext db, Guid id) =>
    {
      var s = await db.Schedules.FindAsync(id);
      return s is null
        ? Results.NotFound()
        : Results.Ok(new ScheduleItem(s.Id, s.Name, s.CronExpression, s.RunAt, s.Prompt,
            s.Enabled, s.LastRunAt, s.CreatedAt, s.UpdatedAt));
    });

    admin.MapGet("/items/{id:guid}/runs", async (AjastinDbContext db, Guid id, int limit) =>
    {
      limit = limit <= 0 ? 20 : Math.Clamp(limit, 1, 100);
      var rows = await db.ScheduleRuns
        .Where(r => r.ScheduleId == id)
        .OrderByDescending(r => r.StartedAt)
        .Take(limit)
        .Select(r => new RunItem(r.Id, r.StartedAt, r.CompletedAt, r.Success, r.Response, r.Error))
        .ToListAsync();
      return Results.Ok(rows);
    });

    admin.MapPut("/items/{id:guid}", async (HttpContext ctx, AjastinDbContext db, Guid id, ScheduleUpdate body) =>
    {
      if (!ctx.Request.Headers.TryGetValue("If-Unmodified-Since", out var ius))
        return Results.StatusCode(428);
      if (!DateTimeOffset.TryParse(ius.ToString(), out var clientUpdatedAt))
        return Results.BadRequest(new { error = "invalid-If-Unmodified-Since" });
      var s = await db.Schedules.FindAsync(id);
      if (s is null) return Results.NotFound();
      if (Math.Abs((s.UpdatedAt - clientUpdatedAt).TotalSeconds) > 1)
        return Results.Conflict(new { error = "stale", currentUpdatedAt = s.UpdatedAt });

      if (body.Name is not null) s.Name = body.Name;
      if (body.CronExpression is not null) s.CronExpression = body.CronExpression;
      if (body.RunAt is not null) s.RunAt = body.RunAt;
      if (body.Prompt is not null) s.Prompt = body.Prompt;
      if (body.Enabled is not null) s.Enabled = body.Enabled.Value;
      s.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();

      return Results.Ok(new ScheduleItem(s.Id, s.Name, s.CronExpression, s.RunAt, s.Prompt,
        s.Enabled, s.LastRunAt, s.CreatedAt, s.UpdatedAt));
    });

    admin.MapDelete("/items/{id:guid}", async (AjastinDbContext db, Guid id) =>
    {
      var s = await db.Schedules.FindAsync(id);
      if (s is null) return Results.NotFound();
      db.Schedules.Remove(s);
      await db.SaveChangesAsync();
      return Results.NoContent();
    });

    admin.MapPost("/items/{id:guid}/enable", async (AjastinDbContext db, Guid id) =>
    {
      var s = await db.Schedules.FindAsync(id);
      if (s is null) return Results.NotFound();
      s.Enabled = true;
      s.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();
      return Results.NoContent();
    });

    admin.MapPost("/items/{id:guid}/disable", async (AjastinDbContext db, Guid id) =>
    {
      var s = await db.Schedules.FindAsync(id);
      if (s is null) return Results.NotFound();
      s.Enabled = false;
      s.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();
      return Results.NoContent();
    });

    // POST /items/{id}/run-now: queues an immediate run by setting RunAt to now and
    // letting the existing ScheduleWorker pick it up next tick.
    admin.MapPost("/items/{id:guid}/run-now", async (AjastinDbContext db, Guid id) =>
    {
      var s = await db.Schedules.FindAsync(id);
      if (s is null) return Results.NotFound();
      s.RunAt = DateTimeOffset.UtcNow;
      s.UpdatedAt = DateTimeOffset.UtcNow;
      await db.SaveChangesAsync();
      return Results.NoContent();
    });
  }
}
```

- [ ] **Step 7: Wire into `Program.cs`**

In `src/toimi.tools.ajastin/Program.cs`, after `app.MapGet("/health", …)`:

```csharp
toimi.tools.ajastin.Admin.AdminEndpoints.MapAdminEndpoints(app);
```

The existing `/api/runs` endpoint stays untouched.

- [ ] **Step 8: Write summary test**

`src/toimi.tools.ajastin.Tests/AdminEndpointsTests.cs`:

```csharp
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Toimi.Core.Admin;
using toimi.tools.ajastin.Data;
using Xunit;

namespace toimi.tools.ajastin.Tests;

public class AdminEndpointsTests : IClassFixture<AjastinTestFactory>
{
  private readonly AjastinTestFactory _factory;
  public AdminEndpointsTests(AjastinTestFactory f) => _factory = f;

  [Fact]
  public async Task Summary_returns_schedule_summaries()
  {
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AjastinDbContext>();
      db.Schedules.Add(new Schedule
      {
        Id = Guid.NewGuid(),
        Name = "Morning check",
        CronExpression = "0 8 * * *",
        Prompt = "Summarize calendar",
        Enabled = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
      });
      await db.SaveChangesAsync();
    }
    var client = _factory.CreateClient();
    var summary = await client.GetFromJsonAsync<AdminSummaryDto[]>("/admin/summary");
    var item = Assert.Single(summary!);
    Assert.Equal("schedule", item.Kind);
    Assert.Equal("Morning check", item.Title);
  }

  [Fact]
  public async Task RunNow_sets_RunAt_to_recent_time()
  {
    var id = Guid.NewGuid();
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AjastinDbContext>();
      db.Schedules.Add(new Schedule
      {
        Id = id, Name = "x", Prompt = "p", Enabled = true,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
      });
      await db.SaveChangesAsync();
    }
    var client = _factory.CreateClient();
    (await client.PostAsync($"/admin/items/{id}/run-now", null)).EnsureSuccessStatusCode();
    using var scope2 = _factory.Services.CreateScope();
    var db2 = scope2.ServiceProvider.GetRequiredService<AjastinDbContext>();
    var s = await db2.Schedules.FindAsync(id);
    Assert.NotNull(s!.RunAt);
    Assert.True((DateTimeOffset.UtcNow - s.RunAt.Value).TotalSeconds < 5);
  }
}

public class AjastinTestFactory : WebApplicationFactory<Program>
{
  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    builder.UseSetting("ConnectionStrings:Ajastin", "Server=ignored");
    builder.UseSetting("Toimi:OpenAI:ApiKey", "test");
    builder.UseSetting("Toimi:OpenAI:Model", "gpt-4");
    builder.ConfigureServices(services =>
    {
      var ctx = services.Single(d => d.ServiceType == typeof(DbContextOptions<AjastinDbContext>));
      services.Remove(ctx);
      services.AddDbContext<AjastinDbContext>(o => o.UseInMemoryDatabase($"ajastin-{Guid.NewGuid()}"));
      var hosted = services.Where(d => d.ImplementationType?.Name == "ScheduleWorker").ToArray();
      foreach (var h in hosted) services.Remove(h);
    });
  }
}
```

- [ ] **Step 9: Run tests, expect pass**

Run: `dotnet test src/toimi.tools.ajastin.Tests/ --nologo -v q`
Expected: 2 passed.

- [ ] **Step 10: Commit**

```bash
git add src/toimi.tools.ajastin/ \
        src/toimi.tools.ajastin.Tests/ \
        toimi.sln
git commit -m "feat(ajastin): add /admin REST surface, UpdatedAt column, runs sub-endpoint"
```

---

## Task 5: taidot admin endpoints + Qdrant `updated_at`

**Files:**
- Modify: `src/toimi.tools.taidot/Skills/SkillRepository.cs` (read/write `updated_at`)
- Modify: `src/toimi.tools.taidot/Skills/SkillEntry.cs` (add `UpdatedAt`)
- Create: `src/toimi.tools.taidot/Skills/SkillAdminRepository.cs`
- Create: `src/toimi.tools.taidot/Admin/AdminEndpoints.cs`
- Modify: `src/toimi.tools.taidot/Program.cs`
- Create: `src/toimi.tools.taidot.Tests/` project + tests (uses a Qdrant-free seam — see step 1)
- Modify: `toimi.sln`

- [ ] **Step 1: Introduce an `ISkillStore` seam for testing**

`src/toimi.tools.taidot/Skills/ISkillStore.cs`:

```csharp
namespace toimi.tools.taidot.Skills;

public interface ISkillStore
{
  Task<IReadOnlyList<SkillEntry>> ListAllAsync(CancellationToken ct = default);
  Task<SkillEntry?> GetByIdAsync(Guid id, CancellationToken ct = default);
  Task<bool> DeleteByIdAsync(Guid id, CancellationToken ct = default);
  Task<SkillEntry> UpdateAsync(Guid id, string? name, string? description,
      string? instructions, string[]? tags, CancellationToken ct = default);
}
```

`SkillRepository` will implement it; tests get a hand-written fake.

- [ ] **Step 2: Add `UpdatedAt` to `SkillEntry`**

```csharp
namespace toimi.tools.taidot.Skills;

public record SkillEntry(
    Guid Id,
    string Name,
    string Description,
    string Instructions,
    string[] Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    float? Score = null);
```

- [ ] **Step 3: Replace `ISkillStore` with the right shape (drop `UpdateAsync`, add `UpsertPointAsync`)**

Overwrite `src/toimi.tools.taidot/Skills/ISkillStore.cs` with:

```csharp
namespace toimi.tools.taidot.Skills;

public interface ISkillStore
{
  Task<IReadOnlyList<SkillEntry>> ListAllAsync(CancellationToken ct = default);
  Task<SkillEntry?> GetByIdAsync(Guid id, CancellationToken ct = default);
  Task<bool> DeleteByIdAsync(Guid id, CancellationToken ct = default);
  Task UpsertPointAsync(Guid id, string name, string description, string instructions,
      string[] tags, float[] embedding, DateTimeOffset createdAt, CancellationToken ct = default);
}
```

Decision recorded: embedding orchestration lives in `SkillAdminRepository` (Step 4); `SkillRepository` exposes a low-level `UpsertPointAsync` and the existing `UpsertAsync(name, …)` for MCP tools stays as a thin wrapper.

- [ ] **Step 3a: Make `SkillRepository` implement `ISkillStore`**

Edit `src/toimi.tools.taidot/Skills/SkillRepository.cs`:

1. Change the class declaration line to:

```csharp
public class SkillRepository(QdrantClient qdrant) : ISkillStore
```

2. In `UpsertAsync` (around line 26-64), update the payload-building block to include `updated_at`:

```csharp
    var payload = new Dictionary<string, Value>
    {
      ["name"] = name,
      ["description"] = description,
      ["instructions"] = instructions,
      ["created_at"] = DateTimeOffset.UtcNow.ToString("o"),
      ["updated_at"] = DateTimeOffset.UtcNow.ToString("o"),
    };
```

3. Update `ToSkillEntry` (around line 162-190) — after parsing `created_at`, add an `updated_at` read with fallback, and pass it to the new `SkillEntry` constructor:

```csharp
    var updatedAt = payload.TryGetValue("updated_at", out var uV)
        ? DateTimeOffset.Parse(uV.StringValue, CultureInfo.InvariantCulture)
        : createdAt;

    return new SkillEntry(id, name, description, instructions, entryTags, createdAt, updatedAt, score);
```

4. Append the new `ISkillStore` methods to the class:

```csharp
  public async Task<IReadOnlyList<SkillEntry>> ListAllAsync(CancellationToken ct = default)
  {
    var response = await qdrant.ScrollAsync(
        CollectionName,
        limit: 1000,
        cancellationToken: ct);
    return [.. response.Result.Select(r => ToSkillEntry(r.Id, r.Payload))];
  }

  public async Task<SkillEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
  {
    var filter = new Filter();
    filter.Must.Add(Conditions.HasId(id));
    var response = await qdrant.ScrollAsync(
        CollectionName,
        filter: filter,
        limit: 1,
        cancellationToken: ct);
    var point = response.Result.FirstOrDefault();
    return point is null ? null : ToSkillEntry(point.Id, point.Payload);
  }

  public async Task<bool> DeleteByIdAsync(Guid id, CancellationToken ct = default)
  {
    var existing = await GetByIdAsync(id, ct);
    if (existing is null) return false;
    await qdrant.DeleteAsync(CollectionName, id, cancellationToken: ct);
    return true;
  }

  public async Task UpsertPointAsync(Guid id, string name, string description, string instructions,
      string[] tags, float[] embedding, DateTimeOffset createdAt, CancellationToken ct = default)
  {
    var payload = new Dictionary<string, Value>
    {
      ["name"] = name,
      ["description"] = description,
      ["instructions"] = instructions,
      ["created_at"] = createdAt.ToString("o"),
      ["updated_at"] = DateTimeOffset.UtcNow.ToString("o"),
    };
    if (tags.Length > 0) payload["tags"] = tags;

    var point = new PointStruct { Id = id, Vectors = embedding };
    foreach (var kvp in payload) point.Payload[kvp.Key] = kvp.Value;

    await qdrant.UpsertAsync(CollectionName, [point], cancellationToken: ct);
  }
```

The existing `ListAsync(int limit, int offset, …)`, `UpsertAsync(name, …)`, `GetByNameAsync`, `DeleteByNameAsync`, `SearchAsync`, and `FindByNameAsync` methods stay as-is — they're still used by the MCP tools.

- [ ] **Step 4: Create `SkillAdminRepository`**

`src/toimi.tools.taidot/Skills/SkillAdminRepository.cs`:

```csharp
namespace toimi.tools.taidot.Skills;

public class SkillAdminRepository(ISkillStore store, EmbeddingService embeddings)
{
  public Task<IReadOnlyList<SkillEntry>> ListAsync(CancellationToken ct = default)
    => store.ListAllAsync(ct);

  public Task<SkillEntry?> GetAsync(Guid id, CancellationToken ct = default)
    => store.GetByIdAsync(id, ct);

  public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    => store.DeleteByIdAsync(id, ct);

  public async Task<SkillEntry> UpdateAsync(
      Guid id, string name, string description, string instructions,
      string[] tags, DateTimeOffset createdAt, CancellationToken ct = default)
  {
    var embedding = await embeddings.GenerateEmbeddingAsync($"{name}\n{description}\n{instructions}", ct);
    await store.UpsertPointAsync(id, name, description, instructions, tags, embedding, createdAt, ct);
    return (await store.GetByIdAsync(id, ct))!;
  }
}
```

- [ ] **Step 5: Register `ISkillStore` + admin repo in `Program.cs`**

In `src/toimi.tools.taidot/Program.cs`, just before `var app = builder.Build();`:

```csharp
builder.Services.AddSingleton<ISkillStore>(sp => sp.GetRequiredService<SkillRepository>());
builder.Services.AddSingleton<SkillAdminRepository>();
```

- [ ] **Step 6: Create `Admin/AdminEndpoints.cs`**

`src/toimi.tools.taidot/Admin/AdminEndpoints.cs`:

```csharp
using Toimi.Core.Admin;
using toimi.tools.taidot.Skills;

namespace toimi.tools.taidot.Admin;

public static class AdminEndpoints
{
  public record SkillItem(
      Guid Id, string Name, string Description, string Instructions,
      string[] Tags, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

  public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Size, int Total);

  public record SkillUpdate(string Name, string Description, string Instructions, string[] Tags);

  public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
  {
    var admin = app.MapAdmin();

    admin.MapGet("/summary", async (SkillAdminRepository repo, string? q, int limit, CancellationToken ct) =>
    {
      limit = limit <= 0 ? 50 : Math.Clamp(limit, 1, 200);
      var all = await repo.ListAsync(ct);
      var filtered = string.IsNullOrWhiteSpace(q)
        ? all
        : all.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                      || s.Description.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
      return Results.Ok(filtered
        .OrderByDescending(s => s.UpdatedAt)
        .Take(limit)
        .Select(s => new AdminSummaryDto(
          s.Id.ToString(),
          "skill",
          s.Name,
          s.Description.Length > 80 ? s.Description[..80] : s.Description,
          s.CreatedAt,
          s.UpdatedAt))
        .ToList());
    });

    admin.MapGet("/items", async (SkillAdminRepository repo, string? q, int page, int size, CancellationToken ct) =>
    {
      page = page <= 0 ? 1 : page;
      size = size <= 0 ? 20 : Math.Clamp(size, 1, 100);
      var all = await repo.ListAsync(ct);
      var filtered = string.IsNullOrWhiteSpace(q)
        ? all
        : all.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                      || s.Description.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
      var total = filtered.Count;
      var items = filtered
        .OrderByDescending(s => s.UpdatedAt)
        .Skip((page - 1) * size).Take(size)
        .Select(s => new SkillItem(s.Id, s.Name, s.Description, s.Instructions, s.Tags, s.CreatedAt, s.UpdatedAt))
        .ToList();
      return Results.Ok(new PagedResult<SkillItem>(items, page, size, total));
    });

    admin.MapGet("/items/{id:guid}", async (SkillAdminRepository repo, Guid id, CancellationToken ct) =>
    {
      var s = await repo.GetAsync(id, ct);
      return s is null
        ? Results.NotFound()
        : Results.Ok(new SkillItem(s.Id, s.Name, s.Description, s.Instructions, s.Tags, s.CreatedAt, s.UpdatedAt));
    });

    admin.MapPut("/items/{id:guid}", async (HttpContext ctx, SkillAdminRepository repo, Guid id, SkillUpdate body, CancellationToken ct) =>
    {
      if (!ctx.Request.Headers.TryGetValue("If-Unmodified-Since", out var ius))
        return Results.StatusCode(428);
      if (!DateTimeOffset.TryParse(ius.ToString(), out var clientUpdatedAt))
        return Results.BadRequest(new { error = "invalid-If-Unmodified-Since" });
      var existing = await repo.GetAsync(id, ct);
      if (existing is null) return Results.NotFound();
      if (Math.Abs((existing.UpdatedAt - clientUpdatedAt).TotalSeconds) > 1)
        return Results.Conflict(new { error = "stale", currentUpdatedAt = existing.UpdatedAt });

      var updated = await repo.UpdateAsync(id, body.Name, body.Description, body.Instructions,
          body.Tags ?? [], existing.CreatedAt, ct);
      return Results.Ok(new SkillItem(updated.Id, updated.Name, updated.Description,
          updated.Instructions, updated.Tags, updated.CreatedAt, updated.UpdatedAt));
    });

    admin.MapDelete("/items/{id:guid}", async (SkillAdminRepository repo, Guid id, CancellationToken ct) =>
    {
      var deleted = await repo.DeleteAsync(id, ct);
      return deleted ? Results.NoContent() : Results.NotFound();
    });
  }
}
```

- [ ] **Step 7: Wire into `Program.cs`**

After `app.MapGet("/health", …)` in `src/toimi.tools.taidot/Program.cs`:

```csharp
toimi.tools.taidot.Admin.AdminEndpoints.MapAdminEndpoints(app);
```

- [ ] **Step 8: Create test project with a fake `ISkillStore`**

`src/toimi.tools.taidot.Tests/toimi.tools.taidot.Tests.csproj` (same skeleton as Task 3 step 5, but without `EFCore.InMemory`):

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
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../toimi.tools.taidot/toimi.tools.taidot.csproj" />
    <ProjectReference Include="../toimi.core/toimi.core.csproj" />
  </ItemGroup>
</Project>
```

Then: `dotnet sln toimi.sln add src/toimi.tools.taidot.Tests/toimi.tools.taidot.Tests.csproj`.

- [ ] **Step 9: Create fake store + test**

`src/toimi.tools.taidot.Tests/AdminEndpointsTests.cs`:

```csharp
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qdrant.Client;
using Toimi.Core.Admin;
using toimi.tools.taidot.Skills;
using Xunit;

namespace toimi.tools.taidot.Tests;

public class FakeSkillStore : ISkillStore
{
  public List<SkillEntry> Entries { get; } = [];
  public Task<IReadOnlyList<SkillEntry>> ListAllAsync(CancellationToken ct = default)
    => Task.FromResult<IReadOnlyList<SkillEntry>>(Entries.ToList());
  public Task<SkillEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
    => Task.FromResult(Entries.FirstOrDefault(e => e.Id == id));
  public Task<bool> DeleteByIdAsync(Guid id, CancellationToken ct = default)
  {
    var removed = Entries.RemoveAll(e => e.Id == id) > 0;
    return Task.FromResult(removed);
  }
  public Task UpsertPointAsync(Guid id, string name, string description, string instructions,
      string[] tags, float[] embedding, DateTimeOffset createdAt, CancellationToken ct = default)
  {
    Entries.RemoveAll(e => e.Id == id);
    Entries.Add(new SkillEntry(id, name, description, instructions, tags, createdAt, DateTimeOffset.UtcNow));
    return Task.CompletedTask;
  }
}

public class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
  public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
      IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
      CancellationToken cancellationToken = default)
  {
    var results = new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(new float[1536])).ToList());
    return Task.FromResult(results);
  }
  public EmbeddingGeneratorMetadata Metadata { get; } = new("fake");
  public void Dispose() { }
  public object? GetService(Type serviceType, object? serviceKey = null) => null;
}

public class TaidotTestFactory : WebApplicationFactory<Program>
{
  public FakeSkillStore Store { get; } = new();
  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    builder.UseSetting("OpenAI:ApiKey", "test");
    builder.ConfigureServices(services =>
    {
      var storeReg = services.Single(d => d.ServiceType == typeof(ISkillStore));
      services.Remove(storeReg);
      services.AddSingleton<ISkillStore>(Store);
      var skillRepo = services.SingleOrDefault(d => d.ServiceType == typeof(SkillRepository));
      if (skillRepo is not null) services.Remove(skillRepo);
      var qdrant = services.SingleOrDefault(d => d.ServiceType == typeof(QdrantClient));
      if (qdrant is not null) services.Remove(qdrant);
      services.AddSingleton(new QdrantClient("localhost", 6334));
      var emb = services.Single(d => d.ServiceType == typeof(IEmbeddingGenerator<string, Embedding<float>>));
      services.Remove(emb);
      services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator());
    });
  }
}

public class AdminEndpointsTests : IClassFixture<TaidotTestFactory>
{
  private readonly TaidotTestFactory _factory;
  public AdminEndpointsTests(TaidotTestFactory f) => _factory = f;

  [Fact]
  public async Task Summary_returns_skill_summaries()
  {
    _factory.Store.Entries.Add(new SkillEntry(
        Guid.NewGuid(), "How to brew coffee", "Steps for V60", "1. Boil water...",
        ["coffee"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    var client = _factory.CreateClient();
    var summary = await client.GetFromJsonAsync<AdminSummaryDto[]>("/admin/summary");
    var item = Assert.Single(summary!);
    Assert.Equal("skill", item.Kind);
    Assert.Equal("How to brew coffee", item.Title);
  }
}
```

> Note: the `SkillSeeder` call in `Program.cs` runs at startup and would invoke the fake embedding generator. That's fine; it'll succeed against the fake store.

- [ ] **Step 10: Run, expect pass**

Run: `dotnet test src/toimi.tools.taidot.Tests/ --nologo -v q`
Expected: 1 passed.

- [ ] **Step 11: Commit**

```bash
git add src/toimi.tools.taidot/ \
        src/toimi.tools.taidot.Tests/ \
        toimi.sln
git commit -m "feat(taidot): add /admin REST surface, updated_at payload, ISkillStore seam"
```

---

## Task 6: `toimi.web` admin config + named HttpClients

**Files:**
- Create: `src/toimi.web/Admin/AdminToolsOptions.cs`
- Modify: `src/toimi.web/Program.cs` (replace single `AddHttpClient("ajastin", …)` with a loop; rename existing `/api/activity` to use the loop-registered client)
- Modify: `src/toimi.web/appsettings.json`

- [ ] **Step 1: Create `AdminToolsOptions.cs`**

`src/toimi.web/Admin/AdminToolsOptions.cs`:

```csharp
namespace Toimi.Web.Admin;

public class AdminToolsOptions
{
  public string[] Tools { get; set; } = [];
}
```

- [ ] **Step 2: Update `appsettings.json`**

Read `src/toimi.web/appsettings.json` first, then add inside the `"Toimi"` object:

```jsonc
"Admin": {
  "Tools": ["muistio", "muistutin", "ajastin", "taidot"]
}
```

- [ ] **Step 3: Wire options + named clients in `Program.cs`**

In `src/toimi.web/Program.cs`, replace the existing `builder.Services.AddHttpClient("ajastin", …)` block with:

```csharp
var adminToolsOptions = builder.Configuration.GetSection("Toimi:Admin").Get<Toimi.Web.Admin.AdminToolsOptions>()
  ?? new Toimi.Web.Admin.AdminToolsOptions();
builder.Services.AddSingleton(adminToolsOptions);

foreach (var tool in adminToolsOptions.Tools)
{
  builder.Services.AddHttpClient($"admin-{tool}", client =>
  {
    var overrideUrl = builder.Configuration[$"Toimi:Admin:Urls:{tool}"];
    client.BaseAddress = new Uri(
      overrideUrl ?? $"http://toimi-tools-{tool}.apps.svc.cluster.local");
  });
}
```

The override convention (`Toimi:Admin:Urls:<tool>`) replaces the old ad-hoc `AjastinApiUrl` key. If you need a local-dev override, set `Toimi:Admin:Urls:ajastin=http://localhost:5050` in `appsettings.Development.json` instead of the legacy key.

Then update the existing `/api/activity` handler to use `httpFactory.CreateClient("admin-ajastin")` instead of `CreateClient("ajastin")`. Remove any legacy `AjastinApiUrl` reference in the same handler.

- [ ] **Step 4: Build and confirm**

Run: `dotnet build src/toimi.web/toimi.web.csproj -nologo -v q`
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add src/toimi.web/Admin/ \
        src/toimi.web/Program.cs \
        src/toimi.web/appsettings.json
git commit -m "feat(web): add AdminToolsOptions and per-tool named HttpClients"
```

---

## Task 7: `toimi.web` aggregator `/api/admin/summary`

**Files:**
- Create: `src/toimi.web/Admin/AdminEndpoints.cs`
- Modify: `src/toimi.web/Program.cs` (call `MapAdminEndpoints()`)
- Create: `src/toimi.web.Tests/toimi.web.Tests.csproj`
- Create: `src/toimi.web.Tests/AggregatorTests.cs`
- Modify: `toimi.sln`

- [ ] **Step 1: Create test project**

`src/toimi.web.Tests/toimi.web.Tests.csproj`:

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

Run: `dotnet sln toimi.sln add src/toimi.web.Tests/toimi.web.Tests.csproj`

> The aggregator logic will be a pure-function `AdminAggregator` class with no `WebApplicationFactory`, so the test project only references core.

- [ ] **Step 2: Create `AdminEndpoints.cs` with extracted aggregator**

`src/toimi.web/Admin/AdminEndpoints.cs`:

```csharp
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Toimi.Core.Admin;

namespace Toimi.Web.Admin;

public static class AdminEndpoints
{
  public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
  {
    app.MapGet("/api/admin/summary", async (
        AdminToolsOptions opts, IHttpClientFactory http, string? q, int limit) =>
    {
      var result = await AdminAggregator.AggregateAsync(
          opts.Tools, http, q, limit <= 0 ? 50 : Math.Clamp(limit, 1, 200));
      return Results.Ok(result);
    });
  }
}

public static class AdminAggregator
{
  public static async Task<AggregatedSummary> AggregateAsync(
      string[] tools, IHttpClientFactory http, string? q, int limit)
  {
    var tasks = tools.Select(async tool =>
    {
      try
      {
        var client = http.CreateClient($"admin-{tool}");
        var rows = await client.GetFromJsonAsync<AdminSummaryDto[]>(
            $"/admin/summary?q={Uri.EscapeDataString(q ?? string.Empty)}&limit={limit}");
        return (tool, items: (IReadOnlyList<AdminSummaryDto>)(rows ?? []), error: (string?)null);
      }
      catch (Exception ex)
      {
        return (tool, items: (IReadOnlyList<AdminSummaryDto>)[], error: ex.Message);
      }
    });

    var results = await Task.WhenAll(tasks);
    var merged = results
      .SelectMany(r => r.items)
      .OrderByDescending(i => i.UpdatedAt)
      .Take(limit)
      .ToList();
    var errors = results
      .Where(r => r.error is not null)
      .Select(r => new AdminError(r.tool, r.error!))
      .ToList();
    return new AggregatedSummary(merged, errors);
  }
}
```

- [ ] **Step 3: Wire into `Program.cs`**

In `src/toimi.web/Program.cs`, just before `app.MapHub<ToimiHub>(…)`:

```csharp
Toimi.Web.Admin.AdminEndpoints.MapAdminEndpoints(app);
```

- [ ] **Step 4: Write a failing aggregator unit test**

`src/toimi.web.Tests/AggregatorTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Toimi.Core.Admin;
using Toimi.Web.Admin;
using Xunit;

namespace Toimi.Web.Tests;

public class AggregatorTests
{
  private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
      => Task.FromResult(handler(request));
  }

  private sealed class StubFactory(Dictionary<string, HttpMessageHandler> handlers) : IHttpClientFactory
  {
    public HttpClient CreateClient(string name)
      => new(handlers[name]) { BaseAddress = new Uri("http://localhost") };
  }

  [Fact]
  public async Task Merges_items_by_UpdatedAt_desc_and_collects_errors()
  {
    var now = DateTimeOffset.UtcNow;
    var muistioItem = new AdminSummaryDto("a", "memory", "older", null, now.AddHours(-2), now.AddHours(-2));
    var ajastinItem = new AdminSummaryDto("b", "schedule", "newer", null, now.AddHours(-1), now.AddHours(-1));

    var handlers = new Dictionary<string, HttpMessageHandler>
    {
      ["admin-muistio"] = new StubHandler(_ =>
      {
        var msg = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(new[] { muistioItem }) };
        return msg;
      }),
      ["admin-ajastin"] = new StubHandler(_ =>
      {
        var msg = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(new[] { ajastinItem }) };
        return msg;
      }),
      ["admin-muistutin"] = new StubHandler(_ => throw new HttpRequestException("boom")),
    };
    var factory = new StubFactory(handlers);

    var result = await AdminAggregator.AggregateAsync(
        ["muistio", "ajastin", "muistutin"], factory, q: null, limit: 50);

    Assert.Equal(2, result.Items.Count);
    Assert.Equal("b", result.Items[0].Id); // newer first
    Assert.Equal("a", result.Items[1].Id);
    var err = Assert.Single(result.Errors);
    Assert.Equal("muistutin", err.Tool);
  }
}
```

- [ ] **Step 5: Run, expect pass**

Run: `dotnet test src/toimi.web.Tests/ --nologo -v q`
Expected: 1 passed.

- [ ] **Step 6: Commit**

```bash
git add src/toimi.web/Admin/AdminEndpoints.cs \
        src/toimi.web/Program.cs \
        src/toimi.web.Tests/ \
        toimi.sln
git commit -m "feat(web): add /api/admin/summary aggregator with partial-failure tolerance"
```

---

## Task 8: `toimi.web` generic forwarder `/api/admin/{tool}/{**path}`

**Files:**
- Modify: `src/toimi.web/Admin/AdminEndpoints.cs` (add forwarder)
- Modify: `src/toimi.web.Tests/AggregatorTests.cs` (add forwarder integration test) — or new file

- [ ] **Step 1: Add forwarder to `AdminEndpoints.cs`**

In `src/toimi.web/Admin/AdminEndpoints.cs`, inside `MapAdminEndpoints`, after the summary mapping:

```csharp
app.Map("/api/admin/{tool}/{**path}", async (
    string tool, string? path, HttpContext ctx,
    AdminToolsOptions opts, IHttpClientFactory http) =>
{
  if (!opts.Tools.Contains(tool))
    return Results.NotFound();

  var client = http.CreateClient($"admin-{tool}");
  var upstreamPath = $"/admin/{path}{ctx.Request.QueryString}";
  var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), upstreamPath);

  if (ctx.Request.Headers.TryGetValue("If-Unmodified-Since", out var ius))
    req.Headers.TryAddWithoutValidation("If-Unmodified-Since", ius.ToArray());

  if (HttpMethods.IsPost(ctx.Request.Method)
      || HttpMethods.IsPut(ctx.Request.Method)
      || HttpMethods.IsPatch(ctx.Request.Method))
  {
    var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    ms.Position = 0;
    req.Content = new StreamContent(ms);
    if (!string.IsNullOrEmpty(ctx.Request.ContentType))
      req.Content.Headers.TryAddWithoutValidation("Content-Type", ctx.Request.ContentType);
  }

  HttpResponseMessage resp;
  try { resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead); }
  catch (HttpRequestException ex) { return Results.Problem(ex.Message, statusCode: 502); }

  ctx.Response.StatusCode = (int)resp.StatusCode;
  foreach (var h in resp.Content.Headers)
    ctx.Response.Headers[h.Key] = h.Value.ToArray();
  await resp.Content.CopyToAsync(ctx.Response.Body);
  return Results.Empty;
});
```

- [ ] **Step 2: Write a forwarder unit test**

Append to `src/toimi.web.Tests/AggregatorTests.cs` (or split into a new file `ForwarderTests.cs`):

```csharp
public class ForwarderTests
{
  [Fact]
  public async Task Unknown_tool_returns_404()
  {
    // We can't easily test the minimal-API forwarder without WebApplicationFactory.
    // Instead, refactor the forwarder body into a static helper to test directly,
    // OR add Microsoft.AspNetCore.Mvc.Testing + a ProjectReference to toimi.web.
    Assert.True(true); // placeholder if no factory wired; see step 3.
  }
}
```

> Decision: extract the forwarder body into a testable static `AdminForwarder.ForwardAsync(string tool, string path, HttpContext ctx, AdminToolsOptions opts, IHttpClientFactory http)` so we can unit-test it with stubbed `HttpContext` and `IHttpClientFactory`.

- [ ] **Step 3: Refactor forwarder into `AdminForwarder.ForwardAsync`**

Replace the lambda body in `AdminEndpoints.cs` with:

```csharp
app.Map("/api/admin/{tool}/{**path}", AdminForwarder.ForwardAsync);
```

And add `AdminForwarder` to the same file:

```csharp
public static class AdminForwarder
{
  public static async Task<IResult> ForwardAsync(
      string tool, string? path, HttpContext ctx,
      AdminToolsOptions opts, IHttpClientFactory http)
  {
    if (!opts.Tools.Contains(tool)) return Results.NotFound();

    var client = http.CreateClient($"admin-{tool}");
    var upstreamPath = $"/admin/{path}{ctx.Request.QueryString}";
    var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), upstreamPath);

    if (ctx.Request.Headers.TryGetValue("If-Unmodified-Since", out var ius))
      req.Headers.TryAddWithoutValidation("If-Unmodified-Since", ius.ToArray());

    if (HttpMethods.IsPost(ctx.Request.Method)
        || HttpMethods.IsPut(ctx.Request.Method)
        || HttpMethods.IsPatch(ctx.Request.Method))
    {
      var ms = new MemoryStream();
      await ctx.Request.Body.CopyToAsync(ms);
      ms.Position = 0;
      req.Content = new StreamContent(ms);
      if (!string.IsNullOrEmpty(ctx.Request.ContentType))
        req.Content.Headers.TryAddWithoutValidation("Content-Type", ctx.Request.ContentType);
    }

    HttpResponseMessage resp;
    try { resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead); }
    catch (HttpRequestException ex) { return Results.Problem(ex.Message, statusCode: 502); }

    ctx.Response.StatusCode = (int)resp.StatusCode;
    foreach (var h in resp.Content.Headers)
      ctx.Response.Headers[h.Key] = h.Value.ToArray();
    await resp.Content.CopyToAsync(ctx.Response.Body);
    return Results.Empty;
  }
}
```

- [ ] **Step 4: Replace placeholder test with real unit test**

Replace `ForwarderTests` body in `AggregatorTests.cs`:

```csharp
public class ForwarderTests
{
  private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
      => Task.FromResult(handler(request));
  }

  private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
  {
    public HttpClient CreateClient(string name) => new(handler) { BaseAddress = new Uri("http://upstream") };
  }

  [Fact]
  public async Task Unknown_tool_returns_404()
  {
    var ctx = new DefaultHttpContext();
    ctx.Request.Method = "GET";
    var opts = new AdminToolsOptions { Tools = ["muistio"] };
    var factory = new StubFactory(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
    var result = await AdminForwarder.ForwardAsync("notreal", "items", ctx, opts, factory);
    Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
  }

  [Fact]
  public async Task Forwards_query_and_method()
  {
    HttpRequestMessage? captured = null;
    var handler = new StubHandler(req =>
    {
      captured = req;
      return new HttpResponseMessage(HttpStatusCode.OK)
      { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
    });
    var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
    ctx.Request.Method = "GET";
    ctx.Request.QueryString = new QueryString("?q=foo&page=2");
    var opts = new AdminToolsOptions { Tools = ["muistio"] };
    var factory = new StubFactory(handler);

    await AdminForwarder.ForwardAsync("muistio", "items", ctx, opts, factory);

    Assert.NotNull(captured);
    Assert.Equal(HttpMethod.Get, captured!.Method);
    Assert.Equal("/admin/items?q=foo&page=2", captured.RequestUri!.PathAndQuery);
    Assert.Equal(200, ctx.Response.StatusCode);
  }
}
```

Add `using Microsoft.AspNetCore.Http;` at the top of the file.

- [ ] **Step 5: Add `FrameworkReference` to `Microsoft.AspNetCore.App` in the test project**

Edit `src/toimi.web.Tests/toimi.web.Tests.csproj` — add to the existing `<ItemGroup>` block:

```xml
<FrameworkReference Include="Microsoft.AspNetCore.App" />
<ProjectReference Include="../toimi.web/toimi.web.csproj" />
```

- [ ] **Step 6: Run, expect pass**

Run: `dotnet test src/toimi.web.Tests/ --nologo -v q`
Expected: 3 passed.

- [ ] **Step 7: Commit**

```bash
git add src/toimi.web/Admin/AdminEndpoints.cs \
        src/toimi.web.Tests/
git commit -m "feat(web): add /api/admin/{tool}/* generic forwarder"
```

---

## Task 9: React router setup + admin layout shell

**Files:**
- Modify: `src/toimi.web/ClientApp/package.json` (add `react-router-dom`)
- Modify: `src/toimi.web/ClientApp/src/App.tsx`
- Create: `src/toimi.web/ClientApp/src/admin/AdminLayout.tsx`
- Modify: `src/toimi.web/ClientApp/src/components/ToimiView.tsx` (add "Admin" link)

- [ ] **Step 1: Install `react-router-dom`**

Run: `cd src/toimi.web/ClientApp && npm install react-router-dom`
Expected: dependency added in `package.json`.

- [ ] **Step 2: Create the admin layout shell**

`src/toimi.web/ClientApp/src/admin/AdminLayout.tsx`:

```tsx
import { Link, NavLink, Outlet } from 'react-router-dom'

const links = [
  { to: '/admin', label: 'Dashboard', end: true },
  { to: '/admin/muistio', label: 'Memories' },
  { to: '/admin/muistutin', label: 'Reminders' },
  { to: '/admin/ajastin', label: 'Schedules' },
  { to: '/admin/taidot', label: 'Skills' },
]

export function AdminLayout() {
  return (
    <div className="flex h-screen">
      <aside className="w-48 border-r border-gray-700 p-4 flex flex-col gap-2">
        <Link to="/" className="text-sm text-gray-400 mb-4">← Chat</Link>
        {links.map(l => (
          <NavLink
            key={l.to}
            to={l.to}
            end={l.end}
            className={({ isActive }) =>
              `px-3 py-2 rounded ${isActive ? 'bg-gray-700' : 'hover:bg-gray-800'}`
            }
          >
            {l.label}
          </NavLink>
        ))}
      </aside>
      <main className="flex-1 overflow-y-auto p-6">
        <Outlet />
      </main>
    </div>
  )
}
```

- [ ] **Step 3: Replace `App.tsx` with a router**

`src/toimi.web/ClientApp/src/App.tsx`:

```tsx
import { BrowserRouter, Route, Routes } from 'react-router-dom'
import { ToimiView } from './components/ToimiView.tsx'
import { AdminLayout } from './admin/AdminLayout.tsx'

export default function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/" element={<ToimiView />} />
        <Route path="/admin" element={<AdminLayout />}>
          <Route index element={<div>Dashboard placeholder</div>} />
          <Route path="muistio" element={<div>Memories placeholder</div>} />
          <Route path="muistutin" element={<div>Reminders placeholder</div>} />
          <Route path="ajastin" element={<div>Schedules placeholder</div>} />
          <Route path="ajastin/:id" element={<div>Schedule detail placeholder</div>} />
          <Route path="taidot" element={<div>Skills placeholder</div>} />
        </Route>
      </Routes>
    </BrowserRouter>
  )
}
```

- [ ] **Step 4: Add "Admin" link in `ToimiView`**

Edit `src/toimi.web/ClientApp/src/components/ToimiView.tsx`:

1. Add the import (after the other imports at the top of the file):

```tsx
import { Link } from 'react-router-dom'
```

2. Inside the header's right-side `div` (line 15: `<div className="flex items-center gap-3 text-sm">`), add the Admin link as the first child, before `<ActivityList />`:

```tsx
<Link to="/admin" className="text-zinc-400 hover:text-zinc-100">Admin</Link>
```

After the change, the header right-side div should look like:

```tsx
<div className="flex items-center gap-3 text-sm">
  <Link to="/admin" className="text-zinc-400 hover:text-zinc-100">Admin</Link>
  <ActivityList />
  <ConversationList ... />
  ...
</div>
```

- [ ] **Step 5: Build the SPA**

Run: `cd src/toimi.web/ClientApp && npm run build`
Expected: build succeeds, no TypeScript errors.

- [ ] **Step 6: Update web fallback to serve SPA for /admin paths**

Verify `src/toimi.web/Program.cs` already has `app.MapFallbackToFile("index.html");` — it does. No change needed; routes under `/admin/*` resolve to `index.html` and the SPA router takes over.

- [ ] **Step 7: Commit**

```bash
git add src/toimi.web/ClientApp/package.json \
        src/toimi.web/ClientApp/package-lock.json \
        src/toimi.web/ClientApp/src/App.tsx \
        src/toimi.web/ClientApp/src/admin/AdminLayout.tsx \
        src/toimi.web/ClientApp/src/components/ToimiView.tsx
git commit -m "feat(web): add react-router-dom and /admin layout shell"
```

---

## Task 10: Admin primitives — typed fetch hooks + shared UI components

**Files:**
- Create: `src/toimi.web/ClientApp/src/admin/useAdmin.ts`
- Create: `src/toimi.web/ClientApp/src/admin/useAdminSummary.ts`
- Create: `src/toimi.web/ClientApp/src/admin/DataTable.tsx`
- Create: `src/toimi.web/ClientApp/src/admin/ConfirmDelete.tsx`
- Create: `src/toimi.web/ClientApp/src/admin/EmptyState.tsx`
- Create: `src/toimi.web/ClientApp/src/admin/ErrorBanner.tsx`
- Create: `src/toimi.web/ClientApp/src/admin/StaleConflictModal.tsx`

- [ ] **Step 1: Create `useAdmin.ts`**

`src/toimi.web/ClientApp/src/admin/useAdmin.ts`:

```ts
import { useCallback, useEffect, useState } from 'react'

export interface AdminFetchError { status: number; body?: unknown }

export function useAdminList<T>(tool: string, path: string, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<AdminFetchError | null>(null)
  const [loading, setLoading] = useState(true)
  const reload = useCallback(async () => {
    setLoading(true); setError(null)
    try {
      const resp = await fetch(`/api/admin/${tool}/${path}`)
      if (!resp.ok) { setError({ status: resp.status, body: await safeJson(resp) }); return }
      setData(await resp.json() as T)
    } finally { setLoading(false) }
  }, [tool, path])
  useEffect(() => { void reload() // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tool, path, ...deps])
  return { data, error, loading, reload }
}

export async function adminPut<TBody, TResult>(
  tool: string, path: string, body: TBody, ifUnmodifiedSince: string,
): Promise<{ ok: true; data: TResult } | { ok: false; status: number; body?: unknown }> {
  const resp = await fetch(`/api/admin/${tool}/${path}`, {
    method: 'PUT',
    headers: { 'content-type': 'application/json', 'if-unmodified-since': ifUnmodifiedSince },
    body: JSON.stringify(body),
  })
  if (!resp.ok) return { ok: false, status: resp.status, body: await safeJson(resp) }
  return { ok: true, data: await resp.json() as TResult }
}

export async function adminDelete(tool: string, path: string): Promise<boolean> {
  const resp = await fetch(`/api/admin/${tool}/${path}`, { method: 'DELETE' })
  return resp.ok
}

export async function adminPost(tool: string, path: string): Promise<boolean> {
  const resp = await fetch(`/api/admin/${tool}/${path}`, { method: 'POST' })
  return resp.ok
}

async function safeJson(resp: Response) {
  try { return await resp.json() } catch { return undefined }
}
```

- [ ] **Step 2: Create `useAdminSummary.ts`**

`src/toimi.web/ClientApp/src/admin/useAdminSummary.ts`:

```ts
import { useEffect, useState } from 'react'

export interface AdminSummaryDto {
  id: string
  kind: 'memory' | 'reminder' | 'schedule' | 'skill'
  title: string
  subtitle: string | null
  createdAt: string
  updatedAt: string
}

export interface AggregatedSummary {
  items: AdminSummaryDto[]
  errors: { tool: string; message: string }[]
}

export function useAdminSummary(query: string) {
  const [data, setData] = useState<AggregatedSummary | null>(null)
  const [loading, setLoading] = useState(true)
  useEffect(() => {
    let cancelled = false
    setLoading(true)
    const url = `/api/admin/summary?q=${encodeURIComponent(query)}&limit=50`
    void fetch(url).then(async r => {
      if (cancelled) return
      if (r.ok) setData(await r.json() as AggregatedSummary)
      setLoading(false)
    })
    return () => { cancelled = true }
  }, [query])
  return { data, loading }
}
```

- [ ] **Step 3: Create `DataTable.tsx`**

`src/toimi.web/ClientApp/src/admin/DataTable.tsx`:

```tsx
export interface Column<T> { key: string; header: string; render: (row: T) => React.ReactNode }

export function DataTable<T extends { id: string | number }>({ rows, columns, onRowClick }: {
  rows: T[]; columns: Column<T>[]; onRowClick?: (row: T) => void;
}) {
  return (
    <table className="w-full text-left text-sm">
      <thead className="text-gray-400">
        <tr>{columns.map(c => <th key={c.key} className="px-3 py-2">{c.header}</th>)}</tr>
      </thead>
      <tbody>
        {rows.map(r => (
          <tr
            key={r.id}
            onClick={() => onRowClick?.(r)}
            className={onRowClick ? 'hover:bg-gray-800 cursor-pointer' : ''}
          >
            {columns.map(c => <td key={c.key} className="px-3 py-2 border-t border-gray-800">{c.render(r)}</td>)}
          </tr>
        ))}
      </tbody>
    </table>
  )
}
```

- [ ] **Step 4: Create `ConfirmDelete.tsx`**

`src/toimi.web/ClientApp/src/admin/ConfirmDelete.tsx`:

```tsx
export function ConfirmDelete({ open, label, onConfirm, onCancel }: {
  open: boolean; label: string; onConfirm: () => void; onCancel: () => void;
}) {
  if (!open) return null
  return (
    <div className="fixed inset-0 bg-black/60 flex items-center justify-center">
      <div className="bg-gray-900 border border-gray-700 rounded p-6 w-96">
        <h3 className="text-lg mb-3">Delete {label}?</h3>
        <p className="text-sm text-gray-400 mb-4">This cannot be undone.</p>
        <div className="flex justify-end gap-2">
          <button className="px-3 py-1 rounded bg-gray-700" onClick={onCancel}>Cancel</button>
          <button className="px-3 py-1 rounded bg-red-700" onClick={onConfirm}>Delete</button>
        </div>
      </div>
    </div>
  )
}
```

- [ ] **Step 5: Create `EmptyState.tsx`**

`src/toimi.web/ClientApp/src/admin/EmptyState.tsx`:

```tsx
export function EmptyState({ message }: { message: string }) {
  return <div className="text-gray-500 text-sm p-6 text-center">{message}</div>
}
```

- [ ] **Step 6: Create `ErrorBanner.tsx`**

`src/toimi.web/ClientApp/src/admin/ErrorBanner.tsx`:

```tsx
export function ErrorBanner({ errors }: { errors: { tool: string; message: string }[] }) {
  if (!errors.length) return null
  return (
    <div className="bg-yellow-900/40 border border-yellow-700 text-yellow-200 p-3 rounded mb-4 text-sm">
      Some stores are unavailable:&nbsp;
      {errors.map((e, i) => (
        <span key={e.tool}>
          {e.tool}{i < errors.length - 1 ? ', ' : ''}
        </span>
      ))}
    </div>
  )
}
```

- [ ] **Step 7: Create `StaleConflictModal.tsx`**

`src/toimi.web/ClientApp/src/admin/StaleConflictModal.tsx`:

```tsx
export function StaleConflictModal({ open, onReload, onDismiss }: {
  open: boolean; onReload: () => void; onDismiss: () => void;
}) {
  if (!open) return null
  return (
    <div className="fixed inset-0 bg-black/60 flex items-center justify-center">
      <div className="bg-gray-900 border border-gray-700 rounded p-6 w-96">
        <h3 className="text-lg mb-3">Item changed elsewhere</h3>
        <p className="text-sm text-gray-400 mb-4">Reload to see the latest version.</p>
        <div className="flex justify-end gap-2">
          <button className="px-3 py-1 rounded bg-gray-700" onClick={onDismiss}>Cancel</button>
          <button className="px-3 py-1 rounded bg-blue-700" onClick={onReload}>Reload</button>
        </div>
      </div>
    </div>
  )
}
```

- [ ] **Step 8: Build to verify**

Run: `cd src/toimi.web/ClientApp && npm run build`
Expected: build succeeds.

- [ ] **Step 9: Commit**

```bash
git add src/toimi.web/ClientApp/src/admin/
git commit -m "feat(web): add admin React primitives (hooks, table, modals)"
```

---

## Task 11: Dashboard page (global search + recents)

**Files:**
- Create: `src/toimi.web/ClientApp/src/admin/DashboardPage.tsx`
- Modify: `src/toimi.web/ClientApp/src/App.tsx` (use `DashboardPage` for `/admin` index)

- [ ] **Step 1: Create `DashboardPage.tsx`**

```tsx
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useAdminSummary, type AdminSummaryDto } from './useAdminSummary'
import { ErrorBanner } from './ErrorBanner'
import { EmptyState } from './EmptyState'

const kindToTool: Record<AdminSummaryDto['kind'], string> = {
  memory: 'muistio',
  reminder: 'muistutin',
  schedule: 'ajastin',
  skill: 'taidot',
}

export function DashboardPage() {
  const [q, setQ] = useState('')
  const { data, loading } = useAdminSummary(q)

  return (
    <div>
      <h1 className="text-2xl mb-4">Dashboard</h1>
      <input
        className="w-full bg-gray-800 border border-gray-700 rounded p-2 mb-4"
        placeholder="Search across memories, reminders, schedules, skills…"
        value={q} onChange={e => setQ(e.target.value)}
      />
      <ErrorBanner errors={data?.errors ?? []} />
      {loading && <div className="text-gray-500 text-sm">Loading…</div>}
      {!loading && data && data.items.length === 0 && <EmptyState message="No matches." />}
      <ul className="divide-y divide-gray-800">
        {data?.items.map(item => (
          <li key={`${item.kind}:${item.id}`} className="py-3">
            <Link
              to={`/admin/${kindToTool[item.kind]}#${item.id}`}
              className="block hover:bg-gray-800 -mx-3 px-3 py-1 rounded"
            >
              <div className="flex justify-between text-sm">
                <span className="font-medium">{item.title}</span>
                <span className="text-gray-500">{item.kind}</span>
              </div>
              {item.subtitle && <div className="text-xs text-gray-500">{item.subtitle}</div>}
            </Link>
          </li>
        ))}
      </ul>
    </div>
  )
}
```

- [ ] **Step 2: Wire `DashboardPage` into `App.tsx`**

In `src/toimi.web/ClientApp/src/App.tsx`, replace `<Route index element={<div>Dashboard placeholder</div>} />` with:

```tsx
<Route index element={<DashboardPage />} />
```

And import: `import { DashboardPage } from './admin/DashboardPage.tsx'`.

- [ ] **Step 3: Build**

Run: `cd src/toimi.web/ClientApp && npm run build`
Expected: success.

- [ ] **Step 4: Commit**

```bash
git add src/toimi.web/ClientApp/src/admin/DashboardPage.tsx \
        src/toimi.web/ClientApp/src/App.tsx
git commit -m "feat(web): admin dashboard with global search and partial-failure banner"
```

---

## Task 12: Memories page

**Files:**
- Create: `src/toimi.web/ClientApp/src/admin/MemoriesPage.tsx`
- Modify: `src/toimi.web/ClientApp/src/App.tsx`

- [ ] **Step 1: Create `MemoriesPage.tsx`**

```tsx
import { useEffect, useMemo, useState } from 'react'
import { useAdminList, adminDelete, adminPut } from './useAdmin'
import { DataTable, type Column } from './DataTable'
import { ConfirmDelete } from './ConfirmDelete'
import { ErrorBanner } from './ErrorBanner'
import { EmptyState } from './EmptyState'

interface MemoryItem {
  id: string
  content: string
  category: string | null
  tags: string[]
  source: string
  confirmed: boolean
  expiresAt: string | null
  createdAt: string
  updatedAt: string
}
interface PagedResult<T> { items: T[]; page: number; size: number; total: number }

export function MemoriesPage() {
  const [page, setPage] = useState(1)
  const [q, setQ] = useState('')
  const { data, loading, reload } = useAdminList<PagedResult<MemoryItem>>(
    'muistio', `items?page=${page}&size=20&q=${encodeURIComponent(q)}`, [page, q]
  )
  const [pendingDelete, setPendingDelete] = useState<MemoryItem | null>(null)
  const [editing, setEditing] = useState<MemoryItem | null>(null)

  const columns: Column<MemoryItem>[] = useMemo(() => [
    { key: 'content', header: 'Content', render: r => r.content },
    { key: 'source', header: 'Source', render: r => r.source },
    { key: 'confirmed', header: 'Confirmed', render: r => r.confirmed ? 'Yes' : 'No' },
    { key: 'updated', header: 'Updated', render: r => new Date(r.updatedAt).toLocaleString() },
    { key: 'actions', header: '', render: r => (
        <div className="flex gap-2">
          <button className="text-blue-400" onClick={e => { e.stopPropagation(); setEditing(r) }}>Edit</button>
          <button className="text-red-400" onClick={e => { e.stopPropagation(); setPendingDelete(r) }}>Delete</button>
        </div>
    ) },
  ], [])

  return (
    <div>
      <h1 className="text-2xl mb-4">Memories</h1>
      <ErrorBanner errors={data ? [] : []} />
      <input
        className="w-full bg-gray-800 border border-gray-700 rounded p-2 mb-4"
        placeholder="Search content…"
        value={q} onChange={e => { setPage(1); setQ(e.target.value) }}
      />
      {loading && <div className="text-gray-500 text-sm">Loading…</div>}
      {data && data.items.length === 0 && <EmptyState message="No memories yet." />}
      {data && data.items.length > 0 && (
        <>
          <DataTable rows={data.items} columns={columns} />
          <div className="mt-3 text-sm text-gray-400 flex gap-3 items-center">
            <button disabled={page === 1} onClick={() => setPage(p => p - 1)} className="disabled:opacity-30">← Prev</button>
            <span>Page {data.page} ({data.total} total)</span>
            <button disabled={data.page * data.size >= data.total} onClick={() => setPage(p => p + 1)} className="disabled:opacity-30">Next →</button>
          </div>
        </>
      )}
      <ConfirmDelete
        open={!!pendingDelete}
        label="memory"
        onCancel={() => setPendingDelete(null)}
        onConfirm={async () => {
          if (!pendingDelete) return
          await adminDelete('muistio', `items/${pendingDelete.id}`)
          setPendingDelete(null)
          await reload()
        }}
      />
      <EditMemoryDialog
        item={editing}
        onClose={() => setEditing(null)}
        onSaved={async () => { setEditing(null); await reload() }}
      />
    </div>
  )
}

function EditMemoryDialog({ item, onClose, onSaved }: {
  item: MemoryItem | null; onClose: () => void; onSaved: () => void;
}) {
  const [content, setContent] = useState('')
  useEffect(() => { if (item) setContent(item.content) }, [item])
  if (!item) return null
  return (
    <div className="fixed inset-0 bg-black/60 flex items-center justify-center">
      <div className="bg-gray-900 border border-gray-700 rounded p-6 w-[32rem]">
        <h3 className="text-lg mb-3">Edit memory</h3>
        <textarea
          className="w-full bg-gray-800 border border-gray-700 rounded p-2 h-32"
          defaultValue={item.content}
          onChange={e => setContent(e.target.value)}
        />
        <div className="flex justify-end gap-2 mt-3">
          <button className="px-3 py-1 rounded bg-gray-700" onClick={onClose}>Cancel</button>
          <button
            className="px-3 py-1 rounded bg-blue-700"
            onClick={async () => {
              const result = await adminPut<{ content: string }, MemoryItem>(
                'muistio', `items/${item.id}`,
                { content: content || item.content },
                item.updatedAt
              )
              if (result.ok) onSaved()
              else alert(`Update failed (HTTP ${result.status})`)
            }}
          >Save</button>
        </div>
      </div>
    </div>
  )
}
```

- [ ] **Step 2: Wire route in `App.tsx`**

Replace `<Route path="muistio" element={<div>Memories placeholder</div>} />` with `<Route path="muistio" element={<MemoriesPage />} />` and add the import.

- [ ] **Step 3: Build**

Run: `cd src/toimi.web/ClientApp && npm run build`
Expected: success.

- [ ] **Step 4: Commit**

```bash
git add src/toimi.web/ClientApp/src/admin/MemoriesPage.tsx \
        src/toimi.web/ClientApp/src/App.tsx
git commit -m "feat(web): memories admin page (list/edit/delete)"
```

---

## Task 13: Reminders page

**Files:**
- Create: `src/toimi.web/ClientApp/src/admin/RemindersPage.tsx`
- Modify: `src/toimi.web/ClientApp/src/App.tsx`

- [ ] **Step 1: Create `RemindersPage.tsx`**

```tsx
import { useMemo, useState } from 'react'
import { useAdminList, adminDelete, adminPost } from './useAdmin'
import { DataTable, type Column } from './DataTable'
import { ConfirmDelete } from './ConfirmDelete'
import { EmptyState } from './EmptyState'

interface ReminderItem {
  id: string
  title: string
  description: string | null
  dateTimeUtc: string
  timeZone: string
  recurrenceRule: string | null
  isCompleted: boolean
  updatedAt: string
}
interface PagedResult<T> { items: T[]; page: number; size: number; total: number }

export function RemindersPage() {
  const [page, setPage] = useState(1)
  const [q, setQ] = useState('')
  const { data, loading, reload } = useAdminList<PagedResult<ReminderItem>>(
    'muistutin', `items?page=${page}&size=20&q=${encodeURIComponent(q)}`, [page, q]
  )
  const [pendingDelete, setPendingDelete] = useState<ReminderItem | null>(null)

  const columns: Column<ReminderItem>[] = useMemo(() => [
    { key: 'title', header: 'Title', render: r => r.title },
    { key: 'when', header: 'When', render: r => new Date(r.dateTimeUtc).toLocaleString() },
    { key: 'recurring', header: 'Recurring', render: r => r.recurrenceRule ? 'Yes' : 'No' },
    { key: 'status', header: 'Status', render: r => r.isCompleted ? 'Completed' : 'Pending' },
    { key: 'actions', header: '', render: r => (
      <div className="flex gap-2">
        {!r.isCompleted && (
          <button className="text-green-400" onClick={async e => {
            e.stopPropagation()
            await adminPost('muistutin', `items/${r.id}/complete`)
            await reload()
          }}>Complete</button>
        )}
        <button className="text-red-400" onClick={e => { e.stopPropagation(); setPendingDelete(r) }}>Delete</button>
      </div>
    ) },
  ], [reload])

  return (
    <div>
      <h1 className="text-2xl mb-4">Reminders</h1>
      <input
        className="w-full bg-gray-800 border border-gray-700 rounded p-2 mb-4"
        placeholder="Search title…"
        value={q} onChange={e => { setPage(1); setQ(e.target.value) }}
      />
      {loading && <div className="text-gray-500 text-sm">Loading…</div>}
      {data && data.items.length === 0 && <EmptyState message="No reminders." />}
      {data && data.items.length > 0 && (
        <>
          <DataTable rows={data.items} columns={columns} />
          <div className="mt-3 text-sm text-gray-400 flex gap-3 items-center">
            <button disabled={page === 1} onClick={() => setPage(p => p - 1)} className="disabled:opacity-30">← Prev</button>
            <span>Page {data.page} ({data.total} total)</span>
            <button disabled={data.page * data.size >= data.total} onClick={() => setPage(p => p + 1)} className="disabled:opacity-30">Next →</button>
          </div>
        </>
      )}
      <ConfirmDelete
        open={!!pendingDelete}
        label="reminder"
        onCancel={() => setPendingDelete(null)}
        onConfirm={async () => {
          if (!pendingDelete) return
          await adminDelete('muistutin', `items/${pendingDelete.id}`)
          setPendingDelete(null)
          await reload()
        }}
      />
    </div>
  )
}
```

- [ ] **Step 2: Wire route in `App.tsx`**

Replace placeholder route with `<Route path="muistutin" element={<RemindersPage />} />` and import.

- [ ] **Step 3: Build**

Run: `cd src/toimi.web/ClientApp && npm run build`

- [ ] **Step 4: Commit**

```bash
git add src/toimi.web/ClientApp/src/admin/RemindersPage.tsx \
        src/toimi.web/ClientApp/src/App.tsx
git commit -m "feat(web): reminders admin page (list/complete/delete)"
```

---

## Task 14: Schedules page + detail with runs

**Files:**
- Create: `src/toimi.web/ClientApp/src/admin/SchedulesPage.tsx`
- Create: `src/toimi.web/ClientApp/src/admin/ScheduleDetailPage.tsx`
- Modify: `src/toimi.web/ClientApp/src/App.tsx`

- [ ] **Step 1: Create `SchedulesPage.tsx`**

```tsx
import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useAdminList, adminDelete, adminPost } from './useAdmin'
import { DataTable, type Column } from './DataTable'
import { ConfirmDelete } from './ConfirmDelete'
import { EmptyState } from './EmptyState'

interface ScheduleItem {
  id: string
  name: string
  cronExpression: string | null
  runAt: string | null
  prompt: string
  enabled: boolean
  lastRunAt: string | null
  updatedAt: string
}
interface PagedResult<T> { items: T[]; page: number; size: number; total: number }

export function SchedulesPage() {
  const nav = useNavigate()
  const [page, setPage] = useState(1)
  const [q, setQ] = useState('')
  const { data, loading, reload } = useAdminList<PagedResult<ScheduleItem>>(
    'ajastin', `items?page=${page}&size=20&q=${encodeURIComponent(q)}`, [page, q]
  )
  const [pendingDelete, setPendingDelete] = useState<ScheduleItem | null>(null)

  const columns: Column<ScheduleItem>[] = useMemo(() => [
    { key: 'name', header: 'Name', render: r => r.name },
    { key: 'trigger', header: 'Trigger', render: r => r.cronExpression ?? (r.runAt ? `at ${new Date(r.runAt).toLocaleString()}` : '—') },
    { key: 'enabled', header: 'Enabled', render: r => r.enabled ? 'Yes' : 'No' },
    { key: 'last', header: 'Last run', render: r => r.lastRunAt ? new Date(r.lastRunAt).toLocaleString() : 'Never' },
    { key: 'actions', header: '', render: r => (
      <div className="flex gap-2">
        <button className="text-blue-400" onClick={async e => {
          e.stopPropagation()
          await adminPost('ajastin', `items/${r.id}/run-now`)
          await reload()
        }}>Run now</button>
        <button className="text-yellow-400" onClick={async e => {
          e.stopPropagation()
          await adminPost('ajastin', `items/${r.id}/${r.enabled ? 'disable' : 'enable'}`)
          await reload()
        }}>{r.enabled ? 'Disable' : 'Enable'}</button>
        <button className="text-red-400" onClick={e => { e.stopPropagation(); setPendingDelete(r) }}>Delete</button>
      </div>
    ) },
  ], [reload])

  return (
    <div>
      <h1 className="text-2xl mb-4">Schedules</h1>
      <input
        className="w-full bg-gray-800 border border-gray-700 rounded p-2 mb-4"
        placeholder="Search name…"
        value={q} onChange={e => { setPage(1); setQ(e.target.value) }}
      />
      {loading && <div className="text-gray-500 text-sm">Loading…</div>}
      {data && data.items.length === 0 && <EmptyState message="No schedules." />}
      {data && data.items.length > 0 && (
        <>
          <DataTable
            rows={data.items}
            columns={columns}
            onRowClick={r => nav(`/admin/ajastin/${r.id}`)}
          />
          <div className="mt-3 text-sm text-gray-400 flex gap-3 items-center">
            <button disabled={page === 1} onClick={() => setPage(p => p - 1)} className="disabled:opacity-30">← Prev</button>
            <span>Page {data.page} ({data.total} total)</span>
            <button disabled={data.page * data.size >= data.total} onClick={() => setPage(p => p + 1)} className="disabled:opacity-30">Next →</button>
          </div>
        </>
      )}
      <ConfirmDelete
        open={!!pendingDelete}
        label="schedule"
        onCancel={() => setPendingDelete(null)}
        onConfirm={async () => {
          if (!pendingDelete) return
          await adminDelete('ajastin', `items/${pendingDelete.id}`)
          setPendingDelete(null)
          await reload()
        }}
      />
    </div>
  )
}
```

- [ ] **Step 2: Create `ScheduleDetailPage.tsx`**

```tsx
import { useParams, Link } from 'react-router-dom'
import { useAdminList } from './useAdmin'
import { DataTable, type Column } from './DataTable'
import { EmptyState } from './EmptyState'

interface ScheduleItem {
  id: string; name: string; cronExpression: string | null; runAt: string | null;
  prompt: string; enabled: boolean; lastRunAt: string | null; updatedAt: string;
}
interface RunItem {
  id: string; startedAt: string; completedAt: string | null;
  success: boolean; response: string | null; error: string | null;
}

export function ScheduleDetailPage() {
  const { id } = useParams<{ id: string }>()
  const { data: schedule, loading } = useAdminList<ScheduleItem>('ajastin', `items/${id}`, [id])
  const { data: runs } = useAdminList<RunItem[]>('ajastin', `items/${id}/runs?limit=20`, [id])

  if (loading) return <div className="text-gray-500 text-sm">Loading…</div>
  if (!schedule) return <EmptyState message="Schedule not found." />

  const cols: Column<RunItem>[] = [
    { key: 'when', header: 'Started', render: r => new Date(r.startedAt).toLocaleString() },
    { key: 'ok', header: 'Status', render: r => r.success ? 'Success' : 'Failed' },
    { key: 'preview', header: 'Response', render: r => (r.response ?? r.error ?? '').slice(0, 100) },
  ]

  return (
    <div>
      <Link to="/admin/ajastin" className="text-sm text-gray-400">← Schedules</Link>
      <h1 className="text-2xl mt-2 mb-4">{schedule.name}</h1>
      <dl className="grid grid-cols-2 gap-2 text-sm mb-6">
        <dt className="text-gray-400">Trigger</dt><dd>{schedule.cronExpression ?? (schedule.runAt ? `at ${new Date(schedule.runAt).toLocaleString()}` : '—')}</dd>
        <dt className="text-gray-400">Enabled</dt><dd>{schedule.enabled ? 'Yes' : 'No'}</dd>
        <dt className="text-gray-400">Last run</dt><dd>{schedule.lastRunAt ? new Date(schedule.lastRunAt).toLocaleString() : 'Never'}</dd>
        <dt className="text-gray-400">Prompt</dt><dd className="whitespace-pre-wrap">{schedule.prompt}</dd>
      </dl>
      <h2 className="text-xl mb-2">Recent runs</h2>
      {!runs?.length ? <EmptyState message="No runs yet." /> : <DataTable rows={runs} columns={cols} />}
    </div>
  )
}
```

- [ ] **Step 3: Wire routes in `App.tsx`**

Replace both ajastin placeholders:

```tsx
<Route path="ajastin" element={<SchedulesPage />} />
<Route path="ajastin/:id" element={<ScheduleDetailPage />} />
```

Add imports.

- [ ] **Step 4: Build**

Run: `cd src/toimi.web/ClientApp && npm run build`

- [ ] **Step 5: Commit**

```bash
git add src/toimi.web/ClientApp/src/admin/SchedulesPage.tsx \
        src/toimi.web/ClientApp/src/admin/ScheduleDetailPage.tsx \
        src/toimi.web/ClientApp/src/App.tsx
git commit -m "feat(web): schedules admin pages (list, detail with runs)"
```

---

## Task 15: Skills page

**Files:**
- Create: `src/toimi.web/ClientApp/src/admin/SkillsPage.tsx`
- Modify: `src/toimi.web/ClientApp/src/App.tsx`

- [ ] **Step 1: Create `SkillsPage.tsx`**

```tsx
import { useMemo, useState, useEffect } from 'react'
import { useAdminList, adminDelete, adminPut } from './useAdmin'
import { DataTable, type Column } from './DataTable'
import { ConfirmDelete } from './ConfirmDelete'
import { EmptyState } from './EmptyState'

interface SkillItem {
  id: string; name: string; description: string; instructions: string;
  tags: string[]; createdAt: string; updatedAt: string;
}
interface PagedResult<T> { items: T[]; page: number; size: number; total: number }

export function SkillsPage() {
  const [page, setPage] = useState(1)
  const [q, setQ] = useState('')
  const { data, loading, reload } = useAdminList<PagedResult<SkillItem>>(
    'taidot', `items?page=${page}&size=20&q=${encodeURIComponent(q)}`, [page, q]
  )
  const [pendingDelete, setPendingDelete] = useState<SkillItem | null>(null)
  const [editing, setEditing] = useState<SkillItem | null>(null)

  const columns: Column<SkillItem>[] = useMemo(() => [
    { key: 'name', header: 'Name', render: r => r.name },
    { key: 'description', header: 'Description', render: r => r.description },
    { key: 'tags', header: 'Tags', render: r => r.tags.join(', ') },
    { key: 'updated', header: 'Updated', render: r => new Date(r.updatedAt).toLocaleString() },
    { key: 'actions', header: '', render: r => (
      <div className="flex gap-2">
        <button className="text-blue-400" onClick={e => { e.stopPropagation(); setEditing(r) }}>Edit</button>
        <button className="text-red-400" onClick={e => { e.stopPropagation(); setPendingDelete(r) }}>Delete</button>
      </div>
    ) },
  ], [])

  return (
    <div>
      <h1 className="text-2xl mb-4">Skills</h1>
      <input
        className="w-full bg-gray-800 border border-gray-700 rounded p-2 mb-4"
        placeholder="Search name or description…"
        value={q} onChange={e => { setPage(1); setQ(e.target.value) }}
      />
      {loading && <div className="text-gray-500 text-sm">Loading…</div>}
      {data && data.items.length === 0 && <EmptyState message="No skills." />}
      {data && data.items.length > 0 && (
        <>
          <DataTable rows={data.items} columns={columns} />
          <div className="mt-3 text-sm text-gray-400 flex gap-3 items-center">
            <button disabled={page === 1} onClick={() => setPage(p => p - 1)} className="disabled:opacity-30">← Prev</button>
            <span>Page {data.page} ({data.total} total)</span>
            <button disabled={data.page * data.size >= data.total} onClick={() => setPage(p => p + 1)} className="disabled:opacity-30">Next →</button>
          </div>
        </>
      )}
      <ConfirmDelete
        open={!!pendingDelete}
        label="skill"
        onCancel={() => setPendingDelete(null)}
        onConfirm={async () => {
          if (!pendingDelete) return
          await adminDelete('taidot', `items/${pendingDelete.id}`)
          setPendingDelete(null)
          await reload()
        }}
      />
      <EditSkillDialog
        item={editing}
        onClose={() => setEditing(null)}
        onSaved={async () => { setEditing(null); await reload() }}
      />
    </div>
  )
}

function EditSkillDialog({ item, onClose, onSaved }: {
  item: SkillItem | null; onClose: () => void; onSaved: () => void;
}) {
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [instructions, setInstructions] = useState('')
  const [tags, setTags] = useState('')
  useEffect(() => {
    if (item) { setName(item.name); setDescription(item.description); setInstructions(item.instructions); setTags(item.tags.join(', ')) }
  }, [item])
  if (!item) return null
  return (
    <div className="fixed inset-0 bg-black/60 flex items-center justify-center">
      <div className="bg-gray-900 border border-gray-700 rounded p-6 w-[40rem] max-h-[80vh] overflow-y-auto">
        <h3 className="text-lg mb-3">Edit skill</h3>
        <label className="block text-sm text-gray-400 mb-1">Name</label>
        <input className="w-full bg-gray-800 border border-gray-700 rounded p-2 mb-3" value={name} onChange={e => setName(e.target.value)} />
        <label className="block text-sm text-gray-400 mb-1">Description</label>
        <input className="w-full bg-gray-800 border border-gray-700 rounded p-2 mb-3" value={description} onChange={e => setDescription(e.target.value)} />
        <label className="block text-sm text-gray-400 mb-1">Instructions</label>
        <textarea className="w-full bg-gray-800 border border-gray-700 rounded p-2 h-40 mb-3" value={instructions} onChange={e => setInstructions(e.target.value)} />
        <label className="block text-sm text-gray-400 mb-1">Tags (comma-separated)</label>
        <input className="w-full bg-gray-800 border border-gray-700 rounded p-2 mb-3" value={tags} onChange={e => setTags(e.target.value)} />
        <div className="flex justify-end gap-2 mt-3">
          <button className="px-3 py-1 rounded bg-gray-700" onClick={onClose}>Cancel</button>
          <button
            className="px-3 py-1 rounded bg-blue-700"
            onClick={async () => {
              const result = await adminPut<{ name: string; description: string; instructions: string; tags: string[] }, SkillItem>(
                'taidot', `items/${item.id}`,
                { name, description, instructions, tags: tags.split(',').map(t => t.trim()).filter(Boolean) },
                item.updatedAt
              )
              if (result.ok) onSaved()
              else alert(`Update failed (HTTP ${result.status})`)
            }}
          >Save</button>
        </div>
      </div>
    </div>
  )
}
```

- [ ] **Step 2: Wire route in `App.tsx`**

Replace `<Route path="taidot" element={<div>Skills placeholder</div>} />` with `<Route path="taidot" element={<SkillsPage />} />`. Add import.

- [ ] **Step 3: Build**

Run: `cd src/toimi.web/ClientApp && npm run build`
Expected: success.

- [ ] **Step 4: Final manual smoke test**

Run: `dotnet build toimi.sln -nologo -v q && dotnet test toimi.sln --nologo -v q`
Expected: all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/toimi.web/ClientApp/src/admin/SkillsPage.tsx \
        src/toimi.web/ClientApp/src/App.tsx
git commit -m "feat(web): skills admin page (list/edit/delete)"
```

---

## Coverage map (spec → tasks)

| Spec section | Tasks |
|---|---|
| §3 Shared contract in `toimi.core` | Task 1 |
| §4 Per-tool admin endpoints — uniform path scheme | Tasks 2–5 |
| §4 Per-tool `TItem` shapes | Tasks 2–5 |
| §4 Concurrency control (`If-Unmodified-Since` / 409 / 428) | Tasks 2–5 (PUT handlers) |
| §4 Endpoint registration via `MapAdmin()` | Tasks 1 (helper), 2–5 (calls) |
| §5 `Toimi:Admin:Tools` config | Task 6 |
| §5 `/api/admin/summary` aggregator with partial-failure | Task 7 |
| §5 `/api/admin/{tool}/{**path}` generic forwarder | Task 8 |
| §6 `react-router-dom` + admin layout | Task 9 |
| §6 Shared primitives | Task 10 |
| §6 Dashboard | Task 11 |
| §6 Per-store pages | Tasks 12–15 |
| §7 Auth | (no-op; assumption documented in spec) |
| §8 Testing — per-tool admin endpoint tests | Tasks 2–5 |
| §8 Testing — aggregator unit test | Task 7 |
| §8 Testing — forwarder unit test | Task 8 |
| §8 Testing — React | (deferred; see follow-up below) |

## Follow-ups intentionally NOT in this plan

- Vitest setup + React tests for the dashboard partial-failure flow and one CRUD page. The spec lists this; the plan defers it because Vitest is a fresh dev-dependency stack that adds non-trivial setup. Add as a Task 16 if the project wants it before merging.
- Conversation history management, bulk operations, app-level auth (already listed as follow-ups in the spec).
- Migrating the existing MCP CRUD tools (`SaveMemoryTool`, etc.) to also set `UpdatedAt` consistently — they already do via the entity setter, but verify no path bypasses it.
