# Ruutu — Controllable Displays for Toimi

**Status:** design (brainstorm output, pre-implementation)
**Date:** 2026-06-04

## Summary

`toimi.tools.ruutu` is a new MCP tool server that lets Toimi push content to one
or more web pages running on user-owned displays (an old iPad mounted in the
kitchen, a repurposed phone on a desk, a wall-mounted tablet). Each display is
identified by a user-chosen name, capability-aware (modern vs legacy
rendering tiers, auto-detected), and supports tap-back interactions for
checking off routine steps and dismissing notifications.

The service sits as a sibling alongside `koti`, `muistutin`, `verkko`, etc. —
same 1:1:1 convention (project ↔ Dockerfile ↔ k8s overlay).

## Motivation

Toimi today reaches the user only through the chat UI (`toimi.web`). Many
useful AI interactions are passive — glanceable dashboards, ambient
reminders, in-progress routines — and these belong on a always-on display in
the room where they matter, not in a chat window. `ruutu` is the surface for
that class of interaction.

## Use cases targeting v1

1. **Glanceable dashboard** — kitchen iPad shows current weather, today's
   calendar, upcoming reminders. Updates throughout the day.
2. **Notification overlays** — Toimi pushes a card on top of the dashboard
   ("laundry done", "leave for school in 5 min"). User taps to dismiss.
   Stays on screen until tapped (no auto-clear).
3. **Interactive routines** — user says "let's do evening routine" in chat.
   Kitchen iPad swaps to a `todo_list` showing the steps. User taps to check
   off each step as they go.

## Architectural overview

```
+----------------+        MCP / HTTP+SSE         +-----------------+
|  toimi.web     |  ←———————————————————→        |  ruutu pod      |
|  (chat UI +    |    cluster-internal           |  - MCP tools    |
|   toimi.core   |    /sse endpoint              |  - SSE hub      |
|   AI loop)     |                               |  - Renderer     |
+----------------+                               |  - PostgreSQL   |
                                                 +---------+-------+
                                                           |
                                                  Ingress  |  /ruutu/*
                                                           |
                                                 +---------+-------+
                                                 | Display (iPad)  |
                                                 |  - ES5 shell    |
                                                 |  - SSE consumer |
                                                 |  - tap-back XHR |
                                                 +-----------------+
```

Three-layer separation:

- **AI side (`toimi.web` + `toimi.core`):** thinks in templates and data.
  Calls MCP tools to push content. Never touches HTML at runtime.
- **`ruutu` service:** owns rendering. Looks up templates from DB, picks the
  capability tier per display, runs Scriban to fill in data, pushes HTML
  over SSE to the display.
- **Display browser:** renders what it receives. Reports its capabilities
  on first load. Sends tap events back via plain XHR POST.

The split is: **AI thinks in templates + data; ruutu thinks in HTML +
capability tiers; the display renders what arrives.** Templates are the
contract between the two worlds and live in the DB so the AI can extend
them via tools.

## Service surface (MCP tools)

### Display management

| tool | purpose |
|---|---|
| `display_register(identifier, capability_tier_override?)` | Pre-register before anyone opens the URL. Required before a display can connect (no auto-create on visit). |
| `display_unregister(identifier)` | Remove the display record. |
| `display_list()` | `[{identifier, tier, status: online \| offline, last_seen_at, current_template}]`. Online = `last_seen_at > now() - 30s`. |
| `display_set_tier(identifier, "modern" \| "legacy")` | Manual override; blocks auto-detect overwrites. |

### Content (runtime path)

| tool | purpose |
|---|---|
| `display_show(identifier, template, data)` | Render template with data, push to display, becomes the new "current scene." Replaces whatever was there. |
| `display_overlay(identifier, template, data)` | Push as a temporary overlay (LIFO stack). No auto-clear; user must tap to dismiss. Newest overlay is always on top. |
| `display_clear(identifier)` | Return to the configured idle scene (or splash if none). |

### Templates (author path)

| tool | purpose |
|---|---|
| `display_list_templates()` | `[{name, description, schema, has_modern, has_legacy}]`. AI gets this at session start so it knows the available vocabulary. |
| `display_get_template(name)` | Full definition: both HTML variants, schema, description. |
| `display_create_template(name, description, schema, modern_html, legacy_html)` | Create a new template. Linted before write. |
| `display_update_template(name, ...)` | Partial updates. Linted before write. |
| `display_delete_template(name)` | Cannot delete seeded templates. |
| `display_preview(template, data, tier)` | Render without pushing; returns HTML string. AI uses this to sanity-check before saving a new template. |

### Events (tap-back)

| tool | purpose |
|---|---|
| `display_get_events(identifier, since?)` | Recent tap events `[{type: "tap" \| "check" \| "dismiss", target, value, timestamp}]`. AI polls when relevant. |

**v1 scope note:** tap events do not auto-trigger an AI session. The AI
queries when it cares. Wiring taps to live agent reactions is deliberate
phase 2 (likely lands in `ajastin` or a new background loop).

## Data model

PostgreSQL via EF Core. Database: `ruutu`.

### `displays`

| column | type | notes |
|---|---|---|
| `id` | serial PK | |
| `identifier` | text, unique | URL slug, user-chosen (`kitchen`) |
| `tier` | text, nullable | `"modern"` / `"legacy"` — null until first connect detects |
| `tier_override` | bool | true if set by `display_set_tier`; blocks auto-detect overwrites |
| `last_user_agent` | text, nullable | for debugging tier detection |
| `viewport_width` | int, nullable | reported by display on connect |
| `viewport_height` | int, nullable | |
| `orientation` | text, nullable | `landscape` / `portrait`, derived from dims |
| `current_template` | text, nullable | template name for the current scene |
| `current_data` | jsonb, nullable | data for the current scene |
| `current_pushed_at` | timestamptz, nullable | |
| `overlay_stack` | jsonb, default `[]` | LIFO stack of `{template, data, enqueued_at}`. Cap 10, evict oldest when full. |
| `idle_template` | text, nullable | scene to show on `display_clear` or reconnect with no current scene |
| `idle_data` | jsonb, nullable | |
| `last_seen_at` | timestamptz, nullable | last SSE heartbeat or tap POST |
| `created_at` | timestamptz | |

### `templates`

| column | type | notes |
|---|---|---|
| `id` | serial PK | |
| `name` | text, unique | |
| `description` | text | shown in `display_list_templates`; AI uses to pick |
| `schema_json` | jsonb | JSON Schema for `data` parameter |
| `modern_html` | text, nullable | Scriban template for `modern` tier |
| `legacy_html` | text, nullable | Scriban template for `legacy` tier |
| `is_seeded` | bool | true if from code seeder; re-seeded on startup (idempotent upsert, taidot's pattern). Cannot be deleted. |
| `created_at`, `updated_at` | timestamptz | |

### `display_events`

| column | type | notes |
|---|---|---|
| `id` | bigserial PK | |
| `display_id` | int FK | |
| `event_type` | text | `tap` / `check` / `dismiss` / `overlay_dropped` |
| `target` | text | component ID inside template (`step-2`, `overlay`) |
| `value` | jsonb, nullable | optional payload |
| `created_at` | timestamptz | |

Index: `(display_id, created_at desc)`. No retention policy in v1; add a
"trim events older than 7 days" sweep later if it matters.

### Templating engine

**Scriban.** Lightweight, native .NET, sandboxed, `{{ }}` syntax. Supports
loops/conditionals (needed for `todo_list` and other repeating content).
Not Razor — too much .NET coupling for templates that should be
data-shaped and AI-writeable.

### Deliberately NOT in v1

- No `auth_token` column. LAN trust + identifier is enough. Adding the
  column later is one migration.
- No template versioning/history. A botched update requires a fix-forward.
- No multi-tenancy / user scoping. Toimi is single-user.

## Display page (transport, capability detection, tap-back)

### The HTML shell

Served at `GET /ruutu/<identifier>` from `wwwroot/shell.html`.

- `<head>`: meta viewport, ~50 lines of reset/utility CSS, inline favicon.
- `<body>`: `<div id="scene"></div>` + `<div id="overlay" hidden></div>`.
- One `<script>` block (~150 lines, ES5, plain functions). No build step,
  no framework — hand-written ES5 because the legacy tier can't run a
  modern transpiler bundle reliably.

### Capability detection (first load)

Feature tests in JS, posted to `/ruutu/api/displays/<id>/capabilities`:

```js
var caps = {
  flexbox: testStyle('display','flex'),
  cssGrid: window.CSS && CSS.supports && CSS.supports('display','grid'),
  fetch:   typeof window.fetch === 'function',
  promise: typeof window.Promise === 'function',
  viewport_width: window.innerWidth,
  viewport_height: window.innerHeight,
  user_agent: navigator.userAgent
};
```

Server's v1 classification rule:
`(flexbox && fetch && promise) ? "modern" : "legacy"`. Override
(`tier_override=true`) bypasses this. Detection re-runs on every full page
load (cheap; future-proofs device upgrades).

### Server → display: SSE

After the capabilities POST returns, the page opens
`new EventSource("/ruutu/api/displays/<id>/stream")`. Event types:

| event | payload | display behavior |
|---|---|---|
| `scene` | `{ html }` | `#scene.innerHTML = html`. Doesn't touch overlay slot. |
| `overlay` | `{ html }` | render into `#overlay`, show on top of current scene. |
| `overlay_clear` | `{}` | hide overlay slot (no further overlay queued). |
| `clear` | `{}` | return to idle template + clear overlay slot. |
| `heartbeat` | — | every 15s; keeps proxies alive, updates `last_seen_at`. |

EventSource auto-reconnects on disconnect (browser built-in). On reconnect,
server immediately re-sends current scene + the top of the overlay stack
(if any).

### Dismiss flow (tap → server → next overlay)

1. User taps dismiss → display optimistically hides overlay locally,
   POSTs event `{type: "dismiss", target: "overlay"}`.
2. Server pops top of `overlay_stack`.
3. If stack still has entries: server SSE-pushes `overlay` with the new
   top (slides in to replace the dismissed one).
4. If stack empty: server SSE-pushes `overlay_clear` to confirm state.
   In practice the display has already locally hidden, so this is a
   reconciliation step (useful if the optimistic dismiss happened in a
   stale frame).

The AI cannot programmatically dismiss an overlay in v1 — by deliberate
design, important content shouldn't silently vanish. `display_clear`
clears both scene and overlays as the "reset" action; otherwise overlays
only come down by user tap.

### Interaction between `display_show` and an active overlay

`display_show` replaces the scene **underneath** any active overlay; the
overlay stays on top until tapped. This matches the "user must notice"
intent — pushing a new dashboard doesn't make an unread notification
disappear.

### Display → server: XHR POST

Global click delegation in the shell:

```js
document.addEventListener('click', function(e){
  var el = e.target;
  while (el && !el.getAttribute('data-tap')) el = el.parentNode;
  if (!el) return;
  postEvent({
    type:   el.getAttribute('data-tap'),     // 'tap' | 'check' | 'dismiss'
    target: el.getAttribute('data-target'),  // 'step-2'
    value:  el.getAttribute('data-value')
  });
  applyOptimisticUpdate(el);
});
```

Templates declare interactivity by data attributes — never inline JS.
`innerHTML` doesn't execute `<script>` anyway, so this is a hard
constraint, not just a convention.

### Optimistic UI for taps

The shell locally toggles visual state (checkbox fills, overlay dismisses)
immediately. Server is the source of truth; when the AI reacts and pushes
an updated scene, the authoritative state arrives. Race conditions resolve
toward the server.

### Idle behavior

- If `current_template` is set on reconnect: re-render and send.
- Else if `idle_template` is set: render that.
- Else: default "Toimi" splash showing the display identifier (useful for
  confirming the right URL was opened).

## Capability tier definitions

Tiers exist to give a concrete contract that human seeders, the AI when
authoring, and the legacy-tier-violation linter can all agree on.

| | Modern | Legacy |
|---|---|---|
| **Target devices** | Safari 14+, Chrome 90+ (≈2020+) | iOS Safari 9–12 (iPad 2/3/4/Air 1) |
| **Layout** | flexbox, grid, `gap`, `position: sticky` | tables, floats, `inline-block`. **No flex/grid.** |
| **Units** | `vw`/`vh`/`rem`/`clamp()`/`min()`/`max()` | `px`/`em`/`%`/`vh`/`vw`. No `clamp/min/max`. |
| **CSS variables** | yes | avoid |
| **Colors** | `rgb(0 0 0 / 50%)` modern syntax, `rgba()` | hex + `rgba()` only |
| **Fonts** | system stack preferred; web fonts OK if sparse | **system stack only.** No `@font-face`. |
| **Images** | JPG/PNG/SVG/WebP | JPG/PNG/SVG. **No WebP.** |
| **Selectors** | standard + complex `:not()` | basic + class + id + `:hover`. No `:has() / :is() / :where()`. |
| **Animations** | `@keyframes`, transitions, transforms | basic `@keyframes` + transitions only; no 3D transforms |
| **Viewport** | responsive 768–1920 | tuned for 1024×768, both orientations |

### The "author brief" the AI sees

Tool descriptions for `display_create_template` and
`display_update_template` include the relevant tier rules inline as
`LEGACY_TIER_BRIEF` and `MODERN_TIER_BRIEF` string constants. AI doesn't
have to recall them; they're in the `[Description]` attribute. A
`display_get_tier_brief(tier)` lookup tool exists if the AI wants the
full text without re-reading the tool description.

### Validation (linter) on every template submission

Regex-based linter on submitted HTML/CSS. Examples (illustrative, not
exhaustive):

- `legacy_html` contains `display:\s*(flex|grid)` → reject.
- `legacy_html` contains `var\(--` → reject.
- `legacy_html` contains `\.webp` or `image/webp` → reject.
- `legacy_html` contains `@import` or `@font-face` → reject.
- Either tier contains `<script>` → reject (templates are declarative-only).

Returns `{valid, issues: [{line, rule, message}]}`. AI fixes and retries —
the typecheck-loop pattern works well in practice.

### Deliberately NOT in v1

- No actual browser-based template testing (e.g., Playwright on legacy
  WebKit). The linter + `display_preview` HTML inspection is the v1
  quality gate.
- No per-template capability declaration ("this template requires modern").
  Each template ships both variants. Simpler.

## Seeded v1 templates

Eight leaves + three layouts ship in code via `RuutuTemplateSeeder` —
idempotent upsert on name, taidot's pattern. Both `modern_html` and
`legacy_html` are hand-written for every seeded template (no "AI will
generate later" stubs — the seed set must work out of the box).

### Leaf templates

| name | schema (data) | what it does | tier notes |
|---|---|---|---|
| `splash` | `{message?}` | Toimi splash + display identifier. Default idle for un-configured displays. | trivial both tiers |
| `clock` | `{timezone?, format?: "24h"\|"12h"}` | Large current time + date. Auto-ticks client-side from `Date.now()`. | both fine |
| `message` | `{title?, body}` | Big text card. Useful for "Welcome home" / "Leave for school in 5 min". | both fine |
| `notification` | `{title, body, icon?, severity?: "info"\|"warn"\|"alert"}` | Notification card. Used for overlays. Tap = dismiss event. | both fine |
| `todo_list` | `{title, steps: [{id, label, done}]}` | Title + checkbox list. Tap a row = `check` event with `target=step.id`. | legacy uses `<table>` |
| `weather` | `{location, current: {temp, condition, feels_like}, today: {high, low, notes?}}` | Current temp + brief outlook. Data comes from `koti` (HA weather entity). | both fine |
| `calendar_day` | `{date, events: [{time, title}]}` | Today's events. AI populates from Google Calendar. | both fine |
| `reminders` | `{items: [{due_at, title}]}` | Upcoming reminders. AI populates from `muistutin`. | both fine |

### Layout templates

| name | schema | renders |
|---|---|---|
| `split_horizontal` | `{left: {template,data}, right: {template,data}}` | side by side |
| `split_vertical` | `{top: {...}, bottom: {...}}` | stacked |
| `stack` | `{items: [{template,data}], gap?}` | N tiles vertically with gap |

Layout templates are first-class members of the catalog. Their data schema
accepts nested `{template, data}` references; the renderer recurses
(render each leaf at the display's capability tier, then wrap in the
layout). Nesting depth capped at 3.

Persisted as one `current_template`/`current_data` blob on the display;
restore-after-reconnect re-renders the whole composite.

### Overlay convention

There's no separate "overlay template" type. Any template can be pushed
via `display_overlay`. Most overlays will use `notification` in practice,
but `todo_list` or `message` as an overlay is fine too.

### Overlay stack semantics

- New overlay → push to top of stack → becomes the visible one.
- Tap-to-dismiss → pop top → next-newest becomes visible.
- Cap 10 in stack. When full, evict the **oldest** (bottom).
- On eviction, write an `overlay_dropped` event into `display_events` so
  the AI can re-push if it cares.
- No auto-clear by time. User must tap to dismiss. (This is deliberate —
  important content shouldn't silently vanish.)

### What is NOT seeded in v1

- `question` template (yes/no overlay). Deferred to phase 2 along with
  proper two-way tap → live AI session wiring.
- `grid_2x2` layout. `stack` + `split_horizontal` cover the realistic
  cases; AI can create `grid_2x2` via `display_create_template` if needed.

## Failure modes

| failure | behavior |
|---|---|
| SSE drops mid-session | EventSource auto-reconnects. On reconnect, server re-sends current scene + replays overlay stack. |
| `ruutu` pod restart | All SSE connections drop; displays auto-reconnect. State lives in PostgreSQL; post-restart render is identical. |
| Database unreachable | Tool calls return `{error: "database unavailable"}`. Existing SSE streams hold last-good frame and send heartbeat-only until DB recovers. (No fallback splash — more disruptive than freezing on last known content.) |
| `display_show` with unknown template | `{error: "template 'X' not found", suggestions: [closest matches]}`. AI fixes and retries. |
| Template lint failure on create/update | `{valid: false, issues: [{line, rule, message}]}`. AI iterates until clean. Nothing written to DB until valid. |
| Scriban render error at push time | Caught, logged with template name + data, push aborted, tool returns `{error: "render failed: <message>"}`. Display state unchanged. |
| URL hit for unregistered identifier | Static "this display isn't configured" page. No auto-create. |
| Invalid / hostile capability payload | Schema-validated. Anything malformed → fall back to legacy tier. Logged. |
| Tap on unknown target | Recorded as event anyway (AI might still want to know). Logged warning. |
| Overlay stack overflow (>10 pending) | Evict oldest. Write `overlay_dropped` event. |
| Clock drift on display | Not our problem. The `clock` template ticks client-side from `Date.now()`. NTP is the iPad's concern. |
| AI pushes to offline display | Push succeeds, state stored in DB. When display reconnects, queued state is delivered. No "offline" error to the AI. |

### Deliberately NOT in v1

- No retry-on-failure for failed tool calls — failures bubble up to the AI
  immediately. Cleaner debugging.
- No alerting/monitoring for offline displays. `display_list` shows status.
- No template versioning — botched updates require fix-forward.

## URL routing & deployment

### Ingress (external traffic)

| pattern | routes to |
|---|---|
| `${TOIMI_HOST}/ruutu/*` | ruutu pod |
| `${TOIMI_HOST}/toimihub` | toimi.web (SignalR) |
| `${TOIMI_HOST}/*` | toimi.web (chat UI, catch-all) |

### Inside the ruutu pod

The pod owns the `/ruutu` prefix in its own routes — no ingress rewrite,
keeps the pod self-contained.

| route | method | purpose | reachable from |
|---|---|---|---|
| `/ruutu/<identifier>` | GET | display HTML shell | LAN via ingress |
| `/ruutu/api/displays/<id>/capabilities` | POST | capability report on first load | LAN via ingress |
| `/ruutu/api/displays/<id>/stream` | GET | **SSE channel for display content push** | LAN via ingress |
| `/ruutu/api/displays/<id>/events` | POST | tap-back events | LAN via ingress |
| `/sse` | GET | **MCP transport** — toimi.web ↔ ruutu tool calls | cluster-internal only |
| `/health` | GET | k8s liveness/readiness probes | cluster-internal only |

### Naming wrinkle: two unrelated SSE endpoints

The ruutu pod hosts two Server-Sent Events endpoints that are unrelated to
each other:

- **`/sse`** — MCP HTTP transport (the C# `ModelContextProtocol` SDK
  default; exposed by `app.MapMcp()`). Cluster-internal. toimi.web
  connects here to invoke ruutu's tools. Same path all other Toimi tool
  servers use (`koti`, `muistutin`, etc. — verified at
  `src/toimi.web/appsettings.json:14-39`).
- **`/ruutu/api/displays/<id>/stream`** — display content push. LAN-facing.
  ruutu writes scene/overlay events here; the iPad's `EventSource`
  consumes them.

Same underlying tech (SSE), totally different consumers. Worth
double-checking when debugging — "the SSE channel is down" is ambiguous.

### TLS note

Old iPads have unreliable trust stores: modern Let's Encrypt root certs
often aren't trusted on iOS 9. For v1 LAN-only operation, HTTP is the
pragmatic choice (Toimi is already a home-network service). Exposing
beyond the LAN is a phase-2 concern; TLS strategy would need to account
for legacy clients (probably a long-lived ISRG Root X1 cert or running
HTTPS at a reverse proxy with stronger trust handling for non-iPad clients).

### Project layout

```
src/toimi.tools.ruutu/
├── Program.cs                       MCP server bootstrap, EF migrations on start
├── Dockerfile                       context = repo root
├── Data/
│   ├── RuutuDbContext.cs            Displays, Templates, DisplayEvents
│   ├── Migrations/
│   └── TemplateSeeder.cs            idempotent upsert, taidot-style
├── Tools/
│   ├── DisplayManagementTools.cs
│   ├── DisplayContentTools.cs
│   ├── TemplateTools.cs
│   └── DisplayEventsTools.cs
├── Rendering/
│   ├── ScribanRenderer.cs           template + data → HTML, recursing into layouts
│   ├── CapabilityClassifier.cs      detection payload → "modern"|"legacy"
│   └── TierLinter.cs                legacy-violation regex rules
├── Transport/
│   ├── SseHub.cs                    per-display SSE channel + heartbeat
│   └── DisplayApiController.cs      GET /<id>, POST /capabilities, POST /events
└── wwwroot/
    ├── shell.html                   the ES5 display page
    └── shell.css                    ~50-line reset/utility CSS
```

### K8s (`k8s/base/tools-ruutu/`)

- `deployment.yaml`, `service.yaml` — clone-and-rename from `tools-muistutin`.
  Image: `toimi-tools-ruutu`.
- `ingress.yaml` *(new for this pod)* — routes `${TOIMI_HOST}/ruutu/*` to
  this service. The existing toimi.web ingress retains `/` and `/toimihub`.

### Secrets / config

- New: `ConnectionStrings__Ruutu` in `toimi-secrets`.
- No new env-specific values in `config.env` (display URLs derive from
  `TOIMI_HOST`).
- New entry in `src/toimi.web/appsettings.json` McpServers array:
  `http://toimi-tools-ruutu.apps.svc.cluster.local/sse`.

### Database

Add `ruutu` to `infrastructure/base/helm/postgresql-values.yaml` DB list
and the DB-creation loop in `scripts/dev-setup.sh`.

### Skill seeding (in `taidot`)

New seeded skill `use-displays` teaching the AI:

> Displays are physical screens you can push content to. Use `display_list`
> to see what's available. Use `display_show(identifier, template, data)`
> to set the current scene. Use `display_overlay(identifier, template,
> data)` for transient notifications (user dismisses via tap). Templates
> are listed via `display_list_templates`. If you need a shape that
> doesn't exist, create one with `display_create_template` — both
> `modern_html` and `legacy_html` variants required, both linted before
> save. Composite scenes use layout templates (`split_horizontal`,
> `split_vertical`, `stack`) with nested `{template, data}` slots.

## Out of scope (phase 2 and beyond)

The following are deliberately deferred. Each is non-breaking to add later:

- **Live AI reaction to taps.** Tap events store, AI polls. No background
  loop watches for events and spawns a session. Likely lands in
  `ajastin` or a new "reactor" worker.
- **Slot-based partial scene updates** (`display_show_slot`). Current
  design re-pushes the whole scene on every change. If this becomes an
  optimization that matters, add then.
- **Per-display auth tokens.** Add `auth_token` column + URL-or-header
  check. One migration. Needed if displays ever live outside the LAN.
- **TLS for non-LAN exposure.** Requires handling old-iPad trust store.
- **Template versioning / rollback.** Botched updates currently require
  fix-forward.
- **Event retention sweep.** Add a "trim `display_events` older than 7
  days" background task.
- **Free-form `display_set_layout([tiles])`.** Currently composition is via
  seeded layout templates only.
- **`question` template + two-way conversation flow.** Yes/no UI with the
  display feeding back into a live agent loop.
- **Multi-display broadcast.** AI explicitly targets each display by
  identifier.
- **Multiple capability tiers** (e.g., `text-only` for an e-paper display).
  Two tiers is enough until a third real device shows up.

## Open questions deferred to implementation

- Exact Scriban sandbox configuration (which built-ins to disable for
  AI-authored templates).
- Concrete tier-classification thresholds beyond the v1 boolean
  (`flexbox && fetch && promise`) — likely fine but may need tuning once
  real devices report capabilities.
- Whether the shell page should preload the linter rule descriptions for
  in-page debugging (probably no, but flagging for the implementer to
  decide once the shell is written).
