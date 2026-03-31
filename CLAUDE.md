# Toimi

Personal AI assistant with persistent memory, reusable skills, and autonomous scheduling. Discovers and invokes external tools via the Model Context Protocol (MCP). Built on .NET 10, React 19, and deployed on Kubernetes.

## Architecture

```
toimi.web (React + SignalR)         toimi.tools.ajastin (cron worker)
        \                                /
         toimi.core (shared library)
         - ToimiClientFactory (LLM client + ToolCallNotifier)
         - McpToolAggregator (MCP tool discovery)
         - ToimiDbContext (conversation persistence)
         - System prompt + skill injection
                    |
         MCP tool servers (all independently deployable)
         ├── koti        — Home Assistant REST API (entity state, control, history, areas)
         ├── muistio     — Semantic memory (PostgreSQL + Qdrant + OpenAI embeddings)
         ├── taidot      — Skill repository (Qdrant + OpenAI embeddings, seeded standard skills)
         ├── muistutin   — Reminders (PostgreSQL, RFC 5545 recurring)
         └── ajastin     — Schedule management (PostgreSQL, also runs scheduled prompts via core)
```

New MCP tool servers are auto-discovered by web/ajastin via configuration — no code changes needed.

## Projects

| Project | Purpose |
|---|---|
| `toimi.core` | Shared library: LLM client factory with `ToolCallNotifier`, `McpToolAggregator`, `ToimiDbContext` for conversation persistence, system prompt with skill injection, configuration types. Referenced by web and ajastin. |
| `toimi.web` | Chat UI transport: SignalR hub for streaming AI responses, conversation persistence, tool call visualization. React 19 frontend with conversation list, markdown rendering, collapsible tool call display. |
| `toimi.tools.koti` | Home automation: `GetEntityState`, `ListEntities` (with area/room filter), `CallService`, `GetHistory`. Uses HA REST API + template API for area resolution. |
| `toimi.tools.muistio` | Semantic memory: `SaveMemory`, `RecallMemory`, `ForgetMemory`, `ListMemories`. Hybrid storage: PostgreSQL (source of truth with source/confirmed/expiresAt metadata) + Qdrant (search index). REST API at `/api/memories` for management. |
| `toimi.tools.taidot` | Skill repository: `SaveSkill`, `GetSkill`, `FindSkill`, `ListSkills`, `DeleteSkill`. Qdrant collection `skills`. Seeds standard skills on startup (see `SkillSeeder.cs`). |
| `toimi.tools.muistutin` | Reminders: `CreateReminder`, `ListReminders`, `CompleteReminder`, `DeleteReminder`. PostgreSQL via EF Core, RFC 5545 recurrence with Ical.Net. |
| `toimi.tools.ajastin` | Scheduled tasks + headless agent: `CreateSchedule`, `ListSchedules`, `DeleteSchedule`, `EnableSchedule`, `DisableSchedule`. Cron worker runs prompts via toimi.core with full MCP tool access. Results logged to `schedule_runs` table. |

## Tech Stack

- **Backend**: .NET 10, ASP.NET Core, EF Core (core, muistutin, ajastin), SignalR, `ModelContextProtocol` v1.1.0, `Microsoft.Extensions.AI`, Cronos
- **Frontend**: React 19, TypeScript, Vite, Tailwind CSS v4, `@microsoft/signalr`
- **Database**: PostgreSQL (toimi conversations, muistio memories, muistutin reminders, ajastin schedules), Qdrant (muistio search index, taidot skills)
- **Deployment**: Docker multi-stage builds, Kubernetes with Kustomize

## Configuration

All per-environment config lives in `k8s/overlays/<env>/secrets.env` (template: `k8s/secrets.env.example`).

| Key in secrets.env | Injected as | Used by |
|---|---|---|
| `openai-api-key` | `TOIMI__OPENAI__APIKEY`, `OpenAI__ApiKey`, `Toimi__OpenAI__ApiKey` | web, muistio, taidot, ajastin |
| `openai-model` | `TOIMI__OPENAI__MODEL`, `Toimi__OpenAI__Model` | web, ajastin |
| `ha-bearer-token` | `HomeAssistant__BearerToken` | koti |
| `toimi-connection-string` | `ConnectionStrings__Toimi` | web |
| `muistio-connection-string` | `ConnectionStrings__Muistio` | muistio |
| `muistutin-connection-string` | `ConnectionStrings__Muistutin` | muistutin |
| `ajastin-connection-string` | `ConnectionStrings__Ajastin` | ajastin |

Non-secret config in deployment.yaml env vars:
- `HomeAssistant__BaseUrl` (koti) — HA instance URL
- `Qdrant__Host`, `Qdrant__Port` (muistio, taidot) — Qdrant at `qdrant.data.svc.cluster.local:6334`
- `OpenAI__EmbeddingModel` (muistio, taidot) — default `text-embedding-3-small`

MCP server URLs are in `appsettings.json` (baked into Docker images, not overridden by env vars).

## Development

### Prerequisites

- .NET 10 SDK
- Node.js 24+ (for frontend)
- PostgreSQL (databases: toimi, muistio, muistutin, ajastin — see root CLAUDE.md for setup)
- Qdrant (collections created automatically on startup)
- OpenAI API key
- Home Assistant instance with long-lived access token (for koti, optional)

### Running locally

```bash
# Frontend (from src/toimi.web/ClientApp)
npm install && npm run dev

# Backend
dotnet run --project src/toimi.web

# Tool servers (each in a separate terminal)
dotnet run --project src/toimi.tools.koti
dotnet run --project src/toimi.tools.muistio
dotnet run --project src/toimi.tools.muistutin
dotnet run --project src/toimi.tools.taidot
dotnet run --project src/toimi.tools.ajastin
```

## Deployment

Each service has a `*.Dockerfile` in the project root. Kubernetes configs in `k8s/` using Kustomize:

- `k8s/base/` — base manifests (web, tools-koti, tools-muistio, tools-muistutin, tools-taidot, tools-ajastin)
- `k8s/overlays/dev/` and `k8s/overlays/server/` — image overrides + secrets from `secrets.env`

Secrets template: `k8s/secrets.env.example`. Copy to `k8s/overlays/<env>/secrets.env` and fill in values.

Infrastructure dependencies (PostgreSQL, Qdrant, ingress) must be available — see root CLAUDE.md for setup.

## Key Patterns

- **Thin web transport**: toimi.web is a transport layer only. All AI logic (system prompt, LLM client, tool aggregation) lives in toimi.core so future transports (CLI, Telegram, etc.) get the same experience.
- **Conversation persistence**: Messages saved to PostgreSQL via `ToimiDbContext` in core. Conversations survive refreshes, can be loaded from history via conversation list UI.
- **Tool call visualization**: `ToolCallNotifier` (DelegatingChatClient in core) captures function call/result events in a queue. ToimiHub drains the queue during streaming, sends SignalR events. Frontend renders collapsible tool call indicators with name, duration, arguments, and results.
- **Skill injection**: On session start, `list_skills` is called via MCP and the result is appended to the system prompt. The AI sees all available skills without needing to search first.
- **Standard skill seeding**: `SkillSeeder` in taidot upserts standard skills on startup (idempotent). Update `SkillSeeder.cs` when adding new tools — add skills that teach the AI how to use them.
- **Scheduled agent**: Ajastin's `ScheduleWorker` checks cron schedules every minute, creates a full agent session via toimi.core (MCP tools + LLM), logs results to `schedule_runs`.
- **Home automation areas**: Koti uses HA template API (`area_name()`) to resolve entity-to-room mappings. `ListEntities` supports area filtering.
- **Recurrence handling**: Muistutin uses `Ical.Net` for RFC 5545 recurrence expansion with timezone-aware scheduling.
