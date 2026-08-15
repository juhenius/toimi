# Toimi

A self-hostable, single-user AI assistant. One bounded context: typed entities
with triggered behaviors, surfaced to an LLM agent over MCP.

## Language

### Entities & triggers

**Entity**:
A typed unit of user/AI-owned data (jsonb, schema-validated) stored in tietue.

**Trigger**:
An instruction attached to an entity to fire a handler when its anchor
condition occurs.

**Anchor**:
The condition that fires a trigger. Two kinds: time-anchored (a schedule
determines when it fires) and call-anchored (an inbound HTTP call fires it).
A trigger has exactly one anchor; "fires on the clock and on call" is two
triggers on the same entity.
_Avoid_: "schedule" as a synonym for the general concept — a schedule is one
kind of anchor.

**Webhook**:
A call-anchored trigger. Fired by an external HTTP call to its endpoint, not
by the clock. Always inbound; Toimi calling other systems' URLs is not a
webhook here.
_Avoid_: hook, callback

**Handler**:
What a trigger executes when fired. A cost ladder: notify, set-field, script,
message (agent run).

**Occurrence**:
One firing of a trigger, recorded as an entity event; idempotent per
(entity, occurrence, kind).

**Params**:
The call-time arguments a firing carries, visible to every handler kind:
scripts read `input.params`, notify templates interpolate `{key}` tokens, and
message (agent) prompts receive them as a fenced data block — never
interpolated, because params come from whoever holds the capability URL and
must not become agent instructions. Always present: for a webhook firing it
is the merge of the caller's query string and JSON body (body wins per key);
for time-anchored and manual firings it is empty unless supplied explicitly.
_Avoid_: payload, args, query

### Models

**Fast model**:
The default LLM every agent turn runs on; chosen for cost, always configured.
_Avoid_: cheap model, small model

**Smart model**:
The more capable LLM used for harder work; optional — when unconfigured, the
fast model stands in wherever the smart model is asked for.
_Avoid_: expensive model, large model, flagship

**Delegate**:
The act of an agent handing a self-contained task to a subtask running in a
fresh context, instead of doing it in its own. Three habits: fast→smart
(escalation), smart→fast (cheap chores), same→same (context isolation).
_Avoid_: escalate (only one of the three habits), spawn

**Subtask**:
An agent run created by delegation. It sees only the brief it was given —
never the parent's history — runs on the requested model, and returns its
result to the parent as a tool result. A subtask may delegate once more; a
subtask's subtask may not.
_Avoid_: subagent (names the worker, not the work)

### Displays

**Scene**:
The template + data currently showing on a display, replaced wholesale by
each push.
_Avoid_: page, view, screen (a screen is the physical device)

**Display event**:
The `{type, target, value}` record a display emits when the user interacts
with it (tap, check, dismiss).
_Avoid_: tap-back, interaction, click

**Action**:
An entry in a scene mapping a display event to a webhook; when the event
occurs, ruutu forwards it — the event becomes the firing's params, and no
data comes back. Actions live and die with their scene.
_Avoid_: binding, callback, button-handler
