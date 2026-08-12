# Arch 8: Cull Pass-Throughs + Deferred Follow-Ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete or inline the pass-through modules the architecture survey flagged (modules whose deletion moves complexity without concentrating it) and land the small follow-ups deferred from the C1–C7 refactor branches — every change behavior-preserving except three explicitly-listed test additions and two log additions.

**Architecture:** No new seams, no new packages. Five deletions/inlines (`AdminEndpointBuilder`, `ToimiClientFactory`, `EmbeddingService`, `NtfyNotifier` + tietue's `INotifier`, `UrlGuard`'s two forwarding methods), one rename+relocation (`BehaviorDispatcher` → `Semantic/SemanticSearch` — it dispatches nothing; it IS semantic search), one interface promotion (`INotifier` moves into the `toimi.notifications` library and `NtfyClient` implements it directly, deleting the adapter). Follow-ups: explicit switch arms in `RunTriggerTool`, a verkko notification error-path test, ToolGuard adoption in ruutu's two read-only template tools, an expiry garbage-date `LogWarning`, a pipeline-order comment, hermetic bootstrap tests, and preserving the original exception's stack in ToolGuard's translate-throw path.

**Tech Stack:** .NET 10 minimal APIs, xUnit v2, EF Core 10 (InMemory in tests), ModelContextProtocol 1.4.1, Microsoft.Extensions.AI (`IEmbeddingGenerator`, `ChatOptions`).

## Global Constraints

- Branch: `arch-8-cull-passthroughs` (already checked out).
- dotnet is NOT on PATH: every dotnet command is preceded by `export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"`.
- Per-project test commands: `dotnet test src/<project>.Tests/<project>.Tests.csproj --nologo -v q` from `/Users/jari/private/toimi`. tietue's suite needs Docker (Testcontainers) — Docker is available; those tests RUN, not skip.
- Suite floors — no drops, no assertion weakening. Expected end state: tietue **398** (396 + 2 new), core **113** (112 + 1 new), web **38**, koti **26**, verkko **27** (26 + 1 new), ruutu **105**, selain **60**, toimi.notifications green (count not pinned by the spec — just no failures). Mechanical test edits only where a deleted/renamed class was referenced (`BehaviorDispatcherTests`, `SearchToolTests`, `FakeNotifier`).
- Before each commit: `dotnet format src/<proj>/<proj>.csproj` for every touched project, then `dotnet format src/<proj>/<proj>.csproj --verify-no-changes` exits 0. Enforced as errors: IDE0005 (unused usings — watch for usings orphaned by deletions), IDE0022 (block bodies), IDE0046, whitespace. 2-space indent, file-scoped namespaces.
- Commit style: `<type>(<scope>): <subject>`.
- UNCHANGED surfaces: the MCP tool surface (names, descriptions, parameters) of every server; all pinned error strings (`"Failed to send notification: …"`, `"not semantically indexed"`, the RunTrigger busy/claim/unknown-kind strings, `"Blocked URL: …"`); tietue's `IEntityBehavior` registration order; k8s/deployment.
- CLAUDE.md: only one flagged class appears in it — `ToimiClientFactory` at line 144 (fix in Task 1). One optional accuracy touch-up to the `toimi.notifications` line (Task 2). No structural CLAUDE.md update — this is cleanup.

## Design Decisions

Verdict per cull item (each verified against the code on this branch, 2026-08-12):

1. **`AdminEndpointBuilder` → DELETE.** Verified: the class is 4 lines (`app.MapGroup("/admin")`, no auth — auth is ingress-level `admin-basic-auth`), and `MapAdmin()` has exactly one caller: `src/toimi.tools.tietue/Admin/AdminEndpoints.cs:29`. `toimi.web`'s `Admin/AdminEndpoints.cs` maps its own paths and never references it (it's the proxy half). Inline `app.MapGroup("/admin")` at the tietue caller; `using Toimi.Core.Admin;` stays there (still needed for `AdminSummaryDto`). `AdminError.cs`/`AdminSummaryDto.cs` stay — they are shared DTO/error contracts, not pass-throughs. Pending C9 (admin federation path contract) can reintroduce a shared constant if it earns one; a 4-line extension method pinning nothing is not that contract.
2. **`UrlGuard.IsBlockedHost`/`IsPrivate` → INLINE; class and name KEPT.** Verified: both forward verbatim to `Toimi.Core.Net.PrivateAddress`; `GuardedConnectAsync` is real (DNS resolve + routable filter + guarded socket connect) and stays. Callers of the forwards: `FetchUrlTool.cs:22` (`IsBlockedHost`) and `GuardedConnectAsync` itself (`IsPrivate`). Both call sites switch to `PrivateAddress` directly. The class keeps the name `UrlGuard` — it is still verkko's SSRF connect guard; renaming would churn `Program.cs`, `FetchGuardTests`, and the cross-reference comment in `PrivateAddress.cs:16` for zero clarity gain.
3. **`ToimiClientFactory` → DELETE.** Verified: after C3 it holds only `CreateRequestOptions` (one expression: `new ChatOptions { Tools = [.. tools] }`); the `SystemPrompt` const already lives in `ConversationContext` (line 136) — nothing to relocate. Single caller: `ToimiAgent.StartAsync` (`ToimiAgent.cs:65`). Inline and delete. The two historical comment mentions in `ConversationContextTests.cs:63-65` already speak of it in the past tense ("the old…", "the deleted ToimiClientFactoryTests") and remain accurate history — left as-is.
4. **`NtfyNotifier` → DELETE; `INotifier` MOVES into `toimi.notifications`.** Verified: `NtfyNotifier.SendAsync` forwards its 5 parameters verbatim to `NtfyClient.SendAsync`, and tietue's `INotifier` restates `NtfyClient`'s exact signature. The seam is worth keeping (`FakeNotifier` in tietue.Tests), so: `Toimi.Notifications.INotifier` is created in the library with `NtfyClient`'s exact signature (including its defaults), `NtfyClient` implements it directly, the tietue `Notifications/` folder (adapter + interface) is deleted, and `NotifyHandler`/`FakeNotifier`/`Program.cs` switch to the library interface. verkko's `SendNotificationTool` keeps injecting concrete `NtfyClient` — unaffected. No new dependency: tietue and its tests already reference `toimi.notifications` (transitively via the tietue project).
5. **`EmbeddingService` → INLINE + DELETE.** Verified: 3-line wrapper over `IEmbeddingGenerator<string, Embedding<float>>.GenerateVectorAsync(...).ToArray()`; exactly 2 call sites, both in `QdrantSemanticIndex` (`IndexAsync`, `SearchAsync`); no tests reference it. `QdrantSemanticIndex` injects the generator directly; the `Program.cs:31` registration is deleted (the generator singleton at line 30 stays).
6. **`ValidationResult` → OBSOLETED BY C5 — KEEP, no change.** Verified: it is no longer "produced at one site, converted at one site". C5 made it the return type of `INativeHandler.ValidateConfig` (default impl + 4 handler overrides: Notify/SetField/Message/Script), `ConfigValidation.RequireObject` produces it, `SchemaValidator.Validate` still returns it, and tools join `Errors` from multiple handlers. A real type with ~8 consumers passes the deletion test. Skipped.
7. **`McpInvoker` → KEEP AS-IS, no change.** Verified: the `IMcpInvoker` seam is load-bearing (`FakeMcpInvoker` backs `ScriptEffectApplierTests`/`JobEndToEndTests`/`ScriptHandlerTests`/`TypeSeederTests`), and the body is real orchestration — aggregator lifetime (`await using`), connect-all, args deserialization — not a verbatim forward. Folding it into `ScriptEffectApplier` (its consumer) would force the applier to own MCP session lifetime, deepening nothing. The connect-per-call cost comment stays on the implementation where the cost is incurred; the interface keeps its own contract summary. Honest verdict: the deletion test says keep.
8. **`BehaviorDispatcher` → RENAME to `SemanticSearch`, RELOCATE to `Semantic/`.** Verified post-C4 shape: it dispatches nothing — it checks the type's `SemanticIndex` behavior config, queries `ISemanticIndex`, joins scored ids to entities, re-sorts. That is semantic search, and its imports are already `Semantic` + `Data` + `Validation`. Folding into `SearchEntitiesTool` (its only production caller) fails the deletion test the other way: `BehaviorDispatcherTests` would have to assert through the tool's JSON serialization, weakening them, and DB-join logic would live in the MCP surface layer. Rename + move: class `SemanticSearch`, file `Semantic/SemanticSearch.cs`, namespace `toimi.tools.tietue.Semantic`; `ScoredEntity` moves with it (referenced by name nowhere else — consumers use `var`). Mechanical updates: `Program.cs:34`, `SearchEntitiesTool`, `BehaviorDispatcherTests` (renamed `SemanticSearchTests`), `SearchToolTests`.

Verdict per follow-up:

- **A (RunTriggerTool IDE0072 pragma) → DO.** Replace the pragma pair with explicit `OccurrenceState.Ran or OccurrenceState.Errored or OccurrenceState.EntityDeleted =>` arms. A discard arm throwing `UnreachableException` remains for unnamed enum values only (silences CS8524 without a pragma); named-value behavior is byte-identical, and unnamed values cannot occur (the enum is produced internally by `OccurrenceRunner`).
- **B (verkko SendNotification error-path test) → DO.** No `SendNotificationTool` tests exist at all. One new test drives an `HttpRequestException` through the tool via a throwing `HttpMessageHandler` inside a real `NtfyClient` and asserts the pinned `"Failed to send notification: …"` string. verkko 26 → 27.
- **C (ruutu ToolGuard uniformity) → DO.** Verified the bodies are NOT trivially pure: both hit the DB (`templates.ListAsync`/`GetAsync`) and both call `JsonDocument.Parse(t.SchemaJson)` on stored data — a DB outage or a bad stored schema currently escapes as a raw MCP protocol error while every sibling tool backstops via ToolGuard. Wrap both. Same deliberate error-path delta C7 already established repo-wide; success-path behavior unchanged; no ruutu test pins these two tools.
- **D (ExpiryReconciler garbage-date LogWarning) → DO.** The absent-field and garbage-date cases currently collapse into one silent `null`. Split them: absent stays silent (nothing to arm), present-but-unparseable warns. Optional `ILogger<ExpiryReconciler>? logger = null` ctor param — construction sites verified: `Program.cs:20` `AddScoped` (container injects `ILogger<T>` for the optional param), 4 test sites with 2 args (unchanged via the default). Two new tests (warn on garbage, silent on absent). tietue 396 → 398.
- **E (pipeline-order comment) → DO.** One comment line above the three `IEntityBehavior` registrations in tietue `Program.cs:36-38`.
- **F (hermetic HostingBootstrapTests) → DO.** `Builder()` calls `WebApplication.CreateBuilder()`, whose default config chain includes real environment variables — a developer exporting e.g. `HomeAssistant__BaseUrl` flips the missing-config assertions. Fix in the shared `Builder()` helper: `builder.Configuration.Sources.Clear()` before `AddInMemoryCollection`, making every test in the class hermetic. The two tests that use raw `CreateBuilder()` (`MigrateAndSeedAsync_skips…`, `AddToimiToolServer_names…`) read no config keys and stay as they are.
- **G (ToolGuard translate-throw loses the original stack) → DO.** Today the translate-throw path logs only `translateEx` (with the original's *type name* as a template arg) — the original exception's stack is lost. Fix: log the original exception object first (same `"MCP tool call failed"` message as the backstop path), then the translate failure. One new test pins both log entries. core 112 → 113.

---

## Task 1: Core culls + core follow-ups (F, G) + CLAUDE.md mention

**Files:**
- Modify: `src/toimi.core/ToimiAgent.cs:65`
- Delete: `src/toimi.core/ToimiClientFactory.cs`
- Delete: `src/toimi.core/Admin/AdminEndpointBuilder.cs`
- Modify: `src/toimi.tools.tietue/Admin/AdminEndpoints.cs:29`
- Modify: `src/toimi.core/Tools/ToolGuard.cs` (translate-throw catch block)
- Modify: `src/toimi.core.Tests/ToolGuardTests.cs` (one new test)
- Modify: `src/toimi.core.Tests/HostingBootstrapTests.cs` (`Builder()` helper)
- Modify: `CLAUDE.md:144`

**Interfaces:**
- Consumes: `ToolGuard.RunAsync(Func<Task<string>> body, Func<Exception, string?>? translate = null, ILogger? logger = null, string errorPrefix = "Error")` — signature unchanged.
- Produces: `ToimiClientFactory` and `AdminEndpointBuilder` no longer exist; no later task may reference them.

**Steps:**

- [ ] **Step 1: Commit this plan file** (if not already committed):

```bash
cd /Users/jari/private/toimi
git add docs/superpowers/plans/2026-08-05-arch-8-cull-passthroughs.md
git commit -m "docs: arch-8 cull-passthroughs implementation plan"
```

- [ ] **Step 2: Inline `CreateRequestOptions` at `ToimiAgent.StartAsync`**

In `src/toimi.core/ToimiAgent.cs`, replace line 65:

```csharp
      var options = ToimiClientFactory.CreateRequestOptions(tools);
```

with:

```csharp
      var options = new ChatOptions { Tools = [.. tools] };
```

(`using Microsoft.Extensions.AI;` is already present at the top of the file; `ChatOptions` is already the type of the `_options` field.)

- [ ] **Step 3: Delete `ToimiClientFactory.cs`**

```bash
git rm src/toimi.core/ToimiClientFactory.cs
```

- [ ] **Step 4: Inline `MapAdmin` at the only caller and delete the builder**

In `src/toimi.tools.tietue/Admin/AdminEndpoints.cs`, replace line 29:

```csharp
    var admin = app.MapAdmin();
```

with:

```csharp
    var admin = app.MapGroup("/admin");
```

Keep `using Toimi.Core.Admin;` — it is still required for `AdminSummaryDto` (line 43). Then:

```bash
git rm src/toimi.core/Admin/AdminEndpointBuilder.cs
```

- [ ] **Step 5 (G, RED): Add the failing log-preservation test**

Append to `src/toimi.core.Tests/ToolGuardTests.cs` (inside the existing class — `CapturingLogger` already exists there):

```csharp
  [Fact]
  public async Task A_throwing_translate_delegate_still_logs_the_original_exception()
  {
    var logger = new CapturingLogger();

    _ = await ToolGuard.RunAsync(
      () => throw new InvalidOperationException("nope"),
      translate: _ => throw new NotSupportedException("translate blew up"),
      logger: logger);

    Assert.Equal(2, logger.Entries.Count);
    Assert.IsType<InvalidOperationException>(logger.Entries[0].Exception);
    Assert.IsType<NotSupportedException>(logger.Entries[1].Exception);
  }
```

- [ ] **Step 6: Run it to verify it fails**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj --nologo -v q --filter "FullyQualifiedName~A_throwing_translate_delegate_still_logs"
```

Expected: FAIL — one entry logged (the `NotSupportedException`), not two.

- [ ] **Step 7 (G, GREEN): Log the original exception first in the translate-throw path**

In `src/toimi.core/Tools/ToolGuard.cs`, replace the inner catch block:

```csharp
      catch (Exception translateEx)
      {
        logger?.LogError(translateEx, "translate delegate failed while handling {OriginalException}", ex.GetType().Name);
        return $"{errorPrefix}: {ex.Message}";
      }
```

with:

```csharp
      catch (Exception translateEx)
      {
        // The original failure keeps its own stack — the translate crash must
        // not swallow it. Same message as the backstop path below.
        logger?.LogError(ex, "MCP tool call failed");
        logger?.LogError(translateEx, "translate delegate failed while handling {OriginalException}", ex.GetType().Name);
        return $"{errorPrefix}: {ex.Message}";
      }
```

- [ ] **Step 8 (F): Make `Builder()` hermetic**

In `src/toimi.core.Tests/HostingBootstrapTests.cs`, replace the helper:

```csharp
  private static WebApplicationBuilder Builder(params (string Key, string Value)[] settings)
  {
    var builder = WebApplication.CreateBuilder();
    builder.Configuration.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value));
    return builder;
  }
```

with:

```csharp
  private static WebApplicationBuilder Builder(params (string Key, string Value)[] settings)
  {
    var builder = WebApplication.CreateBuilder();
    // Hermetic: drop the default sources (env vars, appsettings) so a developer's
    // real environment can neither satisfy nor break the missing-config assertions.
    builder.Configuration.Sources.Clear();
    builder.Configuration.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value));
    return builder;
  }
```

- [ ] **Step 9: Fix the CLAUDE.md mention**

In `CLAUDE.md` line 144, the toimi.core "Owns:" sentence — remove the `ToimiClientFactory` clause. Replace:

```
  summary slots, catalog injection, compaction, and `ContextBudget` anchoring),
  request-option assembly (`ToimiClientFactory`), shared tool-server bootstrap
```

with:

```
  summary slots, catalog injection, compaction, and `ContextBudget` anchoring),
  shared tool-server bootstrap
```

- [ ] **Step 10: Run the affected suites**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj --nologo -v q
dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj --nologo -v q
dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --nologo -v q
```

Expected: core **113** passed (112 + the new log test), web **38**, tietue **396** (Docker running; `AdminEndpointsTests` exercises the inlined `MapGroup`).

- [ ] **Step 11: Format + verify + commit**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet format src/toimi.core/toimi.core.csproj && dotnet format src/toimi.core/toimi.core.csproj --verify-no-changes
dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj && dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj && dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
git add -A
git commit -m "refactor(core): cull ToimiClientFactory and AdminEndpointBuilder, keep original stack in ToolGuard, hermetic bootstrap tests"
```

---

## Task 2: tietue culls — EmbeddingService inline, SemanticSearch rename, INotifier move

**Files:**
- Modify: `src/toimi.tools.tietue/Semantic/QdrantSemanticIndex.cs`
- Delete: `src/toimi.tools.tietue/Semantic/EmbeddingService.cs`
- Move: `src/toimi.tools.tietue/Behaviors/BehaviorDispatcher.cs` → `src/toimi.tools.tietue/Semantic/SemanticSearch.cs`
- Modify: `src/toimi.tools.tietue/Tools/SearchEntitiesTool.cs`
- Create: `src/toimi.notifications/INotifier.cs`
- Modify: `src/toimi.notifications/NtfyClient.cs` (implements `INotifier`)
- Delete: `src/toimi.tools.tietue/Notifications/INotifier.cs`, `src/toimi.tools.tietue/Notifications/NtfyNotifier.cs` (folder gone)
- Modify: `src/toimi.tools.tietue/Handlers/NotifyHandler.cs` (using only)
- Modify: `src/toimi.tools.tietue/Program.cs` (lines 31, 34, 43-44)
- Move: `src/toimi.tools.tietue.Tests/BehaviorDispatcherTests.cs` → `src/toimi.tools.tietue.Tests/SemanticSearchTests.cs`
- Modify: `src/toimi.tools.tietue.Tests/SearchToolTests.cs`, `src/toimi.tools.tietue.Tests/FakeNotifier.cs`
- Modify: `CLAUDE.md:154-155` (accuracy touch-up)

**Interfaces:**
- Produces: `toimi.tools.tietue.Semantic.SemanticSearch(TietueDbContext db, ISemanticIndex index)` with `Task<IReadOnlyList<ScoredEntity>> SearchAsync(string type, string query, int limit, CancellationToken ct = default)`; `toimi.tools.tietue.Semantic.ScoredEntity(Entity Entity, float Score)`; `Toimi.Notifications.INotifier` with `Task SendAsync(string message, string? title = null, string priority = "default", string? tags = null, CancellationToken ct = default)`.
- Consumes: `Toimi.Core` unchanged from Task 1.

**Steps:**

- [ ] **Step 1: Inline the embedding calls into `QdrantSemanticIndex`**

In `src/toimi.tools.tietue/Semantic/QdrantSemanticIndex.cs`:

Add `using Microsoft.Extensions.AI;` to the usings and change the class declaration:

```csharp
using Microsoft.Extensions.AI;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace toimi.tools.tietue.Semantic;

public class QdrantSemanticIndex(QdrantClient qdrant, IEmbeddingGenerator<string, Embedding<float>> embeddings) : ISemanticIndex
```

In `IndexAsync`, replace:

```csharp
    var embedding = await embeddings.GenerateEmbeddingAsync(text);
```

with:

```csharp
    var embedding = (await embeddings.GenerateVectorAsync(text)).ToArray();
```

In `SearchAsync`, replace:

```csharp
    var embedding = await embeddings.GenerateEmbeddingAsync(query);
```

with:

```csharp
    var embedding = (await embeddings.GenerateVectorAsync(query)).ToArray();
```

Then delete the wrapper and its registration:

```bash
git rm src/toimi.tools.tietue/Semantic/EmbeddingService.cs
```

In `src/toimi.tools.tietue/Program.cs`, delete line 31:

```csharp
builder.Services.AddSingleton<EmbeddingService>();
```

(The `IEmbeddingGenerator` singleton registered on line 30 stays and now feeds `QdrantSemanticIndex` directly.)

- [ ] **Step 2: Rename `BehaviorDispatcher` → `SemanticSearch` and relocate**

```bash
git mv src/toimi.tools.tietue/Behaviors/BehaviorDispatcher.cs src/toimi.tools.tietue/Semantic/SemanticSearch.cs
```

New full content of `src/toimi.tools.tietue/Semantic/SemanticSearch.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Behaviors;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Semantic;

public record ScoredEntity(Entity Entity, float Score);

/// <summary>
/// Semantic entity search: verifies the type carries a SemanticIndex behavior,
/// queries the vector index, joins the scored ids back to their entities, and
/// returns them ranked by score.
/// </summary>
public class SemanticSearch(TietueDbContext db, ISemanticIndex index)
{
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
    return typeDef is null ? null : TypeBehaviors.Parse(typeDef.Behaviors).SemanticIndex;
  }
}
```

(The body is byte-identical to `BehaviorDispatcher`'s; only namespace, class name, doc comment, and the usings — `Behaviors` in, `Semantic` out — change. `SemanticIndexConfig` and `TypeBehaviors` live in `toimi.tools.tietue.Behaviors` (`TypeBehaviors.cs:5`), covered by the new using; they do not move.)

- [ ] **Step 3: Update the callers of the renamed class**

`src/toimi.tools.tietue/Program.cs` line 34, replace:

```csharp
builder.Services.AddScoped<toimi.tools.tietue.Behaviors.BehaviorDispatcher>();
```

with:

```csharp
builder.Services.AddScoped<SemanticSearch>();
```

(`using toimi.tools.tietue.Semantic;` is already at the top of `Program.cs`.)

`src/toimi.tools.tietue/Tools/SearchEntitiesTool.cs`: replace `using toimi.tools.tietue.Behaviors;` with `using toimi.tools.tietue.Semantic;`, and change the class declaration and call:

```csharp
public class SearchEntitiesTool(SemanticSearch search)
```

and inside `Search(...)`:

```csharp
      var results = await search.SearchAsync(type, query, limit);
```

Everything else in the tool (descriptions, JSON shape, `TietueValidationException` catch) is untouched.

- [ ] **Step 4: Update the tests mechanically**

```bash
git mv src/toimi.tools.tietue.Tests/BehaviorDispatcherTests.cs src/toimi.tools.tietue.Tests/SemanticSearchTests.cs
```

In `SemanticSearchTests.cs`: replace `using toimi.tools.tietue.Behaviors;` with `using toimi.tools.tietue.Semantic;`, rename the class to `SemanticSearchTests`, and update the two construction sites — the `SetupAsync` tuple/return becomes:

```csharp
  private static async Task<(TietueDbContext db, FakeSemanticIndex idx, SemanticSearch search)> SetupAsync(string? behaviors)
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("note", Schema, behaviors);
    var idx = new FakeSemanticIndex();
    return (db, idx, new SemanticSearch(db, idx));
  }
```

and the two tests destructure `(db, idx, search)` and call `search.SearchAsync(...)`. Assertions unchanged.

In `src/toimi.tools.tietue.Tests/SearchToolTests.cs`: both `new BehaviorDispatcher(db, idx)` / `new BehaviorDispatcher(db, new FakeSemanticIndex())` become `new SemanticSearch(...)` (rename the `dispatcher` locals to `search`). The file's existing `using toimi.tools.tietue.Behaviors;` (still needed for `SemanticIndexBehavior`) and `using toimi.tools.tietue.Semantic;` both stay. Assertions unchanged.

- [ ] **Step 5: Move `INotifier` into the notifications library**

Create `src/toimi.notifications/INotifier.cs`:

```csharp
namespace Toimi.Notifications;

/// <summary>
/// Push-notification seam. NtfyClient is the production implementation;
/// tietue's NotifyHandler depends on this interface so tests can capture
/// sends (FakeNotifier) without HTTP.
/// </summary>
public interface INotifier
{
  Task SendAsync(string message, string? title = null, string priority = "default", string? tags = null, CancellationToken ct = default);
}
```

In `src/toimi.notifications/NtfyClient.cs`, change the class declaration (the existing `SendAsync` already matches the interface exactly, defaults included):

```csharp
public class NtfyClient(NtfyOptions options, HttpClient? httpClient = null) : INotifier
```

Delete the tietue adapter and interface:

```bash
git rm src/toimi.tools.tietue/Notifications/INotifier.cs src/toimi.tools.tietue/Notifications/NtfyNotifier.cs
```

- [ ] **Step 6: Repoint tietue at the library interface**

`src/toimi.tools.tietue/Handlers/NotifyHandler.cs`: replace `using toimi.tools.tietue.Notifications;` with `using Toimi.Notifications;`. No other change — the ctor stays `NotifyHandler(INotifier notifier)`.

`src/toimi.tools.tietue/Program.cs` lines 43-44, replace:

```csharp
builder.Services.AddSingleton(new Toimi.Notifications.NtfyClient(ntfyOptions));
builder.Services.AddSingleton<toimi.tools.tietue.Notifications.INotifier, toimi.tools.tietue.Notifications.NtfyNotifier>();
```

with:

```csharp
builder.Services.AddSingleton<Toimi.Notifications.INotifier>(new Toimi.Notifications.NtfyClient(ntfyOptions));
```

(Nothing in tietue resolves the concrete `NtfyClient` — verified; only the handler's `INotifier` matters.)

`src/toimi.tools.tietue.Tests/FakeNotifier.cs`: replace `using toimi.tools.tietue.Notifications;` with `using Toimi.Notifications;`. The class body is unchanged — it already implements the exact signature.

- [ ] **Step 7: CLAUDE.md accuracy touch-up**

Replace lines 154-155:

```
`toimi.notifications` — `ntfy` client library, used by `verkko` and by
tietue's `notify` handler.
```

with:

```
`toimi.notifications` — `ntfy` client library (`NtfyClient` + the `INotifier`
seam it implements), used by `verkko` and by tietue's `notify` handler.
```

- [ ] **Step 8: Run the affected suites**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --nologo -v q
dotnet test src/toimi.notifications.Tests/toimi.notifications.Tests.csproj --nologo -v q
dotnet test src/toimi.tools.verkko.Tests/toimi.tools.verkko.Tests.csproj --nologo -v q
```

Expected: tietue **396** (rename is mechanical — same test count), notifications all green, verkko **26** (recompiles against the `NtfyClient : INotifier` change).

- [ ] **Step 9: Format + verify + commit**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet format src/toimi.notifications/toimi.notifications.csproj && dotnet format src/toimi.notifications/toimi.notifications.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj && dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj && dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
git add -A
git commit -m "refactor(tietue): inline EmbeddingService, rename BehaviorDispatcher to SemanticSearch, move INotifier into toimi.notifications"
```

---

## Task 3: tietue follow-ups (A, D, E)

**Files:**
- Modify: `src/toimi.tools.tietue/Tools/RunTriggerTool.cs:42-50`
- Modify: `src/toimi.tools.tietue/Provisioning/ExpiryReconciler.cs`
- Modify: `src/toimi.tools.tietue.Tests/ExpiryReconcilerTests.cs` (two new tests + capturing logger)
- Modify: `src/toimi.tools.tietue/Program.cs` (one comment line)

**Interfaces:**
- Produces: `ExpiryReconciler(TietueDbContext db, TriggerRepository triggers, ILogger<ExpiryReconciler>? logger = null)` — existing 2-arg call sites keep compiling via the default.
- Consumes: `OccurrenceState` members `Ran, Errored, AlreadyHandled, InProgress, UnknownKind, EntityDeleted, Busy` (verified in `Scheduling/OccurrenceRunner.cs:10-19`).

**Steps:**

- [ ] **Step 1 (A): Replace the IDE0072 pragma with explicit arms**

In `src/toimi.tools.tietue/Tools/RunTriggerTool.cs`, add `using System.Diagnostics;` to the usings, then replace lines 42-50:

```csharp
#pragma warning disable IDE0072
    return outcome.State switch
    {
      OccurrenceState.Busy => /*lang=json,strict*/ """{"status":"busy","error":"a scheduler tick holds the run lock; try again shortly"}""",
      OccurrenceState.InProgress or OccurrenceState.AlreadyHandled => "Could not claim a run for this occurrence; try again.",
      OccurrenceState.UnknownKind => $"No handler registered for kind '{trigger.HandlerKind}'. Recorded an error event for this occurrence.",
      _ => JsonSerializer.Serialize(new { status = outcome.Status, result = outcome.ResultJson }),
    };
#pragma warning restore IDE0072
```

with:

```csharp
    return outcome.State switch
    {
      OccurrenceState.Busy => /*lang=json,strict*/ """{"status":"busy","error":"a scheduler tick holds the run lock; try again shortly"}""",
      OccurrenceState.InProgress or OccurrenceState.AlreadyHandled => "Could not claim a run for this occurrence; try again.",
      OccurrenceState.UnknownKind => $"No handler registered for kind '{trigger.HandlerKind}'. Recorded an error event for this occurrence.",
      OccurrenceState.Ran or OccurrenceState.Errored or OccurrenceState.EntityDeleted =>
        JsonSerializer.Serialize(new { status = outcome.Status, result = outcome.ResultJson }),
      _ => throw new UnreachableException($"unhandled OccurrenceState {outcome.State}"),
    };
```

(All 7 named members are now explicit, so IDE0072 is satisfied without a pragma; the discard handles only unnamed enum values — impossible from `OccurrenceRunner` — and silences CS8524.)

- [ ] **Step 2 (D, RED): Add the failing warning tests**

In `src/toimi.tools.tietue.Tests/ExpiryReconcilerTests.cs`, add `using Microsoft.Extensions.Logging;` to the usings, then append inside the class:

```csharp
  private sealed class CapturingLogger : ILogger<ExpiryReconciler>
  {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
      return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
      return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
      Entries.Add((logLevel, formatter(state, exception)));
    }
  }

  private static async Task<(EntityRepository repo, CapturingLogger log)> LoggedSetupAsync(Data.TietueDbContext db)
  {
    await new TypeRepository(db).DefineAsync("temp", Schema, DeleteExpiry);
    var log = new CapturingLogger();
    var reconciler = new ExpiryReconciler(db, new TriggerRepository(db, TestConfig.Default), log);
    return (new EntityRepository(db, new SchemaValidator(), [new ExpiryBehavior(reconciler)]), log);
  }

  [Fact]
  public async Task Garbage_expiry_date_logs_a_warning()
  {
    using var db = TestDb.New();
    var (repo, log) = await LoggedSetupAsync(db);

    await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x","expiresAt":"soon"}"""), []);

    var entry = Assert.Single(log.Entries);
    Assert.Equal(LogLevel.Warning, entry.Level);
    Assert.Contains("expiresAt", entry.Message);
  }

  [Fact]
  public async Task Absent_expiry_field_stays_silent()
  {
    using var db = TestDb.New();
    var (repo, log) = await LoggedSetupAsync(db);

    await repo.CreateAsync("temp", JsonNode.Parse("""{"name":"x"}"""), []);

    Assert.Empty(log.Entries);
  }
```

- [ ] **Step 3: Run them to verify they fail**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ExpiryReconcilerTests"
```

Expected: compile FAIL first (no 3-arg ctor); after the ctor exists it would fail on the missing warning. Either red counts.

- [ ] **Step 4 (D, GREEN): Split absent-vs-garbage and warn**

In `src/toimi.tools.tietue/Provisioning/ExpiryReconciler.cs`, change the class declaration:

```csharp
public class ExpiryReconciler(TietueDbContext db, TriggerRepository triggers, ILogger<ExpiryReconciler>? logger = null)
```

Replace the middle of `ReconcileAsync` — from `var at = ExpiryAt(entity.Data, cfg.Field);` through the `return;` of the null check:

```csharp
    var at = ExpiryAt(entity.Data, cfg.Field);
    if (at is null)
    {
      return; // field absent OR not a parseable date — a garbage date must not arm a dead trigger
    }
```

with:

```csharp
    if (!entity.Data.RootElement.TryGetProperty(cfg.Field, out var raw))
    {
      return; // field absent — nothing to arm
    }

    var at = ParseExpiry(raw);
    if (at is null)
    {
      // A garbage date must not arm a dead trigger — but silently skipping made
      // it look like expiry was never configured, so say why nothing armed.
      logger?.LogWarning(
        "Entity {EntityId} ({EntityType}): expiry field '{Field}' is not a parseable date; no expiry trigger armed.",
        entity.Id, entity.Type, cfg.Field);
      return;
    }
```

Replace the private `ExpiryAt` helper with:

```csharp
  private static DateTimeOffset? ParseExpiry(JsonElement value)
  {
    return value.ValueKind == JsonValueKind.String
      && DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
        ? at
        : null;
  }
```

(No `Program.cs` change: `AddScoped<ExpiryReconciler>()` lets the container fill the optional `ILogger<ExpiryReconciler>`; the 4 existing 2-arg test constructions compile unchanged.)

- [ ] **Step 5 (E): Pipeline-order comment**

In `src/toimi.tools.tietue/Program.cs`, directly above the first `IEntityBehavior` registration (`SemanticIndexBehavior`), add:

```csharp
// Registration order = pipeline order: EntityRepository runs these IEntityBehaviors in the order registered.
```

- [ ] **Step 6: Run the tietue suite**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --nologo -v q
```

Expected: **398** passed (396 + the two new expiry tests), including the pre-existing `Garbage_expiry_date_does_not_arm_a_zombie_trigger` and every `RunTriggerTool` test unchanged.

- [ ] **Step 7: Format + verify + commit**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj && dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj && dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
git add -A
git commit -m "fix(tietue): warn on unparseable expiry date, explicit RunTrigger switch arms, pipeline-order comment"
```

---

## Task 4: verkko + ruutu (cull 2, follow-ups B, C)

**Files:**
- Modify: `src/toimi.tools.verkko/Fetcher/UrlGuard.cs` (delete the two forwards)
- Modify: `src/toimi.tools.verkko/Tools/FetchUrlTool.cs:22`
- Create: `src/toimi.tools.verkko.Tests/SendNotificationToolTests.cs`
- Modify: `src/toimi.tools.ruutu/Tools/TemplateTools.cs` (`DisplayListTemplates`, `DisplayGetTemplate`)

**Interfaces:**
- Consumes: `Toimi.Core.Net.PrivateAddress.IsBlockedHost(string host)` / `IsPrivate(IPAddress ip)` (existing, unchanged); `Toimi.Core.Tools.ToolGuard.RunAsync` (unchanged); `Toimi.Notifications.NtfyClient(NtfyOptions, HttpClient?)` from Task 2.
- Produces: `UrlGuard` keeps only `GuardedConnectAsync(SocketsHttpConnectionContext, CancellationToken)` — same signature, same behavior.

**Steps:**

- [ ] **Step 1: Inline the UrlGuard forwards**

Replace `src/toimi.tools.verkko/Fetcher/UrlGuard.cs` in full with:

```csharp
using System.Net;
using System.Net.Sockets;
using Toimi.Core.Net;

namespace toimi.tools.verkko.Fetcher;

/// <summary>
/// SSRF guard for outbound fetches. The private/non-routable address policy is
/// the shared Toimi.Core.Net.PrivateAddress; this class applies it at connect
/// time. Scheme policy lives in FetchUrlTool (http is allowed here).
/// </summary>
public static class UrlGuard
{
  /// <summary>
  /// SocketsHttpHandler.ConnectCallback that resolves the target host and refuses
  /// to connect to private/internal addresses. Runs for every connection the
  /// HttpClient opens — including redirect targets — so a public URL cannot
  /// redirect the fetcher into the cluster or local network.
  /// </summary>
  public static async ValueTask<Stream> GuardedConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
  {
    var host = context.DnsEndPoint.Host;
    var addresses = IPAddress.TryParse(host, out var literal)
      ? [literal]
      : await Dns.GetHostAddressesAsync(host, ct);

    var routable = addresses.Where(ip => !PrivateAddress.IsPrivate(ip)).ToArray();
    if (routable.Length == 0)
    {
      throw new HttpRequestException($"Blocked: '{host}' resolves to a private or internal address.");
    }

    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
    try
    {
      await socket.ConnectAsync(routable, context.DnsEndPoint.Port, ct);
      return new NetworkStream(socket, ownsSocket: true);
    }
    catch
    {
      socket.Dispose();
      throw;
    }
  }
}
```

In `src/toimi.tools.verkko/Tools/FetchUrlTool.cs`, add `using Toimi.Core.Net;` and replace line 22:

```csharp
    if (UrlGuard.IsBlockedHost(uri.DnsSafeHost))
```

with:

```csharp
    if (PrivateAddress.IsBlockedHost(uri.DnsSafeHost))
```

(`Program.cs:19` and `FetchGuardTests.cs:11` use only `GuardedConnectAsync` — verified, no other edits. The `PrivateAddress.cs:16` comment referencing `UrlGuard.GuardedConnectAsync` stays accurate.)

- [ ] **Step 2 (B, RED): Add the notification error-path test**

Create `src/toimi.tools.verkko.Tests/SendNotificationToolTests.cs`:

```csharp
using toimi.tools.verkko.Tools;
using Toimi.Notifications;
using Xunit;

namespace toimi.tools.verkko.Tests;

public class SendNotificationToolTests
{
  private sealed class FailingHandler : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      throw new HttpRequestException("connection refused");
    }
  }

  [Fact]
  public async Task Send_failure_returns_the_error_string_instead_of_throwing()
  {
    var ntfy = new NtfyClient(
      new NtfyOptions { BaseUrl = "http://ntfy.test", Topic = "toimi" },
      new HttpClient(new FailingHandler()));
    var tool = new SendNotificationTool(ntfy);

    var result = await tool.SendNotification("hello");

    Assert.StartsWith("Failed to send notification: ", result);
    Assert.Contains("connection refused", result);
  }
}
```

- [ ] **Step 3: Run it — it must pass immediately (it pins EXISTING behavior)**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet test src/toimi.tools.verkko.Tests/toimi.tools.verkko.Tests.csproj --nologo -v q
```

Expected: **27** passed. (This test is coverage for an existing path, not TDD of new behavior — green on first run is the success criterion. If it fails, the tool's guard wiring is broken; stop and investigate, do not adjust the assertion.)

- [ ] **Step 4 (C): Adopt ToolGuard in ruutu's two read-only template tools**

In `src/toimi.tools.ruutu/Tools/TemplateTools.cs`, replace `DisplayListTemplates`:

```csharp
  [McpServerTool, Description("List all available templates with their schemas. Read this at session start to know what shapes you can push to a display without writing HTML.")]
  public Task<string> DisplayListTemplates(CancellationToken ct = default)
  {
    return ToolGuard.RunAsync(async () =>
    {
      var list = await templates.ListAsync(ct);
      var view = list.Select(t => new
      {
        t.Name,
        t.Description,
        schema = JsonDocument.Parse(t.SchemaJson).RootElement,
        has_modern = !string.IsNullOrEmpty(t.ModernHtml),
        has_legacy = !string.IsNullOrEmpty(t.LegacyHtml),
        t.IsSeeded
      });
      return JsonSerializer.Serialize(view);
    });
  }
```

and `DisplayGetTemplate`:

```csharp
  [McpServerTool, Description("Fetch the full definition of a single template including both modern_html and legacy_html variants. Useful when modifying an existing template.")]
  public Task<string> DisplayGetTemplate(
    [Description("Template name.")] string name,
    CancellationToken ct = default)
  {
    return ToolGuard.RunAsync(async () =>
    {
      var t = await templates.GetAsync(name, ct);
      return t is null
        ? $"Template '{name}' not found."
        : JsonSerializer.Serialize(new
        {
          t.Name,
          t.Description,
          schema = JsonDocument.Parse(t.SchemaJson).RootElement,
          modern_html = t.ModernHtml,
          legacy_html = t.LegacyHtml,
          t.IsSeeded
        });
    });
  }
```

(Descriptions, method names, parameters, and success-path output are byte-identical; only a DB failure or a corrupt stored `SchemaJson` now comes back as `"Error: …"` instead of an MCP protocol error — the C7 convention. The non-async `return ToolGuard.RunAsync(...)` shape matches `DisplayContentTools`.)

- [ ] **Step 5: Run the ruutu suite**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet test src/toimi.tools.ruutu.Tests/toimi.tools.ruutu.Tests.csproj --nologo -v q
```

Expected: **105** passed (no ruutu test pins these two tools — verified).

- [ ] **Step 6: Format + verify + commit (one commit per scope)**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet format src/toimi.tools.verkko/toimi.tools.verkko.csproj && dotnet format src/toimi.tools.verkko/toimi.tools.verkko.csproj --verify-no-changes
dotnet format src/toimi.tools.verkko.Tests/toimi.tools.verkko.Tests.csproj && dotnet format src/toimi.tools.verkko.Tests/toimi.tools.verkko.Tests.csproj --verify-no-changes
git add src/toimi.tools.verkko src/toimi.tools.verkko.Tests
git commit -m "refactor(verkko): inline UrlGuard forwards to PrivateAddress, cover SendNotification error path"

dotnet format src/toimi.tools.ruutu/toimi.tools.ruutu.csproj && dotnet format src/toimi.tools.ruutu/toimi.tools.ruutu.csproj --verify-no-changes
git add src/toimi.tools.ruutu
git commit -m "refactor(ruutu): adopt ToolGuard in template list/get for convention uniformity"
```

---

## Task 5: Full gate

**Files:** none (verification only).

**Steps:**

- [ ] **Step 1: Zero dangling references to culled names**

```bash
cd /Users/jari/private/toimi
grep -rn "AdminEndpointBuilder\|MapAdmin()\|NtfyNotifier\|EmbeddingService\|BehaviorDispatcher" --include="*.cs" src/ || echo CLEAN
grep -rn "ToimiClientFactory" --include="*.cs" src/
grep -rn "UrlGuard.IsBlockedHost\|UrlGuard.IsPrivate" --include="*.cs" src/ || echo CLEAN
grep -rn "toimi.tools.tietue.Notifications" --include="*.cs" src/ || echo CLEAN
```

Expected: first, third, and fourth greps print `CLEAN`. The `ToimiClientFactory` grep may match ONLY the two historical comment lines in `src/toimi.core.Tests/ConversationContextTests.cs:63-65` (they describe deleted code in the past tense — acceptable residue); any other hit is a defect.

- [ ] **Step 2: Full test sweep — every suite, expected counts**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --nologo -v q   # 398 (Docker: Testcontainers tests RUN)
dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj --nologo -v q                   # 113
dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj --nologo -v q                     # 38
dotnet test src/toimi.tools.koti.Tests/toimi.tools.koti.Tests.csproj --nologo -v q       # 26
dotnet test src/toimi.tools.verkko.Tests/toimi.tools.verkko.Tests.csproj --nologo -v q   # 27
dotnet test src/toimi.tools.ruutu.Tests/toimi.tools.ruutu.Tests.csproj --nologo -v q     # 105
dotnet test src/toimi.tools.selain.Tests/toimi.tools.selain.Tests.csproj --nologo -v q   # 60
dotnet test src/toimi.notifications.Tests/toimi.notifications.Tests.csproj --nologo -v q # all green
```

Expected: exactly the counts above, zero failures, zero unexpected skips (tietue Docker tests must show as executed).

- [ ] **Step 3: Repo-wide format verify across every touched project**

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
for p in toimi.core toimi.core.Tests toimi.notifications toimi.tools.tietue toimi.tools.tietue.Tests toimi.tools.verkko toimi.tools.verkko.Tests toimi.tools.ruutu; do
  dotnet format "src/$p/$p.csproj" --verify-no-changes || echo "FORMAT DRIFT: $p"
done
```

Expected: no `FORMAT DRIFT` lines.

- [ ] **Step 4: Working tree clean, history reviewable**

```bash
git status --short   # empty
git log --oneline main..arch-8-cull-passthroughs
```

Expected: clean tree; commits — the plan doc, `refactor(core)`, `refactor(tietue)`, `fix(tietue)`, `refactor(verkko)`, `refactor(ruutu)`.

- [ ] **Step 5: Done.** Do not merge — hand back for review per the finishing-a-development-branch flow (Jari squash-merges finished branches into local `wip`).
