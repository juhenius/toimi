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
