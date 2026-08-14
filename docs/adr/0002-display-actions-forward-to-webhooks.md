# Display actions forward display events to webhooks

Ruutu displays needed to *cause* things in Toimi — check a todo on the wall
and the entity updates — not just record taps for later polling. We decided a
scene may declare **actions**: a mapping from display events to webhook
capability URLs, supplied alongside the scene's data at push time and living
only as long as the scene. When a matching display event arrives, ruutu
forwards it server-side to the capability URL — `{type, target, value,
display}` become the firing's params — with doorbell semantics: `202`, no
data back, exactly the "separate ruutu feature" that ADR 0001's
request/reply rejection pointed at, built on the webhook door rather than a
new one.

## Considered options

- **Capability URLs in the page HTML** (a shell data-attribute the browser
  POSTs to directly; same-origin, would work today) — rejected: display
  pages are unauthenticated surfaces gated only by the display identifier,
  so hook secrets in markup are harvestable by anything that can fetch the
  page; templates would need changes (`todo_list` already emits the right
  events untouched); and the bridge keeps legacy-tier displays working with
  zero shell changes.
- **Ruutu as an MCP client** calling tietue tools on tap — rejected: a new
  trust relationship, and it duplicates dispatch, idempotency, rate limiting,
  and the handler ladder that triggers already own. Ruutu stays render-only;
  a display is just another webhook caller.
- **A bespoke display-actions API in tietue** — rejected: a parallel
  lifecycle of verbs next to the webhook one, for no added capability.
- **One per-display funnel trigger** (forward every event to a single hook,
  route in the handler) — rejected: every tap costs a dispatch even for
  unwired elements, and routing logic sinks into script/agent code. The
  scene is what gives a `target` meaning, so the wiring is scene-scoped —
  which also self-solves staleness: push a new scene and the old buttons
  stop forwarding.

## Consequences

- **Ruutu gains its first outbound HTTP call** (to `/hooks` only), ending
  its no-outbound-calls purity. Accepted as the price of keeping secrets
  server-side.
- **Feedback is push, not response.** The display's return channel is its
  SSE stream: the handler that mutates state is responsible for re-pushing
  the scene (`display_show` — a script gets the MCP grant, an agent just
  calls it), which is why `display` is in the params. Optimistic UI covers
  the gap; a handler that forgets to re-push leaves a stale screen — an
  authoring bug, not a design hole.
- **Forward failures are ruutu's voice.** On `404`/`429`/connection error,
  ruutu itself pushes a notification overlay ("couldn't reach toimi") and
  records the failure on the display event — the one place ruutu composes
  content autonomously. Plumbing feedback only, never domain content.
- **Query-style interactions stay out of scope.** A tap never gets data in
  the HTTP response (ADR 0001). Every display scenario resolves to "handler
  pushes the answer"; a caller with no push channel (e.g. a phone shortcut
  wanting an answer body) is a different future feature, not a bend in this
  one.
- **Trust model: knowing a display's identifier = permission to press its
  buttons.** Accepted for a LAN-only, single-user deployment; identifiers
  are treated as capabilities (unguessable slugs), and the risk budget is
  carried by handler choice — a display's actions should point at
  deterministic handlers unless there is a deliberate reason otherwise.
