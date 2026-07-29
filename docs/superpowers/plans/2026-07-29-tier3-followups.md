# Tier 3 Follow-ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the four remaining follow-ups from the three-tier test-gap effort: bound error text that reaches the DB, stop a runaway script from pinning the scheduler tick, cover the last untested admin-hook branch, and get real integration coverage on `PostgresTickLock`.

**Architecture:** Branch base `wip` (Tiers 1-3 landed: 501 .NET tests + 6 frontend tests, all green). Two TDD fixes, one test-only addition, one new integration-test capability (Testcontainers + Postgres, docker-gated so the suite still runs where docker is absent).

**Environment (critical):** work from `/Users/jari/private/toimi/.claude/worktrees/tier3-followups` (branch `worktree-tier3-followups`). `mise exec dotnet -- dotnet <args>`; `mise exec node -- npm <args>` from `src/toimi.web/ClientApp`. Format every changed C# project (`dotnet format <csproj>` then `--verify-no-changes`) before each commit. Commits `<type>(<scope>): <subject>` + Co-Authored-By Claude line (blank line before it). 2-space indent, file-scoped namespaces.

**Baseline:** `dotnet test toimi.sln` → 501 passing (core 63, web 38, notifications 22, ruutu 105, tietue 221, verkko 26, koti 26). ClientApp `npm test` → 6.

**Verified environment facts** (do not re-derive): docker is available locally (29.2.1) and on CI's `ubuntu-latest`; CI runs `dotnet test toimi.sln` for every push/PR. `PostgresTickLock` uses ONLY raw SQL advisory-lock calls — it touches no tables, so an integration test needs a live Postgres but **no migrations and no schema**.

---

### Task 1: bound error text that reaches the database

**Why:** `NtfyClient` now embeds the upstream response body verbatim in its exception message (Tier 3). `SchedulerTick` catches any handler exception and serializes `ex.Message` into `EntityEvent.Result` (a jsonb column). A misbehaving proxy returning a large HTML error page would land whole in the DB, once per failed occurrence. Two cheap caps: one at the source, one as generic insurance for every handler.

**Files:**
- Modify: `src/toimi.notifications/NtfyClient.cs`
- Modify: `src/toimi.tools.tietue/Scheduling/SchedulerTick.cs`
- Modify: `src/toimi.notifications.Tests/NtfyClientTests.cs`
- Modify: `src/toimi.tools.tietue.Tests/SchedulerTickTests.cs`

- [ ] **Step 1: Write the failing tests**

In `NtfyClientTests.cs` (reuse the existing StubHandler; mirror the file's style):

```csharp
  [Fact]
  public async Task Error_body_is_truncated_so_it_cannot_flood_the_event_log()
  {
    // The message is serialized into tietue's EntityEvent.Result (jsonb) by SchedulerTick.
    // A proxy returning a large HTML error page must not land whole in the database.
    var handler = new StubHandler
    {
      Response = () => new HttpResponseMessage(HttpStatusCode.BadGateway)
      {
        Content = new StringContent(new string('x', 10_000)),
      },
    };
    var client = new NtfyClient(
      new NtfyOptions { BaseUrl = "http://ntfy.test", Topic = "toimi" },
      new HttpClient(handler));

    var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync("m"));

    Assert.Contains("502", ex.Message);
    Assert.True(ex.Message.Length < 1000, $"message was {ex.Message.Length} chars; expected a truncated body");
    Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
  }
```

(Adapt the StubHandler usage to whatever shape that file's handler already has — it was extended in Tier 3 to return configurable responses. If its response hook has a different name, use it and report.)

In `SchedulerTickTests.cs` — a handler whose exception message is huge. Mirror the file's existing due-trigger setup (see `ClaimThenRunTests` / the file's own `SetupAsync`), registering a throwing handler:

```csharp
  private sealed class ExplodingHandler(string message) : INativeHandler
  {
    public string Kind => "notify";

    public Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
    {
      throw new InvalidOperationException(message);
    }
  }

  [Fact]
  public async Task Handler_error_text_is_capped_before_it_reaches_the_event_log()
  {
    // [setup: TestDb, define type, create entity, create a due 'notify' one-shot trigger —
    //  same shape as this file's other tests — but with HandlerRegistry([new ExplodingHandler(new string('y', 20_000))])]

    await tick.RunDueAsync(tickTime, default);

    var evt = await db.EntityEvents.SingleAsync(e => e.EntityId == entityId && e.Kind == "notify");
    Assert.Equal("error", evt.Status);
    Assert.NotNull(evt.Result);
    Assert.True(evt.Result!.Length < 2000, $"result was {evt.Result.Length} chars; expected the message to be capped");
    Assert.Contains("yyy", evt.Result); // the head of the real message survives
  }
```

(The bracketed setup comment is for you to replace with the file's real helper calls; the assertions are the spec.)

- [ ] **Step 2: Verify both fail**

`mise exec dotnet -- dotnet test src/toimi.notifications.Tests/toimi.notifications.Tests.csproj --filter "FullyQualifiedName~Error_body_is_truncated"`
`mise exec dotnet -- dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~Handler_error_text_is_capped"`
Expected: both FAIL on the length assertions (full 10k / 20k text present).

- [ ] **Step 3: Implement both caps**

In `src/toimi.notifications/NtfyClient.cs`, add a constant next to `PriorityMap` and truncate the body:

```csharp
  private const int MaxErrorBodyChars = 500;
```

and in the non-success branch:

```csharp
    if (!response.IsSuccessStatusCode)
    {
      var body = await response.Content.ReadAsStringAsync(ct);
      // The message ends up in tietue's EntityEvent.Result (jsonb) — cap it so an
      // HTML error page from a proxy cannot flood the event log.
      if (body.Length > MaxErrorBodyChars)
      {
        body = body[..MaxErrorBodyChars] + "… [truncated]";
      }

      throw new HttpRequestException(
        $"ntfy returned {(int)response.StatusCode} ({response.StatusCode}): {body}", null, response.StatusCode);
    }
```

In `src/toimi.tools.tietue/Scheduling/SchedulerTick.cs`, add a constant on the class and cap the serialized message:

```csharp
  private const int MaxErrorMessageChars = 1000;
```

and in the handler catch block:

```csharp
            catch (Exception ex)
            {
              status = "error";
              // Generic insurance: any handler's exception message lands in a jsonb column.
              var message = ex.Message.Length > MaxErrorMessageChars
                ? ex.Message[..MaxErrorMessageChars] + "… [truncated]"
                : ex.Message;
              resultJson = System.Text.Json.JsonSerializer.Serialize(new { error = message });
              _logger.LogError(ex, "Handler {HandlerKind} failed for trigger {TriggerId} (entity {EntityId}).",
                trigger.HandlerKind, trigger.Id, trigger.EntityId);
            }
```

(The full exception, untruncated, still goes to the log — only the DB copy is capped.)

- [ ] **Step 4: Green + suites**

Run notifications, tietue, and verkko suites (verkko's `SendNotificationTool` surfaces the message). All green.

- [ ] **Step 5: Format and commit**

Format `toimi.notifications`, `toimi.notifications.Tests`, `toimi.tools.tietue`, `toimi.tools.tietue.Tests` (+ `--verify-no-changes` each), then:

```bash
git add src/toimi.notifications/NtfyClient.cs src/toimi.tools.tietue/Scheduling/SchedulerTick.cs src/toimi.notifications.Tests/NtfyClientTests.cs src/toimi.tools.tietue.Tests/SchedulerTickTests.cs
git commit -m "fix: cap error text before it lands in the event log"
```

---

### Task 2: wall-clock watchdog around script evaluation

**Why:** Jint's limits are cooperative — a single atomic native call escapes them. Tier 3 closed the two known vectors (`repeat`, literal regex) but left a documented residual: `new RegExp('(a+)+$').test(...)` and `.split(/(a+)+$/)` stall a flat ~5s. The tick holds the Postgres advisory lock while that runs, so every replica's scheduler is blocked. A wall-clock budget at the handler bounds ANY such stall — known or future — rather than playing whack-a-mole inside the sandbox.

**Honest limitation to encode in the comment:** .NET cannot abort the orphaned thread. The runaway keeps burning a thread-pool thread until Jint's internal caps end it; the watchdog stops the *tick* from waiting, which is the property that matters.

**Files:**
- Modify: `src/toimi.tools.tietue/Scripts/ScriptOptions.cs`
- Modify: `src/toimi.tools.tietue/Handlers/ScriptHandler.cs`
- Modify: `src/toimi.tools.tietue/appsettings.json` (add the new key under the existing `"Scripts"` section)
- Modify: `src/toimi.tools.tietue.Tests/ScriptHandlerTests.cs`

- [ ] **Step 0: Add the inert option first (TDD ordering)**

The red test constructs `ScriptOptions { TimeoutSeconds = 1 }`, which cannot compile until the property exists. Add it now — with nothing reading it, it is inert and the test in Step 1 goes red for the RIGHT reason (status `"ran"`, ~5s elapsed) instead of failing to compile:

```csharp
namespace toimi.tools.tietue.Scripts;

public class ScriptOptions
{
  public bool Enabled { get; set; } = true;

  /// <summary>Wall-clock budget for one script evaluation; see ScriptHandler for why this exists.</summary>
  public int TimeoutSeconds { get; set; } = 5;
}
```

- [ ] **Step 1: Write the failing test**

Read `ScriptHandlerTests.cs` first and mirror its construction of `ScriptHandler(engine, applier, options)`. Append:

```csharp
  [Fact]
  public async Task Script_exceeding_the_wall_clock_budget_is_abandoned_without_stalling_the_tick()
  {
    // Dynamically-constructed RegExp escapes Jint's RegexTimeout constraint and stalls
    // ~5s (documented in ScriptEngine). The tick holds the Postgres advisory lock while a
    // handler runs, so the budget — not the sandbox — is what bounds the damage.
    const string source = "var re = new RegExp('(a+)+$'); return { hit: re.test('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab') };";
    // [construct: ScriptHandler with ScriptOptions { Enabled = true, TimeoutSeconds = 1 }
    //  and a ctx whose ConfigJson is {"source": <source>, "capabilities": []} — mirror
    //  this file's existing helper]

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await handler.HandleAsync(ctx);
    sw.Stop();

    Assert.Equal("timeout", result.Status);
    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
      $"handler took {sw.Elapsed.TotalSeconds:F1}s; the 1s budget must bound the tick");
    // No effects may be applied from a script that never produced a result.
    // [assert via the file's fake/real applier that nothing was applied]
  }
```

Replace the bracketed parts with the file's real setup. If `ScriptHandlerTests` builds a real `ScriptEffectApplier` over a TestDb entity, assert the entity is unchanged; if it uses a fake, assert no effects recorded.

- [ ] **Step 2: Verify it fails**

`mise exec dotnet -- dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~Script_exceeding_the_wall_clock"`
Expected: FAIL — takes ~5s (elapsed assertion) and `Status` is `"ran"`, not `"timeout"`.
If it does NOT take ~5s (Jint behavior drifted), STOP and report DONE_WITH_CONCERNS with the measured time rather than forcing the test.

- [ ] **Step 3: Implement**

(`ScriptOptions.cs` already changed in Step 0.)

`src/toimi.tools.tietue/Handlers/ScriptHandler.cs` — replace the direct `engine.Evaluate(...)` call:

```csharp
    var effectsJson = await EvaluateWithWatchdogAsync(source, ctx.Entity.Data.RootElement.GetRawText(), ct);
    if (effectsJson is null)
    {
      return new HandlerResult("timeout", /*lang=json,strict*/ """{"error":"script exceeded its wall-clock budget"}""");
    }

    var effects = ScriptEffects.Parse(effectsJson);
```

and add the private method:

```csharp
  /// <summary>
  /// Evaluates on a pool thread and stops WAITING at the configured budget. Jint's own
  /// limits are cooperative, so a single atomic native call (a dynamically-built
  /// catastrophic regex, a large allocation) can outrun them. The scheduler tick holds
  /// the Postgres advisory tick lock while a handler runs, so an unbounded script stalls
  /// every replica's scheduler — this bounds that to the budget.
  ///
  /// .NET cannot abort the abandoned thread: it keeps burning a thread-pool thread until
  /// Jint's internal caps end it. What this guarantees is that the TICK moves on, not that
  /// the runaway stops immediately.
  /// </summary>
  private async Task<string?> EvaluateWithWatchdogAsync(string source, string dataJson, CancellationToken ct)
  {
    try
    {
      return await Task.Run(() => engine.Evaluate(source, dataJson), ct)
        .WaitAsync(TimeSpan.FromSeconds(options.TimeoutSeconds), ct);
    }
    catch (TimeoutException)
    {
      return null;
    }
  }
```

Deliberate behavior note (encode it in the comment if you shorten it): passing `ct` to `WaitAsync` makes script evaluation cancellation-aware for the first time — a shutdown mid-script now surfaces as an `OperationCanceledException` out of `HandleAsync` (SchedulerTick records an error event and advances) instead of the script silently running to completion. That is intended: shutdown should not wait on untrusted scripts.

`src/toimi.tools.tietue/appsettings.json` — add `"TimeoutSeconds": 5` inside the existing `"Scripts"` object (match the file's formatting; yamllint doesn't apply but keep JSON tidy).

- [ ] **Step 4: Green + full tietue suite**

The new test must pass in well under 3s; every existing ScriptHandler/ScriptEngine test must stay green (benign scripts run in ~1ms, far inside a 5s default).

- [ ] **Step 5: Update the ScriptEngine residual note**

`src/toimi.tools.tietue/Scripts/ScriptEngine.cs`'s header documents the dynamic-RegExp residual and says the real fix belongs at the handler/tick level. Add one sentence stating that `ScriptHandler` now enforces a wall-clock budget (`Scripts:TimeoutSeconds`), so the residual degrades a single handler run rather than the tick. Do not overclaim — the stall still happens, it is just no longer awaited.

- [ ] **Step 6: Format and commit**

```bash
git add src/toimi.tools.tietue/Scripts/ScriptOptions.cs src/toimi.tools.tietue/Handlers/ScriptHandler.cs src/toimi.tools.tietue/Scripts/ScriptEngine.cs src/toimi.tools.tietue/appsettings.json src/toimi.tools.tietue.Tests/ScriptHandlerTests.cs
git commit -m "fix(tietue): bound script evaluation with a wall-clock watchdog"
```

---

### Task 3: cover `useAdminList`'s HTTP-error branch

**Why:** Tier 3 fixed and tested the network-rejection path; the sibling `!resp.ok` branch (`setError({ status, body })`) has no test anywhere.

**Files:** Modify `src/toimi.web/ClientApp/src/admin/useAdmin.test.ts`.

- [ ] **Step 1: Append the test** (mirror the file's existing `vi.stubGlobal('fetch', ...)` + `renderHook`/`waitFor` style):

```ts
  it('surfaces an HTTP error response as status + body', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: false,
      status: 409,
      json: () => Promise.resolve({ message: 'stale' }),
    }))

    const { result } = renderHook(() => useAdminList('tietue', 'entities'))

    await waitFor(() => expect(result.current.loading).toBe(false))
    expect(result.current.error).toEqual({ status: 409, body: { message: 'stale' } })
    expect(result.current.data).toBeNull()
  })
```

Adapt the `useAdminList` import/signature to the file's existing usage. If the assertion shape differs from what the hook actually produces, pin the observed shape and report.

- [ ] **Step 2:** `mise exec node -- npm test` from `src/toimi.web/ClientApp` (expect 7 passing), `npm run lint`.

- [ ] **Step 3: Commit**

```bash
git add src/toimi.web/ClientApp/src/admin/useAdmin.test.ts
git commit -m "test(web): cover the admin HTTP-error branch"
```

---

### Task 4: `PostgresTickLock` integration tests

**Why:** the tick lock is the ONLY thing making the stale-claim take-over in `EntityEventStore` safe (its own comment says so), and it is entirely untested — `SchedulerTickLockTests` uses a fake `ITickLock`. The real EF connection ref-counting, the acquire/refuse semantics, and lease release have never run against Postgres.

**Approach:** Testcontainers spins up a throwaway Postgres per test class. `PostgresTickLock` issues only `pg_try_advisory_lock`/`pg_advisory_unlock` — **no tables, no migrations needed**. Tests are gated on docker so the suite still passes on a machine without it.

**Files:**
- Modify: `src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`
- Create: `src/toimi.tools.tietue.Tests/DockerFactAttribute.cs`
- Create: `src/toimi.tools.tietue.Tests/PostgresTickLockTests.cs`

- [ ] **Step 1: Add the package**

Add to the test csproj (resolve the current 4.x version with `mise exec dotnet -- dotnet package search Testcontainers.PostgreSql --take 1` or by trying a restore; report the version you pin):

```xml
    <PackageReference Include="Testcontainers.PostgreSql" Version="4.x.y" />
```

Check whether `Npgsql.EntityFrameworkCore.PostgreSQL` flows transitively from the tietue project reference (it should — no `PrivateAssets`); if `UseNpgsql` does not resolve, add an explicit reference at the same version the main project pins (10.0.3) and report.

- [ ] **Step 2: The docker gate**

`src/toimi.tools.tietue.Tests/DockerFactAttribute.cs`:

```csharp
using Xunit;

namespace toimi.tools.tietue.Tests;

/// <summary>
/// A Fact that skips itself when no Docker daemon is reachable, so the suite still
/// passes on a machine without Docker while CI (ubuntu-latest, Docker present) runs it.
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
  private static readonly Lazy<bool> DockerAvailable = new(Probe);

  public DockerFactAttribute()
  {
    if (!DockerAvailable.Value)
    {
      Skip = "Docker is not available; skipping the Postgres integration test.";
    }
  }

  private static bool Probe()
  {
    return Environment.GetEnvironmentVariable("DOCKER_HOST") is not null
      || File.Exists("/var/run/docker.sock")
      || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker", "run", "docker.sock"));
  }
}
```

Note: setting `Skip` inside a custom attribute's constructor does not trip the repo's `xUnit1004` analyzer (which inspects `Skip` arguments at the usage site). If the analyzer complains anyway, report rather than suppressing broadly.

- [ ] **Step 3: The tests**

`src/toimi.tools.tietue.Tests/PostgresTickLockTests.cs` — **per-test container lifecycle, deliberately**: xUnit v2 instantiates the test class once per test method, so `IAsyncLifetime` here starts one container per test (three total, ~1-2s each after the first image pull). Do NOT "optimize" this into an `IClassFixture` — a skipped `[DockerFact]` never constructs the class, so on a docker-less machine no container start is ever attempted; a class fixture would initialize (and fail) even when every test in the class is skipped. Put this rationale in a comment on the class:

```csharp
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Scheduling;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class PostgresTickLockTests : IAsyncLifetime
{
  private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
    .WithImage("postgres:17-alpine")
    .Build();

  public Task InitializeAsync()
  {
    return _postgres.StartAsync();
  }

  public Task DisposeAsync()
  {
    return _postgres.DisposeAsync().AsTask();
  }

  // A context per lock instance: advisory locks are SESSION-scoped, so two locks must
  // not share a connection or the second would trivially "already hold" the lock.
  private TietueDbContext NewContext()
  {
    return new TietueDbContext(new DbContextOptionsBuilder<TietueDbContext>()
      .UseNpgsql(_postgres.GetConnectionString())
      .Options);
  }

  [DockerFact]
  public async Task Second_acquire_is_refused_while_the_lease_is_held()
  {
    using var dbA = NewContext();
    using var dbB = NewContext();

    var leaseA = await new PostgresTickLock(dbA).TryAcquireAsync(default);
    Assert.NotNull(leaseA);

    // This is the property the whole scheduler design rests on: a second replica's tick
    // is refused, which is what makes EntityEventStore's read-modify-write stale-claim
    // take-over safe (see its comment).
    Assert.Null(await new PostgresTickLock(dbB).TryAcquireAsync(default));

    await leaseA!.DisposeAsync();
  }

  [DockerFact]
  public async Task Lock_is_released_when_the_lease_is_disposed()
  {
    using var dbA = NewContext();
    using var dbB = NewContext();

    var leaseA = await new PostgresTickLock(dbA).TryAcquireAsync(default);
    Assert.NotNull(leaseA);
    await leaseA!.DisposeAsync();

    var leaseB = await new PostgresTickLock(dbB).TryAcquireAsync(default);
    Assert.NotNull(leaseB);
    await leaseB!.DisposeAsync();
  }

  [DockerFact]
  public async Task Queries_issued_during_the_lease_keep_holding_the_lock()
  {
    using var dbA = NewContext();
    using var dbB = NewContext();

    var leaseA = await new PostgresTickLock(dbA).TryAcquireAsync(default);
    Assert.NotNull(leaseA);

    // EF ref-counts the explicit OpenConnection, so work done during the tick reuses the
    // same session. If it silently opened a second connection, the advisory lock would
    // live on a session that closes early and a second replica could tick concurrently.
    await dbA.Database.ExecuteSqlRawAsync("SELECT 1");
    Assert.Null(await new PostgresTickLock(dbB).TryAcquireAsync(default));

    await leaseA!.DisposeAsync();
    var leaseB = await new PostgresTickLock(dbB).TryAcquireAsync(default);
    Assert.NotNull(leaseB);
    await leaseB!.DisposeAsync();
  }
}
```

Adapt: `TietueDbContext`'s constructor/options shape (check `DbContextTests.cs`'s `TestDb` for how it is built), whether `ILogger` is required, and xunit 2.9.3's `IAsyncLifetime` signatures (`Task InitializeAsync()` / `Task DisposeAsync()`). Report adaptations.

- [ ] **Step 4: Verify the tests genuinely exercise the lock**

Run: `mise exec dotnet -- dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~PostgresTickLockTests"` — all 3 pass against a real container (first run pulls the image).

Then prove non-vacuity: temporarily change `LockKey` usage in a scratch copy — simplest honest check — make `TryAcquireAsync` always return a lease without calling `pg_try_advisory_lock` (edit `PostgresTickLock.cs`, run, expect `Second_acquire_is_refused...` to FAIL), then `git checkout -- src/toimi.tools.tietue/Scheduling/PostgresTickLock.cs` and re-run green. Capture both outputs.

- [ ] **Step 5: Verify the docker gate**

Confirm the tests SKIP (not fail) when docker is unreachable: run with `DOCKER_HOST` pointing nowhere is not a reliable probe test — instead temporarily edit `DockerFactAttribute.Probe` to `return false;`, run the suite, confirm the three tests report as skipped and the suite still passes, then restore. Report the skipped-count output.

- [ ] **Step 6: Full tietue suite + format + commit**

Full suite green (224 + 3 docker-gated = report the actual number). Format the test csproj (+ `--verify-no-changes`).

```bash
git add src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj src/toimi.tools.tietue.Tests/DockerFactAttribute.cs src/toimi.tools.tietue.Tests/PostgresTickLockTests.cs
git commit -m "test(tietue): add Postgres integration tests for the tick lock"
```

---

### Final verification

- [ ] `mise exec dotnet -- dotnet test toimi.sln --nologo -v q` — all green (501 baseline + new).
- [ ] `mise exec dotnet -- dotnet format toimi.sln --verify-no-changes` — exit 0.
- [ ] ClientApp: `npm test` (7) + `npm run lint` + `npm run build`.
- [ ] Note in the final report: CI now pulls a Postgres image during `dotnet test`; confirm the gate means a docker-less environment still passes.

### Closes the effort

After this, the only remaining item from the original three-tier review is `McpToolAggregator`'s collision/reconnect seams (needs an MCP test server or a production refactor) — genuinely deferred, not forgotten.
