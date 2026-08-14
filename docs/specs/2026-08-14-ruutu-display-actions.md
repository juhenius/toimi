# Ruutu display actions — two-way interaction via webhooks

**Date:** 2026-08-14
**Status:** Designed (grilling session 2026-08-13/14); see ADR 0002 and the
Displays section of `CONTEXT.md` for the decision record and vocabulary.

## 1. Motivation

Displays are currently one-way: the AI pushes scenes; user taps are recorded
as display events and sit in the `display_events` table until the AI happens
to poll `display_get_events`. Nothing *happens* when you check a todo on the
wall — the strike-through is client-side make-believe.

This feature adds the reactive half: a tap on a wired element causes a real
change in toimi (the todo entity's `done` field flips), through the webhook
machinery that already exists (ADR 0001). Because a webhook trigger carries
the whole handler cost ladder, the same wiring reaches *any* toimi
capability: set a field, run a script, call an MCP tool, wake an agent.

## 2. Decision summary

| Decision | Choice |
|---|---|
| Pathway | Server-side bridge: ruutu forwards matching display events to webhook capability URLs (ADR 0002) |
| Declaration | Scene-scoped **actions** map, pushed with `display_show`, replaced with every push |
| Secrets | Capability URLs live in ruutu's DB only — never in page HTML |
| Response | Doorbell only (202, no data back); the display's answer channel is its SSE stream |
| Feedback | The trigger's handler re-pushes the scene (`display_show`); optimistic UI covers the gap |
| Failure UX | Ruutu pushes a `notification` overlay when a forward fails, and records the outcome on the display event |
| Params | `{type, target, value, display}` |
| Shell/templates | Zero changes — `todo_list` already emits the right events |

## 3. Design

### Actions map

`display_show` gains an optional `actionsJson` parameter: a JSON object
mapping an **event selector** to a **webhook capability URL**:

```json
{
  "check": "http://toimi-tools-tietue.apps.svc.cluster.local/hooks/{triggerId}/{secret}",
  "tap:snooze-btn": "http://.../hooks/{triggerId2}/{secret2}"
}
```

- Selector is `"<type>"` or `"<type>:<target>"`. On event arrival the
  `type:target` form wins over the bare `type` form.
- Values must be absolute `http`/`https` URLs; validated at push time,
  rejected otherwise (`ToolGuard` message, no partial push).
- Stored on the display row (`displays.current_actions`, jsonb) alongside
  `CurrentData`. Every `display_show` **replaces** the map (absent ⇒ null);
  `display_clear` nulls it. Actions live and die with their scene, so a
  stale page's buttons stop forwarding the moment a new scene is pushed.
- Idle scenes (`display_set_idle`) carry no actions — idle is passive.

### Forwarding

On `POST /ruutu/api/displays/{identifier}/events`, after the event is
appended. A `dismiss:overlay` event pops the overlay and stops there — overlay
dismissal is shell plumbing and never resolves against scene actions (a bare
`"dismiss"` wiring must not fire on ruutu's own failure overlay):

1. Resolve the current scene's actions map against `{type, target}` and
   return 200 immediately — the forward itself is queued to a background
   worker (the shell's POST is fire-and-forget; it must ride neither the
   forward's latency nor its cancellation token).
2. The worker POSTs the capability URL with JSON body
   `{"type": ..., "target": ..., "value": ..., "display": "<identifier>"}`
   — these become the firing's params per ADR 0001. Timeout 10 s. `value`
   is the element's `data-value` JSON-parsed when parseable (booleans,
   numbers, objects arrive typed), else the raw string.
3. Record the outcome on the display event
   (`display_events.forward_outcome`) using a closed vocabulary:
   `ok` / `error: <status>` / `error: timeout` / `error: unreachable` /
   `error: failed`.
4. On failure: push a `notification` overlay ("couldn't reach toimi") over
   SSE — deduped, so repeated failed taps don't stack identical cards. This
   is ruutu's own voice — plumbing feedback only, never domain content.

**In-cluster rewrite:** capability URLs are composed by tietue from
`Webhooks__PublicBaseUrl`, so agents naturally wire actions with the public
`https://${TOIMI_HOST}/hooks/...` form — which a pod cannot reach when the
ingress certificate isn't in its trust store (found in first live test).
Ruutu therefore rewrites a URL whose host is `Actions__PublicHookHost` and
whose path starts with `/hooks/` onto `Actions__InternalHookBase`
(`http://toimi-tools-tietue.apps.svc.cluster.local`) at forward time: no
ingress hop, no TLS dependency, and already-stored actions maps keep
working. Both values come from the base deployment manifest; unset (tests,
local runs) means forward verbatim. URLs for other hosts/paths are never
rewritten.

No retries: the user's finger is the retry loop, and handlers are cheap to
re-fire (occurrence idempotency lives in tietue).

### What deliberately does not change

- The shell JS, the `data-tap` vocabulary, all seeded templates.
- The webhook contract (ADR 0001): doorbell semantics, no response data.
  Query-style interactions are out of scope; a caller with no push channel
  is a future, separate feature.
- Trust model: knowing a display identifier already means being able to
  POST events; actions add no new surface (the capability URL is checked by
  tietue as always). LAN-only deployment, identifier-as-capability.

## 4. Implementation shape

- `Display.CurrentActions` (`string?`, jsonb) + migration; `DisplayEvent.ForwardOutcome` (`string?`) in the same migration.
- `Transport/SceneActions.cs` — parse/validate the map (push time) and
  resolve a selector (event time). Pure, unit-testable.
- `Transport/ActionForwarder.cs` — the outbound POST + outcome recording +
  failure overlay. Named `HttpClient` (`"actions"`, 10 s timeout) — ruutu's
  first outbound call, accepted in ADR 0002.
- `ContentPushService.ShowSceneAsync` gains the actions parameter;
  `ClearAsync` nulls it.
- `DisplayContentTools.DisplayShow` gains optional `actionsJson`.
- Controller `PostEvent` wires resolve → forward.

## 5. Testing

- `SceneActions`: selector precedence (`type:target` over `type`), no-match,
  malformed JSON/URL rejection.
- `PostEvent` with a fake `HttpMessageHandler`: matched event POSTs the
  params body; outcome recorded; failure pushes a notification overlay on
  the SSE hub; unmatched events forward nothing.
- Scene replacement clears/replaces actions; `display_clear` nulls them.

## 6. Example: the wall todo list

1. Agent creates a `todo` entity in tietue, sets a call-anchored trigger on
   it whose script handler flips `steps[target].done` and re-pushes the
   scene (script grants: `mcp:update`, `mcp:display_show`).
2. Agent pushes the scene:
   `display_show("kitchen", "todo_list", {steps: [...]}, actions: {"check": "<hookURL>"})`.
3. User taps a step → shell POSTs `{type:"check", target:"step-3", value:false}`
   → ruutu forwards to the hook → trigger fires → script updates the entity
   and re-pushes the todo list → SSE swaps the scene, now authoritative.
