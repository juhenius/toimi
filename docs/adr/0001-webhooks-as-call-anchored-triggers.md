# Webhooks are call-anchored triggers with doorbell semantics

Toimi needs inbound webhooks: an external HTTP call (a Home Assistant
automation, a curl, a ruutu dashboard button) causes a handler — typically a
script — to run. We decided a webhook is **not** a new concept, table, or pod:
it is a third anchor form in the existing trigger grammar. A trigger's anchor
is exactly one of `{at}` (one-shot), `{start,rrule,tz}` (recurring), or
`{webhook: {activeAfter?, activeUntil?, rateLimit?}}` (call-anchored). The
entire downstream machinery — handler ladder, `OccurrenceRunner`,
`EntityEvent` idempotency, the `set_trigger` MCP lifecycle, `DefaultTriggers`
copy-down — is reused untouched; call-anchored triggers keep `NextFireAt`
null, so the scheduler never sees them.

## Considered options

- **A new seeded type or a separate webhook subsystem** — rejected: a `job`
  is already "an entity whose trigger runs a script"; the only missing piece
  was a call-based way to fire a trigger. Modeling the webhook as a firing
  path (not an owner of code) gives "webhook that notifies / flips a field /
  wakes the agent" for free, and avoids a parallel lifecycle of verbs.
- **Hybrid anchors (time + call on one trigger)** — rejected: made
  `NextFireAt`, `LastFiredAt`, validity windows, and rate limits ambiguous.
  "Fires on the clock and on call" is two triggers on the same entity; code
  duplication is avoided by `{fromEntity: true}` job triggers.
- **Request/reply ("webhook as API")** — rejected: the response to a valid
  call is `202` plus the occurrence id, never handler output. The bearer of a
  capability URL is whatever system the URL was handed to (or leaked to);
  returning script output would let a leaked URL exfiltrate data, and holding
  the connection open across a `message` handler (a full agent run) invites
  timeouts at every hop. Results land in `EntityEvents`, like every other
  firing. If a ruutu button ever needs the result, that is a separate
  cluster-internal ruutu↔tietue feature, not a webhook.

## Consequences

- **Auth is a capability URL**: `/hooks/{triggerId}/{secret}` — the id
  routes, the server-minted secret (its own column on `Trigger`, not part of
  the anchor grammar) authorizes via constant-time compare. The secret is
  retrievable through the trigger tools (the agent must be able to hand the
  URL to external systems); it is deliberately not hashed — it lives in the
  same database as the scripts it fires. HMAC signing was considered and
  deferred: our callers are systems we configure, not third-party platforms.
- **Uniform `404`** for unknown id, wrong secret, disabled, kill-switched, or
  outside `activeAfter`/`activeUntil` — the endpoint never confirms a
  webhook's existence. Post-auth errors are diagnostic: `400` malformed JSON,
  `413` over the 64 KB body cap, `429` over the per-webhook rate limit
  (default 6/min). A global pre-auth cap (default 120/min across all of
  `/hooks`) meters unauthenticated probe floods before they reach the
  database. Methods: `GET` and `POST` only. Writes reject a webhook whose
  `activeUntil` is already past — the call-anchored analogue of an exhausted
  recurrence.
- **Validity windows are anchor grammar, not agent housekeeping**: enforced
  by compare-at-request-time, which cannot fail open — a scheduled script
  flipping `Enabled` off could (and would need the dangerous
  `mcp:update_trigger` grant, or a full agent run, to flip a boolean).
- **`params` unify call-time input**: every firing carries params — for
  webhook firings the merge of query string and JSON body (body wins per
  key), empty for time-anchored firings, optionally supplied to
  `run_trigger` for testing. Scripts read `input.params`; notify templates
  interpolate `{key}` tokens; message (agent) prompts receive params as a
  fenced data block, never interpolated — rendered into the prompt they
  would let a capability-URL holder inject instructions into an agent run
  that reaches every MCP tool. Scripts never see raw request
  bodies/queries/headers, so the same script runs unchanged under any anchor.
- **Notify templates render caller params**: a `{token}` absent from the
  entity's `Data` falls through to the firing's params, so the holder of a
  capability URL can shape a push notification's text (`?who=Postman` →
  "Postman at the door"). Accepted deliberately (2026-08-15): possession of
  the URL already grants firing the notification at will; the deployment is
  single-user and LAN-only; and param interpolation is the notify handler's
  main webhook use. `Data` wins on collision, and agent prompts stay fenced —
  params never become instructions.
- **Exposure**: a path-scoped `/hooks` Ingress on `${TOIMI_HOST}` routes to
  the tietue pod (the ruutu/selain precedent); tietue's MCP and admin
  surfaces stay cluster-internal. A global `Webhooks:Enabled` kill switch
  mirrors `Scripts:Enabled`.
