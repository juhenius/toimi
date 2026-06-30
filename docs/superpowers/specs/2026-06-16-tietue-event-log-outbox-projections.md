# tietue: Event Log + Outbox Projections — Design Note

> **Status:** Design note (no implementation). Evolves the existing `EntityEvent`
> log into an ordered, append-only event stream that drives projections (Qdrant
> indexing today; notifications/others later) via an outbox cursor.
> Companion to `2026-06-14-generic-entity-component-engine-design.md`.

## 1. Why

This is the pragmatic middle path between "current-state-only" and full event
sourcing (e.g. Marten). We deliberately **do not** event-source the generic
entity state — tietue's types are user/AI-defined at runtime, so domain events
collapse to generic CRUD and event-versioning fights the runtime-mutable schema.
Instead we keep current-state `jsonb` as the authoritative read model and add an
ordered change log that:

- gives an **audit/history** of entity mutations (today there is none — see §2);
- removes the **synchronous dual-write** to Qdrant (today's silent drift risk);
- enables **replay** (rebuild a Qdrant collection from the log);
- generalises to **future projections** (a notifications outbox) without new infra.

## 2. Current state (what we're evolving)

- **`Data/EntityEvent.cs`** — `{ Id, EntityId, OccurrenceUtc, Kind, Status,
  Result, CreatedAt }`. Written by **`Scheduling/SchedulerTick.cs`** after a
  handler runs. Its sole structural job is **scheduler idempotency**: the unique
  `(EntityId, OccurrenceUtc, Kind)` shape backs
  `Events/EntityEventStore.OccurrenceHandledAsync` ("already fired this
  occurrence?"). A `complete` event suppresses an occurrence; an `error` status
  isolates a throwing handler while the trigger still advances.
- **Indexing is an in-band dual-write.** `Entities/EntityRepository`
  `CreateAsync`/`UpdateAsync`/`DeleteAsync` call `db.SaveChangesAsync()` for the
  entity, then synchronously call `Behaviors/BehaviorDispatcher`
  `OnEntitySavedAsync` / `OnEntityDeletedAsync`, which extracts the configured
  `SemanticIndex` fields, embeds them, and upserts/removes in Qdrant — **no
  retry, best-effort.** If Qdrant is unavailable, Postgres and Qdrant drift with
  no record and no recovery path.
- **No state-change history.** `EntityEvent` logs only *trigger fires*. There is
  no row for "this entity's `Data` was created/updated/deleted."

## 3. Goal

One ordered, append-only event stream per entity, written atomically with state
mutations, consumed by independent, idempotent, replayable projections. Current
behaviour (scheduler idempotency, handler cost ladder, semantic search results)
is preserved exactly.

## 4. Design

### 4.1 One stream, not two (decision)

We **extend `EntityEvent`** rather than introduce a parallel `EntityChange`
table. `EntityEvent` is already an event log; it lacks (a) state-change event
kinds, (b) a total order, and (c) a projection cursor. We add those.

- **Alternative considered:** a separate `EntityChange` log, leaving
  `EntityEvent` untouched. Lower risk to existing tests, but yields two logs and
  two "history of this entity" queries. **Rejected** in favour of a single
  stream — simpler replay, one audit query, and the admin Events view naturally
  shows state changes beside trigger fires.

### 4.2 Stream schema (`EntityEvent` additions)

Add to `EntityEvent`:

- `Sequence` — `bigint`, database identity, **monotonic across all entities**.
  The total order projections consume by. (Postgres `GENERATED ALWAYS AS
  IDENTITY`; under the EF in-memory test provider, fall back to an in-context
  monotonic counter or `ValueGeneratedOnAdd` — verify ordering in a test.)
- `Category` — small enum/string discriminator: `state` (created/updated/
  deleted) vs `occurrence` (scheduler-fired handler runs + complete/error).
  Keeps the two concerns legible without two tables.
- `Payload` — `string? jsonb` (mapped `.HasColumnType("jsonb")`, per the
  cross-provider rule in CLAUDE.md). For `state` events, a compact change
  descriptor (e.g. `{ "type": "...", "tags": [...] }` and optionally changed
  field names). **Not** a full before/after snapshot in v1 (the current-state
  row is the read model; snapshots are a later option — §7).

`Kind` taxonomy after the change:
- `state` category: `created`, `updated`, `deleted`.
- `occurrence` category: existing `notify` / `set-field` / `script` / `message`,
  plus `complete` and `error` (unchanged semantics).

### 4.3 Scheduler idempotency is preserved

The `(EntityId, OccurrenceUtc, Kind)` uniqueness that guards trigger firing
becomes a **partial unique index** scoped to `Category = 'occurrence'`. State
events (`Category = 'state'`) carry the mutation time in `OccurrenceUtc` but are
**never deduped** — every mutation is a new event. `EntityEventStore`'s
occurrence queries gain a `Category = 'occurrence'` predicate; their behaviour is
otherwise identical.

### 4.4 State events written atomically

`EntityRepository.CreateAsync/UpdateAsync/DeleteAsync` add the corresponding
`EntityEvent` (`created`/`updated`/`deleted`) to the **same `DbContext` before
`SaveChangesAsync`**, so the state row and its event commit in one transaction —
eliminating the dual-write. The synchronous `BehaviorDispatcher.OnEntitySaved/
Deleted` call is **removed from the write path** (it becomes the projection in
§4.6). `EntityRepository` no longer needs the `BehaviorDispatcher` dependency for
writes; `SearchAsync` keeps using the dispatcher/index for reads.

### 4.5 Outbox cursor

New table `projection_offsets`: `{ Projection (pk, string), LastSequence
(bigint), UpdatedAt }`. One row per projection (initially `qdrant-index`). A
projection has made progress through `LastSequence`; resetting it to `0` triggers
a full replay.

### 4.6 Projection worker

A `ProjectionWorker` (hosted service, short poll — seconds, faster than the
1-minute `TriggerWorker`; the two are independent loops):

1. For each registered projection, load events with `Sequence > LastSequence`
   ordered by `Sequence`, in batches.
2. Dispatch each event to the projection handler **idempotently**:
   - **`qdrant-index` projection** = today's `BehaviorDispatcher` embedding logic,
     relocated. `created`/`updated` → ensure collection + embed configured
     `SemanticIndex` fields + upsert by entity id (idempotent). `deleted` →
     remove by id. Events for types without a `SemanticIndex` behavior are
     no-ops (cursor still advances).
3. On success advance `LastSequence` to the processed `Sequence`.
4. **Failure isolation (poison handling):** a handler that throws does **not**
   advance the cursor; retry with backoff up to a max attempt count, then log an
   error and skip (advance past it) so one bad event can't wedge the stream —
   mirroring `SchedulerTick`'s `error`-event isolation. Skips are logged loudly.

Because Qdrant upserts/removes are keyed by entity id, re-processing is safe;
the worst case of a crash mid-batch is reprocessing a few events.

### 4.7 Replay

Rebuilding a Qdrant collection (embedding-model change, lost/corrupt collection,
schema field change): set `projection_offsets['qdrant-index'].LastSequence = 0`
(optionally drop/recreate the collection first). The worker re-emits every
`state` event in order and rebuilds the index. No bespoke backfill code.

### 4.8 Admin / agent visibility (free win)

The admin **Events** tab (`/admin/items/{id}/events`, `EntityDetailPage`) already
renders `EntityEvent` rows; once state events live in the same stream it shows
created/updated/deleted beside trigger fires — entity history with no new
endpoint. An agent can likewise read "what happened to this entity" from one log.

## 5. What stays the same

- Scheduler firing semantics, idempotency, `complete`/`error` handling.
- Handler cost ladder (`notify`/`set-field`/`script`/`message`, `escalate`).
- Semantic search **results** and the `SemanticIndex` behavior config — only the
  *trigger* of indexing moves from synchronous to projection-driven.
- Current-state `jsonb` remains the authoritative read model for CRUD/search.
- The 113 existing tietue tests should remain green; the index-on-save tests
  shift from "synchronous after save" to "after the projection runs" (drive the
  worker once in-test, or assert via the projection handler directly).

## 6. Non-goals

- **Full event sourcing of entity state.** State stays current-state `jsonb`;
  events are an audit/outbox stream, not the source of truth folded into
  aggregates. (Rationale: runtime-defined schemas — see §1 and the brainstorm.)
- **Marten / a second persistence stack.** Stays EF Core + Npgsql; no new infra.
- **Cross-aggregate sagas / process managers.**

## 7. Future options (YAGNI for v1)

- **Notifications-as-projection:** a second projection consuming the same stream
  (replaces/augments the inline `notify` handler path) for at-least-once,
  replayable delivery.
- **Retention / compaction:** the stream is append-only and grows unbounded.
  Options when needed: time-based pruning of `occurrence` events, periodic
  per-entity state snapshots + prune older `state` events, or partitioning.
- **Before/after snapshots in `Payload`** for richer diffs / point-in-time
  reconstruction, if temporal queries become a product feature.
- **Per-entity ordering guarantees** if a projection ever needs strict
  per-entity causal order under parallel workers (today: single worker, global
  `Sequence` order — sufficient).

## 8. Risks / trade-offs

- **`Sequence` under the in-memory test provider.** Database identity columns
  behave differently in EF in-memory; ordering must be verified by a test, with
  a provider-agnostic fallback if needed.
- **Partial unique index portability.** Postgres supports filtered unique
  indexes; the in-memory provider ignores index filters. Keep the
  `Category = 'occurrence'` predicate in the `EntityEventStore` *queries* (not
  only in the index) so dedup is correct regardless of provider.
- **Projection lag.** Indexing is now eventually consistent (seconds). A search
  immediately after a write may miss the newest entity until the worker catches
  up — acceptable for this assistant; note it where it matters.
- **Poison events** block per-projection progress until skipped; the max-retry +
  logged-skip policy bounds this, but skips are real data loss for that
  projection — they must be observable (logged, and surfaced if an admin view is
  added later).

## 9. Rough implementation outline (for a later plan)

1. Extend `EntityEvent` (`Sequence`, `Category`, `Payload`) + migration;
   partial unique index on `occurrence`; update `EntityEventStore` queries.
2. Emit `state` events inside `EntityRepository` writes (same `SaveChanges`);
   drop the synchronous dispatcher calls from the write path.
3. Add `projection_offsets` + repository.
4. Add `ProjectionWorker` + an `IProjection` abstraction; port the Qdrant
   indexing from `BehaviorDispatcher` into a `qdrant-index` projection.
5. Replay entry point (reset cursor) + an admin/ops hook.
6. Tests: atomic state-event-on-write; partial-dedup unchanged; projection
   indexes on create/update and removes on delete; poison skip; replay rebuilds.
