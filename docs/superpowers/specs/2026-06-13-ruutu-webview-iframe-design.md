# ruutu `webview` — embed external web pages on a display

**Date:** 2026-06-13
**Status:** Approved (design)
**Scope:** `src/toimi.tools.ruutu`, plus one line in `src/toimi.tools.taidot/Skills/SkillSeeder.cs`

## Goal

Let Toimi put an external web page — e.g. a parcel-tracking page — on a
registered ruutu display by pushing one seeded template with a URL. The page
is shown inside a sandboxed `<iframe>`. Toimi reaches this through the existing
`DisplayShow` tool; no new MCP tool is added.

## Background

ruutu renders Scriban templates to raw HTML strings; the display shell injects
that HTML via `.innerHTML` over an SSE stream. So an `<iframe src="…">` in a
template renders as-is — no transport change is needed. Relevant existing
pieces:

- `Rendering/ScribanRenderer.cs` — `Template.Parse` + `template.Render` in
  Scriban **text mode** (no HTML auto-escaping). Data is exposed via a
  `ScriptObject` pushed as a global; no custom Scriban functions are registered
  today. Scriban's built-in `html.escape` is available.
- `Data/SeedTemplates.cs` — `SeedTemplate(Name, Description, SchemaJson,
  ModernHtml, LegacyHtml)` records in `SeedTemplates.All`, upserted on startup.
- `Rendering/TierLinter.cs` — lints template HTML at create/update time. Bans
  `<script>` for both tiers; bans flex/grid, CSS variables, WebP,
  `@import`/`@font-face`, `clamp()/min()/max()`, `:has()/:is()/:where()` for
  legacy. **Does not lint runtime data** — it only sees template text, so it is
  not the place to enforce a URL scheme.
- `Transport/ContentPushService.cs` — `ShowSceneAsync` renders and publishes a
  scene and persists `CurrentTemplate` + `CurrentData` + `CurrentPushedAt` on
  the display row.
- `Tools/DisplayContentTools.cs` — `DisplayShow(identifier, template, dataJson)`.
- `src/toimi.tools.taidot/Skills/SkillSeeder.cs:271` — the `use-displays`
  seeded skill that teaches Toimi how to drive displays.

## Decisions (from brainstorming)

- URLs may come from Toimi itself, not only the user.
- Guarding = **https-only + sandbox + current-state audit**; no domain
  allowlist, no per-show confirmation.
- **One** template with an optional title (covers both full-bleed and titled
  looks).
- Store **current** data only (no historical log).

## Design

### 1. `safe_url` Scriban filter — the only new code

Add a static `SafeUrl(object? input) -> string` and register it into the
per-render `ScriptObject` in `ScribanRenderer.RenderInternalAsync`, so every
template can use `{{ url | safe_url }}`.

Behavior:

1. Coerce input to string; treat null/empty as failure.
2. `Uri.TryCreate(s, UriKind.Absolute, out uri)` must succeed.
3. Require `uri.Scheme == "https"` (case-insensitive). This rejects
   `javascript:`, `data:`, `http:`, `file:`, etc.
4. Reject hosts that are not safe to point a home display's browser at:
   - `uri.IsLoopback`
   - host that parses to an `IPAddress` in a private/link-local range
     (10/8, 172.16/12, 192.168/16, 169.254/16, `::1`, `fc00::/7`, `fe80::/10`)
   - single-label hosts (no dot) — e.g. `router`, `localhost`
5. On any failure return the literal string `about:blank`.
6. On success return the absolute URI **HTML-attribute-escaped** (`&`, `"`,
   `<`, `>`, `'`). The renderer is text-mode, so escaping must be explicit.

The filter is a general-purpose primitive (not iframe-specific) and has no side
effects.

### 2. Seeded `webview` template

Add to `SeedTemplates.All`:

- **Name:** `webview`
- **Description:** "Embed an external web page (e.g. a parcel-tracking page) in
  a sandboxed iframe. Provide an https `url`; optional `title` shows a header
  bar. Works on modern and legacy displays. Note: sites that forbid framing
  (X-Frame-Options / CSP frame-ancestors) will appear blank."
- **SchemaJson:**
  ```json
  {
    "type": "object",
    "properties": {
      "url":   { "type": "string", "description": "https URL to embed" },
      "title": { "type": "string", "description": "optional header label" }
    },
    "required": ["url"],
    "additionalProperties": false
  }
  ```
- **ModernHtml == LegacyHtml** (identical; uses no flex/grid/variables/clamp, so
  it is lint-clean on both tiers):
  ```html
  {{ if title }}<div style="height:40px;background:#222;color:#fff;font:500 15px -apple-system,Helvetica,Arial,sans-serif;line-height:40px;padding:0 14px;overflow:hidden;white-space:nowrap">{{ title | html.escape }}</div>{{ end }}
  <iframe src="{{ url | safe_url }}"
          sandbox="allow-scripts allow-same-origin"
          referrerpolicy="no-referrer"
          style="display:block;width:100%;height:{{ if title }}calc(100% - 40px){{ else }}100%{{ end }};border:0;background:#fff"></iframe>
  ```

Notes:

- `sandbox="allow-scripts allow-same-origin"` lets the embedded **cross-origin**
  page run its own JS (so tracking pages work) while keeping it unable to script
  the display shell or navigate the top frame. `allow-top-navigation` and
  `allow-popups` are deliberately omitted. Because the embedded content is
  third-party/cross-origin, the well-known "allow-scripts + allow-same-origin
  escapes the sandbox" footgun does not apply — `safe_url` additionally refuses
  same-host/internal targets.
- `title` is escaped with Scriban's built-in `html.escape`; `url` is validated
  and escaped by `safe_url`.

### 3. No new tool, no linter change

Toimi uses the existing `DisplayShow(identifier, "webview", {url, title?})`.
The template composes as a slot in `split_*`/`stack` layouts via the existing
slot-ref mechanism with no extra work. Audit trail = the existing
`CurrentTemplate`/`CurrentData`/`CurrentPushedAt` persisted on every show.

### 4. Teach Toimi

Extend the `use-displays` seeded skill (`SkillSeeder.cs:271`) with a line:

> To show a web page or tracking link on a display, `DisplayShow` the `webview`
> template with `{ "url": "https://…", "title": "Parcel tracking" }` (title
> optional). Only https URLs are accepted; pages that forbid framing will show
> blank.

## Testing (TDD, alongside ruutu's existing 57 tests)

`safe_url` unit tests:

- `https://posti.fi/track/123` → returned, attribute-escaped, scheme preserved.
- `http://example.com` → `about:blank`.
- `javascript:alert(1)` → `about:blank`.
- `data:text/html,<h1>x` → `about:blank`.
- `https://localhost/admin`, `https://192.168.1.1/`, `https://router` →
  `about:blank`.
- malformed / null / empty → `about:blank`.
- `https://x.test/a"onload="y` → returned with the `"` escaped (no attribute
  breakout).

Render tests (via `ScribanRenderer` / `DisplayPreview`):

- `webview` with `{url, title}` → output contains one `<iframe>` with the
  `sandbox` attribute and the escaped `src`, plus a header containing the
  escaped title.
- `webview` with `{url}` only → no header div; iframe height `100%`.

Lint test:

- The seeded `webview` body passes `TierLinter` for the `legacy` tier.

## Out of scope (YAGNI)

- Historical logging of shown URLs (current-state persistence is enough for now).
- iframe → shell interactivity / `postMessage` (read-only display).
- Auto-refresh (the embedded page refreshes itself; user can re-ask).
- Domain allowlist; per-show confirmation.
- Making `<iframe>` a first-class linter capability for arbitrary
  AI-authored templates.

## Known limitations

- Sites sending `X-Frame-Options: DENY`/`SAMEORIGIN` or a CSP
  `frame-ancestors` directive will refuse to load in the iframe and render
  blank. This is enforced by the remote site and cannot be bypassed
  client-side. The skill text and template description call this out.
