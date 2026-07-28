# Toimi

Self-hostable, single-user AI assistant. .NET 10 + React microservices over
MCP, deployed on Kubernetes (kind dev / k3s server). One product, repo root.

## Layout

```
src/<app>/            .NET project. Dir WITH a Dockerfile = deployable pod;
                       WITHOUT = library (toimi.core, toimi.notifications).
src/<app>/Dockerfile   build CONTEXT IS THE REPO ROOT (COPYs toimi.sln, src/)
toimi.sln              solution at root
k8s/base|overlays      Kustomize; overlays = secretGenerator only
infrastructure/        PostgreSQL (Helm), Qdrant, Adminer, registry, namespaces
scripts/               dev-setup.sh, server-setup.sh, deploy.sh, deploy-all.sh, lint.sh
config.env(.example)   non-secret per-env values (gitignored real file)
docs/superpowers/      design specs + phase implementation plans (e.g. the tietue effort)
```

## Pods (1:1:1 convention)

`src/toimi.tools.<x>/` ↔ `src/toimi.tools.<x>/Dockerfile` ↔
`k8s/base/tools-<x>/`. Image name = dir with dots→dashes
(`toimi.tools.koti` → `toimi-tools-koti`). `toimi.core` and
`toimi.notifications` are libraries (no Dockerfile, not deployed).

Deployable pods: **tietue, koti, verkko, ruutu** (tool servers) + **toimi.web**.

> **History:** `tietue` is a generic entity engine that replaced four
> single-purpose servers — `muistio` (memory), `taidot` (skills),
> `muistutin` (reminders), `ajastin` (scheduled agent runs). They were
> deleted in the consolidation; their functions are now tietue *types +
> behaviors + triggers + handlers*. See `docs/superpowers/` for the design
> study and the six phase plans. Don't recreate them as separate servers.

**tietue — Generic typed-entity engine (the core data + behavior store).**
- Owns: arbitrary user/AI-defined types, entity CRUD, semantic search,
  time-anchored triggers, a scheduler, and the handlers triggers fire.
  Functionally subsumes memory, skills, reminders, and scheduled agent runs.
- **Model:** `Entity { Id, Type, Data (jsonb, validated against the type's
  JSON Schema), Tags }`; `TypeDefinition { Name, JsonSchema, Behaviors,
  DefaultTriggers }`; `Trigger { EntityId, Schedule (one-shot `{at}` or
  recurring `{start,rrule,tz}`), HandlerKind+Config, NextFireAt }`;
  `EntityEvent` (unified occurrence/run/observation log, unique on
  `(entity, occurrence, kind)`).
- **Declarative behaviors** (passive, per-type): `SemanticIndex` (embed
  configured fields → Qdrant on save, semantic `search`), `UniqueName`
  (reject a second entity of the type sharing a keyed field — pre-check plus a
  `unique_keys` DB unique index; config `{"field":"<name>"}`, default `name`),
  and `Expiry` (`{"field":"<dateField>","prompt"?:"..."}` — provisions a
  one-shot trigger at that time that deletes the entity, or, when a `prompt` is
  set, runs an agent that deletes it or pushes the date forward).
- **Handlers** (reactive, fired by the scheduler — a cost ladder):
  deterministic native `notify` (ntfy) / `set-field`; sandboxed `script`
  (Jint, capability-gated, no CLR/IO); `message` (a full headless agent run
  via `toimi.core`). `script` can `escalate` to a `message` run.
- **Seeded standard types** (`TypeSeeder`, idempotent): `memory` +
  `skill` (SemanticIndex), `reminder` (default notify trigger from
  `dueAt`/`rrule`), `schedule` (default message/agent trigger from
  `prompt`/`startAt`/`rrule`).
- **MCP surface:** `define_type`/`list_types`/`get_type`/`delete_type`;
  `create`/`get`/`update`/`delete`/`list`/`search`;
  `set_trigger`/`update_trigger`/`delete_trigger`/`list_triggers`;
  `complete_occurrence`; `activate`.
- Extend when: adding a native handler, a declarative behavior, a seeded
  type, or an MCP verb over entities/triggers. A new *capability* the agent
  needs is usually a new type + handler/behavior here, NOT a new pod.
- New tool server only for a genuinely external integration (see koti/verkko).
- Storage: `tietue` PostgreSQL DB (jsonb) + one Qdrant collection per
  semantically-indexed type. Hosts the `TriggerWorker` scheduler loop and
  (for `message`/`activate`) a `toimi.core` agent session reaching all MCP
  servers.

**koti — Home Assistant integration.**
- Owns: entity state, service calls, history, area resolution against a
  Home Assistant instance.
- Extend when: adding HA verbs (new service categories, area-aware
  queries, state subscriptions).
- New tool server when: integrating a different home platform (HomeKit,
  Hue directly) — koti is HA-specific.

**verkko — External-world access (web fetch + push notifications).**
- Owns: HTTP fetch with HTML extraction; ntfy push notifications.
  Catch-all for one-off external utilities.
- Extend when: adding small external helpers.
- Carve a new tool server when: a sub-domain accumulates ≥2 actions
  (e.g., a dedicated email tool deserves its own server, not a verkko
  extension).

**ruutu — Display/dashboard surfaces (embed external web pages on a display).**
- Owns: dashboard/webview templates seeded into its DB; rendering surfaces.
- Extend when: adding display/template behavior.

**toimi.web — Transport only (SignalR hub + React UI).**
- Owns: SignalR transport, React chat UI, conversation streaming, and the
  federated `/admin` panel (proxies to surviving servers' admin endpoints —
  see `Toimi:Admin:Tools`, currently `tietue`).
- Extend when: adding UI features or SignalR events that surface what
  `toimi.core` already does.
- NEVER put AI logic here. A new transport (CLI, Telegram bot) is a new
  project that depends on `toimi.core`, NOT a `toimi.web` extension.

**toimi.core — Shared cross-cutting AI behavior (library).**
- Owns: LLM client factory (with `ToolCallNotifier`), MCP tool
  aggregation (`McpToolAggregator`), conversation persistence
  (`ToimiDbContext`), context-window management (`ContextManager`),
  system-prompt assembly + catalog injection (`ToimiClientFactory`).
- Extend when: adding cross-cutting behavior used by multiple agent hosts
  (`toimi.web` and tietue's agent runner) — e.g. a new system-prompt
  enrichment step or a different summarization strategy.
- NEVER tool-specific code — tool logic belongs in a `toimi.tools.<x>` project.

`toimi.notifications` — `ntfy` client library, used by `verkko` and by
tietue's `notify` handler.

## Configuration model

- **Single source of truth** → root `toimi.env` (gitignored; template
  `toimi.env.example`). Holds every per-machine value: hostnames,
  `IMAGE_REGISTRY`, `OPENAI_MODEL`/`OPENAI_API_KEY`, `HOMEASSISTANT_BASE_URL`/
  `HA_BEARER_TOKEN`, `POSTGRES_PASSWORD` (set ONCE), ntfy creds, and
  `ADMIN_USER`/`ADMIN_PASSWORD`.
- `scripts/render-config.sh <dev|server>` (run first by dev-setup/server-setup/
  deploy) generates the derived, gitignored files from `toimi.env`:
  `config.env` (non-secret vars sourced for envsubst), the per-overlay
  `secrets.env` (app secrets → `toimi-secrets`; the three DB connection strings
  are COMPOSED from `POSTGRES_PASSWORD`), the infra `secrets.env`
  (`postgres-password`), and — server only — the two `admin-auth.env`
  (`admin-basic-auth`; htpasswd DERIVED from `ADMIN_PASSWORD`). `toimi.env` is
  parsed literally (never shell-sourced), so secret values may contain `$`,
  spaces, etc. Rotate admin creds by editing `toimi.env` + deleting the two
  generated `admin-auth.env` files, then re-rendering.
- Kustomize `secretGenerator` consumes the generated `secrets.env`/
  `admin-auth.env` files unchanged. Manifests carry `${VAR}` placeholders;
  rendering pipeline (scripts only):
  `kubectl kustomize <overlay> | envsubst '<allowlist>' | kubectl apply -f -`.
  envsubst uses an explicit allowlist so secret/`$` content is never touched.
- MCP server URLs are cluster-internal (`*.apps.svc.cluster.local/sse`) —
  configured in `src/toimi.web/appsettings.json` (`Toimi:McpServers`) AND in
  `src/toimi.tools.tietue/appsettings.json` (the agent runner's `Toimi:McpServers`,
  which includes tietue itself so an agent run can self-schedule). Not
  env-specific, not parameterized.

## Key Patterns

- **Generic entity engine** — instead of one server per data kind, tietue
  stores typed entities (jsonb `Data` + per-type JSON Schema). New kinds of
  data are *types* (a schema + behaviors + default triggers), definable at
  runtime via `define_type` — no new pod/deploy.
- **Declarative semantic index** — a type's `SemanticIndex` behavior embeds
  configured fields to a per-type Qdrant collection on save; `search` rolls up
  results by entity. One embedding pipeline for all semantically-indexed types.
- **Triggers + scheduler** — `TriggerWorker` (1-min loop) → `SchedulerTick`
  scans due triggers (`Enabled && NextFireAt <= now`), dispatches the handler,
  records an `EntityEvent`, and recomputes `NextFireAt` (RFC 5545 via `Ical.Net`)
  or disables one-shots. Firing is idempotent (unique `(entity,occurrence,kind)`);
  a `complete` event suppresses an occurrence; a throwing handler is isolated
  (recorded as `error`, trigger still advances).
- **Handler cost ladder** — deterministic native (`notify`/`set-field`) →
  sandboxed `script` → `message` (full agent run); `script` may `escalate` to
  an agent. The agent run reuses the `toimi.core` stack and reaches all MCP
  tools (including tietue's own), so entities **self-schedule** via `set_trigger`.
- **Copy-down default triggers** — `TriggerProvisioner` stamps a type's
  `DefaultTriggers` onto each new entity at create, resolving `Data` fields
  (e.g. a `reminder`'s `dueAt`/`rrule` → a concrete notify trigger).
- **Sandboxed scripts** — the `script` handler runs AI-authored JS in `Jint`
  (no CLR/IO; timeout + statement + memory + recursion caps) as a pure
  `data → effects` function; the host applies only the effects the script's
  capability grant allows (`setField`/`notify`/`trigger`/`escalate`). Global
  `Scripts:Enabled` kill switch.
- **Thin web transport** — all AI logic lives in `toimi.core`; `toimi.web` is
  transport only so future transports (CLI, Telegram) inherit the same
  experience.
- **Conversation persistence** — messages save to PostgreSQL via
  `ToimiDbContext` in core; per-message estimated token usage is tracked for
  context-window decisions.
- **Context window management** — `ContextManager` in core estimates token
  count before each LLM call. Near the ~100k limit it summarizes older
  messages via the LLM and replaces them with a compact summary, preserving
  system messages and the 10 most recent exchanges.
- **Tool call visualization** — `ToolCallNotifier` (a `DelegatingChatClient`
  in core) captures function-call/result events into a queue; `ToimiHub`
  drains the queue during streaming and sends SignalR events; the React UI
  renders collapsible indicators showing tool name, duration, arguments, and
  result.
- **Catalog injection** — on session start the host calls `list_types` (and,
  if present, `list_skills`) via MCP and appends the result to the system
  prompt, so the AI sees the available types/skills without searching first.
  Null results degrade gracefully (the section is omitted).
- **Home automation areas** — `koti` uses the Home Assistant template API
  (`area_name()`) to resolve entity-to-room mappings; `ListEntities`
  supports area filtering so the AI doesn't need hardcoded entity maps.

## Deployment

`scripts/deploy.sh <dev|server> <app>` builds `-f src/<app>/Dockerfile` with
context `.` (repo root), pushes to `IMAGE_REGISTRY`, renders+applies the
overlay, restarts the deployment. `deploy-all.sh` iterates every
`src/*/Dockerfile`. Both require `config.env`. Always use the scripts, never
raw `kubectl apply -k` (it skips envsubst).

## Conventions

- `.editorconfig`: 2-space indent, file-scoped C# namespaces. `dotnet format`
  enforces (as errors) IDE0005 (unused usings), IDE0022 (block bodies),
  IDE0046 (use conditional expression), and whitespace — run
  `dotnet format <csproj>` and verify `--verify-no-changes` exits 0 before
  committing (the apply step does not always auto-fix IDE0046).
- `.yamllint.yaml`: 2-space indent, 200-char lines.
- `scripts/lint.sh [--fix]`: dotnet format + yamllint + shellcheck.
- Commits: `<type>(<scope>): <subject>` (feat, fix, docs, refactor, chore).
- Adding a DB: add to `infrastructure/base/helm/postgresql-values.yaml`, the
  DB-creation loop in `scripts/dev-setup.sh`, and (for server installs)
  `scripts/server-setup.sh`. Active DBs: `tietue`, `toimi`, `ruutu`.
- Storing JSON as jsonb across providers: map a `string?` column
  `.HasColumnType("jsonb")` (works under the EF in-memory test provider and
  Npgsql); keep cross-entity relationships as real FK columns, not in jsonb.

## Service DNS

`<service>.<namespace>.svc.cluster.local` —
`postgresql.data:5432`, `qdrant.data:6334`,
`toimi-tools-<x>.apps` (tietue, koti, verkko, ruutu), `toimi-web.apps`.
