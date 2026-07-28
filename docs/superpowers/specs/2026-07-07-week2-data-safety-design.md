# Week 2: Data Safety & Accounting — Design

**Status:** Approved (2026-07-07)
**Scope:** Backups + restore verification, Qdrant/Postgres consistency (outbox), real token accounting with an admin cost view, ContextManager fidelity, Scriban upgrade, scheduler claim-then-run. Follows the Week 1 hardening iteration (`docs/superpowers/plans/2026-07-06-week1-hardening.md`).

## Goals

1. A disk-level mistake (dropped table, bad migration, corrupted DB) is recoverable from nightly backups, and the restore path is scripted and verified — not aspirational.
2. PostgreSQL and Qdrant cannot silently drift: every entity save/delete durably records its indexing intent, failures retry automatically, and drift is repairable from /admin.
3. Token usage stored in the database is real (from API responses), not `chars/4` estimates, and daily cost is visible in /admin.
4. Context compaction stops discarding tool-call content and stops guessing token counts blind.
5. The Scriban advisories (5.12.1, six high-severity) are cleared.
6. A crash mid-handler no longer re-fires a trigger occurrence (duplicate notification) — it delays it instead.

## Non-goals

- Off-site backups (documented as a future upgrade in the runbook; destination decided: **local PVC only for now**).
- Cost budgets/alerts or per-conversation cost drill-down (view-only dashboard this iteration).
- Multi-provider LLM abstraction, auth/TLS, resource limits (later iterations per the architecture review).

---

## 1. Backups (local PVC)

New `infrastructure/base/backup/` kustomize unit in the `data` namespace:

- **PVC `backups`**, 5Gi.
- **CronJob `postgres-backup`**, nightly 02:00 Europe/Helsinki. Runs the Bitnami postgres image matching the chart; executes `pg_dump -Fc` against `postgresql.data.svc.cluster.local:5432` for each of `tietue`, `toimi`, `ruutu` into `/backups/postgres/<db>-<date>.dump`; prunes files older than **14 days**. Password from the existing infrastructure Postgres secret.
- **CronJob `qdrant-backup`**, nightly 02:30. For each collection (discovered via `GET /collections`): `POST /collections/{name}/snapshots`, download the snapshot file to `/backups/qdrant/<collection>-<date>.snapshot`, `DELETE` the server-side snapshot; prunes to **7 days**. Uses Qdrant's REST port (6333) via a curl-capable image.
- Registered in `infrastructure/base/kustomization.yaml`; no overlay changes (no new secrets — reuses existing ones).

**Restore tooling:**

- `scripts/verify-backup.sh`: finds the newest dump per DB on the PVC (via `kubectl exec` into a throwaway pod mounting the PVC), restores it into a scratch `<db>_verify` database, asserts the expected tables exist and row counts are non-negative, drops the scratch DB, prints a PASS/FAIL summary. Exits non-zero on failure.
- `docs/ops/disaster-recovery.md`: runbook covering (a) restoring Postgres from a dump, (b) restoring a Qdrant collection from a snapshot, (c) rebuilding Qdrant from scratch via the reconcile endpoint (§2) when snapshots are unavailable, (d) the explicit limitation that same-disk backups do not survive disk failure and the off-site upgrade path.

## 2. Qdrant consistency — outbox

**Table `index_outbox`** (tietue DB, EF migration):

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `EntityId` | uuid | no FK — must survive entity deletion (delete ops) |
| `Type` | text | entity type = Qdrant collection |
| `Op` | text | `upsert` \| `delete` |
| `Attempts` | int | default 0 |
| `LastError` | text? | |
| `LastAttemptAt` | timestamptz? | |
| `CreatedAt` | timestamptz | |

**Write path** (`EntityRepository` create/update/delete): the outbox row is added to the same `DbContext` change set as the entity mutation, so `SaveChanges` commits both atomically. After commit, the repository attempts to process the row inline (freshness on the happy path: an entity is searchable immediately after create); on success the row is deleted, on failure it stays with `LastError` recorded. The direct `BehaviorDispatcher.OnEntitySavedAsync/OnEntityDeletedAsync` call sites are replaced by this enqueue-then-drain flow. Types without the `SemanticIndex` behavior enqueue nothing (checked before enqueue, as today).

**Processing semantics** (shared by inline path and worker): an `upsert` re-reads the entity at processing time and indexes its current data — newest wins; if the entity no longer exists the op is dropped as success. A `delete` removes the vector. Ops are idempotent.

**`OutboxWorker`** (new BackgroundService, 30s loop): claims due rows (`Attempts < 8` and `LastAttemptAt` older than `2^Attempts` minutes, oldest first, small batch), processes each, deletes on success, increments `Attempts`/`LastError` on failure. At the attempt cap it logs `LogError` and leaves the row (visible in admin). Uses a per-scope DbContext like `TriggerWorker`; no cross-pod lock needed (ops are idempotent; worst case duplicate upsert).

**Admin** (extends existing tietue admin endpoints):
- Summary gains outbox counts: pending, failing (`Attempts > 0`), dead (`Attempts >= cap`).
- `POST /admin/semantic/reconcile/{type}`: scrolls all Qdrant point ids for the collection and all entity ids of that type from the DB; enqueues `upsert` for missing/every mismatched entity and `delete` ops for orphaned points. Returns counts. Re-embedding cost is bounded to what's actually missing.

## 3. Token accounting + /admin cost view

**Capture:**
- `ToimiHub.SendMessage`: read `UsageContent` from the streaming updates (final update carries `UsageDetails`); store real `InputTokenCount`/`OutputTokenCount`/`TotalTokenCount` in the existing `ConversationMessage` columns. Fall back to the current estimate when usage is absent, and mark nothing else.
- `AgentRunner`: return `response.Usage` in `AgentRunResult`; `MessageHandler` serializes it into the `EntityEvent` result JSON (`promptTokens`, `completionTokens`).

**Aggregation endpoints:**
- toimi.web: `GET /admin/api/usage` — daily sums of prompt/completion tokens from `conversation_messages` for the last 30 days (its own DB; no proxying).
- tietue admin: `GET /admin/usage` — daily sums for agent runs via a jsonb aggregation over `entity_events` (`kind = 'message'`, casting `result->>'promptTokens'` etc.), last 30 days.

**UI:** a "Usage" page in the React admin: one table (or simple bars) of day × {web tokens, agent tokens, estimated cost}. Cost = tokens × prices from new `ToimiConfiguration` settings `TokenPriceInputPer1M` / `TokenPriceOutputPer1M` (defaults matching the configured `OPENAI_MODEL`; price lives in config because models change).

## 4. ContextManager fidelity

- **Summarization input**: serialize `FunctionCallContent` (name + args) and `FunctionResultContent` (truncated result) into the text being summarized instead of dropping them. Cap the summarization input length defensively.
- **Estimation**: `CompactIfNeeded` gets a real anchor: hosts pass the last actual prompt-token count (from usage capture, §3) for the message list as of the last LLM call; the estimate becomes `lastRealPromptTokens + charsAddedSince / 3` (conservative 3 chars/token for the delta). Before any real measurement exists, fall back to today's `chars/4`. Implementation: `ContextManager` stops being fully static — a small `ContextBudget` state object owned by the session (web) / run (agent) carries the anchor; the static entry point stays for compatibility.
- **Config**: `MaxContextTokens` moves to `ToimiConfiguration` (default 100_000).

## 5. Scriban upgrade

Bump `Scriban` 5.12.1 → 7.2.5 in `src/toimi.tools.ruutu/toimi.tools.ruutu.csproj`. Acceptance: build clean, all ruutu tests green (the 91 tests render every seed template in both tiers — that's the compatibility gate), `SafeUrl` filter behavior unchanged. Fix any API breaks within the task; if the upgrade requires template-visible behavior changes, stop and surface them rather than adapting templates silently.

## 6. Scheduler claim-then-run

`SchedulerTick` today: run handler → record `EntityEvent`. A crash in between re-fires the occurrence after restart (duplicate notify/agent run).

**Change:** insert the event row with `status = 'started'` **before** dispatching the handler (the existing unique `(entity, occurrence, kind)` index makes any concurrent/late claimer skip), then update the same row to the terminal status (`ok`/`error`/handler-specific) after the handler returns.

**Crash recovery:** `OccurrenceHandledAsync` semantics become: an occurrence is handled if a terminal event exists, OR a `started` event exists that is younger than **15 minutes**. A `started` row older than 15 minutes with no terminal status is considered abandoned: the tick re-claims it (updates `LastAttemptAt`-equivalent timestamp on the row) and re-runs the handler. Net effect: at-least-once with a 15-minute duplicate-suppression window instead of instant re-fire; a crash mid-handler delays the occurrence rather than duplicating or dropping it. The `complete_occurrence` suppression flow is unchanged (a `complete` event is terminal).

## Testing strategy

- Outbox: unit tests on enqueue-in-same-save, inline drain success/failure, worker backoff/cap, reconcile diffing (fake `ISemanticIndex`, InMemory EF).
- Claim-then-run: unit tests for claim insert, terminal update, stale-started retry, concurrent-claim skip (unique index behavior needs the relational path — assert via the store abstraction as existing EntityEventStore tests do).
- ContextManager: new tests for tool-content inclusion and anchored estimation (no LLM needed — fake IChatClient as seam; this creates the first `toimi.core.Tests` project).
- Usage capture: unit test MessageHandler's usage serialization; hub capture verified by integration smoke (limited — noted gap).
- Scriban: existing suite is the gate.
- Backups: `verify-backup.sh` is itself the test; CI cannot run it (no cluster) — runbook documents manual cadence (monthly).

## Sequencing

6 (claim-then-run) → 2 (outbox) → 3 (accounting) → 4 (ContextManager) → 5 (Scriban) → 1 (backup infra + runbook). Code first so CI protects everything; infra last. Each task independently committable.
