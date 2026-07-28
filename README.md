# Toimi

A self-hostable, single-user AI assistant with persistent semantic memory,
reusable skills, scheduled automation, reminders, smart-home control, and web
access. Built as .NET 10 + React microservices that talk over the
[Model Context Protocol](https://modelcontextprotocol.io/) and run on
Kubernetes (kind for local dev, k3s for a server).

## What you get

- **Chat UI** (`toimi.web`) with conversation history and live tool-call
  visualization.
- A small set of MCP tool servers, each independently deployable. Finnish
  names — glossary below.

### Tool glossary

| Tool | Finnish → English | Capability | External dependency |
|---|---|---|---|
| `tietue` | record | Generic typed-entity engine — long-term memory, skills, reminders, and scheduled/autonomous agent runs as runtime-defined types with semantic search, time-anchored triggers, and a handler ladder (notify → sandboxed script → agent run) | PostgreSQL + Qdrant + OpenAI |
| `koti` | home | Home Assistant control (entities, services, history, areas) | **Requires a reachable Home Assistant instance + long-lived token** |
| `verkko` | web/net | Web fetch + push notifications | **Notifications require an [ntfy](https://ntfy.sh) server** |
| `ruutu` | screen | Display/dashboard surfaces (embed web pages on a display) | PostgreSQL |

`koti` and `verkko` notifications simply error when invoked if their external
dependency is absent — everything else works. (`tietue` consolidated four
former servers — memory, skills, reminders, scheduling; see `CLAUDE.md`.)

## Prerequisites

- A Kubernetes cluster: [kind](https://kind.sigs.k8s.io/) for local dev **or**
  k3s for a server. The setup scripts install k3s/kind, Traefik, PostgreSQL
  (Bitnami Helm), Qdrant, Adminer, and a local image registry.
- `docker`, `kubectl`, `helm`, `envsubst` (gettext:
  `apt-get install gettext-base` / `brew install gettext`).
- For a server: `curl`, `helm` on the host.
- An OpenAI API key. (Optional: a Home Assistant instance for `koti`, an ntfy
  server for `verkko` notifications.)
- To build images: .NET 10 SDK and Node.js 24+ are baked into the Docker
  build stages — you only need Docker locally.

## Configure

Everything environment-specific lives in one gitignored file, `toimi.env`,
copied from the tracked `toimi.env.example` template. **You never edit tracked
files.**

```bash
cp toimi.env.example toimi.env                   # edit with your values
# The setup/deploy scripts run scripts/render-config.sh <env> first, which
# generates config.env and the per-overlay secrets.env / admin-auth.env files
# (all gitignored) from this one file — no per-overlay copies to maintain.
```

`toimi.env` is the single source: it holds the non-secret ingress hosts,
registry, and model, plus every secret. Setting `POSTGRES_PASSWORD` once
composes all three DB connection strings, and `ADMIN_USER`/`ADMIN_PASSWORD`
(server only) derives the admin basic-auth htpasswd for you.

| Key | Meaning |
|---|---|
| `TOIMI_HOST` | Ingress host for the chat UI |
| `ADMINER_HOST` | Ingress host for the Adminer DB UI |
| `QDRANT_HOST` | Ingress host for the Qdrant dashboard |
| `IMAGE_REGISTRY` | Registry `deploy.sh` pushes to and pods pull from |
| `HOMEASSISTANT_BASE_URL` / `HA_BEARER_TOKEN` | Home Assistant URL + token for `koti` |
| `OPENAI_MODEL` / `OPENAI_API_KEY` | OpenAI chat model name + API key |
| `POSTGRES_PASSWORD` | PostgreSQL password (composes every connection string) |
| `NTFY_BASE_URL` / `NTFY_TOPIC` / `NTFY_USERNAME` / `NTFY_PASSWORD` | ntfy push notifications |
| `ADMIN_USER` / `ADMIN_PASSWORD` | Admin basic-auth (server overlay only) |

See `toimi.env.example` for the full list with inline notes.

## Run it

Local (kind):

```bash
scripts/dev-setup.sh           # cluster + infra
scripts/deploy-all.sh dev      # build, push, deploy all pods
# add the printed line to /etc/hosts, then open http://$TOIMI_HOST
```

Server (k3s, run on the host after cloning):

```bash
scripts/server-setup.sh        # k3s + infra   (--reset to rebuild)
scripts/deploy-all.sh server
# point DNS for the printed hosts at the server IP
```

Deploy one pod: `scripts/deploy.sh <dev|server> <app>` where `<app>` is a
`src/` project dir (`web`, `tools.koti`, …).

## Layout

```
src/                .NET projects. A dir with a Dockerfile = a pod;
                     without one = a library (toimi.core, toimi.notifications).
k8s/                 Kustomize base + dev/server overlays
infrastructure/      PostgreSQL (Helm), Qdrant, Adminer, registry, namespaces
scripts/             setup + deploy automation
```

## Adding a tool server

Most new *capabilities* are now a `tietue` type (a JSON-Schema type +
behaviors + triggers/handlers), defined at runtime — no new pod. Add a whole
new tool server only for a genuinely external integration (like `koti`/`verkko`).

To add one: create `src/toimi.tools.<name>/` (with a `Dockerfile`) and
`k8s/base/tools-<name>/` (deployment + service + kustomization), list it in
`k8s/base/kustomization.yaml`, and add its MCP URL to
`src/toimi.web/appsettings.json`. The deploy scripts auto-discover it.

## License & author

See `LICENSE`. Created by Jari Helenius.
