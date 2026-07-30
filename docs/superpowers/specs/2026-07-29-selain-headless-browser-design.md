# selain — headless browser tool server

**Date:** 2026-07-29
**Status:** Approved (design)
**Scope:** new `src/toimi.tools.selain/` pod + `k8s/base/tools-selain/`; one
config line in each of the two `Toimi:McpServers` lists; one description-only
change to verkko's `fetch_url`; no changes to ruutu.

## Goal

Give the agent a real browser. Today verkko's `fetch_url` is a one-shot HTTP
GET (HTML→text, 50 KB cap, 5-min cache, SSRF guard) — it cannot read
JS-rendered pages, act on pages, or feed live page views to a display. selain
is a new tool server (`toimi.tools.selain`, 1:1:1 convention) that runs
headless Chromium via Playwright for .NET and exposes:

1. **Reading** JS-heavy / bot-hostile pages (accessibility snapshot + text).
2. **Acting**: click, type, select, multi-step public-site flows.
3. **Vision**: screenshots as MCP image blocks.
4. **Live display feeds**: per-tab screenshot polling *and* CDP-screencast
   streaming, consumable by ruutu's existing `webview` template — the
   "watch the delivery driver move on the wall display" case, which iframes
   can't serve (X-Frame-Options) and slow polling can't serve (moving map).

`fetch_url` stays the cheap first rung; selain is the expensive rung — same
cost-ladder philosophy as tietue's handlers.

## Decisions (from brainstorming)

- **Approach:** own curated .NET tool server, not a deployed `playwright-mcp`
  (no toimi glue there) and not a verkko extension (browsing is a fat
  sub-domain; the ≥2-actions carve-out rule fires).
- **v1 drops VNC and logins.** No Xvfb/x11vnc/noVNC, no credential store, no
  selain database, no PVC — pure headless, fully stateless pod. Re-adding
  later is additive: headful is a launch flag + image packages; credentials
  are a new DB + two tools; the profile is a PVC + manifest change.
- **Live view = CDP screencast, not VNC.** VNC mirrors the focused X window,
  so it cannot show tab N while the agent uses tab M. `Page.startScreencast`
  streams JPEG frames per tab, focus-independent, works headless, and streams
  nothing while the page is idle.
- **Concurrency:** many tabs may be open and loading; *actions* are
  serialized behind one context-wide lock. Snapshot refs are tied to the
  active tab, so concurrent cross-tab actions would race ref validity.
  Ruutu feeds capture background tabs without focusing them.
- **Ruutu integration reuses `webview`:** selain hosts a self-contained
  viewer page per tab; ruutu embeds its URL in the existing sandboxed iframe
  template. No ruutu code changes.

## Design

### 1. Runtime

- **Image:** build stage on the .NET SDK image; runtime stage on
  `mcr.microsoft.com/playwright/dotnet` (Chromium + system deps
  preinstalled). Dockerfile context = repo root, per convention.
- **Process model:** ordinary ASP.NET host. On first use (lazy) it launches
  one headless Chromium via `Playwright.CreateAsync()` +
  `LaunchAsync(new() { Headless = true, Args = ["--disable-dev-shm-usage"] })`
  and one `IBrowserContext`. `--disable-dev-shm-usage` (or an
  emptyDir-memory volume at `/dev/shm`) avoids the classic k8s shm crash.
- **State:** none persisted. Tabs, cookies, and stream tokens are in-memory;
  a pod restart resets the browser world and tools report that plainly.
- **Idle shutdown:** when there are no open tabs and no active streams for
  15 minutes, close the browser (relaunch is lazy anyway). A weeks-running
  Chromium slowly leaks memory; periodic teardown at idle is free hygiene.
- **Resources:** requests ~512 Mi / limits ~2 Gi memory; Chromium is the
  budget.

### 2. Tab model

A `TabManager` owns the context. Each open page gets a `TabInfo { Id (GUID),
Page, Title, Url }`. The GUID doubles as the capability token for the HTTP
view endpoints — endpoints only answer to an exact tab id, and ids are never
guessable. One tab is "active" (the target of snapshot refs and actions).
A `SemaphoreSlim` serializes every mutating operation.

Browser-crash handling: every tool entry point checks
`IBrowser.IsConnected`; if the browser died it is relaunched and tools return
"browser restarted — all tabs were lost" instead of throwing.

**Popups:** a click can open a new window. The context's `Page` event adopts
every new page into the `TabManager` as a regular tab (id, viewer URL, the
lot) and the action's result notes "opened a new tab: {title}". Without this
the popup — often the page the user actually wanted — is orphaned and
unaddressable.

**Dialogs:** Playwright's default (auto-dismiss JS alert/confirm/prompt)
stands; the dismissal is appended to the action result so the agent knows it
happened.

### 3. MCP tool surface (12 tools)

Every action tool returns the resulting page snapshot in its result, so the
agent sees the effect without a follow-up call — subject to the token rules
below.

**Token economy (design constraint, not an afterthought):** a browser emits
far more text per step than any existing tool, and `ContextManager`
summarizes near ~100 K tokens — an unbounded ten-step flow could evict the
whole conversation. Rules:

- Snapshots returned by tools are capped at **15 K chars** (≈4 K tokens)
  with a truncation marker pointing at `read_page`/`wait_for` for more.
  (Verkko's 50 K cap is for a *one-shot* fetch; per-action returns need a
  tighter budget.)
- Action tools hash the post-action snapshot; if it equals the last snapshot
  the agent was shown for that tab, they return `page unchanged` instead of
  repeating it.
- `read_page` keeps a 50 K cap — it is the explicit "give me everything"
  escape hatch, used deliberately.

**Load/settle strategy:** `browse` waits for `Load`, then attempts
network-quiet (no requests for 500 ms) with a 3 s budget — SPAs that poll
forever fall through to the snapshot rather than hanging. The snapshot may
therefore be pre-hydration; `wait_for(text)` / `snapshot()` are the agent's
retry loop, and `browse`'s description says so.

Observe:

- `browse(url)` — validate scheme + `UrlGuard` (see §5), open in the active
  tab (or first tab), settle per above, return title/URL + aria snapshot
  with refs. Description starts: "Prefer verkko's `fetch_url` for simple
  static pages — use `browse` when a page needs JS, interaction, or a
  display feed." (Verkko's `fetch_url` description gains the mirror line:
  "if the result looks like an empty shell or says JS is required, use
  selain's `browse`.")
- `snapshot()` — re-read the active tab.
- `read_page()` — plain extracted text of the active tab, for long articles
  where the a11y tree is noise.
- `screenshot(fullPage?)` — PNG of the active tab as an MCP image block.

Act:

- `click(ref)`
- `hover(ref)` — menus and content that reveal on hover.
- `type(ref, text, pressEnter?)`
- `select_option(ref, value)`
- `press_key(key)` — Escape, PageDown, etc.
- `go_back()`
- `wait_for(text?, seconds?)` — bounded (max ~30 s).

Tabs:

- `tabs(action, tabId?, url?, width?, height?)` — `list` / `new` / `switch`
  / `close`. `new` accepts an optional viewport size (default 1280×720) —
  screencast and screenshots render at viewport dimensions, so a tab
  destined for a wall display gets a viewport matching that display. `list`
  returns for each tab: id, title, URL, and its shareable viewer URL (see
  §4) so the agent can hand it to ruutu's `webview` template directly.

Snapshots use Playwright's aria snapshot with refs (`e1`, `e2`, …) resolved
via the `aria-ref=` selector engine — the mechanism playwright-mcp uses.
A stale ref (element gone after page change) returns "ref not found — take a
new snapshot", not an exception.

**Screenshot-for-vision verification spike (blocks the `screenshot` tool):**
the spec assumes an MCP image block survives `McpToolAggregator` →
`Microsoft.Extensions.AI` → the configured OpenAI model. That must be
*proven first* — many pipelines flatten tool results to text, which would
make the vision use case silently return garbage. The implementation plan
starts with a spike: return a known image from a stub tool and confirm the
model describes it. If the pipeline flattens, the fix is a toimi.core
extension (image-content passthrough in the aggregation layer) — legitimate
cross-cutting behavior, but scoped and named here rather than discovered
mid-build.

Spike verified 2026-07-30: image blocks reach the model (verified headlessly
through the toimi.core pipeline: a temporary stub tool returned a 5x5 solid-red
PNG as a `CallToolResult` image block; a console harness composed the exact
toimi.web stack — `OpenAiLlmClientProvider` (OpenAI → `ToolCallNotifier` →
`UseFunctionInvocation`) + `McpToolAggregator`/`ResilientMcpTool` over
streamable HTTP + `ToimiClientFactory` options/messages — and the configured
model answered "red"). Static trace agrees: MCP SDK 1.4.1's `McpClientTool`
returns the image block as a `Microsoft.Extensions.AI.DataContent` (an
`AIContent`, not JSON text), `ResilientMcpTool` passes the result object
through unchanged, and M.E.AI 10.8.3's `FunctionInvokingChatClient` +
OpenAI serializer emit it as a real image part. Two implementation notes for
Task 10: `ImageContentBlock.Data` in SDK 1.4.1 is base64 *as UTF-8 bytes* —
build blocks with `ImageContentBlock.FromBytes(rawBytes, mimeType)`, never by
assigning raw image bytes to `Data` (that ships mojibake on the wire); and
`ToolCallNotifier`'s result event renders such results as the type name
("Microsoft.Extensions.AI.DataContent"), so the UI tool indicator shows no
useful payload for image results — cosmetic only, the model path is unaffected.

### 4. Display feed endpoints (plain HTTP, not MCP)

- `GET /tabs/{id}/screenshot` — current PNG of that tab, captured without
  focusing it. For static-ish surfaces and as the stream fallback.
- `GET /tabs/{id}/view` — a self-contained HTML page (inline CSS/JS, no
  external assets): a canvas that connects to the WebSocket stream, paints
  frames, auto-reconnects, and falls back to polling the screenshot endpoint
  if the socket won't open.
- `WS /tabs/{id}/stream` — on connect, opens a CDP session on the tab
  (`Context.NewCDPSessionAsync(page)`), calls `Page.startScreencast`
  (JPEG, quality ~60), relays each `screencastFrame` to the socket and acks
  it. On disconnect (or tab close) calls `Page.stopScreencast` and disposes
  the CDP session — an unwatched tab costs nothing.

**Ruutu flow:** agent opens the tracking link in a new tab, takes the viewer
URL from `tabs(list)`, and calls ruutu's existing
`DisplayShow(display, "webview", { url: viewerUrl, title: "Delivery" })`.
The viewer URL must therefore be reachable *from the display's browser*, i.e.
externally: a new config value `Selain:PublicBaseUrl` (from `toimi.env` →
`config.env` → envsubst) names the ingress host; the server overlay adds an
ingress route for `/tabs/*` on selain. Note ruutu's `safe_url` filter
requires https and a public-looking host — satisfied on the server install;
on dev the viewer is exercised directly, not through a display.

The tab GUID in the URL is the only access control on these endpoints —
acceptable for a single-user LAN/ingress deployment; they expose only pixels
of pages the agent itself opened, and the ids die with the tab.

### 5. Guardrails

- **SSRF containment — the browser is a wider hole than `fetch_url`, and
  browse-time checks alone do not close it.** A hostile page can load a
  *subresource* from `http://toimi-tools-tietue.apps.svc.cluster.local/…`,
  or redirect/JS-navigate to internal hosts — none of which pass through
  `browse`. Two layers:
  1. **NetworkPolicy on the selain pod (the real gate):** egress allows DNS
     and the public internet only; denies cluster CIDRs and RFC1918/link-
     local ranges. Enforced by the network, immune to anything the page
     does. Lives in `k8s/base/tools-selain/`.
  2. **Request-level routing (defense in depth + dev parity):**
     `context.RouteAsync("**/*")` aborts any request whose host fails
     `UrlGuard` — covers subresources, redirects, and JS navigation, and
     also protects dev clusters where the CNI may not enforce
     NetworkPolicy (kind's default CNI does not).
- **`UrlGuard`** — verkko's private/internal-host blocklist, applied to
  `browse` (fast, friendly error) and inside the route handler above. The
  class is small; copy it into selain rather than creating a shared library
  for one file (revisit if a third copy ever appears).
- **`Selain:Enabled`** — global kill switch, mirroring `Scripts:Enabled`;
  when false every tool returns "browser tools are disabled".
- **Timeouts** — navigation 20 s, actions 10 s, `wait_for` capped at 30 s.
  Timeouts return messages, never unhandled exceptions (verkko error style).
- **Prompt-injection posture (v1):** with no logins, a hostile page can
  waste effort but holds no sessions to abuse and `UrlGuard` keeps the
  browser off internal hosts. Revisit hard (domain-locking per authenticated
  tab, action confirmation) when logins are added.

### 6. Registration & deployment

- Add `http://toimi-tools-selain.apps.svc.cluster.local/sse` to
  `Toimi:McpServers` in **both** `src/toimi.web/appsettings.json` and
  `src/toimi.tools.tietue/appsettings.json` (agent runs from triggers get the
  browser too).
- `k8s/base/tools-selain/`: deployment (+shm volume if not using the launch
  flag), service, **egress NetworkPolicy (§5)**; server overlay ingress for
  `/tabs/*`. Deployed by the existing `deploy.sh` / `deploy-all.sh` (it
  iterates `src/*/Dockerfile`).
- New project added to `toimi.sln`.
- No new database, no new secrets; the only new config is
  `Selain:PublicBaseUrl`.

## Error handling summary

- Playwright timeouts / navigation failures → descriptive tool-result
  strings (site, phase, elapsed), matching verkko's catch-and-report style.
- Stale ref → "take a new snapshot" message.
- Browser crash → auto-relaunch on next call + "tabs were lost" notice.
- Stream socket errors → screencast stopped server-side; viewer page
  auto-reconnects, falling back to screenshot polling.

## Testing

Follows the repo's seam + gated-integration pattern (as in tietue.Tests):

- **Unit (no browser):** `TabManager` against an `IPageSession` seam (fake
  pages): active-tab bookkeeping, popup adoption, GUID/view-URL composition,
  serialization lock, crash-reset behavior. `UrlGuard` cases (copied tests).
  Snapshot truncation at the 15 K action cap and 50 K `read_page` cap;
  unchanged-snapshot hashing returns `page unchanged`.
  `Selain:Enabled=false` short-circuits every tool. Ref parsing/stale-ref
  messaging.
- **Integration (env-gated, needs installed browsers — skip otherwise, like
  the docker-gated Testcontainers tests):** Kestrel test host serving local
  fixture pages; real headless Chromium: `browse` returns a snapshot with
  refs; `click`/`type` round-trip mutates the fixture DOM; JS-rendered
  content appears in `read_page` (the thing `fetch_url` can't do); a fixture
  page whose subresource points at a private-range host shows the request
  aborted by the route handler; a popup-opening click yields an adopted tab
  in `tabs(list)`; `/tabs/{id}/screenshot` returns a decodable PNG;
  `/tabs/{id}/stream` delivers ≥1 screencast frame after a page mutation;
  screenshot of a background tab while another tab is active.
- **Spike (before the `screenshot` tool is built):** image tool-result
  passthrough end-to-end (§3).

## Out of scope (YAGNI, v1)

- VNC / headful mode, logins, credential store, persistent profile (PVC) —
  the deferred auth design is hybrid (manual VNC login for 2FA sites,
  stored credentials for simple re-logins) per the brainstorm.
- File downloads/uploads.
- Per-site domain locking (only matters for authenticated tabs).
- Snapshot *diffing* (returning only what changed) — the 15 K cap +
  unchanged-hash rule is the v1 token defense; diffing is the v2 refinement
  if real flows still run hot.
- Multi-context isolation / parallel actions.
- A seeded tietue `skill` teaching browsing patterns (tool descriptions
  should carry v1; add a skill entity later if the agent fumbles).

## Known limitations

- Logged-in-only content (some courier live maps require an account) is out
  until the auth phase; SMS/email tracking links are usually tokenized
  public URLs and work.
- Anti-bot walls (Cloudflare challenges, CAPTCHAs) may still block; a real
  browser passes more than raw HTTP but not everything.
- A pod restart drops all tabs and any display currently streaming one; the
  viewer page then shows its reconnect/fallback state until the agent
  reopens the page.
