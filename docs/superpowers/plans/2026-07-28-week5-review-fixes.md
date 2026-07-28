# Week 5 Review-Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the ten items from the 2026-07-27 codebase review: two security bugs (ruutu XSS, verkko OOM), four correctness gaps (timezone recurrence, entity-create atomicity, hub failure modes, `set_trigger` validation), and four architectural cleanups (SSRF dedup, tool-server bootstrap extraction, LLM-provider seam, lazy conversations).

**Architecture:** Ten tasks on one branch, ordered security → extractions → tietue correctness → core/web. New shared code lands in `toimi.core` (`Toimi.Core.Net.PrivateAddress`, `Toimi.Core.Hosting` helpers, `ILlmClientProvider`). No new services.

**Tech Stack:** .NET 10, EF Core transactions, `Ical.Net` (tz-aware recurrence), OpenAI SDK client options, ASP.NET minimal-API hosting extensions, React/SignalR.

**Conventions:** 2-space indent, file-scoped namespaces, block bodies (IDE0022 as error); CA1873 IsEnabled guards only if the build demands. `dotnet format <csproj> --verify-no-changes` before each commit. dotnet at `/Users/jari/.local/share/mise/installs/dotnet/10.0.301/` if not on PATH. TS strict; `npm run lint && npm run build` for frontend. `bash scripts/lint.sh` for yaml/shell changes. Verify branch with `git branch --show-current`.

**Design decisions locked in (do not relitigate):**
- **Timezone (item 3):** a recurring schedule's `tz` is resolved at *trigger-creation* time — if absent, stamped with `ToimiConfiguration.UserTimeZone` (default `Europe/Helsinki`) into the persisted schedule JSON, so the schedule is self-describing and DST-correct forever. `RecurrenceCalculator` expands in that zone and converts back to UTC.
- **Atomicity (item 4):** one `db.Database.BeginTransactionAsync()` around entity-create + provisioning + expiry. Simpler than a reconciler and correct for the single-writer engine.
- **Provider seam (item 9):** `ILlmClientProvider` injected at the two `Create` call sites; the OpenAI impl sets explicit `NetworkTimeout` + `RetryPolicy`. Not a chat-loop rewrite.
- **Lazy conversations (item 10):** no DB row until the first message; a `ConversationCreated(id)` event tells the client its id for reconnect.

---

## Task 1: Validate the ruutu display identifier (security — stored XSS)

Context: `DisplayRegister` lets the AI set an `identifier` that `DisplayRepository.RegisterAsync` stores verbatim and `DisplayApiController.GetShell` splices raw into `shell.html` as `var ID = "__IDENTIFIER__";`. A name like `x";<script>…//` runs arbitrary JS in the ruutu origin on every device that opens that display. One regex in `RegisterAsync` closes the XSS plus the splash JSON-injection crash.

**Files:**
- Modify: `src/toimi.tools.ruutu/Data/Repositories/DisplayRepository.cs`
- Test: `src/toimi.tools.ruutu.Tests/DisplayIdentifierTests.cs` (new)

- [x] **Step 1: Write the failing tests**

Create `src/toimi.tools.ruutu.Tests/DisplayIdentifierTests.cs`. Check the existing ruutu test setup (there's an in-memory DbContext helper — grep `RuutuDbContext` in the tests dir and mirror it):

```csharp
using System.Threading.Tasks;
using toimi.tools.ruutu.Data.Repositories;
using Xunit;

namespace toimi.tools.ruutu.Tests;

public class DisplayIdentifierTests
{
  [Theory]
  [InlineData("living-room")]
  [InlineData("kitchen")]
  [InlineData("display-1")]
  [InlineData("a")]
  public async Task Accepts_valid_slugs(string id)
  {
    using var db = TestDb.New(); // adapt to the real ruutu test-db helper name
    var repo = new DisplayRepository(db);
    var d = await repo.RegisterAsync(id, null);
    Assert.Equal(id, d.Identifier);
  }

  [Theory]
  [InlineData("x\";<script>alert(1)</script>//")]
  [InlineData("has space")]
  [InlineData("Upper")]
  [InlineData("-leading-dash")]
  [InlineData("has\"quote")]
  [InlineData("")]
  [InlineData("way-too-long-way-too-long-way-too-long-way-too-long-way-too-long-x")] // >64
  public async Task Rejects_non_slug_identifiers(string id)
  {
    using var db = TestDb.New();
    var repo = new DisplayRepository(db);
    await Assert.ThrowsAsync<ArgumentException>(() => repo.RegisterAsync(id, null));
  }
}
```

- [x] **Step 2: Run to verify it fails** — `dotnet test src/toimi.tools.ruutu.Tests --filter DisplayIdentifierTests` → FAIL (no validation; invalid ids currently persist).

- [x] **Step 3: Add validation in `RegisterAsync`**

At the top of `DisplayRepository.RegisterAsync`, before the existing-lookup:

```csharp
    if (!IsValidIdentifier(identifier))
    {
      throw new ArgumentException(
        $"Invalid display identifier '{identifier}'. Use a lowercase slug: letters, digits, and hyphens, 1-64 chars, not starting with a hyphen.",
        nameof(identifier));
    }
```

and add a private static (with `using System.Text.RegularExpressions;`):

```csharp
  private static readonly Regex SlugPattern = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);

  private static bool IsValidIdentifier(string identifier)
  {
    return !string.IsNullOrEmpty(identifier) && SlugPattern.IsMatch(identifier);
  }
```

- [x] **Step 4: Surface it cleanly at the tool boundary**

`DisplayRegister` in `src/toimi.tools.ruutu/Tools/DisplayManagementTools.cs` calls `RegisterAsync`. Wrap that call so the `ArgumentException` returns a readable string to the agent instead of throwing (match the file's existing error-string style — read it first):

```csharp
    try
    {
      // ...existing RegisterAsync call...
    }
    catch (ArgumentException ex)
    {
      return $"Error: {ex.Message}";
    }
```

Also check `DisplayApiController.GetShell` (route `{identifier}`): an unregistered identifier already returns the not-configured page, so no XSS reaches the shell for unknown ids — the fix is purely at registration. Confirm no other write path (SSE subscribe, capabilities) persists a fresh identifier without going through `RegisterAsync`; if one does, guard it too and note it.

- [x] **Step 5: Run tests, format, commit**

```bash
dotnet test src/toimi.tools.ruutu.Tests
dotnet format src/toimi.tools.ruutu/toimi.tools.ruutu.csproj --verify-no-changes
dotnet format src/toimi.tools.ruutu.Tests/toimi.tools.ruutu.Tests.csproj --verify-no-changes
git add src/toimi.tools.ruutu src/toimi.tools.ruutu.Tests
git commit -m "fix(ruutu): validate display identifiers as slugs to close a stored-XSS path"
```

---

## Task 2: Bound the verkko response body (security — OOM)

Context: `WebFetcher.FetchAsync` calls `ReadAsStringAsync` (buffering the whole body) before the 50k truncation, and no `MaxResponseContentBufferSize` is set. A public URL streaming gigabytes OOMs the pod.

**Files:**
- Modify: `src/toimi.tools.verkko/Program.cs` (the `AddHttpClient<WebFetcher>` block)
- Modify: `src/toimi.tools.verkko/Tools/FetchUrlTool.cs` (catch the new exception)

- [x] **Step 1: Cap the buffer**

In `src/toimi.tools.verkko/Program.cs`, inside the existing `AddHttpClient<WebFetcher>(client => { ... })` lambda, add:

```csharp
  // Refuse oversized bodies before buffering them into memory (OOM guard).
  // Slightly above the 50k the fetcher keeps, so truncation still applies to normal pages.
  client.MaxResponseContentBufferSize = 8_000_000;
```

- [x] **Step 2: Return a clean message when a body is too large**

`HttpClient` throws `HttpRequestException` when the buffer limit is exceeded. `FetchUrlTool.FetchUrl` already catches `HttpRequestException` and returns a string, so oversized responses already degrade to a readable error — verify by reading `FetchUrlTool.cs`; if the existing catch message would be confusing for this case, no change is needed (the generic "HTTP error fetching" is acceptable). Confirm in your report which catch handles it.

- [x] **Step 3: Build, format, commit**

```bash
dotnet build src/toimi.tools.verkko/toimi.tools.verkko.csproj
dotnet format src/toimi.tools.verkko/toimi.tools.verkko.csproj --verify-no-changes
git add src/toimi.tools.verkko
git commit -m "fix(verkko): cap response buffer size to prevent OOM on huge bodies"
```

---

## Task 3: Extract shared private-address SSRF logic to core (item 7)

Context: `UrlGuard.IsPrivate` (verkko) and `ScribanRenderer.IsPrivate` (ruutu) are hand-maintained copies of the same CIDR blocklist and have **already diverged** (verkko handles `::a.b.c.d`, ruutu doesn't). One shared predicate in core removes the drift-is-a-vuln class.

**Files:**
- Create: `src/toimi.core/Net/PrivateAddress.cs`
- Create: `src/toimi.core.Tests/PrivateAddressTests.cs` (new — or extend if a Net test exists)
- Modify: `src/toimi.tools.verkko/Fetcher/UrlGuard.cs` (delegate to core), `src/toimi.tools.verkko/toimi.tools.verkko.csproj` (reference core if not already)
- Modify: `src/toimi.tools.ruutu/Rendering/ScribanRenderer.cs` (delegate to core), `src/toimi.tools.ruutu/toimi.tools.ruutu.csproj` (reference core)

- [x] **Step 1: Move the canonical logic to core (tests first)**

Read the CURRENT `UrlGuard.IsPrivate`/`IsBlockedHost` (verkko — the more complete copy, includes `::a.b.c.d`) and `ScribanRenderer.IsPrivate`. Create `src/toimi.core/Net/PrivateAddress.cs` with the UNION of both (verkko's is the superset — use it verbatim, adapting the namespace):

```csharp
using System.Net;
using System.Net.Sockets;

namespace Toimi.Core.Net;

/// <summary>
/// Canonical private/non-routable address policy shared by verkko's fetch SSRF
/// guard and ruutu's safe_url template filter. One copy so a new reserved range
/// added here protects both — the earlier hand-maintained copies had already drifted.
/// </summary>
public static class PrivateAddress
{
  // ...paste verkko UrlGuard's IsBlockedHost(string) and IsPrivate(IPAddress) bodies verbatim...
}
```

Copy verkko's `IsBlockedHost` and `IsPrivate` exactly (they're the superset). Create `src/toimi.core.Tests/PrivateAddressTests.cs` porting verkko's existing `UrlGuardTests` IsPrivate/IsBlockedHost cases (read `src/toimi.tools.verkko.Tests/UrlGuardTests.cs` and move the predicate cases; keep verkko's `GuardedConnectAsync`/fetch tests where they are). Run → these pass immediately (pure move); that's fine, they lock the behavior in its new home.

- [x] **Step 2: Delegate verkko's UrlGuard to core**

Ensure `src/toimi.tools.verkko/toimi.tools.verkko.csproj` has `<ProjectReference Include="../toimi.core/toimi.core.csproj" />` (add if missing). In `UrlGuard.cs`, replace the `IsPrivate`/`IsBlockedHost` bodies with delegation:

```csharp
  public static bool IsBlockedHost(string host)
  {
    return Toimi.Core.Net.PrivateAddress.IsBlockedHost(host);
  }

  public static bool IsPrivate(IPAddress ip)
  {
    return Toimi.Core.Net.PrivateAddress.IsPrivate(ip);
  }
```

Keep `GuardedConnectAsync` as-is (it calls `IsPrivate`, now delegating). verkko's existing UrlGuardTests still pass (behavior unchanged).

- [x] **Step 3: Delegate ruutu's ScribanRenderer.SafeUrl to core**

Add the core ProjectReference to `src/toimi.tools.ruutu/toimi.tools.ruutu.csproj` if missing. In `ScribanRenderer.cs`, replace the private `IsPrivate` with a call to `Toimi.Core.Net.PrivateAddress.IsPrivate` inside `SafeUrl` (keep `SafeUrl`'s https-only + HTML-escape wrapper local — only the IP predicate moves). This is where the fix has teeth: ruutu now inherits verkko's `::a.b.c.d` handling. Add a ruutu test asserting `safe_url` rejects an IPv4-compatible IPv6 form (e.g. `https://[::0a00:0001]/`) → `about:blank`.

- [x] **Step 4: Run all affected suites, format, commit**

```bash
dotnet test src/toimi.core.Tests src/toimi.tools.verkko.Tests src/toimi.tools.ruutu.Tests
dotnet build toimi.sln
dotnet format src/toimi.core/toimi.core.csproj --verify-no-changes
dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj --verify-no-changes
dotnet format src/toimi.tools.verkko/toimi.tools.verkko.csproj --verify-no-changes
dotnet format src/toimi.tools.ruutu/toimi.tools.ruutu.csproj --verify-no-changes
git add src/toimi.core src/toimi.core.Tests src/toimi.tools.verkko src/toimi.tools.ruutu
git commit -m "refactor: share one private-address SSRF policy in core across verkko and ruutu"
```

---

## Task 4: Extract shared tool-server hosting bootstrap (item 8)

Context: the MCP-server block, migrate-at-startup scope, and `/health` are copy-pasted across all four tool servers (this copy-paste culture is how the SSRF divergence happened). Extract small `toimi.core` hosting helpers and add a real `/ready` DB check.

**Files:**
- Create: `src/toimi.core/Hosting/ToimiHostingExtensions.cs`
- Modify: `src/toimi.tools.{tietue,koti,verkko,ruutu}/Program.cs`
- Modify: `k8s/base/tools-tietue/deployment.yaml`, `k8s/base/tools-ruutu/deployment.yaml` (readinessProbe → `/ready`)

- [x] **Step 1: Create the hosting extensions**

Create `src/toimi.core/Hosting/ToimiHostingExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Toimi.Core.Hosting;

public static class ToimiHostingExtensions
{
  /// <summary>Registers an MCP server with tools discovered from the calling assembly + HTTP transport.</summary>
  public static IServiceCollection AddToimiMcpServer(this IServiceCollection services, string name)
  {
    services
      .AddMcpServer(o => o.ServerInfo = new() { Name = name, Version = "1.0.0" })
      .WithHttpTransport()
      .WithToolsFromAssembly();
    return services;
  }

  /// <summary>Maps the MCP endpoint plus a liveness /health (bare 200).</summary>
  public static void MapToimiMcp(this WebApplication app)
  {
    app.MapMcp();
    app.MapGet("/health", () => Results.Ok());
  }

  /// <summary>Adds a readiness /ready that verifies the DbContext can reach its database.</summary>
  public static void MapToimiReadiness<TContext>(this WebApplication app) where TContext : DbContext
  {
    app.MapGet("/ready", async (TContext db) =>
    {
      try
      {
        return await db.Database.CanConnectAsync() ? Results.Ok() : Results.StatusCode(503);
      }
      catch
      {
        return Results.StatusCode(503);
      }
    });
  }
}
```

Note: `WithToolsFromAssembly()` with no argument discovers tools from the *calling* assembly. When called from inside core it would scan core, not the tool server. Verify the ModelContextProtocol API — if `WithToolsFromAssembly()` binds to core's assembly here, change the signature to `AddToimiMcpServer(this IServiceCollection services, string name, System.Reflection.Assembly toolAssembly)` and pass `typeof(Program).Assembly` from each server, calling `.WithToolsFromAssembly(toolAssembly)`. Pick whichever the API supports and note it.

- [x] **Step 2: Rewire all four tool servers**

In each of `src/toimi.tools.{tietue,koti,verkko,ruutu}/Program.cs`, replace the inline `.AddMcpServer(...).WithHttpTransport().WithToolsFromAssembly()` with `builder.Services.AddToimiMcpServer("<name>")` (or the assembly-arg form), and replace `app.MapMcp(); app.MapGet("/health", ...)` with `app.MapToimiMcp();` (+ `using Toimi.Core.Hosting;`). For tietue and ruutu (DB-backed), also add `app.MapToimiReadiness<TietueDbContext>();` / `<RuutuDbContext>();`. koti and verkko keep liveness-only. Leave every other line (DI registrations, migrate-at-startup, admin endpoints) exactly as-is. koti/verkko may not reference core yet — add the ProjectReference.

- [x] **Step 3: Point the readiness probes at /ready**

In `k8s/base/tools-tietue/deployment.yaml` and `k8s/base/tools-ruutu/deployment.yaml`, change the `readinessProbe.httpGet.path` from `/health` to `/ready` (leave livenessProbe on `/health`). Do NOT touch koti/verkko/web manifests. `yamllint -c .yamllint.yaml` the two files.

- [x] **Step 4: Build, test, lint, commit**

```bash
dotnet build toimi.sln && dotnet test toimi.sln
bash scripts/lint.sh
git add src/toimi.core src/toimi.tools.tietue src/toimi.tools.koti src/toimi.tools.verkko src/toimi.tools.ruutu k8s/base/tools-tietue k8s/base/tools-ruutu
git commit -m "refactor: share tool-server MCP bootstrap in core; add DB readiness probes"
```

---

## Task 5: Timezone-aware recurrence with creation-time default (item 3)

Context: `tz` is parsed and advertised but `RecurrenceCalculator` expands purely in UTC, so recurring wall-clock rules DST-drift by an hour. Make the calculator tz-aware and stamp a default tz at creation.

**Files:**
- Modify: `src/toimi.core/Configuration/ToimiOptions.cs` (add `UserTimeZone`)
- Modify: `src/toimi.tools.tietue/Scheduling/RecurrenceCalculator.cs` (tz param)
- Modify: `src/toimi.tools.tietue/Scheduling/Schedules.cs` (thread tz; add default-stamping helper)
- Modify: `src/toimi.tools.tietue/Scheduling/TriggerRepository.cs` (stamp default tz at create; inject config)
- Modify: `src/toimi.tools.tietue/Program.cs` (TriggerRepository already scoped; ToimiConfiguration already singleton — verify DI)
- Test: `src/toimi.tools.tietue.Tests/RecurrenceCalculatorTests.cs` (extend), `SchedulesTests.cs` (extend)

- [x] **Step 1: Add the config knob**

In `ToimiConfiguration` (`ToimiOptions.cs`):

```csharp
  /// <summary>IANA tz stamped onto recurring triggers that omit their own tz, so wall-clock rules survive DST.</summary>
  public string UserTimeZone { get; set; } = "Europe/Helsinki";
```

- [x] **Step 2: Failing DST test**

Extend `RecurrenceCalculatorTests.cs` with a tz-aware overload test that crosses a DST boundary. Helsinki springs forward 2026-03-29 03:00. A `FREQ=DAILY` rule at `2026-03-27T09:00` local should fire at 07:00Z on the 28th (before) and 06:00Z on the 30th (after) — the UTC instant shifts by the DST hour while wall-clock stays 09:00:

```csharp
  [Fact]
  public void Daily_rule_in_a_timezone_keeps_wall_clock_across_dst()
  {
    // 2026-03-29 Helsinki springs forward (EET+2 -> EEST+3).
    var start = new DateTimeOffset(2026, 3, 27, 9, 0, 0, TimeSpan.FromHours(2)); // 09:00 local, 07:00Z
    var beforeDst = RecurrenceCalculator.NextOccurrenceAfter(
      start, "FREQ=DAILY", new DateTimeOffset(2026, 3, 27, 12, 0, 0, TimeSpan.Zero), "Europe/Helsinki");
    var afterDst = RecurrenceCalculator.NextOccurrenceAfter(
      start, "FREQ=DAILY", new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.Zero), "Europe/Helsinki");

    Assert.Equal(new DateTimeOffset(2026, 3, 28, 7, 0, 0, TimeSpan.Zero), beforeDst!.Value.ToUniversalTime());
    Assert.Equal(new DateTimeOffset(2026, 3, 30, 6, 0, 0, TimeSpan.Zero), afterDst!.Value.ToUniversalTime());
  }
```

Run → FAIL to compile (the `tz` param doesn't exist).

- [x] **Step 3: Make `RecurrenceCalculator` tz-aware**

Add an optional `string? tz = null` parameter to `NextOccurrenceAfter`, `NextOccurrenceOnOrAfter`, and `FirstOccurrence`. In `FirstOccurrence`, when `tz` resolves to a `TimeZoneInfo` (`TimeZoneInfo.FindSystemTimeZoneById(tz)` — .NET 10 accepts IANA ids cross-platform), build the `CalDateTime` with the tzid so `Ical.Net` expands in wall-clock:

```csharp
  private static DateTimeOffset? FirstOccurrence(DateTimeOffset start, string rrule, DateTimeOffset after, bool inclusive, string? tz = null)
  {
    var tzInfo = ResolveTz(tz);
    var startCal = tzInfo is null
      ? new CalDateTime(start.UtcDateTime)
      : new CalDateTime(TimeZoneInfo.ConvertTime(start, tzInfo).DateTime, tz);

    var calendar = new Calendar();
    calendar.Events.Add(new CalendarEvent
    {
      Start = startCal,
      Duration = System.TimeSpan.FromHours(1),
      RecurrenceRules = [new RecurrencePattern(rrule)],
    });

    var windowBase = after < start ? start : after;
    var from = windowBase.AddSeconds(-1).UtcDateTime;
    var to = windowBase.Add(Window).UtcDateTime;

    return calendar.GetOccurrences(new CalDateTime(from), new CalDateTime(to))
      .Select(o => o.Period.StartTime.AsDateTimeOffset)
      .Where(o => inclusive ? o >= after : o > after)
      .OrderBy(o => o)
      .Cast<DateTimeOffset?>()
      .FirstOrDefault();
  }

  private static TimeZoneInfo? ResolveTz(string? tz)
  {
    if (string.IsNullOrWhiteSpace(tz))
    {
      return null;
    }
    try
    {
      return TimeZoneInfo.FindSystemTimeZoneById(tz);
    }
    catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
    {
      return null; // unknown tz → fall back to UTC expansion rather than throwing
    }
  }
```

VERIFY against the installed `Ical.Net` version: the `CalDateTime(DateTime, string tzId)` ctor, `Duration`, `AsDateTimeOffset`, and `GetOccurrences` signatures. Adapt the exact API while keeping the behavior (expand in-zone, return UTC-comparable offsets). If `AsDateTimeOffset` yields a wall-clock-with-offset value, `.ToUniversalTime()` before comparing to `after` — make the comparison UTC-correct. Run the DST test → PASS.

- [x] **Step 4: Thread tz through `Schedules`**

`Schedules.InitialNextFireAt`/`NextAfter` already `Parse` a `Spec` with `Tz`. Pass `spec.Tz` into the `RecurrenceCalculator` calls. Add a public helper used by trigger creation to stamp a default tz:

```csharp
  /// <summary>Returns the schedule JSON with a default tz stamped onto recurring specs that omit one.</summary>
  public static string WithDefaultTimeZone(string scheduleJson, string defaultTz)
  {
    var spec = Parse(scheduleJson);
    if (spec is null || spec.Rrule is null || !string.IsNullOrEmpty(spec.Tz))
    {
      return scheduleJson; // one-shot, unparseable, or already has a tz
    }
    try
    {
      var node = System.Text.Json.Nodes.JsonNode.Parse(scheduleJson)!.AsObject();
      node["tz"] = defaultTz;
      return node.ToJsonString();
    }
    catch (System.Text.Json.JsonException)
    {
      return scheduleJson;
    }
  }
```

- [x] **Step 5: Stamp the default at creation**

In `TriggerRepository`, inject `ToimiConfiguration` (`public class TriggerRepository(TietueDbContext db, Toimi.Core.Configuration.ToimiConfiguration config)`), and in `CreateAsync`, before computing `InitialNextFireAt`, replace the incoming `scheduleJson` with `Schedules.WithDefaultTimeZone(scheduleJson, config.UserTimeZone)`. This covers BOTH creation paths (provisioner and `set_trigger`) since both go through `CreateAsync`. Confirm `ToimiConfiguration` is DI-registered in tietue's `Program.cs` (it is — used by the agent runner) and that `TriggerRepository` construction sites in tests get updated (pass a `new ToimiConfiguration { OpenAI = ... , UserTimeZone = "Europe/Helsinki" }` or a test default — grep test construction sites).

- [x] **Step 6: Test the stamping + run full suite**

Add a `SchedulesTests` case: `WithDefaultTimeZone` on `{"start":...,"rrule":"FREQ=DAILY"}` adds `tz`; on `{"at":...}` and on a spec that already has `tz`, returns unchanged. Then:

```bash
dotnet test src/toimi.tools.tietue.Tests
dotnet format src/toimi.core/toimi.core.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
git add src/toimi.core src/toimi.tools.tietue src/toimi.tools.tietue.Tests
git commit -m "fix(tietue): expand recurring triggers in their timezone, defaulting to the user's at creation"
```

---

## Task 6: Atomic entity create + provisioning + expiry (item 4)

Context: entity + unique-key + outbox commit together, but `ProvisionAsync` and `ExpiryReconciler` each run their own later `SaveChanges` with no repair path. A crash between leaves a reminder with no trigger, or a create-succeeded-but-provision-threw duplicate. Wrap the three in one transaction.

**Files:**
- Modify: `src/toimi.tools.tietue/Entities/EntityRepository.cs` (`CreateAsync`)
- Test: `src/toimi.tools.tietue.Tests/EntityRepositoryFailureTests.cs` (extend)

- [x] **Step 1: Failing test — provision failure rolls back the entity**

Extend `EntityRepositoryFailureTests.cs`. Use the InMemory provider's transaction-warning suppression OR a fake provisioner that throws. Simplest: inject a `TriggerProvisioner` whose dependency throws, and assert that after the exception, NO entity of that type exists (transaction rolled back). The InMemory provider does not support real transactions — `BeginTransactionAsync` is a no-op warning there. So test the transaction behavior at the seam instead: assert `CreateAsync` propagates the provisioner exception AND (with a relational-less fallback) that the outbox/entity are consistent. Given InMemory's limitation, the highest-value test is: a provisioner that throws causes `CreateAsync` to throw and the tool to see it. Add:

```csharp
  [Fact]
  public async Task Create_propagates_provisioning_failure()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("reminder", ReminderSchema, ReminderBehaviors);
    var throwingProvisioner = new TriggerProvisioner(new ThrowingTriggerRepository(db)); // fake that throws in CreateAsync
    var repo = new EntityRepository(db, new SchemaValidator(), outbox: null, provisioner: throwingProvisioner);

    await Assert.ThrowsAnyAsync<Exception>(() =>
      repo.CreateAsync("reminder", JsonNode.Parse("""{"dueAt":"2026-06-01T09:00:00Z"}"""), []));
  }
```

(Adapt to real ctor params — check `EntityRepository`'s constructor and `TriggerProvisioner`'s. If a throwing fake is awkward, this step can assert the wrap exists by reading; but prefer a behavioral test.) The transaction's ROLLBACK correctness is only fully exercised under Postgres — note that in the report; the InMemory test pins the exception propagation.

- [x] **Step 2: Wrap create in a transaction**

In `EntityRepository.CreateAsync`, wrap the entity-save + provision + expiry in a transaction that only executes when the provider is relational (InMemory throws on `BeginTransaction`):

```csharp
    var useTx = db.Database.IsRelational();
    var tx = useTx ? await db.Database.BeginTransactionAsync(ct) : null;
    try
    {
      db.Entities.Add(entity);
      var indexOp = outbox?.Enqueue(entity, typeDef.Behaviors, "upsert");
      await EnforceUniqueOnCreateAsync(entity, typeDef.Behaviors, ct);
      await SaveGuardingUniqueAsync(entity.Type, ct);

      if (provisioner is not null)
      {
        await provisioner.ProvisionAsync(entity, typeDef.DefaultTriggers, entity.CreatedAt, ct);
      }
      if (expiry is not null)
      {
        await expiry.ReconcileAsync(entity, typeDef.Behaviors, entity.CreatedAt, ct);
      }

      if (tx is not null)
      {
        await tx.CommitAsync(ct);
      }

      // Inline outbox drain AFTER commit so a Qdrant hiccup can't roll back the entity.
      if (outbox is not null)
      {
        await outbox.DrainAsync(indexOp, ct);
      }
      return entity;
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
```

IMPORTANT ordering notes: (a) preserve the existing pre-check/enqueue ordering from the current `CreateAsync` (unique pre-check before save — the Week-4 change-tracker-safety fix; read the current method and keep that intact). (b) The outbox DrainAsync stays OUTSIDE/AFTER the transaction commit — its whole design is that a Qdrant failure must not roll back the DB (it's exception-safe and leaves the row for the worker). Adapt the exact statements to the current method body; the shape above is the target. Also confirm `ExpiryReconciler.ReconcileAsync`'s internal two-SaveChanges now run inside this ambient transaction (they will, since they share the DbContext connection enlisted in `tx`).

- [x] **Step 3: Run full suite, format, commit**

```bash
dotnet test src/toimi.tools.tietue.Tests
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
git add src/toimi.tools.tietue src/toimi.tools.tietue.Tests
git commit -m "fix(tietue): commit entity create, provisioning, and expiry in one transaction"
```

---

## Task 7: Validate `set_trigger` inputs (item 6)

Context: a bad entity id surfaces as a raw `DbUpdateException`; an unknown `handlerKind` creates a trigger the scheduler logs as "no handler" and advances forever; a null-resolving schedule creates an `Enabled=true, NextFireAt=null` trigger that never fires — all returned to the agent with no error.

**Files:**
- Modify: `src/toimi.tools.tietue/Tools/SetTriggerTool.cs`
- Modify: `src/toimi.tools.tietue/Scripts/ScriptEffectApplier.cs` (the `trigger` effect path — same validation)
- Test: `src/toimi.tools.tietue.Tests/SetTriggerToolTests.cs` (new or extend an existing trigger-tool test file)

- [x] **Step 1: Failing tests**

Create/extend a test for `SetTriggerTool.SetTrigger` asserting readable error strings (not exceptions, not silent nulls) for: (a) unknown entity id → "entity not found"; (b) unknown `handlerKind` → lists valid kinds; (c) a schedule that resolves to null `NextFireAt` (e.g. `{"start":"2020-01-01T00:00:00Z","rrule":"FREQ=YEARLY;COUNT=1"}` fully in the past, or a malformed schedule) → "schedule does not resolve to a future fire time". Read the existing tietue tool tests for the harness (they construct repositories over `TestDb.New()`). The tool needs `EntityRepository`/`TietueDbContext` and `HandlerRegistry` to validate — see Step 2 for the new dependencies.

- [x] **Step 2: Add validation**

`SetTriggerTool` currently depends only on `TriggerRepository`. Add `TietueDbContext db` and `HandlerRegistry handlers` to its primary constructor. Before `CreateAsync`:

```csharp
    if (!Guid.TryParse(entityId, out var id))
    {
      return "Invalid entityId. Expected a GUID.";
    }
    if (!await db.Entities.AnyAsync(e => e.Id == id))
    {
      return $"No entity found with id {id}.";
    }
    if (handlers.Resolve(handlerKind) is null)
    {
      return $"Unknown handlerKind '{handlerKind}'. Valid kinds: {string.Join(", ", handlers.Kinds)}.";
    }
    if (Scheduling.Schedules.InitialNextFireAt(schedule, DateTimeOffset.UtcNow) is null)
    {
      return "Schedule does not resolve to a future fire time. Check the 'at'/'start'+'rrule' fields.";
    }
```

`HandlerRegistry` needs a `Kinds` accessor — add `public IReadOnlyCollection<string> Kinds => _byKind.Keys.ToList();` to it. (`using Microsoft.EntityFrameworkCore;` for `AnyAsync`.) `SetTriggerTool` is discovered by `WithToolsFromAssembly` and its deps resolved from DI — `TietueDbContext` (scoped) and `HandlerRegistry` (scoped) are already registered, so no Program.cs change beyond confirming.

- [x] **Step 3: Same guard on the script `trigger` effect**

`ScriptEffectApplier` (the `trigger` effect at ~line 33) creates triggers from sandboxed scripts with the same lack of validation. Apply the same three checks there (entity existence is implicit — the script acts on a known entity; still validate handlerKind and the resolved fire time), returning/logging a clear effect error rather than creating a dead trigger. Read the current method and match its effect-result style.

- [x] **Step 4: Run full suite, format, commit**

```bash
dotnet test src/toimi.tools.tietue.Tests
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
git add src/toimi.tools.tietue src/toimi.tools.tietue.Tests
git commit -m "fix(tietue): validate set_trigger inputs (entity, handler kind, resolvable schedule)"
```

---

## Task 8: LLM-provider seam with timeout/retry policy (item 9)

Context: `ToimiClientFactory.Create` hardcodes OpenAI with no configured timeout/retry, and has exactly two call sites. Introduce `ILlmClientProvider` and give the OpenAI impl explicit resilience.

**Files:**
- Create: `src/toimi.core/Llm/ILlmClientProvider.cs`, `src/toimi.core/Llm/OpenAiLlmClientProvider.cs`
- Modify: `src/toimi.core/ToimiClientFactory.cs` (delegate `Create` to the provider, or move construction into the provider)
- Modify: `src/toimi.web/Program.cs` + `src/toimi.web/Hubs/ToimiHub.cs` (inject provider), `src/toimi.tools.tietue/Program.cs` + `src/toimi.tools.tietue/Agents/AgentRunner.cs` (inject provider)
- Modify: `src/toimi.core/Configuration/ToimiOptions.cs` (timeout/retry knobs)

- [x] **Step 1: Config knobs**

In `OpenAIOptions`:

```csharp
  /// <summary>Per-request network timeout for LLM calls.</summary>
  public int NetworkTimeoutSeconds { get; set; } = 100;
  /// <summary>Max transient retries (429/5xx) at the SDK pipeline layer.</summary>
  public int MaxRetries { get; set; } = 3;
```

- [x] **Step 2: The interface + OpenAI impl**

`src/toimi.core/Llm/ILlmClientProvider.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Toimi.Core.Llm;

/// <summary>Constructs the chat client + tool-call notifier for a session or agent run.</summary>
public interface ILlmClientProvider
{
  (IChatClient Client, ToolCallNotifier Notifier) Create();
}
```

`src/toimi.core/Llm/OpenAiLlmClientProvider.cs`:

```csharp
using Microsoft.Extensions.AI;
using OpenAI;
using Toimi.Core.Configuration;

namespace Toimi.Core.Llm;

public sealed class OpenAiLlmClientProvider(ToimiConfiguration config) : ILlmClientProvider
{
  public (IChatClient Client, ToolCallNotifier Notifier) Create()
  {
    var options = new OpenAIClientOptions
    {
      NetworkTimeout = TimeSpan.FromSeconds(config.OpenAI.NetworkTimeoutSeconds),
    };
    options.RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(config.OpenAI.MaxRetries);

    var openAiClient = new OpenAIClient(new System.ClientModel.ApiKeyCredential(config.OpenAI.ApiKey), options);
    var inner = openAiClient.GetChatClient(config.OpenAI.Model).AsIChatClient();
    var notifier = new ToolCallNotifier(inner);
    var client = new ChatClientBuilder(notifier).UseFunctionInvocation().Build();
    return (client, notifier);
  }
}
```

VERIFY the OpenAI SDK API: the `OpenAIClientOptions.NetworkTimeout`/`RetryPolicy` property names and the `ClientRetryPolicy(maxRetries)` ctor against the installed `OpenAI` package version. Adapt exact names; the intent is explicit timeout + bounded retries. If `RetryPolicy` isn't settable that way, use the SDK's documented mechanism for the installed version and note it.

- [x] **Step 3: Keep `ToimiClientFactory.Create` as a thin shim, register in DI**

Leave `ToimiClientFactory`'s static message-assembly helpers alone. Change `Create(ToimiConfiguration)` to delegate — or (cleaner) delete `Create` and update the two call sites to use the injected provider. Recommended minimal change: keep a `Create(ToimiConfiguration)` that news up `OpenAiLlmClientProvider(config).Create()` so nothing else breaks, AND register `services.AddSingleton<ILlmClientProvider, OpenAiLlmClientProvider>()` in both `src/toimi.web/Program.cs` and `src/toimi.tools.tietue/Program.cs`. Then inject `ILlmClientProvider` into `ToimiHub` (replace `ToimiClientFactory.Create(_config)` at the OnConnected site with `_llmProvider.Create()`) and `AgentRunner` (replace the `ToimiClientFactory.Create(config)` call). This gives the seam + resilience with the smallest blast radius. Document in the commit which call sites moved to the injected provider.

- [x] **Step 4: Build, test, format, commit**

```bash
dotnet build toimi.sln && dotnet test toimi.sln
dotnet format src/toimi.core/toimi.core.csproj --verify-no-changes
dotnet format src/toimi.web/toimi.web.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
git add src/toimi.core src/toimi.web src/toimi.tools.tietue
git commit -m "refactor(core): introduce ILlmClientProvider with configurable timeout and retry"
```

---

## Task 9: Harden the hub's failure paths (item 5)

Context: `CompactIfNeeded` runs before `SendMessage`'s try (a thrown summarization kills the turn with dirty state), and the catch's blind `RemoveAt(Count-1)` strips the already-persisted user message when streaming throws early.

**Files:**
- Modify: `src/toimi.core/ContextManager.cs` (try/catch fallback + optional timeout)
- Modify: `src/toimi.web/Hubs/ToimiHub.cs` (move compaction inside try; explicit rollback intent)
- Test: `src/toimi.core.Tests/ContextManagerTests.cs` (extend — summarization-failure fallback)

- [x] **Step 1: Failing test — summarization failure degrades to uncompacted**

Extend `ContextManagerTests.cs`. The existing `FakeChatClient` returns a canned response; add a mode where `GetResponseAsync` throws, and assert `CompactIfNeeded` returns `false` (proceeds uncompacted) instead of throwing, leaving `messages` unchanged:

```csharp
  [Fact]
  public async Task Compaction_that_fails_to_summarize_proceeds_uncompacted()
  {
    var client = new FakeChatClient { Throw = true }; // add a Throw flag to the fake
    var messages = BuildOverBudgetHistory(); // enough to trigger compaction
    var before = messages.Count;

    var compacted = await ContextManager.CompactIfNeeded(messages, client, budget: null, maxTokens: 1, ct: default);

    Assert.False(compacted);
    Assert.Equal(before, messages.Count); // untouched on failure
  }
```

Run → FAIL (currently throws).

- [x] **Step 2: Wrap summarization in `ContextManager`**

Around the `client.GetResponseAsync(summaryMessages, ...)` call, add a try/catch and an optional timeout:

```csharp
    string summary;
    try
    {
      using var summaryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
      summaryCts.CancelAfter(TimeSpan.FromSeconds(30));
      var response = await client.GetResponseAsync(summaryMessages, cancellationToken: summaryCts.Token);
      summary = response.Text ?? "Earlier conversation summary unavailable.";
    }
    catch (Exception)
    {
      // Summarization failed/timed out: proceed uncompacted. An over-budget prompt the
      // provider trims is strictly better than dropping the user's turn.
      return false;
    }
```

Leave the message-splice logic after this unchanged.

- [x] **Step 3: Move compaction inside the hub's try + fix the rollback**

In `ToimiHub.SendMessage`: move the `await ContextManager.CompactIfNeeded(...)` call (currently line ~111, before the `try` at ~113) to just INSIDE the `try`. And replace the catch's blind removal with intent tracking:

```csharp
    var assistantAppended = false;
    try
    {
      await ContextManager.CompactIfNeeded(session.Messages, session.ChatClient, session.Budget, _config.MaxContextTokens, Context.ConnectionAborted);
      // ...streaming...
      session.Messages.Add(new(ChatRole.Assistant, responseText));
      assistantAppended = true;
      // ...persist...
    }
    catch (Exception ex)
    {
      if (assistantAppended)
      {
        session.Messages.RemoveAt(session.Messages.Count - 1); // only remove what we added
      }
      await Clients.Caller.SendAsync("Error", ex.Message);
    }
```

The already-persisted user message stays in-memory (matches the DB). Read the current method to place `assistantAppended = true` immediately after the assistant `Add`, and keep every other line.

- [x] **Step 4: Run suites, format, commit**

```bash
dotnet test src/toimi.core.Tests src/toimi.web.Tests
dotnet format src/toimi.core/toimi.core.csproj --verify-no-changes
dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj --verify-no-changes
dotnet format src/toimi.web/toimi.web.csproj --verify-no-changes
git add src/toimi.core src/toimi.core.Tests src/toimi.web
git commit -m "fix(web): summarization failure no longer kills a turn; rollback only removes what was added"
```

---

## Task 10: Lazy conversation creation (item 10)

Context: every no-param connect / reconnect / abandoned "New" writes a titleless conversation row that sorts to the top of the sidebar. Defer the row until the first message.

**Files:**
- Modify: `src/toimi.web/Hubs/ToimiHub.cs` (nullable session ConversationId; create-on-first-message; `ConversationCreated` event)
- Modify: `src/toimi.web/ClientApp/src/hooks/useToimi.ts` (handle `ConversationCreated`)
- Test: `src/toimi.web.Tests/` (a hub-logic test if a seam allows; otherwise document manual verification)

- [x] **Step 1: Make the session's ConversationId nullable and defer creation**

In `ToimiHub`, change the `ToimiSession` record's `ConversationId` from `Guid` to `Guid?`. In `OnConnectedAsync`, the no-param branch (and `NewConversation`) should NOT call `_repository.CreateAsync()` — set `ConversationId = null` and send the client an empty state (no `ConversationLoaded` with a real id, or a distinct "new/empty" signal). The `?conversationId=` branch that loads an existing conversation is unchanged.

In `SendMessage`, before the first `AddMessageAsync`, create the row lazily:

```csharp
    if (session.ConversationId is null)
    {
      var created = await _repository.CreateAsync();
      session = session with { ConversationId = created.Id };
      Sessions[Context.ConnectionId] = session;
      await Clients.Caller.SendAsync("ConversationCreated", created.Id);
    }
```

Then use `session.ConversationId!.Value` for the `AddMessageAsync`/`UpdateTitleAsync` calls. (Records are immutable — the `with` + dictionary reassign is how the session gets its id; confirm `ToimiSession` is a `record` and `Sessions` is the `ConcurrentDictionary`.) `NewConversation` becomes: clear in-memory messages, set `ConversationId = null`, emit the empty state — no DB row.

- [x] **Step 2: Client learns its id via `ConversationCreated`**

In `useToimi.ts`, add a handler mirroring the existing `ConversationLoaded` id-capture: `connection.on('ConversationCreated', (id: string) => { setCurrentConversationId(id); currentConversationIdRef.current = id })`. This keeps the reconnect-resync (Week 4) working: once the first message creates the row and the client learns the id, a later reconnect rebuilds with `?conversationId=<id>`. Read the hook's existing `ConversationLoaded` handler and mirror its state/ref updates exactly. Confirm the sidebar list (`ListConversations`) now only shows conversations that have messages (they will — empty ones no longer exist).

- [x] **Step 3: Verify, lint, build, commit**

```bash
dotnet test toimi.sln
cd src/toimi.web/ClientApp && npm run lint && npm run build && cd -
dotnet format src/toimi.web/toimi.web.csproj --verify-no-changes
git add src/toimi.web
git commit -m "fix(web): create conversations lazily on first message to stop orphan-row growth"
```

---

## Final verification

- [x] `bash scripts/lint.sh && dotnet test toimi.sln` — all green.
- [x] `cd src/toimi.web/ClientApp && npm run lint && npm run build` — clean.
- [x] `git status` clean; commits follow convention.
- [x] Completion report to the user MUST note: (a) the two security fixes and that ruutu/verkko need redeploy; (b) tietue needs redeploy (tz recurrence, atomic create, set_trigger validation) — NO new migration; (c) `UserTimeZone` config default is Europe/Helsinki, override via `Toimi:UserTimeZone` if needed; (d) the `/ready` readiness probes change the tietue+ruutu manifests (apply via deploy); (e) the LLM timeout/retry defaults (100s/3) are new `Toimi:OpenAI` knobs.
