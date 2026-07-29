# Tier 3 Test Gaps Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the remaining deferred gaps from the 2026-07-29 coverage review: persistence/repository pins, the MCP resilience wrapper, Jint sandbox limits, five small real fixes (expiry zombie triggers, ntfy error detail, koti error hardening, hub auto-title after compaction, two frontend admin-hook bugs), plus seams (FetchCache TimeProvider) and the admin proxy contract.

**Architecture:** Branch base `wip` (Tiers 1+2 landed: 441 .NET tests, vitest harness, hub test fakes, koti/notifications test projects all exist). Same discipline as prior rounds: TDD where a fix is made, characterization pins elsewhere; observed behavior wins for pins; a crash or surprise during characterization is reported (DONE_WITH_CONCERNS), not silently patched.

**Environment (critical):** work from `/Users/jari/private/toimi/.claude/worktrees/tier3-test-gaps` (branch `worktree-tier3-test-gaps`). `mise exec dotnet -- dotnet <args>`; `mise exec node -- npm <args>`. Format every changed C# project (`dotnet format <csproj>` then `--verify-no-changes`) before each commit. Commits `<type>(<scope>): <subject>` + Co-Authored-By Claude line (blank line before it). 2-space indent, file-scoped namespaces.

**Baseline:** 441 .NET tests + 2 frontend tests, all green.

---

### Task 1: core — ConversationRepository pins

**Files:** Modify `src/toimi.core.Tests/toimi.core.Tests.csproj` (add `<PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" Version="10.0.10" />`); Create `src/toimi.core.Tests/ConversationRepositoryTests.cs`.

- [ ] Fresh `UseInMemoryDatabase($"core-{Guid.NewGuid()}")` context per test (pattern: `toimi.web.Tests/ToimiHubTests.cs`). **InMemory ignores the Postgres `now()`/`gen_random_uuid()` column defaults** — set `CreatedAt` explicitly on entities when ordering matters, and note this provider gap in a file-header comment.
- [ ] Tests (assertions are the spec; read `src/toimi.core/Data/ConversationRepository.cs` + the two `*Configuration.cs` first):
  1. `Create_then_add_messages_round_trips_in_insertion_order` — create → add user msg → add assistant msg (with `toolCallsJson` and token counts) → `GetByIdAsync` returns both messages with content/`ToolCallsJson`/tokens intact (set `CreatedAt` explicitly via the returned `ConversationMessage` entities + `SaveChangesAsync` so the `OrderBy(CreatedAt)` include is meaningful, then re-fetch).
  2. `AddMessage_bumps_LastMessageAt` — after adding, conversation's `LastMessageAt` > its `CreatedAt`.
  3. `ListRecent_returns_most_recently_active_first_and_respects_limit` — three conversations with staggered `LastMessageAt`, `ListRecentAsync(limit: 2)` → newest two, descending.
  4. `Delete_removes_child_messages_and_returns_false_for_missing` — delete cascades (no orphan `ConversationMessages` rows); unknown id → false.
  5. `AddMessage_to_unknown_conversation_pins_current_behavior` — `AddMessageAsync(Guid.NewGuid(), ...)`: under InMemory (no FK enforcement) the row is written and no bump happens; pin exactly what you observe, with a comment that real Postgres would FK-throw — this is a characterization of the repository's (lack of) guard, feeding a future hardening decision.
- [ ] Run core suite, format, commit: `test(core): pin ConversationRepository persistence contract`.

### Task 2: core — ResilientMcpTool via InternalsVisibleTo

**Files:** Modify `src/toimi.core/toimi.core.csproj` (add `<ItemGroup><InternalsVisibleTo Include="toimi.core.Tests" /></ItemGroup>`); Create `src/toimi.core.Tests/ResilientMcpToolTests.cs`.

- [ ] Read `src/toimi.core/ResilientMcpTool.cs` first. Test double: a throwing `AIFunction` subclass —

```csharp
  private sealed class ThrowingFunction(Exception ex) : AIFunction
  {
    public override string Name => "probe";
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
      throw ex;
    }
  }
```

(If `AIFunction` demands more overrides to compile, add minimal ones and report.) Plus a capturing logger:

```csharp
  private sealed class CapturingLogger : ILogger
  {
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
      Messages.Add(formatter(state, exception));
    }
  }
```

- [ ] Construct `new ResilientMcpTool(new McpToolAggregator(), "srv", new ThrowingFunction(...), logger)` (aggregator has no connections, so `ReconnectAndGetToolAsync` returns null and the ORIGINAL exception rethrows — that's the observable). The reconnect-attempt discriminator is the log: the `"failed with transport error, reconnecting"` warning fires only on the transport path. Tests:
  1. `Cancellation_rethrows_without_attempting_reconnect` — `OperationCanceledException` propagates; `logger.Messages` contains NO "reconnecting" entry (a regression here turns every user cancel into a reconnect storm).
  2. `Transport_fault_attempts_reconnect_then_surfaces_original` — `HttpRequestException` propagates (aggregator can't reconnect) AND a "reconnecting" log entry exists.
  3. `Non_transport_exception_passes_through_without_reconnect` — `InvalidOperationException` propagates, no "reconnecting" entry.
  4. `Wrapped_transport_fault_still_classifies` — `new AggregateException(new McpException("gone"))`... note `IsTransportFault` walks `InnerException`, and `AggregateException` wraps via InnerException — verify with `new Exception("outer", new McpException("inner"))` (guaranteed InnerException chain): propagates + "reconnecting" logged.
- [ ] Invoke via `tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None)` (or the minimal public entry that reaches `InvokeCoreAsync` — adapt and report). Run, format BOTH csprojs, commit: `test(core): pin ResilientMcpTool retry classification via InternalsVisibleTo`.

### Task 3: tietue — ScriptEngine sandbox limit characterization

**Files:** Modify `src/toimi.tools.tietue.Tests/ScriptEngineTests.cs` (append; read it first to mirror style).

- [ ] Append (characterization — if any assertion fails, pin observed behavior; if the ReDoS case runs >10s the timeout is NOT interrupting mid-statement → DONE_WITH_CONCERNS, that's a scheduler-stall finding):
  1. `Catastrophic_regex_is_interrupted_within_the_timeout` — source: `return { hit: /(a+)+$/.test('a'.repeat(40) + 'b') };` — wrap `Evaluate` in a `Stopwatch`; assert result `"{}"` AND elapsed < 10s (generous bound; the configured `TimeoutInterval` is 2s).
  2. `Memory_limit_stops_huge_allocations` — `return { s: 'a'.repeat(1e9) };` → `"{}"`, no `OutOfMemoryException` escaping.
  3. `Huge_array_fill_is_stopped` — `return { a: new Array(1e7).fill(0).length };` → pin result (`"{}"` if the memory limit trips; if it actually completes, pin the real output and note it).
  4. `Malformed_data_json_yields_no_effects` — `Evaluate("return {ok:true}", "not json")` → `"{}"`.
  5. `Sandbox_exposes_no_host_globals` — source: `return { globals: Object.getOwnPropertyNames(globalThis).join(',') };` — parse the returned JSON, assert the `globals` string does NOT contain `System`, `importNamespace`, or `clr` (a Jint upgrade re-adding host interop must fail loudly here).
- [ ] Run tietue suite, format, commit: `test(tietue): characterize script sandbox limits`.

### Task 4: tietue — expiry zombie-trigger fix (TDD)

**Bug:** a garbage `expiresAt` (`"soon"`) flows `ExpiryReconciler` → `TriggerRepository.CreateAsync` → `Schedules.InitialNextFireAt` null → a trigger persisted with `Enabled=true, NextFireAt=null` — the same dead-but-enabled state Task 6 of Tier 2 eliminated for `UpdateAsync`, still reachable at creation (also via `ScriptEffectApplier`; `SetTriggerTool` pre-validates so the LLM path is safe).

**Fix (system-wide invariant at the source):** in `src/toimi.tools.tietue/Scheduling/TriggerRepository.cs` `CreateAsync`, after computing `NextFireAt`, set `Enabled = NextFireAt is not null` instead of the literal `true` (add the one-line comment: a trigger that can never fire must not sit enabled and invisible to the scheduler). A valid PAST `at` still yields a non-null past instant → enabled → fires next tick (the Expiry immediate-delete flow is untouched).

**Files:** Modify `TriggerRepository.cs`; Modify `src/toimi.tools.tietue.Tests/ExpiryReconcilerTests.cs` + `TriggerRepositoryTests.cs` (append; read both first, mirror setup).

- [ ] Red tests first:
  1. `TriggerRepositoryTests`: `Create_with_unresolvable_schedule_yields_a_disabled_trigger` — `CreateAsync(entityId, """{"at":"soon"}""", "notify", null, now)` → assert `!(t.Enabled && t.NextFireAt is null)` and specifically `Assert.False(t.Enabled)`. (FAILS today: Enabled=true.)
  2. `ExpiryReconcilerTests`: `Garbage_expiry_date_does_not_arm_a_zombie_trigger` — entity with `expiresAt: "soon"` + Expiry behavior → reconcile → the provisioned expiry trigger (if any) is NOT `Enabled=true, NextFireAt=null`. (FAILS today.)
  3. `ExpiryReconcilerTests`: `Past_expiry_date_arms_an_immediately_due_trigger` — `expiresAt` in the past → trigger Enabled with `NextFireAt` = that past instant (PIN: this is intended — an already-expired entity is cleaned up on the next tick; expected to pass both before and after).
- [ ] Apply the one-line fix; full tietue suite (all existing CreateAsync-based tests use valid schedules and must stay green — if any existing test seeded an unresolvable schedule expecting Enabled=true, STOP and report). Format both csprojs, commit: `fix(tietue): never create an enabled trigger that cannot fire`.

### Task 5: notifications — payload/auth pins + error-detail fix (TDD)

**Files:** Modify `src/toimi.notifications/NtfyClient.cs`; Modify `src/toimi.notifications.Tests/NtfyClientTests.cs` (append; reuse its StubHandler — extend it to also capture `Headers.Authorization` and to return a configurable response).

- [ ] Pins (pass today): `title`/`tags` keys ABSENT from payload JSON when null; `tags: "package, delivered"` → `["package","delivered"]` (trimmed); `topic` always from options; BaseUrl with and without trailing slash POST to the same URL; auth matrix — header only when both username+password non-empty (`(u,p)` → `Basic base64(u:p)`; `(u,null)`, `(null,p)`, `("","")` → no header); UTF-8 creds (`"käyttäjä"/"salasana"`) round-trip through base64 as UTF-8.
- [ ] Fix (red first): `Error_response_includes_ntfy_diagnostic_body` — stub 403 with body `{"code":40301,"error":"forbidden"}` → the thrown exception's message must contain `forbidden`. Today `EnsureSuccessStatusCode` discards the body. Replace lines 59-60 with:

```csharp
    var response = await _http.SendAsync(request, ct);
    if (!response.IsSuccessStatusCode)
    {
      var body = await response.Content.ReadAsStringAsync(ct);
      throw new HttpRequestException(
        $"ntfy returned {(int)response.StatusCode} ({response.StatusCode}): {body}", null, response.StatusCode);
    }
```

  (Callers pin: verkko's `SendNotificationTool` catch-all surfaces `ex.Message` — now actionable. Run the verkko suite too.)
- [ ] Format, commit: `fix(notifications): surface ntfy error bodies; pin payload and auth contract`.

### Task 6: koti — client config pins + tool error hardening (TDD)

**Files:** Modify all four tools in `src/toimi.tools.koti/Tools/` (`GetEntityStateTool.cs`, `GetHistoryTool.cs`, `CallServiceTool.cs`, `ListEntitiesTool.cs` — the last only for its remaining uncaught `GetStatesAsync` call); Create `src/toimi.tools.koti.Tests/HomeAssistantClientConfigTests.cs`; Modify `CallServiceToolTests.cs`/`ListEntitiesToolTests.cs` or new `ToolErrorHandlingTests.cs` (your choice — keep one coherent home).

- [ ] Config pins (`HomeAssistantClientConfigTests`, StubHandler capturing `RequestUri` + `Authorization`):
  1. BaseUrl `http://ha.test:8123` and `http://ha.test:8123/` → identical absolute request URI for `GetStatesAsync`.
  2. Sub-path base `http://ha.test:8123/hass` → requests hit `/hass/api/states` (the `+ "/"` in the ctor is load-bearing for reverse-proxied installs).
  3. Token `"Bearer abc"` and `"bearer abc"` → `Authorization.Parameter == "abc"`; plain `"abc"` → `"abc"`.
- [ ] Error hardening (red first — currently raw exceptions escape): every tool returns a friendly string when HA is unreachable/erroring, mirroring verkko's `SendNotificationTool` pattern. Wrap each tool's client call(s):

```csharp
    try
    {
      ...existing body...
    }
    catch (HttpRequestException ex)
    {
      return $"Home Assistant request failed: {ex.Message}";
    }
    catch (TaskCanceledException)
    {
      return "Home Assistant request timed out.";
    }
```

  Red tests: `GetEntityState` with stub 401 → friendly string, and the string does NOT contain the bearer token; `GetHistory` with handler throwing `HttpRequestException("boom")` → friendly string; `CallService` with stub 500 → friendly string (adjust the Tier-1 `Empty_success_body` test ONLY if the new try/catch changes its shape — it shouldn't). `ListEntities` non-array/template cases already covered; add one: `GetStatesAsync` throwing `HttpRequestException` → friendly string.
  Also pin: `GetEntityState` stub 404 → still exactly `"Entity not found."` (the 404→null mapping must survive the hardening).
- [ ] Full koti suite, format, commit: `fix(koti): return readable errors when Home Assistant is unreachable`.

### Task 7: verkko — FetchCache TimeProvider seam + FetchUrlTool tests

**Files:** Modify `src/toimi.tools.verkko/Fetcher/FetchCache.cs`; Modify `src/toimi.tools.verkko/Program.cs` ONLY if DI registration needs it (check — `FetchCache` is likely registered as singleton with a parameterless ctor; an optional ctor arg keeps that working, verify by building); Modify `src/toimi.tools.verkko.Tests/FetchCacheTests.cs` (append); Create `src/toimi.tools.verkko.Tests/FetchUrlToolTests.cs`.

- [ ] Seam: `public class FetchCache(TimeProvider? time = null)` with `private readonly TimeProvider _time = time ?? TimeProvider.System;`; replace both `DateTime.UtcNow` uses with `_time.GetUtcNow().UtcDateTime`. Hand-rolled test clock (no new package):

```csharp
  private sealed class ManualTimeProvider : TimeProvider
  {
    public DateTimeOffset Now { get; set; } = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
  }
```

- [ ] New FetchCache tests: entry expires after 5 minutes (advance `Now` past TTL → `Get` null); `Set` sweeps expired entries; eviction over `MaxEntries` removes the SOONEST-expiring entry (stagger `Now` between sets, assert the survivor set). Existing FetchCacheTests must pass unchanged (parameterless ctor → system clock).
- [ ] `FetchUrlToolTests` (StubHandler counting requests; `new FetchUrlTool(new WebFetcher(new HttpClient(stub)), new FetchCache(clock))`):
  1. `Second_fetch_serves_from_cache_with_a_note` — two calls → 1 upstream request, second result contains `"(from cache)"`.
  2. `Skip_cache_bypasses_read_but_refreshes_entry` — call, then `skipCache: true` → 2 upstream requests; a third normal call → still 2 (refreshed entry served).
  3. `Http_error_composes_inner_reason_once` — handler throws `new HttpRequestException("outer", new Exception("inner detail"))` → result contains both `outer` and `inner detail`, and contains `inner detail` exactly once.
  4. `Timeout_maps_to_the_timed_out_message` — handler throws `TaskCanceledException` → `"Request timed out fetching ..."`.
  5. `Non_success_responses_are_cached` — stub 502; two calls → 1 upstream request (PIN with a comment: 5-minute caching of upstream errors is current, deliberate-looking behavior — surface it for a future decision).
- [ ] Full verkko suite, format, commit: `test(verkko): add FetchCache time seam and pin FetchUrl cache semantics`.

### Task 8: web — admin proxy contract pins

**Files:** Modify `src/toimi.web.Tests/AggregatorTests.cs` (append to whichever classes exist — read the file; it holds the StubHandler/StubFactory pattern and the existing forwarder tests).

- [ ] Forwarder tests (via `AdminForwarder.ForwardAsync` with `DefaultHttpContext`, mirroring the existing tests):
  1. `Put_forwards_body_and_content_type` — PUT with JSON body → captured upstream request has the body bytes and `Content-Type: application/json`.
  2. `Get_does_not_forward_a_body` — GET → upstream request `Content` is null.
  3. `If_unmodified_since_passes_through` — header present on the upstream request; a different header (e.g. `X-Custom`) is NOT forwarded (pin: only this one conditional header crosses).
  4. `Upstream_error_status_and_body_propagate` — upstream 409 + body → response 409 and the body bytes reach `ctx.Response.Body`.
  5. `Unreachable_upstream_maps_to_502` — handler throws `HttpRequestException` → 502 problem result.
- [ ] Aggregator tests (append): `Null_query_produces_an_empty_q_parameter` (captured URL contains `q=&`); `All_tools_failing_yields_empty_items_and_all_errors` (2 failing tools → 0 items, 2 errors).
- [ ] Run web suite, format, commit: `test(web): pin admin proxy forwarding contract`.

### Task 9: web — hub auto-title overwrite after compaction (TDD fix)

**Bug:** `ToimiHub.SendMessage` decides auto-title by `session.Messages.Count(m => m.Role == ChatRole.User) == 1`, evaluated AFTER `CompactIfNeeded` rewrote history. A compaction that leaves exactly one user message in the retained window silently overwrites a weeks-old conversation's title with the latest message.

**Fix:** capture first-message-ness from durable state, not the in-memory window: at the top of `SendMessage` (before the lazy-create block) add `var isFirstMessage = session.ConversationId is null;` and change the auto-title condition to `if (isFirstMessage)`. (The conversation row is created exactly once, on the true first message — that turn and only that turn titles it.)

**Files:** Modify `src/toimi.web/Hubs/ToimiHub.cs`; Modify `src/toimi.web.Tests/ToimiHubTests.cs`.

- [ ] Test infrastructure: the red test needs a hub session attached to an EXISTING conversation, which requires the query-param path through `OnConnectedAsync`. Extend `FakeHubCallerContext` to optionally carry an HttpContext:

```csharp
  private sealed class FakeHubCallerContext(string connectionId, string? conversationId = null) : HubCallerContext
  {
    // ... existing members unchanged, except Features:
    public override IFeatureCollection Features { get; } = BuildFeatures(conversationId);

    private static IFeatureCollection BuildFeatures(string? conversationId)
    {
      var features = new FeatureCollection();
      if (conversationId is not null)
      {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString($"?conversationId={conversationId}");
        features.Set<IHttpContextFeature>(new HttpContextFeature { HttpContext = http });
      }
      return features;
    }
  }
```

  (`HttpContextFeature` is `Microsoft.AspNetCore.Http.Features`' concrete implementation; if the name differs, implement the one-property interface inline.) Add a `ConnectedHub` overload taking `conversationId` and a `ToimiConfiguration` (so a test can pass `MaxContextTokens = 1`).
- [ ] Red test `Compaction_must_not_retrigger_auto_title_on_an_old_conversation`:
  1. Seed the DB directly: a conversation with `Title = "original title"`, one `user` row ("old question") and **twelve** `assistant` rows (role is just a string — this makes the replayed in-memory history assistant-heavy, so compaction summarizes the lone old user message away).
  2. `ConnectedHub(conversationId: <id>, config: new ToimiConfiguration { OpenAI = ..., MaxContextTokens = 1 })` — OnConnectedAsync replays 13 messages; `GetResponseAsync` on the scriptable fake already returns "summary" for the compaction call.
  3. `await hub.SendMessage("brand new topic that must not become the title");`
  4. Compaction leaves the just-added user message as the only `ChatRole.User` in the window → pre-fix the hub re-titles. Assert `db.Conversations.Single().Title == "original title"` — FAILS pre-fix (title becomes "brand new topic...").
  5. Also assert the existing happy path still titles: the pre-existing `SendMessage_streams_and_persists...` test gains one line — `Assert.Equal("hello", db.Conversations.Single().Title);` (the true first message still sets the title post-fix).
  If the red test does NOT go red (compaction thresholds shifted), do not force it — report DONE_WITH_CONCERNS with the observed message counts so the controller can rework the scenario.
- [ ] Apply the fix, green run, full web suite, format, commit: `fix(web): only auto-title on the conversation's true first message`.

### Task 10: frontend — admin hook fixes (TDD) + tool-call edge pins

**Files:** Modify `src/toimi.web/ClientApp/src/admin/useAdmin.ts` + `src/admin/useAdminSummary.ts`; Create `src/toimi.web/ClientApp/src/admin/useAdmin.test.ts`; Modify `src/toimi.web/ClientApp/src/hooks/useToimi.test.ts` (append).

- [ ] Red tests (`useAdmin.test.ts`, `vi.stubGlobal('fetch', vi.fn())` + `renderHook`/`waitFor` from the existing harness):
  1. `useAdminList surfaces a network failure as an error` — fetch rejects → `error` becomes non-null (status 0) and `loading` false. FAILS today (rejection escapes the try — only `finally` runs — leaving `error: null`, an empty-state lie).
  2. `useAdminSummary clears loading when the fetch rejects` — fetch rejects → `loading` false. FAILS today (`.then`-only chain → spinner forever).
  Fixes: in `useAdminList.reload` add `catch { setError({ status: 0 }) }` between try and finally; in `useAdminSummary` append `.catch(() => { if (!cancelled) setLoading(false) })`.
- [ ] Pins appended to `useToimi.test.ts` (existing FakeConnection harness):
  1. `unmatched ToolCallEnd is dropped silently` — send + one `ToolCallStart('a', ...)`, then `ToolCallEnd('never-started', ...)` → tool call `a` still `running`, no crash (pin the drop).
  2. `interleaved tool calls complete independently` — start a, start b, end b, end a → each ends `complete` with its own `durationMs`; b completes while a is still running at the midpoint.
- [ ] `npm test` (expect 2 existing + 4 new = 6), `npm run lint`, `npm run build`. Two commits: `fix(web): surface admin fetch failures instead of hanging or lying` (hooks + their tests), `test(web): pin tool-call indicator edge cases` (useToimi additions).

---

### Final verification

- [ ] `mise exec dotnet -- dotnet test toimi.sln --nologo -v q` — all pass (441 baseline + new).
- [ ] `mise exec dotnet -- dotnet format toimi.sln --verify-no-changes` — exit 0.
- [ ] ClientApp: `mise exec node -- npm test` (6) + `npm run lint` + `npm run build`.

### Deferred (still out of scope)

PostgresTickLock (needs live Postgres); McpToolAggregator collision/reconnect seams (needs an MCP test server); UrlGuard routable-filter extraction (recommend doing when next touching verkko networking — requires refactoring `GuardedConnectAsync`); ToolCallList component render tests (formatJson is module-private; revisit if it grows).
