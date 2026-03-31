# Toimi

A personal AI assistant with persistent memory, skills, and the ability to act autonomously. Built as a collection of microservices running on Kubernetes, using the [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) to connect an LLM to modular tool servers — each handling a specific domain like memory, scheduling, home automation, or any future capability.

## What it does

You chat with Toimi through a web interface. Behind the scenes, the AI decides which tools to use based on your request — it can remember things you've told it, follow procedures it has learned, set reminders, run tasks on a schedule, and control smart home devices. New capabilities are added as independent tool servers without changing the core. All tool usage is visible in the UI as collapsible indicators showing what was called, with what arguments, and what came back.

## Architecture

```
┌─────────────────────────────────────────────────────┐
│  Web UI (React)                                     │
│  Chat interface with conversation history,          │
│  tool call visualization, conversation list         │
└──────────────────┬──────────────────────────────────┘
                   │ SignalR WebSocket
┌──────────────────▼──────────────────────────────────┐
│  toimi.core (shared .NET library)                   │
│  LLM client, MCP tool aggregation, system prompt,   │
│  conversation persistence, tool call notification   │
└──────────────────┬──────────────────────────────────┘
                   │ MCP (HTTP)
┌──────────────────▼──────────────────────────────────┐
│  Tool servers (each independently deployable)       │
│                                                     │
│  muistio    — Long-term semantic memory             │
│  taidot     — Reusable skill/procedure library      │
│  muistutin  — Reminders with recurring support      │
│  ajastin    — Scheduled tasks + autonomous agent    │
│  koti       — Smart home control via Home Assistant │
│  ...        — Add any new capability as a tool server│
└─────────────────────────────────────────────────────┘
```

Two things can talk to the AI: the **web UI** (interactive chat) and **ajastin** (runs prompts on a cron schedule). Both use the same shared library, so they have identical capabilities.

## Building blocks

### Semantic memory (muistio)

The AI can save and recall facts using vector similarity search. When you tell it "I prefer Celsius" or "the guest wifi password is X", it stores the information in PostgreSQL with rich metadata (source, confidence, expiry) and indexes it in [Qdrant](https://qdrant.tech/) for semantic search. Later, when context is relevant, it searches by meaning — not keywords.

Each memory tracks where it came from (user-stated vs AI-inferred), whether the user has confirmed it, and optionally when it should expire. This prevents memory pollution and supports cleanup of stale information. A REST API enables external memory management and inspection.

This gives the AI persistent knowledge across conversations without stuffing everything into the system prompt.

### Skill repository (taidot)

Skills are saved procedures that teach the AI how to do multi-step tasks. For example, "update the home inventory" is a skill that tells the AI to list all Home Assistant entities, group them by room, and save the result back as a reference document.

Skills are stored in Qdrant with semantic search, so the AI can find relevant skills even with fuzzy queries. A set of standard skills is seeded on startup, and the AI can create new ones when it learns a repeatable procedure.

On every new session, a summary of all available skills is injected into the system prompt — the AI knows what it can do without searching first.

### Smart home control (koti)

Direct integration with [Home Assistant](https://www.home-assistant.io/) via its REST API. The AI can:
- Query the state of any entity (lights, sensors, switches, climate)
- List all devices filtered by domain or room/area
- Call any HA service (turn on lights, set temperature, trigger automations)
- Retrieve state history for any entity

Area/room assignments are resolved using HA's template API, so the AI understands "turn off the kitchen lights" without needing hardcoded entity mappings.

### Reminders (muistutin)

Create one-time or recurring reminders with full [RFC 5545](https://datatracker.ietf.org/doc/html/rfc5545) recurrence support (daily, weekly on specific days, monthly, yearly). The AI handles the conversion from natural language to cron-like rules.

### Scheduled agent (ajastin)

The AI can schedule itself to run prompts autonomously on a cron schedule. A background worker checks every minute for due schedules, creates a full agent session with access to all tools, runs the prompt, and logs the result.

This enables things like "every morning at 7, check my reminders and give me a briefing" — the AI runs with full tool access and stores the result for later review.

### Conversation persistence

Chat history is saved to PostgreSQL. Conversations survive page refreshes and can be loaded from a conversation list. Each conversation is auto-titled based on the first message.

### Tool call visualization

Every tool call is visible in the chat UI. A collapsible indicator shows the tool name and execution time. Expanding it reveals the arguments sent and the result received. While a tool is running, a pulsing indicator shows it's in progress.

## Tech stack

- **Backend**: .NET 10, ASP.NET Core, SignalR, Entity Framework Core
- **Frontend**: React 19, TypeScript, Vite, Tailwind CSS v4
- **AI**: OpenAI GPT-4o (configurable), [Microsoft.Extensions.AI](https://devblogs.microsoft.com/dotnet/introducing-microsoft-extensions-ai-preview/) abstraction
- **Tool protocol**: [Model Context Protocol](https://modelcontextprotocol.io/) (MCP) over SSE
- **Vector DB**: Qdrant (semantic memory + skills)
- **Database**: PostgreSQL (conversations, memories, reminders, schedules)
- **Infrastructure**: Kubernetes (k3s production, kind local dev), Kustomize, Docker

## Deployment

### Prerequisites

- Kubernetes cluster with nginx ingress
- PostgreSQL with databases: `toimi`, `muistio`, `muistutin`, `ajastin`
- Qdrant (collections are created automatically on startup)
- OpenAI API key
- Home Assistant instance with a long-lived access token (for koti, optional)

### Building

Each service has its own Dockerfile in the project root. Build context is the toimi directory:

```bash
docker build -f web.Dockerfile -t your-registry/toimi-web:latest .
docker build -f tools-muistio.Dockerfile -t your-registry/toimi-tools-muistio:latest .
docker build -f tools-muistutin.Dockerfile -t your-registry/toimi-tools-muistutin:latest .
docker build -f tools-taidot.Dockerfile -t your-registry/toimi-tools-taidot:latest .
docker build -f tools-ajastin.Dockerfile -t your-registry/toimi-tools-ajastin:latest .
docker build -f tools-koti.Dockerfile -t your-registry/toimi-tools-koti:latest .
```

### Kubernetes

Manifests are in `k8s/` using Kustomize with base/overlay pattern:

```bash
# Apply base manifests (adjust image names in overlay first)
kubectl apply -k k8s/overlays/dev
```

Before applying, create a `k8s/overlays/dev/secrets.env` from the template:

```bash
cp k8s/secrets.env.example k8s/overlays/dev/secrets.env
# Edit with real values
```

Required secrets (see `k8s/secrets.env.example`):
- `openai-api-key` — OpenAI API key
- `openai-model` — LLM model name (e.g. `gpt-4o`)
- `ha-bearer-token` — Home Assistant long-lived access token
- `toimi-connection-string` — PostgreSQL connection for conversations
- `muistio-connection-string` — PostgreSQL connection for memories
- `muistutin-connection-string` — PostgreSQL connection for reminders
- `ajastin-connection-string` — PostgreSQL connection for schedules

All services expose `/health` for liveness/readiness probes and listen on port 8080.

EF Core migrations run automatically on startup for services that use PostgreSQL. Qdrant collections are created automatically.

### Configuration

MCP server URLs are in `src/toimi.web/appsettings.json`. If your service names or namespace differ from the defaults (`toimi-tools-*.apps.svc.cluster.local`), update them there and rebuild the web image.

The Home Assistant URL is configured via `HomeAssistant__BaseUrl` environment variable on the koti deployment (default: `http://homeassistant.local:8123`).

## Author

Jari Helenius

[![LinkedIn][linkedin-shield]][linkedin-url]

<!-- MARKDOWN LINKS & IMAGES -->

[linkedin-shield]: https://img.shields.io/badge/-LinkedIn-black.svg?style=for-the-badge&logo=linkedin&colorB=555
[linkedin-url]: https://linkedin.com/in/jari-helenius-a445478a
