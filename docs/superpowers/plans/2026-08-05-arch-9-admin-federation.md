# Arch 9: Admin Federation — Real Tests + Shared Path Contract Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The federated-admin seam in toimi.web still tests against servers deleted in the consolidation (`muistio`, `ajastin`, `muistutin`) and pins its upstream path contract as unshared string literals on both sides. Retarget the tests to the real surviving adapter (`tietue`, with clearly-labeled hypothetical names for fan-out coverage), give the upstream path contract one owner (`Toimi.Core.Admin.AdminRoutes`, consumed by web's forwarder/aggregator AND tietue's route mapping), and add one cheap tietue-side test that the real admin surface serves the exact URL shape the aggregator composes. Behavior identical; every wire URL byte-identical. The federation fan-out itself stays — a second admin-bearing pod remains plausible.

**Architecture:** One new 3-constant static class in `toimi.core` (`Admin/AdminRoutes.cs`: `Base = "/admin"`, `Summary = "/summary"`, `SummaryPath = Base + Summary`). Both existing consumers of the contract switch to it: `toimi.web`'s `AdminForwarder`/`AdminAggregator` compose upstream URLs from it, and tietue's `Admin/AdminEndpoints.cs` maps `MapGroup(AdminRoutes.Base)` / `MapGet(AdminRoutes.Summary)`. The React client cannot consume a C# constant, so it gets the C6 paired-comment discipline instead (comment in `useAdmin.ts` naming `AdminRoutes.cs`, and vice versa). Tests deliberately keep/pin the *literal* paths so an accidental `AdminRoutes` edit that would move the wire URLs fails a test instead of shipping. No new pods, no new packages, no config changes.

**Tech Stack:** .NET 10 minimal APIs, xUnit v2, EF Core 10 (InMemory via `TietueTestFactory` + `WebApplicationFactory<Program>`), React/TypeScript (comment only).

## Global Constraints

- Branch: `arch-9-admin-federation` (already checked out).
- dotnet is NOT on PATH: every dotnet command is preceded by `export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"`.
- Per-project test commands from `/Users/jari/private/toimi`: `dotnet test src/<project>.Tests/<project>.Tests.csproj --nologo -v q`. tietue's suite needs Docker (Testcontainers) — Docker is available; those tests RUN, not skip.
- Suite floors — no drops, no assertion weakening. Expected end state: web **38** (AggregatorTests/ForwarderTests change only by renames + strengthened assertions), tietue **399** (398 + 1 new summary-shape test), all other suites untouched (do not modify them).
- URLs byte-identical: the constants must compose to exactly today's paths — upstream forwarder `/admin/{path}{query}`, aggregator `/admin/summary?q=&limit=N`, tietue routes `/admin/summary`, `/admin/items`, … The web-facing `/api/admin/...` routes and the React client are untouched except one comment in `useAdmin.ts`.
- Before each commit: `dotnet format src/<proj>/<proj>.csproj` for every touched project, then `--verify-no-changes` exits 0. Enforced as errors: IDE0005, IDE0022, IDE0046, whitespace. 2-space indent, file-scoped namespaces.
- Commit style: `<type>(<scope>): <subject>`.
- Do not merge at the end — hand back for review (Jari squash-merges finished branches into local `wip`).

## Design Decisions

1. **Test retarget: `tietue` + labeled hypotheticals.** Verified: `src/toimi.web.Tests/AggregatorTests.cs` uses `muistio`/`ajastin`/`muistutin` in all 3 `AggregatorTests` tests and all 8 `ForwarderTests` tests (lines 32–98, 125–279); production config is `Toimi:Admin:Tools = ["tietue"]` (`src/toimi.web/appsettings.json:7`). The aggregator tests need ≥2 tools to prove merge/fan-out, so they use `tietue` plus hypothetical `posti`/`kalenteri`, with a comment stating explicitly that production is `["tietue"]` and the extra names exist only to cover the fan-out that a future second admin-bearing pod would use. The forwarder tests exercise one tool — all become `tietue`. Pure rename/retarget: no assertion weakened, one assertion strengthened (see decision 4).
2. **`AdminRoutes` owns the upstream contract only.** The seam this plan fixes is web-proxy ↔ tool-server: `AdminForwarder`'s `$"/admin/{path}…"` (`src/toimi.web/Admin/AdminEndpoints.cs:75`), `AdminAggregator`'s `"/admin/summary?q=…&limit=…"` (`:133-134`), and tietue's `MapGroup("/admin")` + `MapGet("/summary")` (`src/toimi.tools.tietue/Admin/AdminEndpoints.cs:29,31`). Those four sites consume `AdminRoutes`. Deliberately NOT converted: (a) the web-facing `/api/admin/...` routes in web's `AdminEndpoints.cs:12,21,30` — that surface is web's own contract with the React client, owned by web + the paired comment, and must not be coupled to the upstream base; (b) `AdminPathGuard.cs` — its `"/admin"`/`"/api/admin"` literals mirror the Traefik basicAuth router's RAW-path match rule (a deployment contract, per its own doc comment), not the upstream proxy contract; (c) tietue's sub-routes (`/items`, `/types`, …) — only web's forwarder touches them, and it does so generically via `{**path}`, so there is no second literal to share. Only `/summary` is composed on both sides, hence only it gets a constant beyond `Base`.
3. **Item 3 (tietue serves what the aggregator parses): INCLUDE — it is cheap and mostly exists.** Verified: `src/toimi.tools.tietue.Tests/AdminEndpointsTests.cs:36` already boots the real `Program` via `TietueTestFactory` and deserializes `GET /admin/summary` into the shared `Toimi.Core.Admin.AdminSummaryDto[]` — the DTO-shape half of the gap is already closed by compile-time type sharing plus that test. The remaining gap is the URL shape: nothing tietue-side proves the endpoint honors the exact `?q=&limit=` query the aggregator composes. One new test requests the byte-identical aggregator URL (`/admin/summary?q=note&limit=1`) and asserts filtering + limit + ordering. No new infrastructure needed. tietue 398 → 399.
4. **Production code uses the constants; tests pin the literals.** Since both production halves consume `AdminRoutes`, they can no longer drift from *each other* — but an edit to `AdminRoutes` would silently move the wire URL on both. So the tests assert literal strings: web-side, the null-query aggregator test is strengthened from `Contains("q=&", …)` to `Equal("/admin/summary?q=&limit=50", …)` (the existing forwarder test at `AggregatorTests.cs:151` already pins `"/admin/items?q=foo&page=2"` — kept as-is); tietue-side, the existing and new tests request literal `/admin/summary…`. Either side drifting now fails a test.
5. **React side: one comment, in `useAdmin.ts`.** `ClientApp/src/admin/useAdmin.ts:12,25,35,40` hard-codes `/api/admin/${tool}/${path}`; `useAdminSummary.ts:24` and `UsagePage.tsx:18-19` hard-code `/api/admin/summary` / `/api/admin/usage` / `/api/admin/tietue/usage`. TS cannot consume a C# const, so per the C6 "counterpart" discipline the pairing is documentary: `AdminRoutes.cs`'s doc comment names the React files, and `useAdmin.ts` carries one comment naming `AdminRoutes.cs` and its sibling React files. No fetch path changes.

---

## Task 1: Retarget the web admin tests to real (and labeled-hypothetical) tool names

**Files:**
- Modify: `src/toimi.web.Tests/AggregatorTests.cs` (both classes: `AggregatorTests`, `ForwarderTests`)

**Interfaces:**
- Consumes: `AdminAggregator.AggregateAsync(string[] tools, IHttpClientFactory http, string? q, int limit)`, `AdminForwarder.ForwardAsync(string tool, string? path, HttpContext ctx, AdminToolsOptions opts, IHttpClientFactory http)` — both unchanged.
- Produces: no `muistio`/`ajastin`/`muistutin` literal remains anywhere in `src/`.

**Steps:**

- [ ] **Step 1: Commit this plan file** (if not already committed):

```bash
cd /Users/jari/private/toimi
git add docs/superpowers/plans/2026-08-05-arch-9-admin-federation.md
git commit -m "docs: arch-9 admin-federation implementation plan"
```

- [ ] **Step 2: Rewrite the three `AggregatorTests` tests**

In `src/toimi.web.Tests/AggregatorTests.cs`, replace the body of `public class AggregatorTests` — the three `[Fact]` methods only; `StubHandler`/`StubFactory` stay — with:

```csharp
  // Production Toimi:Admin:Tools is ["tietue"] (src/toimi.web/appsettings.json)
  // — tietue is the only admin-bearing pod since the server consolidation.
  // "posti" and "kalenteri" below are HYPOTHETICAL future admin-bearing pods:
  // they exist only so these tests keep proving the multi-tool fan-out a
  // second real pod would rely on.
  [Fact]
  public async Task Merges_items_by_UpdatedAt_desc_and_collects_errors()
  {
    var now = DateTimeOffset.UtcNow;
    var tietueItem = new AdminSummaryDto("a", "memory", "older", null, now.AddHours(-2), now.AddHours(-2));
    var postiItem = new AdminSummaryDto("b", "schedule", "newer", null, now.AddHours(-1), now.AddHours(-1));

    var handlers = new Dictionary<string, HttpMessageHandler>
    {
      ["admin-tietue"] = new StubHandler(_ =>
      {
        var msg = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(new[] { tietueItem }) };
        return msg;
      }),
      ["admin-posti"] = new StubHandler(_ =>
      {
        var msg = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(new[] { postiItem }) };
        return msg;
      }),
      ["admin-kalenteri"] = new StubHandler(_ => throw new HttpRequestException("boom")),
    };
    var factory = new StubFactory(handlers);

    var result = await AdminAggregator.AggregateAsync(
        ["tietue", "posti", "kalenteri"], factory, q: null, limit: 50);

    Assert.Equal(2, result.Items.Count);
    Assert.Equal("b", result.Items[0].Id); // newer first
    Assert.Equal("a", result.Items[1].Id);
    var err = Assert.Single(result.Errors);
    Assert.Equal("kalenteri", err.Tool);
  }

  [Fact]
  public async Task Summary_url_is_the_contract_path_with_an_empty_q_for_null_query()
  {
    Uri? captured = null;
    var handlers = new Dictionary<string, HttpMessageHandler>
    {
      ["admin-tietue"] = new StubHandler(req =>
      {
        captured = req.RequestUri;
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(Array.Empty<AdminSummaryDto>()) };
      }),
    };
    var factory = new StubFactory(handlers);

    await AdminAggregator.AggregateAsync(["tietue"], factory, q: null, limit: 50);

    Assert.NotNull(captured);
    // Literal on purpose: pins the upstream wire URL byte-for-byte, so an
    // AdminRoutes edit that would move it fails here instead of shipping.
    // The serving half of this contract is pinned in tietue's
    // AdminEndpointsTests (Summary_serves_the_aggregator_url_shape…).
    Assert.Equal("/admin/summary?q=&limit=50", captured!.PathAndQuery);
  }

  [Fact]
  public async Task All_tools_failing_yields_empty_items_and_all_errors()
  {
    var handlers = new Dictionary<string, HttpMessageHandler>
    {
      ["admin-tietue"] = new StubHandler(_ => throw new HttpRequestException("boom1")),
      ["admin-posti"] = new StubHandler(_ => throw new HttpRequestException("boom2")),
    };
    var factory = new StubFactory(handlers);

    var result = await AdminAggregator.AggregateAsync(["tietue", "posti"], factory, q: null, limit: 50);

    Assert.Empty(result.Items);
    Assert.Equal(2, result.Errors.Count);
    Assert.Equal(["tietue", "posti"], result.Errors.Select(e => e.Tool));
  }
```

(Rename note: `Null_query_produces_an_empty_q_parameter` → `Summary_url_is_the_contract_path_with_an_empty_q_for_null_query`; the assertion is strictly stronger — `Equal` on the whole `PathAndQuery` instead of `Contains("q=&", …)`. The other two keep their names. Count in this class: 3 → 3.)

- [ ] **Step 3: Retarget `ForwarderTests` mechanically**

After Step 2 the only remaining `"muistio"` literals in the file are inside `public class ForwarderTests`: ten `Tools = ["muistio"]` option constructions and eight `ForwardAsync("muistio", …)` calls. Replace every remaining occurrence of the string `muistio` in this file with `tietue`:

```bash
cd /Users/jari/private/toimi
sed -i 's/muistio/tietue/g' src/toimi.web.Tests/AggregatorTests.cs
grep -c "tietue" src/toimi.web.Tests/AggregatorTests.cs   # sanity: > 0
grep -n "muistio\|ajastin\|muistutin" src/toimi.web.Tests/AggregatorTests.cs || echo CLEAN
```

Expected: final grep prints `CLEAN`. No assertion, method name, path (`"items"`), or header value changes in `ForwarderTests` — tool-name strings only (e.g. `AggregatorTests.cs:151`'s `"/admin/items?q=foo&page=2"` assertion is untouched).

- [ ] **Step 4: Run the web suite**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj --nologo -v q
```

Expected: **38** passed (same count — renames/retargets only).

- [ ] **Step 5: Format + verify + commit**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet format src/toimi.web.Tests/toimi.web.Tests.csproj && dotnet format src/toimi.web.Tests/toimi.web.Tests.csproj --verify-no-changes
git add src/toimi.web.Tests
git commit -m "test(web): retarget admin federation tests from deleted servers to tietue"
```

---

## Task 2: `AdminRoutes` — one owner for the upstream path contract

**Files:**
- Create: `src/toimi.core/Admin/AdminRoutes.cs`
- Modify: `src/toimi.web/Admin/AdminEndpoints.cs` (lines 75 and 133-134)
- Modify: `src/toimi.tools.tietue/Admin/AdminEndpoints.cs` (lines 29 and 31)
- Modify: `src/toimi.web/ClientApp/src/admin/useAdmin.ts` (comment only)

**Interfaces:**
- Produces: `Toimi.Core.Admin.AdminRoutes` — `const string Base = "/admin"`, `const string Summary = "/summary"`, `const string SummaryPath = Base + Summary` (compile-time `"/admin/summary"`).
- Consumes: nothing new — both `AdminEndpoints.cs` files already have `using Toimi.Core.Admin;` (web line 2, tietue line 2) and both projects already reference `toimi.core`.

**Steps:**

- [ ] **Step 1: Create the shared constant**

Create `src/toimi.core/Admin/AdminRoutes.cs`:

```csharp
namespace Toimi.Core.Admin;

/// <summary>
/// The federated-admin upstream path contract, shared by both halves of the
/// seam: admin-bearing tool servers map their endpoints under <see cref="Base"/>
/// (tietue Admin/AdminEndpoints.cs), and toimi.web's AdminForwarder /
/// AdminAggregator compose upstream URLs from the same constants — change a
/// value here and both halves move together (tests pin the literal wire paths,
/// so an accidental edit fails a test rather than shipping).
/// counterpart: the React client cannot consume C# constants — the web-facing
/// /api/admin/... prefix in front of these routes is hard-coded in
/// ClientApp/src/admin/useAdmin.ts (and useAdminSummary.ts / UsagePage.tsx).
/// </summary>
public static class AdminRoutes
{
  /// <summary>Path each admin-bearing server maps its admin route group at.</summary>
  public const string Base = "/admin";

  /// <summary>Cross-server summary route, relative to the admin group.</summary>
  public const string Summary = "/summary";

  /// <summary>Absolute upstream path of the summary endpoint the aggregator fans out to.</summary>
  public const string SummaryPath = Base + Summary;
}
```

- [ ] **Step 2: Consume it in web's forwarder and aggregator**

In `src/toimi.web/Admin/AdminEndpoints.cs`, replace line 75:

```csharp
    var upstreamPath = $"/admin/{path}{ctx.Request.QueryString}";
```

with:

```csharp
    var upstreamPath = $"{AdminRoutes.Base}/{path}{ctx.Request.QueryString}";
```

and replace lines 133-134:

```csharp
        var rows = await client.GetFromJsonAsync<AdminSummaryDto[]>(
            $"/admin/summary?q={Uri.EscapeDataString(q ?? string.Empty)}&limit={limit}");
```

with:

```csharp
        var rows = await client.GetFromJsonAsync<AdminSummaryDto[]>(
            $"{AdminRoutes.SummaryPath}?q={Uri.EscapeDataString(q ?? string.Empty)}&limit={limit}");
```

(`using Toimi.Core.Admin;` is already present at line 2. The web-facing `MapGet("/api/admin/summary"…)`, `MapGet("/api/admin/usage"…)`, and `Map("/api/admin/{tool}/{**path}"…)` literals are deliberately NOT converted — see Design Decision 2.)

- [ ] **Step 3: Consume it in tietue's route mapping**

In `src/toimi.tools.tietue/Admin/AdminEndpoints.cs`, replace line 29:

```csharp
    var admin = app.MapGroup("/admin");
```

with:

```csharp
    var admin = app.MapGroup(AdminRoutes.Base);
```

and replace line 31:

```csharp
    admin.MapGet("/summary", async (TietueDbContext db, string? q, int limit = 0) =>
```

with:

```csharp
    admin.MapGet(AdminRoutes.Summary, async (TietueDbContext db, string? q, int limit = 0) =>
```

(`using Toimi.Core.Admin;` is already present at line 2. The sub-routes `/items`, `/types`, `/usage`, `/outbox`, `/semantic/reconcile/{type}` stay literal — the forwarder reaches them generically via `{**path}`, so there is no second composing site to share; see Design Decision 2.)

- [ ] **Step 4: Paired comment on the React side**

In `src/toimi.web/ClientApp/src/admin/useAdmin.ts`, insert between line 1 (`import …`) and line 3 (`export interface AdminFetchError …`):

```typescript
// counterpart: src/toimi.core/Admin/AdminRoutes.cs — the C# owner of the
// upstream /admin path contract behind the /api/admin/... prefix used here.
// TS cannot consume a C# constant: if toimi.web's /api/admin routes move,
// update the fetch paths here and in useAdminSummary.ts / UsagePage.tsx.
```

No other React change — fetch paths, hooks, and behavior are untouched.

- [ ] **Step 5: Run both suites (URLs must still be byte-identical)**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj --nologo -v q
dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --nologo -v q
```

Expected: web **38** (including the new literal `Equal("/admin/summary?q=&limit=50", …)` pin and the forwarder's `"/admin/items?q=foo&page=2"` pin — both prove the constants composed to today's bytes), tietue **398** (Docker running; `AdminEndpointsTests` exercises the constant-mapped routes at their literal URLs).

- [ ] **Step 6: Format + verify + commit**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet format src/toimi.core/toimi.core.csproj && dotnet format src/toimi.core/toimi.core.csproj --verify-no-changes
dotnet format src/toimi.web/toimi.web.csproj && dotnet format src/toimi.web/toimi.web.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj && dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
git add src/toimi.core src/toimi.web src/toimi.tools.tietue
git commit -m "refactor(core): AdminRoutes owns the federated admin path contract"
```

---

## Task 3: tietue serves the aggregator's URL shape (item 3) + full gate

**Files:**
- Modify: `src/toimi.tools.tietue.Tests/AdminEndpointsTests.cs` (one new test)

**Interfaces:**
- Consumes: `TietueTestFactory` (already defined at the bottom of the same file, boots the real `Program`), `Toimi.Core.Admin.AdminSummaryDto` (already imported at line 7).

**Steps:**

- [ ] **Step 1: Add the aggregator-URL-shape test**

In `src/toimi.tools.tietue.Tests/AdminEndpointsTests.cs`, append inside `public class AdminEndpointsTests`, directly after the existing `Summary_returns_entity_summaries` test (after its closing brace at line 39):

```csharp
  [Fact]
  public async Task Summary_serves_the_aggregator_url_shape_with_q_and_limit()
  {
    var newer = Guid.NewGuid();
    using (var scope = _factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<TietueDbContext>();
      var now = DateTimeOffset.UtcNow;
      db.Entities.Add(new Entity { Id = Guid.NewGuid(), Type = "note", Data = JsonDocument.Parse("{}"), CreatedAt = now.AddHours(-2), UpdatedAt = now.AddHours(-2) });
      db.Entities.Add(new Entity { Id = newer, Type = "note", Data = JsonDocument.Parse("{}"), CreatedAt = now.AddHours(-1), UpdatedAt = now.AddHours(-1) });
      db.Entities.Add(new Entity { Id = Guid.NewGuid(), Type = "task", Data = JsonDocument.Parse("{}"), CreatedAt = now, UpdatedAt = now });
      await db.SaveChangesAsync();
    }

    var client = _factory.CreateClient();
    // Literal on purpose: byte-identical to the URL toimi.web's AdminAggregator
    // composes (AdminRoutes.SummaryPath + ?q=&limit=), and deserialized into the
    // same shared AdminSummaryDto the aggregator parses. The composing half of
    // this contract is pinned in toimi.web.Tests AggregatorTests
    // (Summary_url_is_the_contract_path_with_an_empty_q_for_null_query).
    var rows = await client.GetFromJsonAsync<AdminSummaryDto[]>("/admin/summary?q=note&limit=1");

    var item = Assert.Single(rows!);
    Assert.Equal(newer.ToString(), item.Id); // q filtered the task out; limit=1 kept the newest note
    Assert.Equal("note", item.Kind);
  }
```

(No new usings needed — `System.Net.Http.Json`, `System.Text.Json`, `Toimi.Core.Admin`, and `toimi.tools.tietue.Data` are already imported. No new infrastructure — `TietueTestFactory` already boots the real `Program` with an isolated InMemory DB per instance.)

- [ ] **Step 2: Run the tietue suite**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --nologo -v q
```

Expected: **399** passed (398 + the new test; Docker running, Testcontainers tests executed).

- [ ] **Step 3: Gate — no stale names, both suites, format verify across every touched project**

```bash
cd /Users/jari/private/toimi
grep -rn "muistio\|ajastin\|muistutin" --include="*.cs" --include="*.ts" --include="*.tsx" --include="*.json" \
  --exclude-dir=node_modules --exclude-dir=obj --exclude-dir=bin src/ || echo CLEAN

export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj --nologo -v q                     # 38
dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --nologo -v q  # 399

for p in toimi.core toimi.web toimi.web.Tests toimi.tools.tietue toimi.tools.tietue.Tests; do
  dotnet format "src/$p/$p.csproj" --verify-no-changes || echo "FORMAT DRIFT: $p"
done
```

Expected: grep prints `CLEAN`; web **38**, tietue **399**, zero failures, no unexpected skips; no `FORMAT DRIFT` lines.

- [ ] **Step 4: Format + commit**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj && dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
git add src/toimi.tools.tietue.Tests
git commit -m "test(tietue): pin admin summary against the aggregator's exact URL shape"
git status --short   # empty
git log --oneline main..arch-9-admin-federation
```

Expected: clean tree; commits — the plan doc, `test(web)`, `refactor(core)`, `test(tietue)`.

- [ ] **Step 5: Done.** Do not merge — hand back for review per the finishing-a-development-branch flow.
