# Suoritin — general-purpose code sandbox for repetitive tasks

**Date:** 2026-07-30
**Status:** Implemented on branch suoritin-sandbox (2026-07-31)

## 1. Motivation

Repetitive tasks should not cost an LLM call per repetition. The LLM writes
code **once**; the code runs on a schedule forever. Motivating cases:

- **Weather display:** fetch Open-Meteo every 30 min, format to the ruutu
  `weather` template schema, push via `display_show`. Today the only path is
  a `message`/`escalate` agent run — a full LLM turn per refresh.
- **Price watch:** fetch a product page, extract the price, compare to last
  seen, notify on change.
- **Headline digest:** fetch a newspaper front page, extract top headlines,
  send as a notification.

The tietue `script` handler (Jint, phase 5) was the first step, but it is
deliberately IO-free: no fetch, no way to reach other services. Its own
design docs deferred exactly this feature ("a `fetch` capability — network
egress — add later behind a domain allowlist"; the specced-but-unbuilt
`poll-diff` handler).

## 2. Decision summary

| Decision | Choice |
|---|---|
| Scope | General-purpose: fetch → transform → effects, any service writable via MCP |
| Isolation | Separate credential-free runner pod, built now (not in-process Jint + fetch) |
| Runtime | Deno; one Worker per execution with Deno-native permission scoping |
| Pod name | **suoritin** (`src/toimi.tools.suoritin/`, `k8s/base/tools-suoritin/`) |
| Script storage | New seeded `job` entity type **and** existing inline trigger scripts — both remain |
| Execution | ALL scripts (job + inline) execute in suoritin; **Jint is deleted** |
| Effect vocabulary | Slimmed to `setField` + `mcpCall` (notify/trigger/escalate become `mcp:` calls) |
| LLM in scripts | v1: `extract()` — one structured completion via tietue callback, `llm` grant |
| Mid-run MCP calls | Deferred to v2 (callback channel is shaped for it) |
| Secrets for authed APIs | Deferred to v2 (fetch-grant shape leaves room for an auth field) |

Rationale for pod-now: every growth trajectory of a general-purpose runner
ends in a pod (real runtime, OS-level resource limits, network-layer SSRF
defense, small blast radius on engine escape). The effects contract designed
for a pod is identical to the in-process one, so the stepping stone saves
nothing. Retiring Jint removes the high-maintenance half of the system: the
repeat-guard, regex-stall mitigation, and cooperative caps all exist only
because the engine shares a process with tietue.

## 3. Threat model

The realistic attacker is a **prompt-injected agent** writing a malicious
script (exfiltration, spam), plus plain runaway code. Assets: cluster-internal
services (PostgreSQL, HA + its token, k8s API, MCP pods), the LLM API key,
entity data, tietue pod integrity, scheduler stability.

Defense layers (all must fail together):

1. **Deno Worker permissions** — per-script `net` allowlist; `read`/`write`/
   `run`/`env` all denied. Scripts use standard `fetch`; the runtime rejects
   non-granted hosts. Not bypassable from script code.
2. **Egress NetworkPolicy** (modeled on selain's) — allow DNS + public
   internet; deny cluster/private CIDRs. One pinhole: suoritin → tietue
   (the callback endpoint). Even a V8 escape lands in a container that cannot
   reach PostgreSQL, HA, or any other pod.
3. **No credentials in the pod** — no DB, no PVC, no API keys, no MCP reach.
   LLM calls happen in tietue via the callback; the key never enters suoritin.
4. **Effects applied by tietue** under per-script capability grants, exactly
   as today. The script computes; tietue acts.
5. **Run tokens** — each `/execute` carries a one-time token encoding the
   run's grants; the tietue callback validates it. A compromised suoritin can
   only do what the currently-running script was already granted.

## 4. suoritin — the runner pod

`src/toimi.tools.suoritin/` — a small Deno HTTP server. Stateless: no DB, no
PVC. **Not an MCP server** — only tietue calls it; it never appears in
`Toimi:McpServers`.

### API

```
POST /execute
{
  "code": "<ES module source>",
  "input": { "data": {...}, "entityId": "...", "entityType": "...",
             "occurrence": "<ISO time>" },
  "timeoutMs": 20000,
  "allowedHosts": ["api.open-meteo.com"],
  "grants": ["llm"],
  "runToken": "<opaque>",
  "callbackUrl": "http://toimi-tools-tietue.apps.svc.cluster.local/..."
}
→ { "ok": true, "effects": {...}, "logs": ["..."],
    "error": null, "stats": { "durationMs": 843 } }

GET /health
```

### Execution model

One **Deno Worker per execution**:

- Spawned with `deno: { permissions: { net: [...allowedHosts, callback host],
  read: false, write: false, run: false, env: false } }`.
- Timeout → `worker.terminate()` — hard preemption of the V8 isolate.
- `console.log` inside the worker is captured and returned as `logs`
  (truncated to a cap) — scripts are debuggable.
- Concurrency: small (one or two workers at a time); the 60 s scheduler tick
  makes throughput a non-issue. Excess requests queue briefly or 429.
- Fetch response size cap enforced by the wrapper; effects payload size cap
  enforced again tietue-side.

### Script contract

The script is an ES module (modern JS, async allowed) with a default export:

```js
export default async function run(input) {
  const res = await fetch("https://api.open-meteo.com/v1/forecast?...");
  const w = await res.json();
  return {
    mcpCall: [{ tool: "display_show", args: {
      identifier: "hall", template: "weather", dataJson: JSON.stringify({...})
    }}]
  };
}
```

`input` fields:

- `data` — the entity's current `Data` (job config fields included for jobs;
  the host entity's data for inline trigger scripts).
- `entityId`, `entityType`, `occurrence` — context the Jint sandbox never
  exposed.
- `extract(prompt, text, schema)` — async host function, present only when
  the `llm` grant is held (§6).

Return value is the effects object (§5.2). A missing/invalid return, throw,
or timeout → `ok: false` with `error` + `logs`.

### The wrapper's own hardening

The worker-host bridge (postMessage RPC for `extract`, log capture, result
marshalling) treats the worker as hostile: message payloads are validated,
logs and results are size-capped, and the host never `eval`s worker-supplied
content. The wrapper itself runs with the minimum Deno permissions it needs
(net + the ephemeral module loading mechanism for `code`).

## 5. tietue changes

### 5.1 Execution swap

- `ScriptHandler` keeps its role (kill switch, watchdog, result shaping) but
  delegates to a new `SuoritinClient` (`POST /execute`).
- **Deleted:** `Scripts/ScriptEngine.cs` (Jint, repeat guard, regex
  mitigations), the Jint package reference, and their tests.
- Config: `Scripts:Enabled` remains the global kill switch;
  new `Suoritin:BaseUrl`, `Suoritin:TimeoutSeconds`. The HTTP timeout ≈
  script timeout + margin; the existing wall-clock watchdog still bounds the
  scheduler tick, so a hung call cannot stall the tick lock.

### 5.2 Effects: `setField` + `mcpCall`

`ScriptEffectApplier` slims to two kinds, applied in order:

- `setField: [{ path, value }]` — unchanged: repository update with JSON
  Schema re-validation and re-index. Grant: `setField`.
- `mcpCall: [{ tool, args }]` — executed through the same MCP client stack
  the agent runner uses (tietue's `Toimi:McpServers` reaches verkko, ruutu,
  koti, selain, and tietue itself). Grant: `mcp:<tool>` per tool name.
  A failed call does not abort the run; each call's outcome is recorded in
  the handler result / `EntityEvent` detail.

The old `notify` / `trigger` / `escalate` effects are removed; their
replacements are `mcp:send_notification` (verkko), `mcp:set_trigger` and
`mcp:activate` (tietue loopback — precedent: the agent runner already lists
tietue in its own MCP servers). **Native handlers are untouched** — `notify`
and `set-field` trigger handlers remain deterministic, in-process, and work
even when suoritin is down.

### 5.3 The `job` seeded type

Added to `TypeSeeder` (idempotent), alongside memory/skill/reminder/schedule:

- **Schema:** `name` (required), `description`, `code` (required),
  `allowedHosts: string[]`, `grants: string[]`, `startAt`, `rrule`, `tz`,
  `enabled`.
- **Behaviors:** `UniqueName` on `name`.
- **Default trigger** (copy-down via `TriggerProvisioner`, like reminder):
  kind `script`, schedule from `startAt`/`rrule`/`tz`, config
  `{ "fromEntity": true }` — at fire time the handler reads `code`,
  `allowedHosts`, `grants` from the entity's **current** `Data`, so editing
  the job takes effect immediately without touching the trigger.
- Inline trigger scripts keep the `{ source, capabilities }` config shape,
  now also accepting `allowedHosts`.
- Run history is free: `EntityEvent` rows on the job entity.

### 5.4 `run_trigger` MCP verb

`run_trigger(triggerId)` fires a trigger immediately and synchronously
returns the handler result **including script logs**. This closes the
authoring loop (write job → run → read logs → fix) instead of iterating
blind against the 60 s scheduler. Generic: works for any handler kind.
It records a normal `EntityEvent` and respects occurrence idempotency.

### 5.5 `extract()` callback endpoint

`POST /internal/runs/{runToken}/extract` on tietue: validates the run token (one-time, expiring,
grant-carrying), then performs a single structured completion — prompt +
text + JSON schema, no tools — via the `toimi.core` LLM client factory, and
returns the parsed result. Gated by the `llm` grant. Per-run call-count cap
(e.g. 3) to bound cost. This is the middle rung of the cost ladder:
deterministic script ≪ script + extract ≪ full agent run.

## 6. End-to-end examples

**Weather (the v1 acceptance case):** LLM creates a `job` once — code
fetches Open-Meteo, formats to the ruutu `weather` schema, returns
`mcpCall: display_show`; `allowedHosts: ["api.open-meteo.com"]`,
`grants: ["mcp:display_show"]`, `rrule` every 30 min — verifies with
`run_trigger`, and the scheduler does the rest forever. Zero LLM tokens per
refresh.

**Price watch (exercises extract + state):** code fetches the product page,
calls `extract("the current price as a number", html, {type:"number"})`,
compares to `input.data.lastPrice`, and on change returns
`setField: [{path:"lastPrice", ...}]` +
`mcpCall: [{tool:"send_notification", ...}]`. Grants:
`["llm", "setField", "mcp:send_notification"]`. State lives in the job
entity itself — no mid-run reads needed.

## 7. Error handling

- Suoritin unreachable / 5xx / timeout → `HandlerResult` error, recorded as
  an `error` `EntityEvent`, trigger advances (existing isolation).
- Script throw / worker timeout → `ok:false` + `error` + captured `logs`,
  same recording path.
- Effect application failures (schema violation on `setField`, MCP tool
  error, ungranted effect) are per-effect: recorded, remaining effects still
  attempted, run marked with detail.
- Caps at the tietue boundary: effects payload size, log length, extract
  calls per run.
- Existing inline Jint scripts require a one-time manual rewrite to the new
  module contract (single-user instance; acceptable).

## 8. Testing

- **suoritin (`deno test`):** allowlist enforced (non-granted host rejected
  at runtime), timeout terminates the worker, logs captured and capped,
  effects marshalled, extract RPC bridged, hostile worker messages rejected.
- **tietue:** existing unit-test patterns — `ScriptHandler` against a faked
  `SuoritinClient`; `ScriptEffectApplier` grant gating (incl. `mcp:` grants)
  against a faked MCP invoker; `run_trigger`; job type seeding + copy-down
  trigger; extract endpoint token validation.
- **Contract fixtures:** shared JSON request/response examples exercised
  from both sides. *Superseded in implementation:* the docker-gated
  integration test exercises the real contract end-to-end instead.
- **Docker-gated integration** (existing Testcontainers pattern in
  tietue.Tests): real suoritin container, end-to-end execute + effects.

## 9. Deployment & infra

- `src/toimi.tools.suoritin/Dockerfile` — Deno base image; repo-root build
  context per convention (copies only its own dir).
- `k8s/base/tools-suoritin/` — deployment (non-root,
  readOnlyRootFilesystem where Deno allows, resource limits), service
  `toimi-tools-suoritin.apps`, **networkpolicy** (egress: DNS + public
  internet + tietue pinhole; ingress: from tietue only).
- `deploy.sh`/`deploy-all.sh` pick it up automatically (they iterate
  `src/*/Dockerfile`).
- No DB. CLAUDE.md: add suoritin to the pods list with an ownership blurb;
  note the Jint removal in tietue's section.

## 10. Deferred (v2+)

- **Secrets for authenticated APIs** — fetch grants gain
  `{host, secretRef, header}`; keys live in a k8s Secret mounted to tietue,
  attached per-invocation, never visible to script code.
- **Mid-run MCP calls** (`input.mcp(tool, args)`) — same callback channel,
  same run-token grants; needed for branch-on-other-service-state scripts
  (e.g. "only notify if someone is home").
- Per-job kill switch / enable toggle enforcement in the scheduler beyond
  the schema field; notify rate-limiting; structured invocation logging
  (input hash, duration, outcome) from the original §6 guard list.
- Script libraries / shared modules; state beyond entity data; job budgets.
- Per-fetch response size cap inside the worker (spec §4 originally promised
  it; the pod memory limit is the current containment).
