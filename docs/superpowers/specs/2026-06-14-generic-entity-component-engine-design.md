# Design Study: Generic Entity Engine (`tietue`)

**Date:** 2026-06-14 (updated 2026-06-15)
**Status:** Design study (greenfield north star — full replacement of the stateful tool servers)
**Author:** Jari + Claude

> **Framing note.** This started as an "entity + components" design. Through
> iteration the spine moved: the center is now an **entity** whose durable state is
> `Data`, driven by **triggers** that invoke **handlers** on a cost ladder, with a
> **conversation** as optional lazy context. "Components" survive as two flavors of
> shipped behavior — *declarative behaviors* (passive) and *built-in handlers*
> (reactive). The naming reflects that shift.

---

## 1. Summary & verdict

Today each stateful domain in Toimi ships as its own tool server with a bespoke
EF Core model: `muistio` (Memory), `taidot` (SkillEntry), `muistutin` (Reminder),
`ajastin` (Schedule). Two capabilities are reimplemented across them — **semantic
indexing** (muistio + taidot) and **time-anchored firing** (muistutin + ajastin) —
and a third (autonomous agent runs, ajastin) is a stateless special case of
something more general.

This study evaluates replacing those four servers with a single **generic entity
engine**: one pod (`tietue`, Finnish for *record*) that stores arbitrary typed
entities, where a **type** is a user/AI-defined JSON Schema plus declarative
behaviors and default triggers, behavior is delivered through a **handler cost
ladder** (cheap deterministic → expensive agentic), and a **sandboxed script**
handler is the escape hatch for bespoke logic.

**Verdict: feasible and worth doing as a greenfield change.** The four domains map
onto the model cleanly — the key feasibility signal. Duplication collapses (one
embedding pipeline, one scheduler, one events table), and *each domain reduces to
"entity + Data + zero-or-more triggers + optional semantic index."* Runtime
user-defined types — impossible today without writing and deploying a pod — become a
first-class capability. The real costs are *intrinsic to the abstraction*, not
migration artifacts: jsonb trades away compile-time type-safety and FK constraints
(heavily mitigable — §15.1), and a generic MCP surface guides the AI less than typed
verbs (mitigated by type-catalog injection). Since Toimi is pre-production, the
migration risk that argues for incrementalism does not apply — build the clean
end-state directly.

`koti` (stateless Home Assistant proxy) and `verkko` (stateless web/ntfy utilities)
have **no domain model** and stay as separate pods. "Full replacement" only ever
meant the four stateful servers.

---

## 2. Goals

All four motivations are in scope (confirmed):

1. **Runtime types** — define a new typed thing (houseplants, workouts, a wishlist)
   with schema + behaviors *without writing or deploying code*.
2. **Kill duplication** — one semantic-index pipeline, one scheduler, one events log.
3. **Unify storage & cross-type query** — one store; query/tag across all data
   ("everything tagged `vacation`") instead of siloed per-server stores.
4. **Simpler AI mental model** — one consistent CRUD + search + trigger surface,
   plus an injected catalog of available types, instead of N bespoke verb sets.

**Non-goals:** absorbing `koti`/`verkko`; multi-tenant isolation (single-user,
self-hosted); a general-purpose plugin marketplace; overridable built-ins (§5).

---

## 3. The model

Four concepts: **Entity** (state), **TypeDefinition** (template), **Trigger**
(reactive scheduling), **Handler** (what a trigger does).

### Entity — durable state, `Data` is the source of truth

```
Entity {
  Id         Guid
  Type       string         // references a TypeDefinition.Name
  Data       jsonb          // user-defined fields, validated against the type schema
  Tags       string[]
  CreatedAt  timestamptz
  UpdatedAt  timestamptz
}
```

**`Data` is the source of truth.** Everything an agent needs to know about an entity
lives here as structured state — not in a conversation transcript. `Data` is
schema-on-write: validated against the type's JSON Schema before persistence.

### TypeDefinition — schema + declarative behaviors + default triggers

```
TypeDefinition {
  Name             string
  JsonSchema       jsonb                          // shape of Entity.Data
  Behaviors        [ { behavior, config } ]       // passive: SemanticIndex, Expiry, UniqueName
  DefaultTriggers  [ Trigger template ]           // copied onto each instance at creation (§7)
  Indexes / Promote                               // jsonb index + generated-column hints (§15.1)
}
```

Type definitions are themselves data (a `type_definitions` table), so the AI can
create/evolve them at runtime. The four current domains ship as **seeded standard
types** (idempotent, mirroring today's `SkillSeeder`).

### Trigger — a reactive, scheduled (or condition-driven) action

```
Trigger {
  Id, EntityId
  When         // "at" timestamp | RRULE | (later) condition
  Handler      // { kind, config } — see §4
  Enabled, NextFireAt, LastFiredAt
}
```

Triggers live on the **instance**, seeded from the type by **copy-down** (§7). A
trigger is the *scheduled* producer of activations to the entity's **inbox** (§7.1);
the scheduler fires due triggers and invokes their handler.

### Handler — what a trigger does, on a cost ladder

A handler is `{ kind, config }` where `kind` is a **built-in name**, `"script"`, or
`"message"`. Handlers form a cost ladder; lower rungs can **escalate** upward
(§4). This is the heart of the design.

---

## 4. The handler cost ladder

The scheduler fires a trigger; the trigger's handler is the **cheapest rung that does
the job**:

| Tier | `kind` | LLM? | Cost | Flexibility |
|---|---|---|---|---|
| **Deterministic** | `<builtin>` (native) or `script` (sandboxed) | no | ~zero–cheap | fixed (builtin) / custom-coded (script) |
| **Agentic — ephemeral** | `message`, `mode: ephemeral` | yes | medium — context = `Data` + the message | full, no memory |
| **Agentic — threaded** | `message`, `mode: threaded` | yes | highest — context = conversation + `Data` | full, with continuity |

The agent picks the lowest rung that works and **escalates up** when it isn't
enough. `escalate()` is a first-class capability every deterministic handler holds —
a `script` that hits a case it can't handle wakes the agent (ephemeral), which can in
turn open a threaded conversation for genuinely multi-turn work. **Escalation is a
requirement, not a nicety:** without it you must either over-use the LLM or let
scripts silently mishandle edge cases. It also makes a *wrong* rung choice
self-correcting (too-low escalates; too-high just costs tokens until the agent
demotes it by writing a script).

**Two deterministic flavors, different blast radius:**

- **Native built-ins** — trusted code Toimi ships, *no sandbox, no capability
  guards*: `notify`, `poll-diff`, `set-field`, … `notify` lives here *deliberately*
  because it is the critical path (reminders depend on it) and must fire even if the
  scripting interpreter is wedged or a capability grant is misconfigured. Routing the
  most important deterministic action through the experimental sandbox would couple
  reliable bread-and-butter to the riskiest subsystem.
- **Scripts** — AI-authored, sandboxed, capability-gated (§6). The escape hatch for
  behavior we didn't ship.

Same cost tier; "builtin vs script" is a *trust/runtime* distinction, not a cost
distinction. For now the native set is **un-overridable** — a custom script is
*additive*, it cannot shadow `notify` et al.

---

## 5. Built-in behaviors & handlers (the palette)

Everything Toimi ships, in two kinds: **declarative behaviors** (passive — run on
save/query, not on a schedule) and **built-in handlers** (reactive — invoked by a
trigger).

### Declarative behaviors (passive, per-type)

| Behavior | Config | Effect |
|---|---|---|
| `SemanticIndex` | `{ fields, mode }` | Embed fields → Qdrant on save; advertise semantic search (§9). **Folds muistio + taidot.** |
| `Expiry` | `{ field }` | Provision a one-shot delete/message trigger at {field}; agent-decided mode via {prompt}. (Implemented.) |
| `UniqueName` | `{ field }` | Enforce field uniqueness within the type. |

### Built-in handlers (reactive, trigger-invoked)

| Handler | Config | Behavior |
|---|---|---|
| `notify` | `{ titleTemplate, messageTemplate, priority }` | Render from `Data`, dispatch via `toimi.notifications` (ntfy), record an event. **Reminder firing.** |
| `poll-diff` | `{ source, extract, on_change }` | Fetch an external source, extract value(s), diff against last recorded; on change record + run `on_change` (e.g. `notify` or escalate to `message`). This is the **"Watch"** capability — *not* a central concept, just one built-in producer. |
| `set-field` | `{ path, value }` | Deterministic state mutation (e.g. flip a status). |
| `message` | `{ mode, charter }` | Agentic activation (§4, §8). `mode: ephemeral \| threaded`. ajastin's "run a prompt on schedule" is `message` + a cron trigger. |

A "reminder" = entity + trigger `{ RRULE, notify }`. A "schedule" (ajastin) = entity
+ trigger `{ cron, message:ephemeral }`. A "wishlist item" = entity + trigger
`{ every 6h, poll-diff → escalate on change }`. **The duplication this erases:** a
reminder and a cron agent run differ only in which handler their trigger carries.

---

## 6. Scripts & the sandbox

Scripts are AI-authored handlers (and validation/derive hooks) the engine runs in a
sandbox. They are the agent's way to **demote** a recurring trigger off the LLM once
it understands the pattern.

```
script {
  language:     "js"                 // embedded interpreter; WASM-swappable later
  source:       "<code>"
  capabilities: [ ]                  // explicitly granted; default none
}
```

**Trigger-handling scripts are not pure** — unlike pure `validate`/`derive` hooks,
they need side effects: `readData`/`writeData`, `notify`, `reschedule` (CRUD a
trigger on the entity), `fetch`, and `escalate`. That is a real capability surface.

**Design principles:**

- **Capability-injected.** The host passes `data` plus *only* the granted
  capabilities; no ambient filesystem/network. A `validate` hook gets `data`, returns
  errors. A trigger script gets `data` + its grants.
- **Why sandbox at all, single-user?** Threat model is lower than multi-tenant SaaS,
  but hooks are **AI-authored**, and a prompt-injected agent (tricked by a fetched
  page) writing a `fetch`+`notify` script could exfiltrate or spam. Sandbox for
  *stability and safety against runaway/injected code*.
- **Guards:** per-script capability grants; `notify` rate-limit; `fetch` domain
  allowlist; execution timeout; memory cap; per-type/per-script kill switch;
  structured logging of every invocation (input hash, duration, outcome).
- **Runtime:** embedded interpreter (Jint/JS or MoonSharp/Lua — pure .NET, sandboxed
  by not exposing IO). Host API designed so the runtime swaps to WASM/Extism later.

Native handlers cover what's performance- and reliability-critical; scripts cover the
long tail. This is the resolution of "fixed palette vs fully extensible" — it is
*both*.

---

## 7. Triggers, self-scheduling & copy-down

**Triggers live on the entity instance.** The scheduler only ever reads instance
triggers; the type is a factory, not a live parent.

**"The agent schedules its next activation" is just trigger CRUD.** There is no
separate scheduling primitive — when an agent (mid-activation) decides "check again in
3 days," it calls a `set_trigger`/`update_trigger` tool on **its own entity**. A
one-shot trigger disables itself after firing; the agent re-arms each wake, or edits
the `nextFireAt`/cadence of a standing recurring trigger. ajastin's "agent reschedules
itself" generalizes for free.

**Why per-instance (not just per-type):** the agent tunes cadence per item (a
flat-priced wishlist item → weekly; a volatile one → hourly); the fire time often
*is* instance `Data` ("remind me at 09:00 tomorrow"); lifecycles diverge (one plant
dormant, another active); transient triggers are instance-born ("re-check in 1h");
and user actions target one entity ("snooze that reminder"). `nextFireAt`,
`enabled`, tuned cadence — all per-instance mutable state that must exist per entity
anyway.

**Copy-down (chosen):** at entity creation, stamp the type's `DefaultTriggers` as
concrete instance rows; thereafter fully independent. The scheduler reads one place,
the agent edits freely, and editing a *type* does **not** silently re-schedule live
entities. Tradeoff: **drift** — changing a default doesn't propagate to existing
instances (batch-update if needed; trivial at single-user scale). Accepted as the
right call — and arguably a feature.

### 7.1. Activation & the entity inbox

A trigger is not the *only* way to activate an entity — it's one **producer**. The
unifying model: every entity has an **inbox**, and anything may post a
message/event to it; each post is processed by the handler ladder (§4) and logged in
`entity_events`. Producers:

- **the scheduler** — posts on a timer (this *is* a trigger firing);
- **the chat agent** — posts on demand (the user says "check the price now");
- **external sources** — webhooks, `poll-diff` detecting a change;
- **another entity** — *deferred*, guarded (§18).

This reframe makes "trigger," "activate now," and "entity→entity" one concept instead
of three bespoke channels.

**Per-entity serial processing (actor model).** An entity processes its inbox **one
post at a time**. A scheduled trigger firing while the user edits the same entity via
chat cannot clobber `Data` mid-flight. Cheap to implement (a per-entity lock/queue)
and it removes a whole class of races.

**Posts are messages, not mutations.** A producer cannot reach into an entity's
`Data` — it posts a *message* the entity's own handler/charter decides how to act on.
The receiver stays in control. (This is the safety property that later makes
entity→entity tractable — §18.)

**The `activate` verb.** `set_trigger` is the persistent/recurring producer; for the
imperative *now*, off-cycle case the chat agent uses:

```
activate(entityId, message, when?)   // when omitted → run now (inline); else schedule
```

Same inbox underneath. Chat→entity is a **v1** capability and low-risk (the chat is
already a trusted agent); it is mostly covered by `create`/`update`/`set_trigger`
already, with `activate` closing the immediate-activation gap.

---

## 8. `Data` as truth; conversation as lazy context

The per-entity conversation is **not** the source of truth — `Data` is. The
conversation is *available context, pulled when needed*, reusing core's
`Conversation`/`ConversationMessage` + `ContextManager` summarization.

**The standing conversation is the exception, not the rule.** Most entities need *no*
persistent thread — just `Data` + occasional deterministic or ephemeral-message
triggers. An ephemeral `message` activation runs with `Data` + the triggering message
as context and persists nothing to a thread. A **threaded** conversation is opened
*lazily* only for genuinely multi-turn in-progress work (the entity is mid-task and
the thread holds the working state of that task), then goes dormant again.

This inverts the original "one chat per entity" instinct and removes the per-entity
conversation-growth cost: durable nuance ("user is traveling, hold off") is written
to a `Data` field, not retained as 200 messages.

---

## 9. SemanticIndex details

Declarative per-type behavior. On save, embed the configured field(s) into a Qdrant
collection (= type name); payload carries `entity_id`.

- **One entry per instance today.** Storage is naturally many-to-one (many points can
  share an `entity_id`), so multiple-entries-per-entity is representable without a
  schema change — it's purely a question of whether the write/search path supports it.
- **Search rolls up by `entity_id` from day one** — return *entities*, scored by their
  best-matching point, deduped. With one vector each it's a no-op, but it means the
  search *contract* never changes when a type later emits multiple points (chunking).
- **Config carries a segmentation `mode`, default `whole`:**
  `{ fields, mode: "whole" | "chunk" | "per-item", chunk?: {...} }`. Only `whole` is
  implemented now; `chunk` (split long bodies into passages → N points → 1 entity) and
  `per-item` (embed each element of a list field) are the documented growth path for
  content-heavy custom types. **Not needed for the four seeded types** (short content)
  — YAGNI for v1, but the door is left open.
- **Deferred:** faceted/named-vectors (one entity searchable under distinct "lenses")
  — revisit when a concrete type demands it.

---

## 10. Mapping the four domains, with worked examples

Clean mapping is the core feasibility evidence. Each domain = entity + `Data` +
behaviors + triggers:

| Domain | `Data` | Behaviors | Triggers |
|---|---|---|---|
| **muistio** | `content, category, source, confirmed` | `SemanticIndex(content)`, `Expiry` | — |
| **taidot** | `name, description, instructions` | `SemanticIndex(desc, instr)`, `UniqueName` | — |
| **muistutin** | `title, dueAt, timezone, rrule` | — | `{ RRULE, notify }` |
| **ajastin** | `name, prompt, cron` | `UniqueName` | `{ cron, message:ephemeral }` |

Worked **custom-type** examples that stress the model (and validate the palette):

| Type | `Data` sketch | Behaviors / Triggers |
|---|---|---|
| **wishlist_item** | `title, url, store, targetPrice?, lastPrice?, available?` | `SemanticIndex`; trigger `{ 6h, poll-diff → escalate to message on change }`; user deletes when done |
| **keep_in_touch** | `name, cadenceDays, lastContactedAt, notes` | `SemanticIndex`; trigger `{ at: derived lastContactedAt+cadence, message:ephemeral }` (custom nextRun via script) |
| **subscription** | `name, cost, renewsAt, cycle, url` | trigger `{ RRULE(renewsAt − lead), notify }`; monthly total is an aggregate (AI lists + sums) |
| **plant** | `name, location, wateringDays, lastWatered, haSensor?` | trigger `{ interval, message }` that may query `koti` before nagging |
| **read_later** | `url, title?, summary?, status, tags` | `SemanticIndex(mode:chunk later)`; save-time `derive` script (fetch → title/summary) |
| **shipment** | `carrier, tracking, status, eta` | trigger `{ poll-diff → notify on change }`, `Expiry` on delivered |
| **recipe** | `title, ingredients[], steps, cuisine` | `SemanticIndex` only — the baseline: a pure document, no triggers |

`recipe` is the important baseline — the model degrades gracefully to "just a
searchable document." `wishlist`/`shipment` exercise `poll-diff` + escalation;
`keep_in_touch`/`subscription` exercise derived fire times; `plant` exercises
cross-tool composition (a `message` handler calling `koti`).

---

## 11. Storage design

- **`entities`** — `id, type, data jsonb, tags text[], created_at, updated_at`. GIN on
  `tags`; per-type expression indexes on `Indexes` paths (e.g. `((data->>'dueAt'))`).
  Hot/constrained fields promoted to `STORED` generated columns (§15.1).
- **`type_definitions`** — schema + behaviors + default-trigger templates + index/promote hints.
- **`triggers`** — `id, entity_id (FK), when, handler jsonb, enabled, next_fire_at,
  last_fired_at`. Index on `(enabled, next_fire_at)` for the scheduler scan.
- **`entity_events`** — unifies muistutin's `NotifiedOccurrence`/`CompletedOccurrence`
  and ajastin's `ScheduleRun`, and doubles as the per-entity **history/observation
  log** (price points, status changes, waterings):

  ```
  entity_events { id, entity_id (FK), occurrence_utc, kind, status, result jsonb, created_at }
  ```
  - reminder fired → `kind=notify, status=sent`; completed → `kind=complete, status=done`
  - agent run → `kind=agent_run, result={response, toolCalls, success, error}`
  - observation → `kind=observation, result={price: 390}` (Watch history)

  Unique on `(entity_id, occurrence_utc, kind)` for idempotent firing. **Every handler
  records here**, giving one uniform timeline per entity across all rungs.
- **`conversations`/`conversation_messages`** — core's existing tables, optionally
  linked to an entity (nullable `entity_id`); opened lazily (§8).
- **Qdrant** — one collection per semantically-indexed type; payload `entity_id`.

---

## 12. The scheduler

One background worker (1-minute tick, replacing `ReminderNotifier` +
`ScheduleWorker`):

1. Scan `triggers` for due rows (`enabled AND next_fire_at <= now`).
2. For each, post to the entity's inbox (§7.1) and dispatch its handler: native
   built-in, sandboxed script, or `message` activation (ephemeral/threaded).
   Deterministic handlers run inline (no LLM); `message` handlers invoke a
   `toimi.core` agent session. Posts are processed per-entity serially.
3. Record an `entity_event`; recompute `next_fire_at` (RRULE) or disable (one-shot).

The scheduler is just the *timer* producer; `activate` (§7.1) is the same dispatch
path triggered on demand.

New behaviors become *new handlers*, not new worker loops. Agents extend their own
schedules by writing `triggers` rows.

---

## 13. MCP surface

Generic primitives instead of ~20 grandfathered typed verbs:

- `define_type(name, jsonSchema, behaviors, defaultTriggers, indexes)`
- `list_types` / `get_type` / `delete_type`
- `create(type, data, tags)` / `update(id, data, tags)` / `delete(id)` / `get(id)`
- `list(type, filters, paging)`
- `search(type | "*", query, filters)` — semantic; rolled up by entity (§9)
- `set_trigger(entityId, when, handler)` / `update_trigger` / `delete_trigger` — the
  self-scheduling surface (the *scheduled* producer)
- `activate(entityId, message, when?)` — post to an entity's inbox; `when` omitted
  runs now off-cycle (the *imperative* producer — §7.1)
- `complete_occurrence(id, occurrenceUtc)` — reminder-style completion

**AI affordance via catalog injection.** On session start, `list_types` (schemas +
behaviors + default triggers + a one-line usage hint per type) is appended to the
system prompt, reusing the skill-injection mechanism. JSON Schema validation on write
returns actionable errors on mis-shaped `Data`.

---

## 14. Pod topology & deployment

**Before:** `koti, muistio, taidot, muistutin, ajastin, verkko` + `toimi.web` +
`toimi.core` (lib).

**After:** **`tietue`**, `koti`, `verkko` + `toimi.web` + `toimi.core` (lib).

Six domain pods → three. `tietue` depends on `toimi.core` (for the agent-session
factory `message` handlers need — the dependency ajastin has today). Follows the
1:1:1 convention: `src/toimi.tools.tietue/` ↔ its `Dockerfile` ↔
`k8s/base/tools-tietue/`. One Postgres database (`tietue`) replaces the four
per-domain databases; Qdrant collections become per-type.

---

## 15. Honest tradeoffs & risks

1. **jsonb vs typed EF.** Lose compile-time models, FK constraints, default per-field
   indexes — the only *intrinsic* cost, and the most mitigable. See **§15.1**.
2. **Per-entity state still exists.** Completion, run history, observations live in
   `entity_events`/`triggers`. Unified, but not free.
3. **AI affordance.** Generic verbs are less self-documenting than `create_reminder`.
   *Mitigation:* catalog injection + rich schema `description`s + validation errors.
   **Worth prototyping early.**
4. **Schema evolution.** *Mitigation:* `schemaVersion` + lazy validation (§16).
5. **Sandbox risk surface grows** because trigger scripts are side-effecting.
   *Mitigation:* per-script capability grants + rate/domain guards + kill switch (§6).
6. **Wrong-rung choice.** The agent may pick a script where judgment was needed, or a
   threaded message where a builtin would do. *Mitigation:* escalation makes "too low"
   self-correcting; "too high" just costs tokens until demoted (§4).
7. **One pod, one blast radius.** A `tietue` outage takes down memory, skills,
   reminders, and schedules together. Acceptable single-user; noted.

None are blockers at single-user scale. They are the price of the model, paid
knowingly.

### 15.1. Mitigating the jsonb costs — "typed islands in a jsonb sea"

Blob-vs-typed is **not a global choice**. `Data` stays jsonb, but fields that need
typing, indexing, or constraints are *promoted* into real columns.

**Lost type safety →** codegen C# records from the seeded JSON Schemas
(NJsonSchema/NSwag) so native code works against `ReminderData`, not raw `JsonNode`;
a typed accessor facade (`entity.As<ReminderData>()`, safe because validated on
write); Npgsql jsonb→POCO mapping translating LINQ into `data->>'field'`. Only
*runtime* user types stay dynamic (they have no compiled logic beyond sandboxed
scripts).

**Lost FK constraints →** the important relationships were never in jsonb —
cross-entity references (`triggers`/`entity_events` → entity) are real relational FK
columns with cascade. For an intra-`Data` reference, promote it to
`GENERATED ALWAYS AS ((data->>'ref')::uuid) STORED` and add a FK. Domain invariants
hold via CHECK over jsonb or a generated column.

**Lost per-field indexes →** expression indexes (`((data->>'dueAt'))`); partial,
type-scoped indexes (`WHERE type='reminder'`); GIN `jsonb_path_ops` for containment;
generated stored column + B-tree for the hottest fields (the scheduler scan).

**Validation/evolution →** one central validation gate (stronger than today's
ad-hoc per-server checks); optional `pg_jsonschema` CHECK for DB-level enforcement
(costs a custom Postgres image — optional); `schemaVersion` + lazy validation +
CI round-trip drift tests (schema ↔ generated record).

**Net:** generated columns + codegen'd records for seeded types, relationships kept
relational, `pg_jsonschema` as optional hardening → ~90% of the strongly-typed
experience for the code-backed parts, while preserving dynamic runtime types.

---

## 16. Recommended build order

Greenfield, sequenced within one effort (not a production migration):

1. **Engine core** — `entities` + `type_definitions`, jsonb store, JSON Schema
   validation, generic CRUD MCP primitives, type-catalog injection.
2. **`SemanticIndex`** (declarative) — port the embedding pipeline once, with
   entity-rollup search + `mode: whole`; seed `memory` + `skill`. (Retires muistio +
   taidot.)
3. **Triggers + scheduler + native handlers** (`notify`, `set-field`, `poll-diff`) +
   `entity_events`; seed `reminder`. (Retires muistutin.)
4. **`message` handler** (ephemeral + lazy threaded, reusing core conversations) +
   self-scheduling trigger CRUD; seed `schedule`. (Retires ajastin.)
5. **Script sandbox** — embedded interpreter, capability grants, guards, escalation.
6. **Cutover** — delete the four old pods, DBs, k8s bases; update `appsettings.json`
   MCP URLs and standard-skill seeds.

---

## 17. Open questions (for the implementation plan)

- **Schema evolution policy** — `schemaVersion` + lazy-validate vs eager migration.
  (Leaning lazy.)
- **Generated-column promotion** — which fields per seeded type; declarative
  `Promote: [path → type]` in `TypeDefinition` so user types opt in too? (Leaning declarative.)
- **Codegen pipeline** — NJsonSchema vs NSwag; source generator vs pre-build step;
  CI drift test.
- **`pg_jsonschema`** — adopt for DB-level CHECKs, or engine-only? (Leaning
  engine-only for v1 to avoid a custom Postgres image.)
- **Sandbox runtime** — Jint (JS) vs MoonSharp (Lua). (Leaning Jint — best AI-authored
  reliability.)
- **Condition triggers** — beyond time-based `when`, do we want data-condition triggers
  (fire when `Data.x` crosses a threshold) in v1, or time-only first? (Leaning time-only.)
- **Cross-type search ranking** — merging/normalizing scores across collections for
  `search("*", …)`.
- **Aggregates** — monthly totals, adherence %. (Defer; AI lists + sums at single-user scale.)

---

## 18. Out of scope

- `koti` — stateless HA proxy, no domain model. Unchanged.
- `verkko` — stateless web fetch + ntfy. Unchanged (its ntfy logic is shared with the
  `notify` built-in via `toimi.notifications`).
- Multi-user / multi-tenant isolation.
- **Overridable built-ins** — the native handler set is un-shadowable for now; scripts
  are additive only.
- User-authored *native* handlers or a plugin marketplace (the sandbox covers bespoke
  logic).
- Faceted/named-vector semantic search (§9) — deferred until a concrete type needs it.
- **Entity→entity posting** — *designed-but-deferred* (§7.1). A power feature for
  composition (a `trip` nudging its `flight_watch` children; a `household` fanning out
  to `plant`s) that no seeded domain or early custom type needs. The inbox abstraction
  makes it a **non-breaking** addition later. Deferred so its guards are designed
  deliberately, not retrofitted after the first cascade bug:
  - **loop/cascade control** — depth + fan-out limits, cycle detection, shared budget
    guard (agentic handlers in a cycle = unbounded LLM cost);
  - **granted, allowlisted authority** — an entity may post only to declared targets
    (e.g. its children), never ambiently to any entity;
  - **messages not mutations** — posts go through the receiver's handler ladder, cheap
    (event→built-in/script) by default, escalating to `message` only when needed;
  - **per-entity serial processing** (§7.1) bounds concurrent effects.

## Future / backlog

- **User question inbox:** let a handler (or agent run) enqueue questions for
  the user; the web chat surfaces a list of pending questions to answer on next
  open. An inbound async-prompt surface, distinct from entity behaviors. Deferred.
