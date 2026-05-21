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
```

## Pods (1:1:1 convention)

`src/toimi.tools.<x>/` ↔ `src/toimi.tools.<x>/Dockerfile` ↔
`k8s/base/tools-<x>/`. Image name = dir with dots→dashes
(`toimi.tools.koti` → `toimi-tools-koti`). `toimi.core` and
`toimi.notifications` are libraries (no Dockerfile, not deployed).

**koti — Home Assistant integration.**
- Owns: entity state, service calls, history, area resolution against a
  Home Assistant instance.
- Extend when: adding HA verbs (new service categories, area-aware
  queries, state subscriptions).
- New tool server when: integrating a different home platform (HomeKit,
  Hue directly) — koti is HA-specific.

**muistio — Long-term semantic memory (facts the AI should recall across sessions).**
- Owns: durable user-stated/inferred facts with source/confidence/expiry;
  hybrid PostgreSQL + Qdrant search.
- Extend when: adding memory metadata, recall ranking, or new retrieval
  modes.
- NOT for: ephemeral conversation context (that's `ContextManager` in
  core) or procedural how-tos (that's `taidot`).

**taidot — Reusable procedural knowledge (skills / how-tos).**
- Owns: AI-authored and seeded multi-step procedures; Qdrant semantic
  search; standard-skill seeding.
- Extend when: adding skill metadata, lifecycle, or search behavior.
- NOT for: factual recall (that's `muistio`) — taidot is the inverse:
  procedures, not facts.

**muistutin — User-facing time-anchored reminders.**
- Owns: one-off and RFC 5545 recurring reminders, notification dispatch
  on due times.
- Extend when: adding recurrence patterns, notification routing, or
  completion semantics.
- Distinct from `ajastin`: muistutin notifies the user; ajastin runs
  autonomous agent sessions.

**ajastin — Autonomous agent runs on a cron schedule.**
- Owns: schedule storage + the `ScheduleWorker` headless agent loop
  that invokes the full `toimi.core` stack (LLM + all MCP tools) on
  schedule, logging results to `schedule_runs`.
- Extend when: adding scheduling features, run-result handling, or new
  trigger types.
- Note: ajastin owns the headless agent loop. A new tool the agent
  invokes is a separate domain tool server, not an ajastin extension.

**verkko — External-world access (web fetch + push notifications).**
- Owns: HTTP fetch with HTML extraction; ntfy push notifications.
  Catch-all for one-off external utilities.
- Extend when: adding small external helpers.
- Carve a new tool server when: a sub-domain accumulates ≥2 actions
  (e.g., a dedicated email tool deserves its own server, not a verkko
  extension).

**toimi.web — Transport only (SignalR hub + React UI).**
- Owns: SignalR transport, React chat UI, conversation streaming.
- Extend when: adding UI features or SignalR events that surface what
  `toimi.core` already does.
- NEVER put AI logic here. A new transport (CLI, Telegram bot) is a new
  project that depends on `toimi.core`, NOT a `toimi.web` extension.

**toimi.core — Shared cross-cutting AI behavior (library).**
- Owns: LLM client factory (with `ToolCallNotifier`), MCP tool
  aggregation (`McpToolAggregator`), conversation persistence
  (`ToimiDbContext`), context-window management (`ContextManager`),
  system-prompt + skill-injection assembly.
- Extend when: adding cross-cutting behavior used by both `toimi.web`
  and `ajastin` (e.g., a new system-prompt enrichment step, a different
  summarization strategy).
- NEVER tool-specific code — tool logic belongs in the appropriate
  `toimi.tools.<x>` project.

`toimi.notifications` — `ntfy` client library used by `verkko`.

## Configuration model

- **Non-secret per-env values** → root `config.env` (template
  `config.env.example`): `TOIMI_HOST`, `ADMINER_HOST`, `QDRANT_HOST`,
  `IMAGE_REGISTRY`, `HOMEASSISTANT_BASE_URL`, `OPENAI_MODEL`.
- **Secrets** → `k8s/overlays/<env>/secrets.env` and
  `infrastructure/overlays/<env>/secrets.env` (templates: matching
  `secrets.env.example`), injected via Kustomize `secretGenerator`.
- Manifests carry `${VAR}` placeholders. Rendering pipeline (scripts only):
  `kubectl kustomize <overlay> | envsubst '<allowlist>' | kubectl apply -f -`.
  envsubst uses an explicit allowlist so secret/`$` content is never touched.
- MCP server URLs in `src/toimi.web/appsettings.json` are cluster-internal
  (`*.apps.svc.cluster.local`) — not env-specific, not parameterized.

## Key Patterns

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
- **Skill injection** — on session start, `list_skills` is called via MCP and
  the result is appended to the system prompt so the AI sees its full skill
  catalog without searching first.
- **Standard skill seeding** — `SkillSeeder` in `taidot` upserts standard
  skills on startup (idempotent). When you add a new tool server, also add a
  seeded skill that teaches the AI how to use it.
- **Scheduled agent** — `ajastin`'s `ScheduleWorker` checks cron schedules
  every minute, creates a full agent session via `toimi.core` (LLM + all
  MCP tools), runs the configured prompt, and logs results to
  `schedule_runs`.
- **Home automation areas** — `koti` uses the Home Assistant template API
  (`area_name()`) to resolve entity-to-room mappings; `ListEntities`
  supports area filtering so the AI doesn't need hardcoded entity maps.
- **Recurrence handling** — `muistutin` uses `Ical.Net` for RFC 5545
  recurrence expansion with timezone-aware scheduling.

## Deployment

`scripts/deploy.sh <dev|server> <app>` builds `-f src/<app>/Dockerfile` with
context `.` (repo root), pushes to `IMAGE_REGISTRY`, renders+applies the
overlay, restarts the deployment. `deploy-all.sh` iterates every
`src/*/Dockerfile`. Both require `config.env`. Always use the scripts, never
raw `kubectl apply -k` (it skips envsubst).

## Conventions

- `.editorconfig`: 2-space indent, file-scoped C# namespaces, IDE0005 = error.
- `.yamllint.yaml`: 2-space indent, 200-char lines.
- `scripts/lint.sh [--fix]`: dotnet format + yamllint + shellcheck.
- Commits: `<type>(<scope>): <subject>` (feat, fix, docs, refactor, chore).
- Adding a DB: add to `infrastructure/base/helm/postgresql-values.yaml` and
  the DB-creation loop in `scripts/dev-setup.sh`.

## Service DNS

`<service>.<namespace>.svc.cluster.local` —
`postgresql.data:5432`, `qdrant.data:6334`,
`toimi-tools-<x>.apps`, `toimi-web.apps`.
