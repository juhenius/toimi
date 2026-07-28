# Week 1 Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the five highest-risk gaps from the architecture review: no CI, SSRF in verkko's `fetch_url`, scheduler double-fire during deploys, unbounded agent runs, and invisible failures (Console logging / unlogged handler errors).

**Architecture:** Five independent hardening changes, no new services. (1) A GitHub Actions workflow mirroring `scripts/lint.sh` + `dotnet test`. (2) An SSRF guard in verkko implemented at the socket layer (`SocketsHttpHandler.ConnectCallback`) so redirects and DNS tricks are covered, plus a friendly pre-check in the tool. (3) A PostgreSQL advisory lock (`ITickLock`) serializing scheduler ticks across pods, plus `strategy: Recreate` on the tietue deployment. (4) A config-driven timeout wrapping the whole agent run in `AgentRunner`. (5) `ILogger` replacing `Console.*` in `McpToolAggregator`/`ResilientMcpTool` and error/fire logging in `SchedulerTick`.

**Tech Stack:** .NET 10, xUnit + EF InMemory (existing test pattern), GitHub Actions, PostgreSQL advisory locks, Kustomize manifests.

**Conventions that apply to every task:** 2-space indent, file-scoped namespaces, block bodies (IDE0022 is enforced as an error — do NOT use expression-bodied methods). After each implementation step, run `dotnet format <csproj> --verify-no-changes`; if it fails, run `dotnet format <csproj>` to fix and re-verify. All commands run from the repo root `/Users/jari/private/toimi`.

---

## Task 1: Verify baseline, then add CI workflow

The repo has no CI at all (no `.github/` directory). Add a GitHub Actions workflow that runs the same checks as `scripts/lint.sh` plus `dotnet test` and the frontend build. First verify the baseline passes locally so CI is green from day one.

**Files:**
- Create: `.github/workflows/ci.yml`

- [x] **Step 1: Verify the local baseline passes**

Run:
```bash
bash scripts/lint.sh && dotnet test toimi.sln
```
Expected: `=== Lint passed ===` and all tests pass.

If `dotnet format` reports violations: run `bash scripts/lint.sh --fix`, re-run the check, and commit the formatting fixes separately first:
```bash
git add -A && git commit -m "chore: apply dotnet format fixes"
```

- [x] **Step 2: Create the workflow file**

Create `.github/workflows/ci.yml` with exactly this content (mind yamllint: 2-space indent, no long lines):

```yaml
name: ci

on:
  push:
    branches: [main]
  pull_request:

permissions:
  contents: read

jobs:
  dotnet:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - run: dotnet restore toimi.sln
      - run: dotnet format toimi.sln --verify-no-changes --no-restore --verbosity minimal
      - run: dotnet build toimi.sln --no-restore
      - run: dotnet test toimi.sln --no-build --verbosity normal

  frontend:
    runs-on: ubuntu-latest
    defaults:
      run:
        working-directory: src/toimi.web/ClientApp
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: 24
          cache: npm
          cache-dependency-path: src/toimi.web/ClientApp/package-lock.json
      - run: npm ci
      - run: npm run lint
      - run: npm run build

  yaml:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - run: pip install yamllint
      - run: yamllint -c .yamllint.yaml .

  shell:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - run: sudo apt-get update && sudo apt-get install -y shellcheck
      - run: find scripts -name '*.sh' -print0 | xargs -0 shellcheck
```

- [x] **Step 3: Verify the workflow file passes yamllint**

Run:
```bash
yamllint -c .yamllint.yaml .github/workflows/ci.yml
```
Expected: no output (pass). If yamllint is not installed locally, run `pip install yamllint` first.

- [x] **Step 4: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add GitHub Actions workflow (format, build, test, frontend, yamllint, shellcheck)"
```

---

## Task 2: verkko test project + `UrlGuard` (TDD)

verkko has no test project. Create one, then TDD the SSRF guard predicate. The IP-range logic intentionally mirrors ruutu's `ScribanRenderer.IsPrivate` (`src/toimi.tools.ruutu/Rendering/ScribanRenderer.cs:50-76`) — a conscious ~30-line duplication rather than making verkko depend on toimi.core (which drags in EF/MCP/AI packages) for an IP check.

**Files:**
- Create: `src/toimi.tools.verkko.Tests/toimi.tools.verkko.Tests.csproj`
- Create: `src/toimi.tools.verkko.Tests/UrlGuardTests.cs`
- Create: `src/toimi.tools.verkko/Fetcher/UrlGuard.cs`
- Modify: `toimi.sln` (via `dotnet sln add`)

- [x] **Step 1: Create the test project**

Create `src/toimi.tools.verkko.Tests/toimi.tools.verkko.Tests.csproj`:

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
    <ProjectReference Include="../toimi.tools.verkko/toimi.tools.verkko.csproj" />
  </ItemGroup>

</Project>
```

Then register it:
```bash
dotnet sln toimi.sln add src/toimi.tools.verkko.Tests/toimi.tools.verkko.Tests.csproj
```

- [x] **Step 2: Write the failing tests**

Create `src/toimi.tools.verkko.Tests/UrlGuardTests.cs`:

```csharp
using System.Net;
using toimi.tools.verkko.Fetcher;
using Xunit;

namespace toimi.tools.verkko.Tests;

public class UrlGuardTests
{
  [Theory]
  [InlineData("10.0.0.1")]
  [InlineData("127.0.0.1")]
  [InlineData("0.0.0.0")]
  [InlineData("100.64.0.1")]
  [InlineData("169.254.1.1")]
  [InlineData("172.16.0.1")]
  [InlineData("172.31.255.255")]
  [InlineData("192.168.1.1")]
  [InlineData("::1")]
  [InlineData("fc00::1")]
  [InlineData("fe80::1")]
  [InlineData("::ffff:10.0.0.1")]
  public void IsPrivate_true_for_internal_addresses(string ip)
  {
    Assert.True(UrlGuard.IsPrivate(IPAddress.Parse(ip)));
  }

  [Theory]
  [InlineData("93.184.216.34")]
  [InlineData("172.32.0.1")]
  [InlineData("100.128.0.1")]
  [InlineData("2606:4700::1111")]
  public void IsPrivate_false_for_public_addresses(string ip)
  {
    Assert.False(UrlGuard.IsPrivate(IPAddress.Parse(ip)));
  }

  [Theory]
  [InlineData("localhost")]
  [InlineData("router")]
  [InlineData("qdrant")]
  [InlineData("192.168.1.1")]
  [InlineData("::1")]
  [InlineData("")]
  public void IsBlockedHost_true_for_internal_hosts(string host)
  {
    Assert.True(UrlGuard.IsBlockedHost(host));
  }

  [Theory]
  [InlineData("example.com")]
  [InlineData("api.github.com")]
  public void IsBlockedHost_false_for_public_hostnames(string host)
  {
    Assert.False(UrlGuard.IsBlockedHost(host));
  }
}
```

- [x] **Step 3: Run tests to verify they fail**

Run:
```bash
dotnet test src/toimi.tools.verkko.Tests
```
Expected: FAIL to compile — `UrlGuard` does not exist.

- [x] **Step 4: Implement `UrlGuard`**

Create `src/toimi.tools.verkko/Fetcher/UrlGuard.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace toimi.tools.verkko.Fetcher;

/// <summary>
/// SSRF guard for outbound fetches: rejects hosts that are loopback, private,
/// link-local, CGNAT, or otherwise not externally routable. The IP-range logic
/// mirrors ruutu's ScribanRenderer.SafeUrl checks, adapted for the fetcher
/// (http is allowed here; scheme policy lives in FetchUrlTool).
/// </summary>
public static class UrlGuard
{
  public static bool IsBlockedHost(string host)
  {
    if (string.IsNullOrWhiteSpace(host))
    {
      return true;
    }
    if (IPAddress.TryParse(host, out var ip))
    {
      return IsPrivate(ip);
    }
    // Single-label hostname (router, localhost, cluster service) — not externally routable.
    return !host.Contains('.');
  }

  public static bool IsPrivate(IPAddress ip)
  {
    if (IPAddress.IsLoopback(ip))
    {
      return true;
    }

    if (ip.AddressFamily == AddressFamily.InterNetworkV6)
    {
      if (ip.IsIPv6LinkLocal)
      {
        return true;
      }
      var b6 = ip.GetAddressBytes();
      if ((b6[0] & 0xFE) == 0xFC)
      {
        return true; // fc00::/7 unique-local
      }
      if (ip.IsIPv4MappedToIPv6)
      {
        return IsPrivate(ip.MapToIPv4()); // unwrap ::ffff:a.b.c.d
      }
      return false;
    }

    if (ip.AddressFamily == AddressFamily.InterNetwork)
    {
      var b = ip.GetAddressBytes();
      return b[0] == 0                                  // 0.0.0.0/8 (unspecified)
          || b[0] == 10                                 // 10/8
          || b[0] == 127                                // 127/8 loopback
          || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // 100.64/10 CGNAT (RFC 6598)
          || (b[0] == 169 && b[1] == 254)               // 169.254/16 link-local
          || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)  // 172.16/12
          || (b[0] == 192 && b[1] == 168);              // 192.168/16
    }

    return false;
  }
}
```

- [x] **Step 5: Run tests to verify they pass**

Run:
```bash
dotnet test src/toimi.tools.verkko.Tests
```
Expected: all UrlGuardTests PASS.

- [x] **Step 6: Format check and commit**

```bash
dotnet format src/toimi.tools.verkko/toimi.tools.verkko.csproj --verify-no-changes
dotnet format src/toimi.tools.verkko.Tests/toimi.tools.verkko.Tests.csproj --verify-no-changes
git add toimi.sln src/toimi.tools.verkko.Tests src/toimi.tools.verkko/Fetcher/UrlGuard.cs
git commit -m "feat(verkko): add UrlGuard SSRF predicate with tests"
```

---

## Task 3: Enforce the guard at the socket layer + tool pre-check (TDD)

Two enforcement points: a `ConnectCallback` on the fetcher's `SocketsHttpHandler` (covers redirects to internal hosts and DNS resolving to private IPs — the callback fires for every connection HttpClient opens, including redirect targets), and a pre-check in `FetchUrlTool` for a friendly agent-facing message on obvious cases.

**Files:**
- Modify: `src/toimi.tools.verkko/Fetcher/UrlGuard.cs` (add `GuardedConnectAsync`)
- Modify: `src/toimi.tools.verkko/Program.cs`
- Modify: `src/toimi.tools.verkko/Tools/FetchUrlTool.cs`
- Create: `src/toimi.tools.verkko.Tests/FetchGuardTests.cs`

- [x] **Step 1: Write the failing tests**

Create `src/toimi.tools.verkko.Tests/FetchGuardTests.cs`:

```csharp
using toimi.tools.verkko.Fetcher;
using toimi.tools.verkko.Tools;
using Xunit;

namespace toimi.tools.verkko.Tests;

public class FetchGuardTests
{
  [Fact]
  public async Task FetchUrl_rejects_private_ip_literal_before_fetching()
  {
    var tool = new FetchUrlTool(new WebFetcher(new HttpClient()), new FetchCache());

    var result = await tool.FetchUrl("http://192.168.1.10/admin");

    Assert.Contains("private or internal", result);
  }

  [Fact]
  public async Task FetchUrl_rejects_single_label_host()
  {
    var tool = new FetchUrlTool(new WebFetcher(new HttpClient()), new FetchCache());

    var result = await tool.FetchUrl("http://qdrant:6333/collections");

    Assert.Contains("private or internal", result);
  }

  [Fact]
  public async Task GuardedHandler_blocks_connections_to_private_literals()
  {
    // The callback rejects before any socket is opened, so no network is needed.
    using var client = new HttpClient(new SocketsHttpHandler { ConnectCallback = UrlGuard.GuardedConnectAsync });

    var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://10.11.12.13:8080/"));

    var messages = ex.Message + " " + (ex.InnerException?.Message ?? "");
    Assert.Contains("private or internal", messages);
  }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run:
```bash
dotnet test src/toimi.tools.verkko.Tests
```
Expected: FAIL to compile — `GuardedConnectAsync` does not exist. (The two tool tests would also fail: today `FetchUrl` happily fetches those URLs.)

- [x] **Step 3: Add `GuardedConnectAsync` to `UrlGuard`**

Add these members to the `UrlGuard` class in `src/toimi.tools.verkko/Fetcher/UrlGuard.cs` (inside the class, after `IsPrivate`), and add `using System.Net.Http;` only if the compiler asks (ImplicitUsings should cover it):

```csharp
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
      ? new[] { literal }
      : await Dns.GetHostAddressesAsync(host, ct);

    var routable = addresses.Where(ip => !IsPrivate(ip)).ToArray();
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
```

- [x] **Step 4: Wire the handler in `Program.cs`**

In `src/toimi.tools.verkko/Program.cs`, replace the existing `AddHttpClient<WebFetcher>` registration:

```csharp
builder.Services.AddHttpClient<WebFetcher>(client =>
{
  client.Timeout = TimeSpan.FromSeconds(15);
  client.DefaultRequestHeaders.UserAgent.ParseAdd("Toimi/1.0 (personal assistant)");
});
```

with:

```csharp
builder.Services.AddHttpClient<WebFetcher>(client =>
{
  client.Timeout = TimeSpan.FromSeconds(15);
  client.DefaultRequestHeaders.UserAgent.ParseAdd("Toimi/1.0 (personal assistant)");
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
  // SSRF guard: validates every connection (incl. redirect targets) against private ranges.
  ConnectCallback = UrlGuard.GuardedConnectAsync
});
```

- [x] **Step 5: Add the pre-check in `FetchUrlTool`**

In `src/toimi.tools.verkko/Tools/FetchUrlTool.cs`, directly after the existing scheme check (the `if (!Uri.TryCreate...)` block ending at line 19), insert:

```csharp
    if (UrlGuard.IsBlockedHost(uri.DnsSafeHost))
    {
      return $"Blocked URL: '{uri.DnsSafeHost}' is a private or internal host.";
    }
```

- [x] **Step 6: Run tests to verify they pass**

Run:
```bash
dotnet test src/toimi.tools.verkko.Tests
```
Expected: all PASS (UrlGuardTests + FetchGuardTests).

Note: the DNS-resolution path of `GuardedConnectAsync` (public hostname resolving to a private IP) is exercised only via the IP-literal test — testing real DNS rebinding requires a controlled resolver and is out of scope.

- [x] **Step 7: Format check and commit**

```bash
dotnet format src/toimi.tools.verkko/toimi.tools.verkko.csproj --verify-no-changes
dotnet format src/toimi.tools.verkko.Tests/toimi.tools.verkko.Tests.csproj --verify-no-changes
git add src/toimi.tools.verkko src/toimi.tools.verkko.Tests
git commit -m "feat(verkko): block SSRF to private/internal hosts at socket layer and tool level"
```

---

## Task 4: Scheduler tick lock + handler error logging (TDD)

`SchedulerTick.RunDueAsync` has no concurrency control; during a rolling deploy two tietue pods briefly coexist and can both fire the same trigger. Add an `ITickLock` (PostgreSQL advisory lock) so only one instance processes a tick, and add `ILogger` so handler failures and fires are visible in pod logs. Both changes touch the same constructor, so they're one task.

**Files:**
- Create: `src/toimi.tools.tietue/Scheduling/ITickLock.cs`
- Create: `src/toimi.tools.tietue/Scheduling/PostgresTickLock.cs`
- Modify: `src/toimi.tools.tietue/Scheduling/SchedulerTick.cs`
- Modify: `src/toimi.tools.tietue/Program.cs:53` (registration)
- Create: `src/toimi.tools.tietue.Tests/SchedulerTickLockTests.cs`

- [x] **Step 1: Write the failing tests**

Create `src/toimi.tools.tietue.Tests/SchedulerTickLockTests.cs`. It mirrors the setup pattern in the existing `SchedulerTickTests.cs` (same helpers: `TestDb`, `FakeNotifier`):

```csharp
using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SchedulerTickLockTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""";

  private sealed class DeniedTickLock : ITickLock
  {
    public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
    {
      return Task.FromResult<IAsyncDisposable?>(null);
    }
  }

  private sealed class RecordingLease : IAsyncDisposable
  {
    public bool Disposed { get; private set; }

    public ValueTask DisposeAsync()
    {
      Disposed = true;
      return ValueTask.CompletedTask;
    }
  }

  private sealed class GrantedTickLock(RecordingLease lease) : ITickLock
  {
    public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
    {
      return Task.FromResult<IAsyncDisposable?>(lease);
    }
  }

  [Fact]
  public async Task Skips_all_triggers_when_lock_denied()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("reminder", Schema);
    var repo = new EntityRepository(db, new SchemaValidator());
    var notifier = new FakeNotifier();
    var registry = new HandlerRegistry([new NotifyHandler(notifier)]);
    var tick = new SchedulerTick(db, registry, new EntityEventStore(db), tickLock: new DeniedTickLock());

    var e = await repo.CreateAsync("reminder", JsonNode.Parse("""{"title":"Call"}"""), []);
    await new TriggerRepository(db).CreateAsync(
      e.Id, /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""", "notify",
      /*lang=json,strict*/ """{"titleTemplate":"{title}"}""",
      new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero));

    await tick.RunDueAsync(new DateTimeOffset(2026, 6, 1, 9, 1, 0, TimeSpan.Zero), default);

    Assert.Empty(notifier.Sent);
    var trigger = (await new TriggerRepository(db).ListByEntityAsync(e.Id))[0];
    Assert.True(trigger.Enabled);
    Assert.NotNull(trigger.NextFireAt);
  }

  [Fact]
  public async Task Releases_lease_after_processing()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("reminder", Schema);
    var notifier = new FakeNotifier();
    var registry = new HandlerRegistry([new NotifyHandler(notifier)]);
    var lease = new RecordingLease();
    var tick = new SchedulerTick(db, registry, new EntityEventStore(db), tickLock: new GrantedTickLock(lease));

    await tick.RunDueAsync(DateTimeOffset.UtcNow, default);

    Assert.True(lease.Disposed);
  }
}
```

Note for the implementer: check the exact signatures of `FakeNotifier` and `TriggerRepository.CreateAsync` in the existing `SchedulerTickTests.cs` in the same directory and match them — the code above follows what that file does today.

- [x] **Step 2: Run tests to verify they fail**

Run:
```bash
dotnet test src/toimi.tools.tietue.Tests --filter SchedulerTickLockTests
```
Expected: FAIL to compile — `ITickLock` does not exist and `SchedulerTick` has no `tickLock` parameter.

- [x] **Step 3: Create `ITickLock`**

Create `src/toimi.tools.tietue/Scheduling/ITickLock.cs`:

```csharp
namespace toimi.tools.tietue.Scheduling;

/// <summary>
/// Serializes scheduler ticks across concurrent tietue instances (e.g. the
/// overlap window during a deploy) so a due trigger fires exactly once.
/// </summary>
public interface ITickLock
{
  /// <summary>Returns a lease to dispose when done, or null when another instance holds the lock.</summary>
  Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct);
}
```

- [x] **Step 4: Create `PostgresTickLock`**

Create `src/toimi.tools.tietue/Scheduling/PostgresTickLock.cs`:

```csharp
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Scheduling;

public sealed class PostgresTickLock(TietueDbContext db) : ITickLock
{
  // Advisory locks are per-database, so the key only needs to be unique within the tietue DB.
  private const long LockKey = 7415011;

  public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken ct)
  {
    // Advisory locks are session-scoped: keep the connection open for the lease's
    // lifetime so the lock is held until released. EF ref-counts explicit opens,
    // so queries issued during the tick reuse this same connection/session.
    await db.Database.OpenConnectionAsync(ct);
    bool acquired;
    try
    {
      acquired = await ExecuteBoolAsync(db, $"SELECT pg_try_advisory_lock({LockKey})", ct);
    }
    catch
    {
      await db.Database.CloseConnectionAsync();
      throw;
    }

    if (!acquired)
    {
      await db.Database.CloseConnectionAsync();
      return null;
    }

    return new Lease(db);
  }

  private static async Task<bool> ExecuteBoolAsync(TietueDbContext db, string sql, CancellationToken ct)
  {
    var connection = db.Database.GetDbConnection();
    using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private sealed class Lease(TietueDbContext db) : IAsyncDisposable
  {
    public async ValueTask DisposeAsync()
    {
      try
      {
        await ExecuteBoolAsync(db, $"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
      }
      finally
      {
        await db.Database.CloseConnectionAsync();
      }
    }
  }
}
```

The `using Microsoft.EntityFrameworkCore;` import is needed for `GetDbConnection()`/`OpenConnectionAsync` extension methods — add it at the top if the compiler asks.

- [x] **Step 5: Update `SchedulerTick`**

Replace the full contents of `src/toimi.tools.tietue/Scheduling/SchedulerTick.cs` with:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Handlers;

namespace toimi.tools.tietue.Scheduling;

public class SchedulerTick(
  TietueDbContext db,
  HandlerRegistry handlers,
  EntityEventStore events,
  ILogger<SchedulerTick>? logger = null,
  ITickLock? tickLock = null)
{
  private readonly ILogger<SchedulerTick> _logger = logger ?? NullLogger<SchedulerTick>.Instance;

  public async Task RunDueAsync(DateTimeOffset now, CancellationToken ct)
  {
    IAsyncDisposable? lease = null;
    if (tickLock is not null)
    {
      lease = await tickLock.TryAcquireAsync(ct);
      if (lease is null)
      {
        _logger.LogDebug("Scheduler tick skipped: another instance holds the tick lock.");
        return;
      }
    }
    await using var _ = lease;

    var due = await db.Triggers
      .Where(t => t.Enabled && t.NextFireAt != null && t.NextFireAt <= now)
      .OrderBy(t => t.NextFireAt)
      .ToListAsync(ct);

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
            _logger.LogError(ex, "Handler {HandlerKind} failed for trigger {TriggerId} (entity {EntityId}).",
              trigger.HandlerKind, trigger.Id, trigger.EntityId);
          }

          _logger.LogInformation("Trigger {TriggerId} ({HandlerKind}) fired for entity {EntityId}: {Status}",
            trigger.Id, trigger.HandlerKind, trigger.EntityId, status);

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
  }
}
```

Note: `ILogger<T>` resolves via the implicit `Microsoft.Extensions.Logging` global using in ASP.NET projects; if the compiler complains, add `using Microsoft.Extensions.Logging;`. If `Trigger` has no `Id` property, check `src/toimi.tools.tietue/Data/Trigger.cs` for the key property name and use that.

- [x] **Step 6: Register the lock in `Program.cs`**

In `src/toimi.tools.tietue/Program.cs`, directly above the line
`builder.Services.AddScoped<toimi.tools.tietue.Scheduling.SchedulerTick>();` (line 53), add:

```csharp
builder.Services.AddScoped<toimi.tools.tietue.Scheduling.ITickLock, toimi.tools.tietue.Scheduling.PostgresTickLock>();
```

DI note: `SchedulerTick`'s optional `logger`/`tickLock` parameters are filled by the container because both `ILogger<SchedulerTick>` and `ITickLock` are registered — no other change needed.

- [x] **Step 7: Run the full tietue test suite**

Run:
```bash
dotnet test src/toimi.tools.tietue.Tests
```
Expected: all PASS — the two new lock tests plus every existing test (existing `SchedulerTickTests` construct `SchedulerTick` without the new optional parameters, which still compiles and behaves as before: no lock configured means the tick always runs).

- [x] **Step 8: Format check and commit**

```bash
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
git add src/toimi.tools.tietue src/toimi.tools.tietue.Tests
git commit -m "feat(tietue): serialize scheduler ticks with a Postgres advisory lock and log handler outcomes"
```

---

## Task 5: `Recreate` strategy on the tietue deployment

Even with the advisory lock, don't run two tietue pods side by side at all: tietue hosts the singleton scheduler, and `replicas: 1` with the default RollingUpdate still surges a second pod during every deploy. `Recreate` trades a few seconds of downtime (fine for single-user) for a hard guarantee.

**Files:**
- Modify: `k8s/base/tools-tietue/deployment.yaml:9`

- [x] **Step 1: Add the strategy block**

In `k8s/base/tools-tietue/deployment.yaml`, change:

```yaml
spec:
  replicas: 1
  selector:
```

to:

```yaml
spec:
  replicas: 1
  strategy:
    type: Recreate  # tietue hosts the singleton trigger scheduler: never run two pods side by side
  selector:
```

- [x] **Step 2: Verify the manifest renders and lints**

Run:
```bash
kubectl kustomize k8s/overlays/dev > /dev/null && echo "kustomize OK"
yamllint -c .yamllint.yaml k8s/base/tools-tietue/deployment.yaml
```
Expected: `kustomize OK` and no yamllint output. (If `kubectl kustomize` fails because overlay secrets are missing on this machine, `kubectl kustomize k8s/base` is an acceptable substitute.)

- [x] **Step 3: Commit**

```bash
git add k8s/base/tools-tietue/deployment.yaml
git commit -m "fix(tietue): use Recreate strategy so deploys never run two scheduler pods"
```

---

## Task 6: Timeout on agent runs

`AgentRunner.RunAsync` has no upper bound: a hung LLM call or MCP connect blocks the scheduler tick indefinitely (the trigger loop is sequential). Wrap the whole run in a linked cancellation token with a config-driven timeout.

No new unit test: `AgentRunner` constructs its aggregator and LLM client internally, so testing the timeout would require injecting a hangable client — a seam worth adding when `toimi.core.Tests` is created (out of Week 1 scope). Verification is compile + existing suites (which use `FakeAgentRunner` and are unaffected).

**Files:**
- Modify: `src/toimi.core/Configuration/ToimiConfiguration.cs` (the file defining `ToimiConfiguration`)
- Modify: `src/toimi.tools.tietue/Agents/AgentRunner.cs`

- [x] **Step 1: Add the config property**

In the file defining `ToimiConfiguration` (`src/toimi.core/Configuration/`), add one property to the class:

```csharp
public class ToimiConfiguration
{
  public required OpenAIOptions OpenAI { get; set; }
  public List<McpServerOptions> McpServers { get; set; } = [];

  /// <summary>Hard wall-clock cap for a headless agent run (MCP connect + LLM turns). </summary>
  public int AgentRunTimeoutSeconds { get; set; } = 300;
}
```

- [x] **Step 2: Apply the timeout in `AgentRunner`**

Replace the full contents of `src/toimi.tools.tietue/Agents/AgentRunner.cs` with:

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
using Toimi.Core;
using Toimi.Core.Configuration;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Agents;

public class AgentRunner(ToimiConfiguration config) : IAgentRunner
{
  public async Task<AgentRunResult> RunAsync(Entity entity, string prompt, CancellationToken ct = default)
  {
    // A hung LLM call or MCP connect must not stall the scheduler tick indefinitely.
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfter(TimeSpan.FromSeconds(config.AgentRunTimeoutSeconds));
    var token = timeoutCts.Token;

    try
    {
      await using var aggregator = new McpToolAggregator();
      await aggregator.ConnectAllAsync(config.McpServers, token);
      var tools = aggregator.GetAllTools();

      var skillSummary = await aggregator.CallToolAsync("list_skills", ct: token);
      var typeCatalog = await aggregator.CallToolAsync("list_types", ct: token);

      var (client, notifier) = ToimiClientFactory.Create(config);
      var options = ToimiClientFactory.CreateRequestOptions(tools);
      var messages = ToimiClientFactory.CreateInitialMessages(skillSummary, typeCatalog);

      messages.Add(new(ChatRole.System,
        $"You are acting on a '{entity.Type}' entity (id {entity.Id}). Its current data is:\n{entity.Data.RootElement.GetRawText()}\n" +
        "Use the tietue tools (create/update/search/set_trigger/...) to act on it; you may schedule your own next run with set_trigger on this entity id."));
      messages.Add(new(ChatRole.User, prompt));

      ToimiClientFactory.RefreshDynamicContext(messages);
      await ContextManager.CompactIfNeeded(messages, client, token);

      var response = await client.GetResponseAsync(messages, options, token);
      var responseText = response.Text ?? "";

      var toolCalls = new List<object>();
      while (notifier.TryDequeueEvent(out var evt))
      {
        toolCalls.Add(evt!);
      }

      var toolCallsJson = toolCalls.Count > 0 ? JsonSerializer.Serialize(toolCalls) : null;
      return new AgentRunResult(true, responseText, toolCallsJson, null);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
      return new AgentRunResult(false, "", null, $"Agent run timed out after {config.AgentRunTimeoutSeconds}s.");
    }
    catch (Exception ex)
    {
      return new AgentRunResult(false, "", null, ex.Message);
    }
  }
}
```

- [x] **Step 3: Build and run all tests**

Run:
```bash
dotnet build toimi.sln && dotnet test toimi.sln
```
Expected: build succeeds; all tests PASS (nothing constructs `ToimiConfiguration` in a way that requires the new property — it has a default).

- [x] **Step 4: Format check and commit**

```bash
dotnet format src/toimi.core/toimi.core.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
git add src/toimi.core src/toimi.tools.tietue/Agents/AgentRunner.cs
git commit -m "feat(tietue): cap agent runs with a configurable timeout (default 300s)"
```

---

## Task 7: `ILogger` in toimi.core MCP plumbing

`McpToolAggregator` and `ResilientMcpTool` log to `Console.WriteLine`/`Console.Error` — unstructured, unleveled, and invisible to any future log pipeline. Both are constructed manually (`new McpToolAggregator()`), so thread an optional `ILogger` through instead of DI.

**Files:**
- Modify: `src/toimi.core/McpToolAggregator.cs`
- Modify: `src/toimi.core/ResilientMcpTool.cs`
- Modify: `src/toimi.web/Hubs/ToimiHub.cs:12,22`
- Modify: `src/toimi.tools.tietue/Agents/AgentRunner.cs:9,15`

- [x] **Step 1: Add logger to `McpToolAggregator`**

In `src/toimi.core/McpToolAggregator.cs`:

Add imports at the top:
```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
```

Change the class declaration and add a field:
```csharp
public class McpToolAggregator(ILogger? logger = null) : IAsyncDisposable
{
  private readonly ILogger _logger = logger ?? NullLogger.Instance;
```

In `ConnectAllAsync`, pass the logger to the wrapper (line 27):
```csharp
        _wrappedTools.Add(new ResilientMcpTool(this, server.Name, tool, _logger));
```

In `ConnectOneAsync`, replace
```csharp
      Console.WriteLine($"  [{server.Name}] Connected, {toolMap.Count} tools discovered.");
```
with
```csharp
      _logger.LogInformation("MCP server {Server} connected, {ToolCount} tools discovered.", server.Name, toolMap.Count);
```
and replace
```csharp
      Console.Error.WriteLine($"  [{server.Name}] Failed to connect: {ex.Message}");
```
with
```csharp
      _logger.LogWarning(ex, "MCP server {Server} failed to connect.", server.Name);
```

- [x] **Step 2: Add logger to `ResilientMcpTool`**

In `src/toimi.core/ResilientMcpTool.cs`:

Add import:
```csharp
using Microsoft.Extensions.Logging;
```

Change the class declaration:
```csharp
internal sealed class ResilientMcpTool(McpToolAggregator aggregator, string serverName, AIFunction initialInner, ILogger logger) : AIFunction
```

Replace the three console lines in `InvokeCoreAsync`:
```csharp
      Console.Error.WriteLine($"  [{serverName}] Tool '{_toolName}' failed with transport error, reconnecting: {ex.Message}");
```
becomes
```csharp
      logger.LogWarning("MCP tool {Tool} on {Server} failed with transport error, reconnecting: {Error}", _toolName, serverName, ex.Message);
```

```csharp
        Console.Error.WriteLine($"  [{serverName}] Reconnect failed; surfacing original error for '{_toolName}'.");
```
becomes
```csharp
        logger.LogWarning("Reconnect to {Server} failed; surfacing original error for {Tool}.", serverName, _toolName);
```

```csharp
      Console.WriteLine($"  [{serverName}] Reconnected; retrying '{_toolName}'.");
```
becomes
```csharp
      logger.LogInformation("Reconnected to {Server}; retrying {Tool}.", serverName, _toolName);
```

- [x] **Step 3: Pass loggers at the two construction sites**

In `src/toimi.web/Hubs/ToimiHub.cs`, change the class declaration (line 12) from:
```csharp
public class ToimiHub(ToimiConfiguration config, ConversationRepository repository) : Hub
```
to:
```csharp
public class ToimiHub(ToimiConfiguration config, ConversationRepository repository, ILogger<ToimiHub> logger) : Hub
```
and change line 22 from `var aggregator = new McpToolAggregator();` to:
```csharp
      var aggregator = new McpToolAggregator(logger);
```
(`ILogger<T>` is available via ASP.NET implicit usings; add `using Microsoft.Extensions.Logging;` only if the build asks.)

In `src/toimi.tools.tietue/Agents/AgentRunner.cs`, change the class declaration from:
```csharp
public class AgentRunner(ToimiConfiguration config) : IAgentRunner
```
to:
```csharp
public class AgentRunner(ToimiConfiguration config, ILogger<AgentRunner>? logger = null) : IAgentRunner
```
and change `await using var aggregator = new McpToolAggregator();` to:
```csharp
      await using var aggregator = new McpToolAggregator(logger);
```

- [x] **Step 4: Build and run all tests**

Run:
```bash
dotnet build toimi.sln && dotnet test toimi.sln
```
Expected: build succeeds; all tests PASS. (If `toimi.web.Tests` constructs `ToimiHub` directly, pass `NullLogger<ToimiHub>.Instance` there — check `src/toimi.web.Tests/` and fix any construction sites.)

- [x] **Step 5: Format check and commit**

```bash
dotnet format src/toimi.core/toimi.core.csproj --verify-no-changes
dotnet format src/toimi.web/toimi.web.csproj --verify-no-changes
dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
git add src/toimi.core src/toimi.web src/toimi.tools.tietue
git commit -m "refactor(core): replace Console logging with ILogger in MCP aggregator and resilient tool"
```

---

## Final verification (after all tasks)

- [x] Run the full suite one last time:

```bash
bash scripts/lint.sh && dotnet test toimi.sln
```
Expected: `=== Lint passed ===`, all tests green.

- [x] Confirm the working tree is clean (`git status`) and all commits follow `<type>(<scope>): <subject>`.
