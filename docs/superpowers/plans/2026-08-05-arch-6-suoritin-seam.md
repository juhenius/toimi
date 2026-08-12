# Suoritin Seam v2: Ship Only What the Sandbox Enforces + ScriptBudget Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deepen the tietue ↔ suoritin HTTP seam so the sandbox receives only what it enforces. Four findings close: (1) the whole `grants` array crosses the wire (`SuoritinClient.cs:57` → `executor.ts:99` → `worker.ts:60`) though suoritin can only act on `"llm"` — the `"setField"`/`"mcp:<tool>"` vocabulary is interpreted solely by `ScriptEffectApplier` in tietue, yet sits inside the untrusted sandbox; (2) the result-size limits are written independently on both sides and one pair disagrees — `limits.ts` `MAX_LOGS = 200` vs `SuoritinClient.MaxLogEntries = 100`, so suoritin returns up to 200 log lines and tietue silently discards half; (3) the timeout ladder exists only as prose across three files — `executor.ts:4-9` (20s default / 60s clamp), the `ScriptOptions.TimeoutSeconds` doc comment ("+5s HTTP, +10s watchdog"), `Program.cs:78` (`TimeoutSeconds + 5`), `ScriptHandler.cs:70` (`TimeoutSeconds + 10`) — and `ScriptHandlerTests.Watchdog_bounds_a_hung_suoritin_connection` forces the watchdog with a `timeoutSeconds: -10` arithmetic hack; (4) the callback route string `/internal/runs/{token}/extract` is written independently at `ExtractEndpoints.cs:84` and `worker.ts:69`.

**Architecture:** The wire request becomes `{code, input, timeoutMs, net?: string[], extract?: {url, token}}`. tietue's `ScriptHandler` composes `net` (the script's `allowedHosts` plus, only when `llm` is granted, the extract-callback host) and, when `llm` is granted, an `ExtractGrant { Url, Token }` where `Url` is the full callback endpoint composed by `ExtractEndpoints.CallbackUrl()` — the route string's single owner. suoritin applies `net` verbatim as the worker's Deno net permission and the worker POSTs to `extract.url` with the token in an `X-Run-Token` header; the sandbox never sees a grant name or a route shape again. The extract endpoint moves from a path token (`/internal/runs/{token}/extract`) to a fixed route (`/internal/runs/extract`) + header token, which is what gives the `{url, token}` pair its meaning (url = endpoint, token = credential; tokens also stop appearing in URL paths/access logs). A new `Scripts/ScriptBudget.cs` value object owns the whole timeout ladder — `Script` (wire `timeoutMs`, clamped to suoritin's 60s max) < `HttpTimeout` (+5s) < `Watchdog` (+10s) < `TokenTtl` (watchdog +20s = today's `TimeoutSeconds + 30`) — plus the `Effects` budget; `Program.cs` and `ScriptHandler` consume it, and the watchdog test constructs a genuinely tiny budget instead of negative arithmetic. Limits stay per-language constants (a shared artifact across C#/TS isn't feasible) with paired comments naming the counterpart file, and `SuoritinIntegrationTests` (Testcontainers, real image) becomes the cross-seam contract test pinning the agreements.

**Tech Stack:** .NET 10 minimal APIs, xUnit v2, Testcontainers (Docker-gated via `DockerFactAttribute` — Docker IS available and these tests MUST run), Deno 2.9.4 (on PATH; per-run Workers with scoped permissions, `deno task test`).

## Global Constraints

- dotnet is NOT on PATH: every dotnet command is preceded by `export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"`.
- deno is on PATH. Deno test command: `cd /Users/jari/private/toimi/src/toimi.tools.suoritin && deno task test` (from `deno.json`: `deno test --allow-net --allow-read=. --deny-import`). Before each suoritin commit: `deno fmt` (2-space per `deno.json`) and `deno lint` in that directory, both clean.
- Before each .NET commit: `dotnet format <csproj>` for every touched project, then `--verify-no-changes` exits 0. Enforced as errors: IDE0005, IDE0022 (block bodies), IDE0046, whitespace. 2-space indent, file-scoped namespaces.
- Commit style: `<type>(<scope>): <subject>` (refactor(suoritin), refactor(tietue), test(tietue), docs).
- Suite floors — no drops: tietue 384 (Docker-gated tests RUN, not skip), core 93, web 38. Deno currently 30 tests (19 executor + 11 main) — all green, expected ~34 after. Expected tietue final ≈ 395.
- UNCHANGED surfaces: MCP tools; the job entity schema (`allowedHosts`/`grants`/`enabled`/`code` fields on job entities — the *entity* vocabulary stays; only the *wire* shape changes); `ScriptEffectApplier` grants enforcement (`setField` / `mcp:<tool>` checks); `RunTokenStore` semantics (issue / llm-grant check / 3-call budget / revoke); suoritin stays credential-free, request cap 1 MiB, `MAX_CONCURRENT = 4`; host-side re-clamping in `executor.ts` `clamp()` (defence in depth); effects payload stays opaque `Record<string, unknown>` to suoritin.
- Security (hard): the sandbox's net allowlist must remain exactly `allowedHosts + (callback host iff llm granted)` — the refactor must not widen egress. The extract URL tietue sends must resolve to the same host as today (`SuoritinOptions.CallbackBaseUrl`); the NetworkPolicy pinhole (`k8s/base/tools-suoritin/networkpolicy.yaml`, tietue-only) is untouched.
- Mid-plan red window: after Task 2 (suoritin speaks v2) and before Task 3 (tietue speaks v2), the Docker-gated `SuoritinIntegrationTests` are not trustworthy against a freshly built image. Task 2's gate is the Deno suite plus the tietue suite with `--filter "FullyQualifiedName!~SuoritinIntegrationTests"`; Task 3 restores the full suite including Docker tests, and Tasks 4–5 run them for real.

## Design Decisions

**Canonical log-entry cap = 100; suoritin's `MAX_LOGS` drops 200 → 100.** The two sides disagree today (suoritin 200, tietue 100 — half the lines silently discarded in `ParseLogs`'s `.Take(100)`). The instruction "sandbox authoritative-or-equal" allows either raising tietue to 200 or lowering suoritin to 100. Lowering the *generator* is chosen: logs are a debugging aid that gets persisted into `EntityEvent` rows (jsonb) and returned through `run_trigger` MCP results — 100 × 2000 chars ≈ 200 KB is already a generous ceiling and doubling tietue's persisted/streamed payload buys nothing; shrinking the untrusted payload's upper bound is strictly safer; and with the truncation happening first in the sandbox, tietue's `.Take(MaxLogEntries)` becomes a true no-op backstop — nothing is ever silently discarded. Both constants carry paired comments naming the counterpart file, and a cross-seam integration test (raw HTTP, not through the client's truncation) pins `logs.length == SuoritinClient.MaxLogEntries` so drift fails loudly.

**Extract token moves from URL path to `X-Run-Token` header; route becomes the fixed `/internal/runs/extract`.** This is what makes the `extract: {url, token}` wire shape non-redundant: `url` is the endpoint (fully composed by tietue — the worker just POSTs to it, never knowing the route shape, closing finding 4), `token` is the credential. Side benefit: run tokens no longer appear in URL paths (request logs). `RunTokenStore` semantics are untouched — only the transport position of the token changes; `ExtractEndpoints.HandleAsync` keeps its `(token, request, …)` test-visible signature (token becomes `string?`, null → 403). The route string and header name exist exactly once, as constants on `ExtractEndpoints`, used by both `MapPost` and the `CallbackUrl()` composer `ScriptHandler` calls.

**extract.url host check in the worker: `Deno.permissions.querySync`, refuse-loudly.** Today there is no explicit check — the executor *widens* net to the callback host itself, so a compromised tietue could aim the sandbox's fetch anywhere it also listed. Under v2 the enforcement is structural: the worker's net permission is exactly the request's `net`, so a fetch to an extract.url outside it is denied by Deno itself (parity, unbypassable). On top, the worker explicitly checks `Deno.permissions.querySync({ name: "net", host: new URL(extract.url).host })` before installing `input.extract` and fails the run with a clear `"extract callback host … is not in the net allowlist"` error instead of a permission trace (improvement: mis-composed requests are loud, and no extra data needs shipping — the worker queries its own permission state). Note `URL.host` includes the port only when non-default, and tietue's `BuildNet` mirrors that exact semantic via `Uri.IsDefaultPort`.

**Explicit JSON nulls for optional wire fields stay tolerated-as-absent; malformed non-null values are rejected.** The prompt-level invariant is "absent optional fields must be omitted, not JSON null": `SuoritinClient` keeps `WhenWritingNull` (so `extract`/`net` are omitted when absent — a unit test pins the absence of the `extract` property on the wire), and `main.ts` keeps its documented regression semantics (`!= null` — an explicit `null` counts as absent, guarding against serializer drift; see the existing "accepts explicit nulls" regression test). What *is* rejected with 400: a present `extract` that is not a well-shaped object — non-object, `url` missing/null/unparseable, `token` missing/null/empty — exactly the way present-but-wrong `runToken`/`callbackUrl` values are rejected today. Paired comments on both sides name each other.

**`ScriptBudget` is a validated class, not raw arithmetic.** `ScriptBudget(script, httpMargin, watchdogMargin, effects)` computes `HttpTimeout = script + httpMargin`, `Watchdog = script + watchdogMargin`, `TokenTtl = Watchdog + 20s`, validating `script > 0`, `httpMargin >= 0`, `watchdogMargin >= httpMargin` (the ordering invariant that today lives only in `ScriptOptions`' doc comment). `ScriptBudget.From(ScriptOptions)` supplies the production ladder (5s / 10s margins — byte-identical behavior to today: 20/25/30/50s defaults, token TTL `Watchdog + 20 = TimeoutSeconds + 30`) and clamps `Script` to `MaxScriptSeconds = 60`, mirroring suoritin's `MAX_TIMEOUT_MS` (paired comment) so the .NET ladder never budgets for time the sandbox won't grant. The `timeoutSeconds: -10` test hack becomes a legitimate seam: the watchdog test constructs `new ScriptBudget(40ms, Zero, Zero, …)` — a genuinely tiny, valid budget — and `ScriptHandler` takes an optional `ScriptBudget? budget = null` primary-ctor param (`?? ScriptBudget.From(options)`, the repo's established optional-ctor-param pattern), so the 3 other construction sites (`TypeSeederTests:97`, `JobEndToEndTests:49`, `ScriptHandlerTests.Handler()`) compile unchanged and prod DI injects the registered singleton.

**Grants stay fully resolved in tietue and never cross the seam.** `ScriptHandler.Resolve` still reads `grants` (entity/config) — they drive token issuance (`llm`), `BuildNet`, and `ScriptEffectApplier.ApplyAsync` exactly as today. Only the `SuoritinRequest` record and wire payload lose the `Grants`/`AllowedHosts`/`RunToken`/`CallbackUrl` members in favor of `Net`/`Extract`. `worker.ts` loses its `grants.includes("llm")` check (the presence of `extract` *is* the grant, decided in tietue).

**Deploy compatibility: a mixed-version window exists and is acceptable.** Old suoritin + new tietue (or vice versa) cannot execute llm scripts (route/shape mismatch → extract 404s or `net` ignored → egress-needing scripts fail closed as permission errors — never open). This is a single-user system; tietue and suoritin deploy together via `scripts/deploy-all.sh`, so the window is the seconds between two rollout restarts. Worst case, one scheduled script run in that window fails, is recorded as an `error` `EntityEvent`, and the trigger advances (handler isolation already guarantees this). No persisted state encodes the wire shape (job *entities* keep `allowedHosts`/`grants` unchanged), so nothing needs migrating. Failing closed + a seconds-long window + at most one visible error event = acceptable; no dual-shape compatibility code is written.

**Cross-seam contract tests use raw HTTP where the client would mask the contract.** The log-cap agreement must be observed *before* `SuoritinClient.ParseLogs` truncates, so the integration test POSTs with a plain `HttpClient` and counts entries in the raw JSON. Extract is deliberately NOT tested end-to-end through the container: the container would need to reach a host-side Kestrel/`HttpListener` callback (new fixture plumbing + new LLM mocking infrastructure the current fixture doesn't have); `executor_test.ts` already covers the extract mechanics in-sandbox against a local Deno server, and `ExtractEndpointsTests` covers tietue's endpoint — the seam between them is the composed URL + header name, pinned by unit tests on both sides against the same literals.

---

## Task 1: `ScriptBudget` value object (tietue, TDD)

**Files**
- Create: `src/toimi.tools.tietue/Scripts/ScriptBudget.cs`
- Create: `src/toimi.tools.tietue.Tests/ScriptBudgetTests.cs`
- Edit: `src/toimi.tools.tietue/Scripts/ScriptOptions.cs` (doc comment now points at ScriptBudget)
- Edit: `src/toimi.tools.tietue/Program.cs` (register singleton; HTTP client uses `HttpTimeout`)
- Edit: `src/toimi.tools.tietue/Handlers/ScriptHandler.cs` (consume the budget)
- Edit: `src/toimi.tools.tietue.Tests/ScriptHandlerTests.cs` (watchdog test's legitimate seam)

**Interfaces**
- `ScriptBudget(TimeSpan script, TimeSpan httpMargin, TimeSpan watchdogMargin, TimeSpan effects)`; `static ScriptBudget From(ScriptOptions)`; members `Script`, `ScriptMs`, `HttpTimeout`, `Watchdog`, `TokenTtl`, `Effects`; `const int MaxScriptSeconds = 60`.
- `ScriptHandler(…, ScriptOptions options, SuoritinOptions suoritinOptions, ScriptBudget? budget = null)`.

**Steps**

- [ ] Write `src/toimi.tools.tietue.Tests/ScriptBudgetTests.cs` (RED — type doesn't exist yet):

```csharp
using toimi.tools.tietue.Scripts;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ScriptBudgetTests
{
  [Fact]
  public void From_defaults_reproduce_the_documented_ladder()
  {
    var b = ScriptBudget.From(new ScriptOptions());

    Assert.Equal(TimeSpan.FromSeconds(20), b.Script);
    Assert.Equal(20_000, b.ScriptMs);
    Assert.Equal(TimeSpan.FromSeconds(25), b.HttpTimeout);
    Assert.Equal(TimeSpan.FromSeconds(30), b.Watchdog);
    Assert.Equal(TimeSpan.FromSeconds(50), b.TokenTtl); // == the old TimeoutSeconds + 30
    Assert.Equal(TimeSpan.FromSeconds(60), b.Effects);
  }

  [Fact]
  public void From_clamps_script_time_to_suoritins_max()
  {
    // suoritin clamps timeoutMs at 60s (executor.ts MAX_TIMEOUT_MS); budgeting
    // beyond it would make the outer layers wait for time the sandbox never grants.
    var b = ScriptBudget.From(new ScriptOptions { TimeoutSeconds = 120 });

    Assert.Equal(TimeSpan.FromSeconds(ScriptBudget.MaxScriptSeconds), b.Script);
    Assert.Equal(TimeSpan.FromSeconds(65), b.HttpTimeout);
    Assert.Equal(TimeSpan.FromSeconds(70), b.Watchdog);
  }

  [Fact]
  public void Ladder_ordering_holds_by_construction()
  {
    var b = ScriptBudget.From(new ScriptOptions { TimeoutSeconds = 7 });

    Assert.True(b.Script < b.HttpTimeout);
    Assert.True(b.HttpTimeout < b.Watchdog);
    Assert.True(b.Watchdog < b.TokenTtl);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-10)]
  public void Non_positive_script_time_fails_fast(int seconds)
  {
    // A misconfigured Scripts:TimeoutSeconds must fail at startup, not produce
    // a zero-length watchdog at fire time (the old -10 test hack's territory).
    Assert.Throws<ArgumentOutOfRangeException>(
      () => ScriptBudget.From(new ScriptOptions { TimeoutSeconds = seconds }));
  }

  [Fact]
  public void Watchdog_margin_below_http_margin_is_rejected()
  {
    // The watchdog must not fire before the HTTP client has had its chance to
    // time out cleanly — equal margins are allowed (tests), inverted are not.
    Assert.Throws<ArgumentOutOfRangeException>(() => new ScriptBudget(
      TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(60)));
  }

  [Fact]
  public void Equal_margins_are_allowed_for_tiny_test_budgets()
  {
    var b = new ScriptBudget(TimeSpan.FromMilliseconds(40), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(60));

    Assert.Equal(TimeSpan.FromMilliseconds(40), b.Watchdog);
  }
}
```

- [ ] Run the new tests, confirm they fail to compile: `export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH" && dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~ScriptBudgetTests"` → compile error (RED).
- [ ] Create `src/toimi.tools.tietue/Scripts/ScriptBudget.cs` (GREEN):

```csharp
namespace toimi.tools.tietue.Scripts;

/// <summary>
/// The script-run timeout ladder, derived once from <see cref="ScriptOptions"/>:
/// Script (the wire timeoutMs) &lt; HttpTimeout (+httpMargin) &lt; Watchdog
/// (+watchdogMargin) &lt; TokenTtl (Watchdog + 20s). Every outer layer outlives
/// the one beneath it, so the scheduler tick (which holds the tick lock while a
/// handler runs) is bounded even if suoritin hangs. Effects is the separate
/// post-run budget for applying setField/mcpCall effects under the same lock.
/// </summary>
public sealed class ScriptBudget
{
  /// <summary>Counterpart: suoritin clamps timeoutMs at 60s (executor.ts MAX_TIMEOUT_MS). Keep equal.</summary>
  public const int MaxScriptSeconds = 60;

  public ScriptBudget(TimeSpan script, TimeSpan httpMargin, TimeSpan watchdogMargin, TimeSpan effects)
  {
    if (script <= TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(script), script, "script budget must be positive");
    }

    if (httpMargin < TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(httpMargin), httpMargin, "HTTP margin must be non-negative");
    }

    if (watchdogMargin < httpMargin)
    {
      throw new ArgumentOutOfRangeException(nameof(watchdogMargin), watchdogMargin, "watchdog margin must not undercut the HTTP margin");
    }

    Script = script;
    HttpTimeout = script + httpMargin;
    Watchdog = script + watchdogMargin;
    TokenTtl = Watchdog + TimeSpan.FromSeconds(20);
    Effects = effects;
  }

  /// <summary>Sandbox execution budget — sent to suoritin as timeoutMs.</summary>
  public TimeSpan Script { get; }

  /// <summary>The named suoritin HttpClient's Timeout (Program.cs).</summary>
  public TimeSpan HttpTimeout { get; }

  /// <summary>ScriptHandler's outer WaitAsync bound on the whole suoritin call.</summary>
  public TimeSpan Watchdog { get; }

  /// <summary>RunTokenStore TTL for the extract() run token — outlives the watchdog.</summary>
  public TimeSpan TokenTtl { get; }

  /// <summary>Post-run budget for ScriptEffectApplier.</summary>
  public TimeSpan Effects { get; }

  public int ScriptMs
  {
    get { return (int)Script.TotalMilliseconds; }
  }

  public static ScriptBudget From(ScriptOptions options)
  {
    return new ScriptBudget(
      TimeSpan.FromSeconds(Math.Min(options.TimeoutSeconds, MaxScriptSeconds)),
      TimeSpan.FromSeconds(5),
      TimeSpan.FromSeconds(10),
      TimeSpan.FromSeconds(options.EffectsTimeoutSeconds));
  }
}
```

- [ ] Replace the `TimeoutSeconds` doc comment in `src/toimi.tools.tietue/Scripts/ScriptOptions.cs`:

```csharp
  /// <summary>
  /// Script execution budget in seconds, sent to suoritin as timeoutMs. The
  /// full timeout ladder (HTTP client, watchdog, token TTL) is derived from
  /// this in <see cref="ScriptBudget"/> — the single owner of the arithmetic.
  /// </summary>
  public int TimeoutSeconds { get; set; } = 20;
```

- [ ] Rewire `src/toimi.tools.tietue/Program.cs`: after the `ScriptOptions` singleton registration (line ~72), add the budget singleton, and change the HTTP client timeout line (~78):

```csharp
builder.Services.AddSingleton(sp =>
  toimi.tools.tietue.Scripts.ScriptBudget.From(sp.GetRequiredService<toimi.tools.tietue.Scripts.ScriptOptions>()));
```

```csharp
  client.Timeout = sp.GetRequiredService<toimi.tools.tietue.Scripts.ScriptBudget>().HttpTimeout;
```

- [ ] Rewire `src/toimi.tools.tietue/Handlers/ScriptHandler.cs` — primary ctor gains the optional budget (existing sites compile unchanged; DI injects the singleton):

```csharp
public class ScriptHandler(
  ISuoritinClient suoritin,
  ScriptEffectApplier applier,
  RunTokenStore tokens,
  ScriptOptions options,
  SuoritinOptions suoritinOptions,
  ScriptBudget? budget = null) : INativeHandler
{
  private readonly ScriptBudget _budget = budget ?? ScriptBudget.From(options);
```

and replace the four arithmetic sites inside `HandleAsync`:
  - `tokens.Issue(ctx.Entity.Id, script.Grants, TimeSpan.FromSeconds(options.TimeoutSeconds + 30))` → `tokens.Issue(ctx.Entity.Id, script.Grants, _budget.TokenTtl)`
  - request timeout arg `options.TimeoutSeconds * 1000` → `_budget.ScriptMs`
  - `.WaitAsync(TimeSpan.FromSeconds(options.TimeoutSeconds + 10), ct)` → `.WaitAsync(_budget.Watchdog, ct)`
  - `TimeSpan.FromSeconds(options.EffectsTimeoutSeconds)` in the `applier.ApplyAsync` call → `_budget.Effects`
- [ ] In `src/toimi.tools.tietue.Tests/ScriptHandlerTests.cs`: change `SetupAsync`'s parameter `int timeoutSeconds = 20` to `ScriptBudget? budget = null`, drop `TimeoutSeconds = timeoutSeconds` from the `ScriptOptions` initializer, and pass `budget` as the handler's last argument. Rewrite the watchdog test to the legitimate seam:

```csharp
  [Fact]
  public async Task Watchdog_bounds_a_hung_suoritin_connection()
  {
    using var db = TestDb.New();
    // A genuinely tiny but valid budget: the watchdog fires in ~40ms instead of
    // stalling the test for the production 30s (was: a timeoutSeconds:-10 hack).
    var tiny = new ScriptBudget(TimeSpan.FromMilliseconds(40), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(60));
    var (e, suoritin, _, _, handler) = await SetupAsync(db, budget: tiny);
    suoritin.Hang = true;

    var result = await handler.HandleAsync(new HandlerContext(e, /*lang=json,strict*/ """{"source":"x"}""", DateTimeOffset.UtcNow));

    Assert.Equal("timeout", result.Status);
  }
```

- [ ] Full tietue suite green (Docker tests included): `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj` → ≥ 384 + 6 new, 0 failed.
- [ ] `dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj && dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`, then both `--verify-no-changes` exit 0.
- [ ] Commit: `refactor(tietue): ScriptBudget value object owns the script timeout ladder`

---

## Task 2: suoritin wire contract v2 (Deno, TDD)

**Files**
- Edit: `src/toimi.tools.suoritin/types.ts`, `main.ts`, `executor.ts`, `worker.ts`, `limits.ts`
- Edit: `src/toimi.tools.suoritin/executor_test.ts`, `main_test.ts`

**Interfaces**
- `ExecuteRequest { code, input, timeoutMs?, net?: string[], extract?: ExtractGrant }`; `ExtractGrant { url: string; token: string }`. `ExecuteResult` unchanged.
- Worker message: `{ code, input, extract }` — no grants, no route knowledge.
- `MAX_LOGS = 100` (canonical, see Design Decisions).

**Steps**

- [ ] Update the tests FIRST (RED). In `executor_test.ts`:
  - Add `MAX_LOG_CHARS, MAX_LOGS` to the imports from `./limits.ts`.
  - Rename `allowedHosts:` to `net:` in the two fetch-permission tests ("fetch to a non-granted host is rejected", "fetch to a granted host succeeds").
  - Replace the extract test:

```ts
Deno.test("extract() posts to the given URL with the run-token header", async () => {
  let seen: { path: string; token: string | null; body: unknown } | null = null;
  const srv = Deno.serve({ port: 0, onListen: () => {} }, async (req) => {
    seen = {
      path: new URL(req.url).pathname,
      token: req.headers.get("x-run-token"),
      body: await req.json(),
    };
    return Response.json({ price: 19.9 });
  });
  try {
    const host = `localhost:${srv.addr.port}`;
    const r = await execute({
      code: `export default async function run(input) {
               const out = await input.extract("get the price", "<html>19,90 €</html>", { type: "object" });
               return { setField: [{ path: "lastPrice", value: out.price }] };
             }`,
      input: {},
      net: [host],
      extract: { url: `http://${host}/internal/runs/extract`, token: "tok123" },
      timeoutMs: 5000,
    });
    assert(r.ok, r.error ?? "");
    assertEquals(r.effects, { setField: [{ path: "lastPrice", value: 19.9 }] });
    // The worker POSTed to the URL verbatim — the route shape is tietue's alone.
    assertEquals(seen!.path, "/internal/runs/extract");
    assertEquals(seen!.token, "tok123");
    assertEquals((seen!.body as { prompt: string }).prompt, "get the price");
  } finally {
    await srv.shutdown();
  }
});
```

  - Replace "extract() is absent without the llm grant" with the grant-free shape:

```ts
Deno.test("extract() is absent without an extract grant", async () => {
  const r = await execute({
    code:
      `export default function run(input) { return { has: typeof input.extract }; }`,
    input: {},
  });
  assert(r.ok);
  assertEquals(r.effects, { has: "undefined" });
});
```

  - Add the host-check test (defense in depth — a mis-composed request fails loudly):

```ts
Deno.test("extract.url host outside the net allowlist is refused", async () => {
  const r = await execute({
    code: `export default async function run(input) {
             await input.extract("p", "t");
             return {};
           }`,
    input: {},
    net: [],
    extract: { url: "http://localhost:1/internal/runs/extract", token: "tok" },
    timeoutMs: 5000,
  });
  assertEquals(r.ok, false);
  assertStringIncludes(r.error!, "not in the net allowlist");
});
```

  - In "direct postMessage abuse is clamped host-side", replace the literals: `assert(r.logs.length <= MAX_LOGS, …)` and `assert(line.length <= MAX_LOG_CHARS + 1, …)`.
- [ ] In `main_test.ts`:
  - Update the explicit-nulls regression test body to the v2 fields (keep the comment):

```ts
      body: JSON.stringify({
        code: "export default () => ({})",
        input: { data: {} },
        timeoutMs: null,
        net: null,
        extract: null,
      }),
```

  - Add validation tests:

```ts
Deno.test("POST /execute rejects a non-array net", async () => {
  const res = await handler(
    new Request("http://x/execute", {
      method: "POST",
      body: JSON.stringify({
        code: "export default () => ({})",
        input: {},
        net: "api.example.com",
      }),
    }),
  );
  assertEquals(res.status, 400);
  await res.body?.cancel();
});

Deno.test("POST /execute rejects a malformed extract", async () => {
  for (
    const extract of [
      "yes",
      { url: null, token: null },
      { url: "http://x/e" },
      { url: "not a url", token: "t" },
      { url: "http://x/e", token: "" },
    ]
  ) {
    const res = await handler(
      new Request("http://x/execute", {
        method: "POST",
        body: JSON.stringify({
          code: "export default () => ({})",
          input: {},
          extract,
        }),
      }),
    );
    assertEquals(res.status, 400, JSON.stringify(extract));
    await res.body?.cancel();
  }
});
```

- [ ] `cd /Users/jari/private/toimi/src/toimi.tools.suoritin && deno task test` → the new/changed tests fail (RED).
- [ ] `types.ts` — full replacement:

```ts
// Wire contract with tietue (counterpart: SuoritinRequest/SuoritinResult in
// src/toimi.tools.tietue/Scripts/SuoritinClient.cs). The request carries only
// what this sandbox enforces: code, input, a timeout, the exact net allowlist,
// and — when tietue granted llm — the extract callback. Capability names
// (setField / mcp:<tool> / llm) never cross this seam: tietue composes `net`
// and `extract` from them and interprets the returned effects against them.
export interface ExtractGrant {
  /** Full callback endpoint. Composed by tietue (ExtractEndpoints.cs owns the route shape). */
  url: string;
  /** One-run token, sent back as the X-Run-Token header. */
  token: string;
}

export interface ExecuteRequest {
  code: string;
  input: Record<string, unknown>;
  timeoutMs?: number;
  net?: string[];
  extract?: ExtractGrant;
}

export interface ExecuteResult {
  ok: boolean;
  effects: Record<string, unknown> | null;
  logs: string[];
  error: string | null;
  stats: { durationMs: number };
}
```

- [ ] `limits.ts` — canonical cap + paired comment:

```ts
// Shared result-size caps. worker.ts applies them best-effort inside the
// sandbox, but the script shares the worker global and can call
// self.postMessage directly — so executor.ts re-applies the same caps
// host-side on every received message.
// Counterpart: tietue re-clamps identical numbers on receipt — keep MAX_LOGS /
// MAX_LOG_CHARS equal to MaxLogEntries / MaxLogChars in
// src/toimi.tools.tietue/Scripts/SuoritinClient.cs (SuoritinIntegrationTests
// pins the log-entry agreement across the seam).
export const MAX_LOGS = 100;
export const MAX_LOG_CHARS = 2000;
export const MAX_ERROR_CHARS = 2000;
```

- [ ] `main.ts` — replace `validateOptionalFields` (keep `isStringArray`; keep the null-as-absent comment, now naming its counterpart):

```ts
// Returns an error message, or null when the optional fields are well-typed.
// An explicit JSON null counts as absent (== null covers both), matching the
// executor's `??` defaults — the counterpart serializer (SuoritinClient.cs,
// WhenWritingNull) omits absent fields, so null only appears if that drifts.
// A PRESENT extract must be a complete {url, token}: partial or null-membered
// objects are rejected, not silently degraded to "no extract".
function validateOptionalFields(p: Record<string, unknown>): string | null {
  if (p.timeoutMs != null && typeof p.timeoutMs !== "number") {
    return "'timeoutMs' must be a number";
  }
  if (p.net != null && !isStringArray(p.net)) {
    return "'net' must be an array of strings";
  }
  if (p.extract != null) {
    if (typeof p.extract !== "object" || Array.isArray(p.extract)) {
      return "'extract' must be an object";
    }
    const e = p.extract as Record<string, unknown>;
    if (typeof e.url !== "string" || !URL.canParse(e.url)) {
      return "'extract.url' must be a valid URL";
    }
    if (typeof e.token !== "string" || e.token.length === 0) {
      return "'extract.token' must be a non-empty string";
    }
  }
  return null;
}
```

- [ ] `executor.ts` — net comes verbatim from the request; the worker message shrinks. Replace the net-composition block and the `postMessage`:

```ts
  // Net permission = exactly the request's `net`: tietue composes it (script
  // allowedHosts + the extract-callback host when llm is granted, see
  // ScriptHandler.BuildNet) — this side never widens it.
  const net = req.net ?? [];
```

```ts
    worker.postMessage({
      code: req.code,
      input: req.input ?? {},
      extract: req.extract,
    });
```

  (Everything else — `clampTimeout`, `clamp`, `MAX_EFFECTS_BYTES` re-check, `terminate()` — stays byte-identical. Add one counterpart line to the `MAX_EFFECTS_BYTES` comment: `// counterpart: SuoritinClient.cs MaxEffectsBytes`.)
- [ ] `worker.ts` — replace the `onmessage` grant/route logic (log capture, `toDataUrl`, `post` stay identical):

```ts
self.onmessage = async (e: MessageEvent) => {
  const { code, input, extract } = e.data;
  try {
    if (extract) {
      const host = new URL(extract.url).host;
      // Defense in depth: this worker's net permission already scopes every
      // fetch, so an out-of-allowlist callback would be denied by Deno anyway —
      // but refuse it explicitly so a mis-composed request (or a compromised
      // caller) fails with a clear error instead of a permission trace.
      if (Deno.permissions.querySync({ name: "net", host }).state !== "granted") {
        throw new Error(`extract callback host ${host} is not in the net allowlist`);
      }
      // The URL arrives fully composed; this sandbox knows no route shapes
      // (counterpart: ExtractEndpoints.cs Route/TokenHeader/CallbackUrl).
      input.extract = async (
        prompt: string,
        text: string,
        schema?: unknown,
      ) => {
        const res = await fetch(extract.url, {
          method: "POST",
          headers: {
            "content-type": "application/json",
            "x-run-token": extract.token,
          },
          body: JSON.stringify({ prompt, text, schema }),
        });
        if (!res.ok) {
          throw new Error(`extract failed: ${res.status} ${await res.text()}`);
        }
        return await res.json();
      };
    }
    const mod = await import(toDataUrl(code));
    if (typeof mod.default !== "function") {
      throw new Error("script must default-export a function(input)");
    }
    const effects = await mod.default(input) ?? {};
    post({ ok: true, effects, logs, error: null });
  } catch (err) {
    post({
      ok: false,
      effects: null,
      logs,
      error: String((err as Error)?.message ?? err),
    });
  }
};
```

- [ ] `deno task test` → all green (~34 tests). `deno fmt && deno lint` → clean.
- [ ] .NET side still compiles and its non-Docker suite is untouched: `export PATH=… && dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName!~SuoritinIntegrationTests"` → green (SuoritinIntegrationTests excluded this once — the freshly built image now speaks v2 while the client still speaks v1; Task 3 re-aligns and re-enables).
- [ ] Commit: `refactor(suoritin): wire contract v2 — net + extract replace grants/runToken/callbackUrl`

---

## Task 3: tietue speaks v2 — request composition + callback route ownership

**Files**
- Edit: `src/toimi.tools.tietue/Scripts/SuoritinClient.cs` (records, payload, cap comments)
- Edit: `src/toimi.tools.tietue/Scripts/ExtractEndpoints.cs` (Route/TokenHeader constants, header binding, `CallbackUrl`)
- Edit: `src/toimi.tools.tietue/Handlers/ScriptHandler.cs` (`BuildNet`, extract grant)
- Edit tests: `SuoritinClientTests.cs`, `ScriptHandlerTests.cs`, `JobEndToEndTests.cs`, `ExtractEndpointsTests.cs`, `SuoritinIntegrationTests.cs` (ctor call sites)

**Interfaces**
- `public record ExtractGrant(string Url, string Token);`
- `public record SuoritinRequest(string Code, JsonElement Input, int TimeoutMs, string[] Net, ExtractGrant? Extract);`
- `ExtractEndpoints`: `const string Route = "/internal/runs/extract"`, `const string TokenHeader = "X-Run-Token"`, `static string CallbackUrl(string callbackBaseUrl)`, `HandleAsync(string? token, …)`.
- `ISuoritinClient`/`SuoritinResult`/`FakeSuoritinClient` shapes otherwise unchanged.

**Steps**

- [ ] Update tests FIRST where they express the new contract (RED — compile break drives the rewire):
  - `SuoritinClientTests.cs`: `Request()` helper → `new SuoritinRequest(code, input.RootElement.Clone(), 20000, ["api.example.com"], null)`. In `Sends_camelcase_payload_with_all_fields`, replace the `allowedHosts`/`grants` asserts and add the omission/presence facts:

```csharp
  [Fact]
  public async Task Sends_camelcase_payload_with_net_and_no_capability_vocabulary()
  {
    var stub = new StubHandler(/*lang=json,strict*/ """{"ok":true,"effects":{},"logs":[],"error":null,"stats":{"durationMs":1}}""");
    var client = new SuoritinClient(new StubFactory(stub));

    await client.ExecuteAsync(Request("CODE"));

    using var sent = JsonDocument.Parse(stub.LastRequestBody!);
    Assert.Equal("CODE", sent.RootElement.GetProperty("code").GetString());
    Assert.Equal(20000, sent.RootElement.GetProperty("timeoutMs").GetInt32());
    Assert.Equal("api.example.com", sent.RootElement.GetProperty("net")[0].GetString());
    // Grants/allowedHosts/runToken/callbackUrl never cross the seam anymore.
    Assert.False(sent.RootElement.TryGetProperty("grants", out _));
    Assert.False(sent.RootElement.TryGetProperty("allowedHosts", out _));
    // Absent extract is OMITTED, not JSON null (suoritin's null-as-absent
    // tolerance is a backstop, not the contract).
    Assert.False(sent.RootElement.TryGetProperty("extract", out _));
  }

  [Fact]
  public async Task Present_extract_serializes_as_camelcase_url_and_token()
  {
    var stub = new StubHandler(/*lang=json,strict*/ """{"ok":true,"effects":{},"logs":[],"error":null,"stats":{"durationMs":1}}""");
    var client = new SuoritinClient(new StubFactory(stub));
    using var input = JsonDocument.Parse(/*lang=json,strict*/ """{"data":{}}""");

    await client.ExecuteAsync(new SuoritinRequest(
      "CODE", input.RootElement.Clone(), 20000, ["h.example"],
      new ExtractGrant("http://tietue.test/internal/runs/extract", "tok")));

    using var sent = JsonDocument.Parse(stub.LastRequestBody!);
    var extract = sent.RootElement.GetProperty("extract");
    Assert.Equal("http://tietue.test/internal/runs/extract", extract.GetProperty("url").GetString());
    Assert.Equal("tok", extract.GetProperty("token").GetString());
  }
```

    `Caps_log_count_and_entry_length` needs no logic change — its 106 stub entries still exceed the (now 100) cap and it asserts against the constant.
  - `ScriptHandlerTests.cs`: replace the wire asserts —
    - `Sends_inline_config_script_to_suoritin_and_applies_effects`: `Assert.Equal(["api.example.com"], request.Net); Assert.Null(request.Extract);` (delete the `request.Grants` assert — grant enforcement is proven by the `mcp.Calls` assert on the same run).
    - `From_entity_mode_reads_code_hosts_grants_from_entity_data`: `Assert.Equal(["a.example"], request.Net);` (grants still resolved from the entity — they now surface only in behavior, not on the wire).
    - `Llm_grant_issues_token_and_callback_url` →

```csharp
  [Fact]
  public async Task Llm_grant_ships_extract_and_widens_net_to_the_callback_host()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, tokens, handler) = await SetupAsync(db);
    var config = /*lang=json,strict*/ """{"source":"export default () => ({})","capabilities":["llm"],"allowedHosts":["api.example.com"]}""";

    await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    var request = Assert.Single(suoritin.Requests);
    Assert.NotNull(request.Extract);
    // Full URL composed here: the sandbox never learns the route shape.
    Assert.Equal(
      ExtractEndpoints.CallbackUrl(new SuoritinOptions().CallbackBaseUrl),
      request.Extract!.Url);
    // net = allowedHosts + callback host, and nothing else.
    Assert.Equal(["api.example.com", "toimi-tools-tietue.apps.svc.cluster.local"], request.Net);
    Assert.False(tokens.TryUseExtract(request.Extract.Token)); // revoked after the run
  }
```

    - `No_llm_grant_means_no_token` →

```csharp
  [Fact]
  public async Task No_llm_grant_means_no_extract_and_no_net_widening()
  {
    using var db = TestDb.New();
    var (e, suoritin, _, _, handler) = await SetupAsync(db);
    var config = /*lang=json,strict*/ """{"source":"export default () => ({})","capabilities":["setField"],"allowedHosts":["api.example.com"]}""";

    await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    var request = Assert.Single(suoritin.Requests);
    Assert.Null(request.Extract);
    Assert.Equal(["api.example.com"], request.Net);
  }
```

  - `JobEndToEndTests.cs`: replace the two wire asserts with `Assert.Equal(["api.open-meteo.com"], request.Net); Assert.Null(request.Extract);` (the entity's `grants` field itself is untouched — same `JobJson`).
  - `ExtractEndpointsTests.cs`: add the missing-header fact:

```csharp
  [Fact]
  public async Task Missing_token_header_is_403()
  {
    var result = await ExtractEndpoints.HandleAsync(null, Request(), new RunTokenStore(), new FakeLlmExtractor(), default);
    Assert.Equal(403, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
  }
```

  - `SuoritinIntegrationTests.cs`: update both existing requests to the 5-arg ctor — `new SuoritinRequest(code, input.RootElement.Clone(), 10000, [], null)` (both already used empty hosts / no grants).
- [ ] `dotnet build src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj` → compile errors enumerate every remaining v1 call site (RED harness).
- [ ] `SuoritinClient.cs` — records, caps comment, payload:

```csharp
public record ExtractGrant(string Url, string Token);

public record SuoritinRequest(
  string Code,
  JsonElement Input,
  int TimeoutMs,
  string[] Net,
  ExtractGrant? Extract);
```

```csharp
  // tietue-side caps on suoritin's untrusted output (spec §4/§7); the named
  // HTTP client additionally caps the whole response body at 1 MB.
  // Counterparts (keep equal — a shared artifact across C#/TS isn't feasible,
  // paired comments + SuoritinIntegrationTests are the discipline):
  //   MaxLogEntries  = MAX_LOGS        (suoritin limits.ts; sandbox truncates
  //                                     first, this Take() is a pure backstop)
  //   MaxLogChars    = MAX_LOG_CHARS   (limits.ts; also reused for error
  //                                     strings, = MAX_ERROR_CHARS)
  //   MaxEffectsBytes = MAX_EFFECTS_BYTES (suoritin executor.ts)
  public const int MaxLogEntries = 100;
  public const int MaxLogChars = 2000;
  public const int MaxEffectsBytes = 256 * 1024;
```

```csharp
    var payload = new
    {
      code = request.Code,
      input = request.Input,
      timeoutMs = request.TimeoutMs,
      net = request.Net,
      extract = request.Extract,
    };
```

  Update the `CamelCase` comment: `// WhenWritingNull: an absent extract must be omitted, not sent as JSON null — // counterpart: suoritin main.ts validateOptionalFields (null tolerated as absent, // malformed non-null rejected).`
- [ ] `ExtractEndpoints.cs` — route ownership + header token (add `using Microsoft.AspNetCore.Mvc;`):

```csharp
  /// <summary>
  /// The extract() callback contract, owned here alone: the worker receives a
  /// fully composed <see cref="CallbackUrl"/> and POSTs to it with the run
  /// token in <see cref="TokenHeader"/> — it never knows the route shape
  /// (counterpart: suoritin worker.ts extract passthrough).
  /// </summary>
  public const string Route = "/internal/runs/extract";
  public const string TokenHeader = "X-Run-Token";

  public static string CallbackUrl(string callbackBaseUrl)
  {
    return new Uri(new Uri(callbackBaseUrl), Route).ToString();
  }

  public static void MapExtractEndpoints(WebApplication app)
  {
    app.MapPost(Route, (
      [FromHeader(Name = TokenHeader)] string? token,
      ExtractRequest request,
      RunTokenStore tokens,
      ILlmExtractor extractor,
      CancellationToken ct) => HandleAsync(token, request, tokens, extractor, ct));
  }

  public static async Task<IResult> HandleAsync(
    string? token, ExtractRequest request, RunTokenStore tokens, ILlmExtractor extractor, CancellationToken ct)
  {
    if (string.IsNullOrEmpty(token) || !tokens.TryUseExtract(token))
    {
      return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    // … remainder unchanged …
```

- [ ] `ScriptHandler.cs` — build the v2 request (token issuance/revocation and grant resolution unchanged):

```csharp
    string? token = null;
    ExtractGrant? extract = null;
    if (script.Grants.Contains("llm", StringComparer.OrdinalIgnoreCase))
    {
      token = tokens.Issue(ctx.Entity.Id, script.Grants, _budget.TokenTtl);
      extract = new ExtractGrant(ExtractEndpoints.CallbackUrl(suoritinOptions.CallbackBaseUrl), token);
    }

    var request = new SuoritinRequest(
      script.Source,
      BuildInput(ctx),
      _budget.ScriptMs,
      BuildNet(script.AllowedHosts, extract),
      extract);
```

  and the composer (private static, near `BuildInput`):

```csharp
  /// <summary>
  /// The sandbox's entire egress: the script's declared hosts plus — only when
  /// llm is granted — the extract-callback host. suoritin applies this verbatim
  /// as the worker's net permission (executor.ts) and must never widen it;
  /// composing it here keeps the capability vocabulary on this side of the seam.
  /// Host format mirrors JS URL.host: port only when non-default.
  /// </summary>
  private static string[] BuildNet(string[] allowedHosts, ExtractGrant? extract)
  {
    if (extract is null)
    {
      return allowedHosts;
    }

    var uri = new Uri(extract.Url);
    var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    return allowedHosts.Contains(host) ? allowedHosts : [.. allowedHosts, host];
  }
```

- [ ] Full tietue suite INCLUDING Docker: `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj` → green, `SuoritinIntegrationTests` listed as passed (the rebuilt image and the client now both speak v2). If they were cached-skipped, force with `--filter "FullyQualifiedName~SuoritinIntegrationTests"` and confirm 2 passed.
- [ ] `dotnet format` apply + `--verify-no-changes` on `toimi.tools.tietue` and `toimi.tools.tietue.Tests`.
- [ ] Commit: `refactor(tietue): compose net + extract for suoritin; ExtractEndpoints owns the callback route`

---

## Task 4: cross-seam contract tests (the seam's executable spec)

**Files**
- Edit: `src/toimi.tools.tietue.Tests/SuoritinIntegrationTests.cs`

**Interfaces** — no production changes; three new `[DockerFact]`s against the real image.

**Steps**

- [ ] Add a raw-HTTP helper (the client's own truncation must not mask the contract):

```csharp
  private static HttpClient RawClientFor(IContainer container)
  {
    return new HttpClient
    {
      BaseAddress = new Uri($"http://{container.Hostname}:{container.GetMappedPublicPort(8080)}"),
      Timeout = TimeSpan.FromSeconds(30),
    };
  }
```

- [ ] Log-entry cap agreement — observed on the raw wire, pinned to tietue's constant:

```csharp
  [DockerFact]
  public async Task Log_entry_cap_agrees_across_the_seam()
  {
    await using var container = await StartContainerAsync();
    using var http = RawClientFor(container);
    var over = SuoritinClient.MaxLogEntries + 50;

    using var response = await http.PostAsJsonAsync("/execute", new
    {
      code = $"export default function run() {{ for (let i = 0; i < {over}; i++) console.log('line', i); return {{}}; }}",
      input = new { },
    });
    response.EnsureSuccessStatusCode();
    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    // Raw count, before SuoritinClient's Take(): if limits.ts MAX_LOGS ever
    // drifts from MaxLogEntries again, one side silently discards lines and
    // this assert is the tripwire.
    Assert.Equal(SuoritinClient.MaxLogEntries, doc.RootElement.GetProperty("logs").GetArrayLength());
  }
```

- [ ] Absent-vs-null and extract-shape validation — the serializer invariant, exercised against the real validator:

```csharp
  [DockerFact]
  public async Task Explicit_nulls_are_tolerated_but_a_malformed_extract_is_rejected()
  {
    await using var container = await StartContainerAsync();
    using var http = RawClientFor(container);

    // Null-as-absent backstop (SuoritinClient omits via WhenWritingNull; this
    // guards against serializer drift ever sending nulls again).
    using var nulls = await http.PostAsync("/execute", new StringContent(
      /*lang=json,strict*/ """{"code":"export default () => ({})","input":{},"timeoutMs":null,"net":null,"extract":null}""",
      System.Text.Encoding.UTF8, "application/json"));
    Assert.Equal(HttpStatusCode.OK, nulls.StatusCode);

    // A PRESENT extract must be complete — null members are rejected, not
    // degraded to "no extract".
    using var malformed = await http.PostAsync("/execute", new StringContent(
      /*lang=json,strict*/ """{"code":"export default () => ({})","input":{},"extract":{"url":null,"token":null}}""",
      System.Text.Encoding.UTF8, "application/json"));
    Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
  }
```

- [ ] Client round-trip at the cap — through the real `SuoritinClient`, nothing discarded:

```csharp
  [DockerFact]
  public async Task Client_receives_exactly_the_canonical_log_cap()
  {
    await using var container = await StartContainerAsync();
    using var input = JsonDocument.Parse(/*lang=json,strict*/ """{"data":{}}""");
    var over = SuoritinClient.MaxLogEntries + 50;

    var result = await ClientFor(container).ExecuteAsync(new SuoritinRequest(
      $"export default function run() {{ for (let i = 0; i < {over}; i++) console.log('line', i); return {{}}; }}",
      input.RootElement.Clone(), 10000, [], null));

    Assert.True(result.Ok, result.Error);
    Assert.Equal(SuoritinClient.MaxLogEntries, result.Logs.Length);
  }
```

  (Extract is deliberately NOT tested end-to-end here — the container cannot reach a host-side callback without new fixture plumbing; `executor_test.ts` covers the sandbox half against a local server and `ExtractEndpointsTests` the tietue half; the composed-URL/header literals are pinned by Task 3's unit tests on both sides.)
- [ ] Add needed usings (`System.Net`, `System.Net.Http.Json`) if missing; run the file for real: `export PATH=… && dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~SuoritinIntegrationTests"` → 5 passed, 0 skipped.
- [ ] `dotnet format` apply + verify on `toimi.tools.tietue.Tests`.
- [ ] Commit: `test(tietue): cross-seam contract tests — log-cap agreement, null tolerance, extract shape`

---

## Task 5: full gate + CLAUDE.md

**Files**
- Edit: `/Users/jari/private/toimi/CLAUDE.md` (suoritin bullet + "Sandboxed scripts" pattern bullet)

**Steps**

- [ ] Full .NET gate, all three suites, Docker tests running for real:

```bash
export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj   # ≥ 393, 0 failed, 0 skipped-when-Docker-present
dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj                   # 93
dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj                     # 38
```

  (Locate the exact core/web test csproj names with `ls src/*Tests*` if they differ; the counts are the contract.) Confirm the tietue output shows `SuoritinIntegrationTests` executed (5 Docker facts passed).
- [ ] Full Deno gate: `cd src/toimi.tools.suoritin && deno task test && deno fmt --check && deno lint` → all green.
- [ ] `scripts/lint.sh` → passes (dotnet format across the solution; yamllint/shellcheck skip locally per environment).
- [ ] Update CLAUDE.md's suoritin bullet — the `POST /execute` shape line becomes:

```
- Owns: executing all AI-authored scripts (`job` entities + inline trigger
  scripts) in per-run Deno Workers. `POST /execute {code, input, timeoutMs,
  net, extract?: {url, token}}` → `{ok, effects, logs, stats}`. tietue composes
  `net` (allowedHosts + extract-callback host iff llm granted) and the full
  extract URL — capability names and route shapes never reach the sandbox.
```

  and in the "Sandboxed scripts" Key Pattern bullet, change "worker net permission = the script's `allowedHosts`" to "worker net permission = the request's `net`, composed by tietue from the script's `allowedHosts` (+ the extract-callback host when `llm` is granted)". Leave every other claim (effects vocabulary in tietue, kill switches, token-gated `extract()`) as is — still true.
- [ ] Commit: `docs: suoritin wire contract v2 in CLAUDE.md`

---

## Self-review checklist (verified against the code as read)

- Finding 1 (grants leak): closed in Tasks 2–3 — `SuoritinRequest`/`ExecuteRequest` carry `net`/`extract` only; `worker.ts` no longer inspects grants; `ScriptEffectApplier` untouched. ✓
- Finding 2 (limit drift): canonical cap 100 decided + justified; `limits.ts` lowered; paired comments both sides; raw-wire tripwire test. ✓
- Finding 3 (timeout prose): `ScriptBudget` owns Script/Http/Watchdog/TokenTtl/Effects with the clamp mirrored from `MAX_TIMEOUT_MS`; `-10` hack replaced by a valid 40ms budget through a real ctor seam. ✓
- Finding 4 (route duplication): `ExtractEndpoints.Route`/`TokenHeader`/`CallbackUrl` are the single owner; worker POSTs verbatim. ✓
- Security: egress = `allowedHosts` + callback host iff llm (BuildNet + executor verbatim `net`); worker refuses out-of-allowlist extract.url via its own permission state (improvement over today's no-check); `RunTokenStore`, 1 MiB caps, `MAX_CONCURRENT=4`, credential-free suoritin, host-side re-clamp, opaque effects all untouched. ✓
- Both sides' tests updated (SuoritinClientTests, ScriptHandlerTests, JobEndToEndTests, ExtractEndpointsTests, SuoritinIntegrationTests, executor_test.ts, main_test.ts); TypeSeederTests compiles unchanged via the optional budget param. ✓
- No placeholders; signatures consistent across tasks (`SuoritinRequest` 5-arg everywhere from Task 3 on; `HandleAsync(string? token, …)`). ✓
