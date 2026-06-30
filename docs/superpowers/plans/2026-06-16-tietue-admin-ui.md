# Tietue Admin UI — Data / Types / Triggers Views Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use `- [ ]`.

**Goal:** Restore (and improve on) the admin panel the cutover hollowed out. After Phase 6, `toimi.web`'s admin has only the read-only Dashboard. Add three tietue admin backend endpoints and three frontend pages so the panel can browse/manage everything tietue holds: a **Data** page (paged, type-filtered entity list → detail with full `Data` + its **Triggers** + recent **Events**, with delete) and a **Types** page (the type catalog: schema + behaviors + default triggers).

**Architecture:** The web `AdminForwarder` already proxies `/api/admin/{tool}/{**path}` → `{tool}`'s `/admin/{path}` for any tool in `Toimi:Admin:Tools` (`["tietue"]`). So we ONLY add GET endpoints to tietue's `Admin/AdminEndpoints.cs` (reachable at `/api/admin/tietue/...`) and build React pages using the existing `useAdminList`/`adminDelete` hooks + shared components (`DataTable`, `ConfirmDelete`, `EmptyState`, `ErrorBanner`, `useDebounced`). Backend endpoints are TDD'd via the existing `WebApplicationFactory` + in-memory pattern (`AdminEndpointsTests`); the React pages are verified by the Vite/TS build (`dotnet build src/toimi.web` compiles the client — a bad import/type fails it), consistent with the repo's no-React-unit-tests convention.

**Tech Stack:** .NET 10 minimal APIs + EF, React 19 + Vite + react-router-dom + Tailwind. Docker SDK image for dotnet (not on PATH). Repo enforces IDE0005/IDE0022/IDE0046/whitespace as errors on C# — `dotnet format` apply + `--verify-no-changes` exit 0 before committing tietue changes; `git add -A src/toimi.tools.tietue src/toimi.tools.tietue.Tests` (and `src/toimi.web` for frontend tasks).

**Scope:** IN — three tietue admin GET endpoints (`/admin/types`, `/admin/items/{id}/triggers`, `/admin/items/{id}/events`); a `DataPage`, `EntityDetailPage`, `TypesPage`; nav links + routes. OUT — editing types/triggers from the UI (read + delete-entity only for now), auth (the admin panel's existing posture is unchanged).

---

## Task 1: tietue admin endpoint — `GET /admin/types`

**Files:** modify `src/toimi.tools.tietue/Admin/AdminEndpoints.cs`; test `src/toimi.tools.tietue.Tests/AdminEndpointsTests.cs`.

- [ ] **Step 1: failing test.** Add to `AdminEndpointsTests` (it already has `TietueTestFactory` + the seed pattern). The endpoint lists type definitions. Add:
```csharp
  [Fact]
  public async Task Types_lists_type_definitions()
  {
    using (var scope = _factory.Services.CreateScope())
    {
      var repo = scope.ServiceProvider.GetRequiredService<toimi.tools.tietue.Types.TypeRepository>();
      await repo.DefineAsync("note", """{"type":"object","properties":{"title":{"type":"string"}}}""",
        """[{"behavior":"SemanticIndex","config":{"fields":["title"]}}]""");
    }

    var client = _factory.CreateClient();
    using var doc = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/admin/types"));
    var first = doc.RootElement.EnumerateArray().First(t => t.GetProperty("name").GetString() == "note");
    Assert.Contains("title", first.GetProperty("jsonSchema").GetString());
    Assert.Contains("SemanticIndex", first.GetProperty("behaviors").GetString());
  }
```
> `TypeRepository` must be resolvable from the test host's DI (it is — registered scoped in Program). If it isn't, construct it directly: `new toimi.tools.tietue.Types.TypeRepository(db)` using the scope's `TietueDbContext`.

- [ ] **Step 2: run, confirm FAIL** (404).
`docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~AdminEndpointsTests"`

- [ ] **Step 3: implement.** In `src/toimi.tools.tietue/Admin/AdminEndpoints.cs`, add a `TypeItem` record next to `EntityItem`:
```csharp
  public record TypeItem(
      string Name, string JsonSchema, string? Behaviors, string? DefaultTriggers,
      DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
```
and inside `MapAdminEndpoints` (after the existing routes) add:
```csharp
    admin.MapGet("/types", async (TietueDbContext db) =>
    {
      // Materialize first: JsonSchema is a JsonDocument (value-converted), not SQL-projectable.
      var defs = await db.TypeDefinitions.OrderBy(t => t.Name).ToListAsync();
      var rows = defs
        .Select(t => new TypeItem(t.Name, t.JsonSchema.RootElement.GetRawText(), t.Behaviors, t.DefaultTriggers, t.CreatedAt, t.UpdatedAt))
        .ToList();
      return Results.Ok(rows);
    });
```

- [ ] **Step 4: run, confirm PASS. LINT (verify exit 0) + commit:**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj; dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes; echo MAIN=$?; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes; echo TESTS=$?'
git add -A src/toimi.tools.tietue src/toimi.tools.tietue.Tests
git commit -m "feat(tietue): admin endpoint to list type definitions"
```

---

## Task 2: tietue admin endpoints — entity triggers + events

**Files:** modify `src/toimi.tools.tietue/Admin/AdminEndpoints.cs`; test `AdminEndpointsTests.cs`.

- [ ] **Step 1: failing tests.** Add:
```csharp
  [Fact]
  public async Task Item_triggers_and_events_are_listed()
  {
    var id = Guid.NewGuid();
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<TietueDbContext>();
      db.Entities.Add(new Entity { Id = id, Type = "reminder", Data = System.Text.Json.JsonDocument.Parse("""{"title":"x"}"""), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
      db.Triggers.Add(new Trigger { Id = Guid.NewGuid(), EntityId = id, Schedule = """{"at":"2026-07-01T09:00:00Z"}""", HandlerKind = "notify", Enabled = true, NextFireAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
      db.EntityEvents.Add(new EntityEvent { Id = Guid.NewGuid(), EntityId = id, OccurrenceUtc = DateTimeOffset.UtcNow, Kind = "notify", Status = "sent", CreatedAt = DateTimeOffset.UtcNow });
      await db.SaveChangesAsync();
    }

    var client = _factory.CreateClient();
    using var triggers = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync($"/admin/items/{id}/triggers"));
    Assert.Equal("notify", triggers.RootElement[0].GetProperty("handlerKind").GetString());
    using var events = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync($"/admin/items/{id}/events"));
    Assert.Equal("sent", events.RootElement[0].GetProperty("status").GetString());
  }
```

- [ ] **Step 2: run, confirm FAIL.**

- [ ] **Step 3: implement.** Add records + routes to `AdminEndpoints.cs`:
```csharp
  public record TriggerItem(
      Guid Id, string Schedule, string HandlerKind, string? HandlerConfig,
      bool Enabled, DateTimeOffset? NextFireAt, DateTimeOffset? LastFiredAt);

  public record EventItem(
      Guid Id, DateTimeOffset OccurrenceUtc, string Kind, string Status, string? Result, DateTimeOffset CreatedAt);
```
```csharp
    admin.MapGet("/items/{id:guid}/triggers", async (TietueDbContext db, Guid id) =>
    {
      var rows = await db.Triggers.Where(t => t.EntityId == id).OrderBy(t => t.CreatedAt)
        .Select(t => new TriggerItem(t.Id, t.Schedule, t.HandlerKind, t.HandlerConfig, t.Enabled, t.NextFireAt, t.LastFiredAt))
        .ToListAsync();
      return Results.Ok(rows);
    });

    admin.MapGet("/items/{id:guid}/events", async (TietueDbContext db, Guid id, int limit = 0) =>
    {
      limit = limit <= 0 ? 50 : Math.Clamp(limit, 1, 200);
      var rows = await db.EntityEvents.Where(e => e.EntityId == id).OrderByDescending(e => e.CreatedAt).Take(limit)
        .Select(e => new EventItem(e.Id, e.OccurrenceUtc, e.Kind, e.Status, e.Result, e.CreatedAt))
        .ToListAsync();
      return Results.Ok(rows);
    });
```
(`Schedule`/`HandlerConfig`/`Result` are `string?` jsonb columns — SQL-projectable, no materialize needed.)

- [ ] **Step 4: run, confirm PASS + full suite green. LINT verify exit 0 + commit:**
```bash
git commit -m "feat(tietue): admin endpoints for an entity's triggers and events"
```

---

## Task 3: Frontend — `DataPage` (entities list) + nav + route

**Files:** create `src/toimi.web/ClientApp/src/admin/DataPage.tsx`; modify `AdminLayout.tsx`, `App.tsx`.

> Read `DashboardPage.tsx`, `DataTable.tsx`, `ConfirmDelete.tsx`, `EmptyState.tsx`, `ErrorBanner.tsx`/`FetchErrorBanner.tsx`, `useDebounced.ts`, and `useAdmin.ts` first to match the existing component APIs + Tailwind styling. Use `useAdminList<T>('tietue', path)`, `adminDelete('tietue', path)`.

- [ ] **Step 1: create `DataPage.tsx`.** A paged, type-filtered table of entities. Backend: `GET /api/admin/tietue/items?q=<type>&page=<n>&size=<n>` → `{ items: {id,type,data,tags,createdAt,updatedAt}[], page, size, total }`. The page:
  - a debounced type-filter text input (`q`),
  - a `DataTable` (or equivalent) with columns: Type, Data preview (first ~80 chars of the `data` string), Tags (joined), Updated (locale time), and a Delete action (`ConfirmDelete` → `adminDelete('tietue', 'items/'+id)` → reload),
  - each row's Type/preview links to `/admin/data/${id}` (react-router `Link`),
  - prev/next paging (disable at bounds using `total`),
  - loading + error (`FetchErrorBanner`) + empty (`EmptyState`) states.
  Match the structure the deleted per-tool pages used (see `git show 7fb7eec^:src/toimi.web/ClientApp/src/admin/MemoriesPage.tsx` for the exact prior pattern of filter+table+delete — adapt it to the generic entity shape).

- [ ] **Step 2: add nav + route.** In `AdminLayout.tsx` add to `links`: `{ to: '/admin/data', label: 'Data' }`. In `App.tsx`, under the `/admin` route, add `<Route path="data" element={<DataPage />} />` (import it).

- [ ] **Step 3: build the client (compiles the React/TS).**
`docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet build src/toimi.web/toimi.web.csproj`
Expected: `Build succeeded.` (a bad import/type/JSX error fails the Vite build). Fix until green.

- [ ] **Step 4: commit.**
```bash
git add -A src/toimi.web
git commit -m "feat(web): tietue Data admin page (entity list, filter, delete)"
```

---

## Task 4: Frontend — `EntityDetailPage` (data + triggers + events)

**Files:** create `src/toimi.web/ClientApp/src/admin/EntityDetailPage.tsx`; modify `App.tsx`.

- [ ] **Step 1: create `EntityDetailPage.tsx`** at route `/admin/data/:id`. Use `useParams()` for `id` and three `useAdminList` calls:
  - `useAdminList<EntityItem>('tietue', 'items/'+id)` → header (type, tags, timestamps) + pretty-printed `Data` (`JSON.stringify(JSON.parse(item.data), null, 2)` in a `<pre>`),
  - `useAdminList<TriggerItem[]>('tietue', 'items/'+id+'/triggers')` → a small table: Handler kind, Schedule (raw), Enabled, Next fire,
  - `useAdminList<EventItem[]>('tietue', 'items/'+id+'/events')` → a small table: Occurrence, Kind, Status, Result (truncated), Created.
  Include a Delete button (`ConfirmDelete` → `adminDelete('tietue','items/'+id)` → navigate back to `/admin/data`), a back link, and loading/error/empty states. Define the TS interfaces (`EntityItem`, `TriggerItem`, `EventItem`) to match the backend records.

- [ ] **Step 2: route.** In `App.tsx`, add `<Route path="data/:id" element={<EntityDetailPage />} />` under `/admin`.

- [ ] **Step 3: build the client; confirm green.** Commit:
```bash
git add -A src/toimi.web
git commit -m "feat(web): entity detail admin page (data, triggers, events)"
```

---

## Task 5: Frontend — `TypesPage` (the type catalog)

**Files:** create `src/toimi.web/ClientApp/src/admin/TypesPage.tsx`; modify `AdminLayout.tsx`, `App.tsx`.

- [ ] **Step 1: create `TypesPage.tsx`** at `/admin/types`. `useAdminList<TypeItem[]>('tietue', 'types')`. Render a list/table of types: Name, a behaviors badge (parse `behaviors` JSON for behavior names, e.g. "SemanticIndex"), and a "default triggers" indicator. Each row is expandable (local `useState` for the expanded name) to show three pretty-printed JSON blocks in `<pre>`: Schema (`jsonSchema`), Behaviors (`behaviors` or "none"), Default triggers (`defaultTriggers` or "none"). `TypeItem` TS interface matches the backend record. Loading/error/empty states.

- [ ] **Step 2: nav + route.** In `AdminLayout.tsx` `links` add `{ to: '/admin/types', label: 'Types' }`. In `App.tsx` add `<Route path="types" element={<TypesPage />} />`.

- [ ] **Step 3: build the client; confirm green. Commit:**
```bash
git add -A src/toimi.web
git commit -m "feat(web): types admin page (schema, behaviors, default triggers)"
```

---

## Task 6: Full verification

- [ ] **Step 1: tietue tests + lint.**
```
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj 2>&1 | grep -E "Passed!|Failed!"
  dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes; echo MAIN=$?
  dotnet build src/toimi.web/toimi.web.csproj 2>&1 | grep -E "Build succeeded|error"
'
```
Expected: tietue tests pass (108 + 2 new admin tests), MAIN=0, web build succeeds.

- [ ] **Step 2: nav sanity.** Confirm `AdminLayout` `links` = Dashboard, Data, Types and `App.tsx` has routes `index`, `data`, `data/:id`, `types` under `/admin`.

- [ ] **Step 3: (optional) manual smoke** — run web + tietue against a real DB, open `/admin`, confirm Data lists/filters/views/deletes entities, the detail shows triggers/events, and Types shows the four seeded types' schemas.

- [ ] **Step 4: final commit if anything changed.** `git commit --allow-empty -m "chore(web): tietue admin UI complete"`

---

## Done

The admin panel regains hands-on management centered on tietue: **Dashboard** (aggregate summary), **Data** (browse/filter/view/delete entities → detail with triggers + events), and **Types** (the runtime type catalog). All via the generic admin proxy + tietue endpoints — no per-server admin code, matching the consolidated architecture.
