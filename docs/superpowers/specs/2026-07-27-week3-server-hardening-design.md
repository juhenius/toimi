# Week 3: Server Hardening — Design

**Status:** Approved (2026-07-27)
**Scope:** TLS via a self-managed CA, basic auth on admin surfaces, Kubernetes/container hardening, Dependabot, and ops documentation. Follows the Week 1 (`2026-07-06-week1-hardening`) and Week 2 (`2026-07-07-week2-data-safety`) iterations.

## Context and decisions

- The k3s server is **LAN-only with no public domain** (hosts like `toimi.test` resolved by local DNS). Let's Encrypt is impossible; TLS comes from a self-managed CA whose root is trusted once per device.
- **Auth protects admin surfaces only**: Adminer, Qdrant, and toimi-web's `/admin` + `/api/admin`. The chat UI stays open on the trusted LAN (zero daily friction; ruutu displays keep working unauthenticated).
- **Off-site backup replication is deferred again** (explicitly accepted risk; backups remain on the node disk from Week 2).
- Dependabot over Renovate: native to the GitHub-hosted repo, no app install; Week 1's CI gates every update PR.
- The Qdrant ingress is retained (TLS + auth) rather than deleted.

## Goals

1. All four ingress-exposed surfaces (web, ruutu, adminer, qdrant) serve HTTPS on the server with auto-renewed certificates; HTTP redirects.
2. Admin surfaces return 401 without credentials; the chat UI and displays require none.
3. Every pod runs non-root with dropped capabilities and bounded resources; images build from pinned base tags with a `.dockerignore`.
4. Dependency updates arrive as CI-gated PRs automatically.
5. An operator can onboard a new device (trust the CA) or rotate the admin credential from a runbook.

## Non-goals

- Off-site backups (deferred, documented). NetworkPolicies (over-engineering for single-user). App-level auth/conversation ownership (perimeter auth covers the threat model). Auth or TLS in the dev (kind) overlay — dev stays plain HTTP.

---

## 1. TLS (cert-manager + self-managed CA)

- **cert-manager** installed via Helm (pinned chart version) in `scripts/dev-setup.sh`? No — server-relevant only, but cert-manager itself is harmless in dev; to keep dev/server drift minimal, install cert-manager in **both** setup scripts, but issue certificates only where ingresses request them (server overlay). New namespace `cert-manager` (Helm default).
- **Bootstrap** (new manifests under `infrastructure/base/cert-manager/`): a self-signed `ClusterIssuer` (`selfsigned-bootstrap`), a CA `Certificate` (`toimi-ca`, CN "Toimi CA", 10-year duration, RSA 4096, stored as secret `toimi-ca-key-pair` in `cert-manager` namespace), and a CA `ClusterIssuer` (`toimi-ca-issuer`) backed by that secret.
- **Ingress TLS via server-overlay patches** (`k8s/overlays/server/` and `infrastructure/overlays/server/`): each ingress gains `cert-manager.io/cluster-issuer: toimi-ca-issuer` + `traefik.ingress.kubernetes.io/router.entrypoints: websecure` annotations and a `tls:` block (`hosts: [${HOST}]`, `secretName: <name>-tls`). HTTP→HTTPS redirect via Traefik's `Middleware` (redirectScheme) applied through a `traefik.ingress.kubernetes.io/router.middlewares` annotation on a companion HTTP ingress, or — simpler — k3s Traefik's global redirect entrypoint config if available; the plan picks the middleware approach (no Traefik reconfiguration).
- **Device trust:** `scripts/export-ca.sh` dumps the CA cert (`kubectl get secret toimi-ca-key-pair -n cert-manager -o jsonpath='{.data.tls\.crt}' | base64 -d`) to `toimi-ca.crt`. Runbook documents trusting it on macOS, iOS, Android, Linux, and the ruutu display browser.
- **envsubst allowlist:** the deploy scripts' envsubst allowlist must keep covering the host variables inside the new `tls:` blocks (already allowlisted: `TOIMI_HOST`, `ADMINER_HOST`, `QDRANT_HOST`; add ruutu's host variable if it is parameterized — verify in the plan).

## 2. Admin auth (Traefik basicAuth)

- New secret `admin-basic-auth` (htpasswd line, generated from `ADMIN_USER`/`ADMIN_PASSWORD` in the overlay `secrets.env` via secretGenerator or a small setup-script step producing the htpasswd hash — plan decides based on what secretGenerator supports; bcrypt via `htpasswd -nbB`).
- New Traefik `Middleware` `admin-auth` (namespace `data` and/or `apps` as needed — Traefik middlewares are namespace-scoped; one per namespace referencing its own copy of the secret, or use the `<ns>-<name>@kubernetescrd` cross-reference syntax; plan verifies which k3s Traefik supports and prefers one middleware per namespace for simplicity).
- Applied to: Adminer ingress, Qdrant ingress, and a **new second ingress** for toimi-web matching only `/admin` and `/api/admin` path prefixes with the middleware annotation. Traefik routes longest-path-first, so `/` (chat, SignalR) stays unauthenticated.
- Server overlay only. Dev keeps everything open.

## 3. Kubernetes + container hardening

- **Resources** (base manifests, applied to all envs): tietue `requests 200m/512Mi, limits 1000m/1Gi`; web/koti/verkko/ruutu `100m/256Mi → 500m/512Mi`; qdrant `200m/512Mi → 1000m/1Gi`; adminer + registry `50m/64Mi → 200m/256Mi`. (PostgreSQL already has resources via Helm values.)
- **securityContext** (all app deployments + qdrant/adminer/registry where images permit): pod-level `runAsNonRoot: true`, `runAsUser: 64198` (`app` user in .NET 10 images — verify UID in the plan; qdrant/adminer/registry get their images' documented non-root users or are flagged as exceptions with a comment), `seccompProfile: RuntimeDefault`; container-level `allowPrivilegeEscalation: false`, `capabilities.drop: [ALL]`. `readOnlyRootFilesystem: true` for the .NET pods with `emptyDir` mounts at `/tmp`; skipped (with comment) for images that need writable paths (registry, qdrant data dir is already a PVC).
- **Dockerfiles:** pin base images to exact current tags (`mcr.microsoft.com/dotnet/sdk:10.0.x`, `aspnet:10.0.x`, `node:24.x-slim` — resolve the actual current patch versions during implementation). Dependabot's docker ecosystem then bumps them.
- **`.dockerignore`** at repo root (context = repo root for all builds): `.git`, `**/bin`, `**/obj`, `**/node_modules`, `docs`, `k8s`, `infrastructure`, `scripts`, `.github`, `*.md`, `.worktrees`.

## 4. Dependabot

`.github/dependabot.yml`: ecosystems `nuget` (root), `npm` (`/src/toimi.web/ClientApp`), `docker` (each of the five Dockerfile directories), `github-actions`; weekly schedule; minor+patch grouped per ecosystem; majors individual.

## 5. Ops documentation

`docs/ops/server-hardening.md`: CA export + per-device trust steps; admin credential rotation (regenerate htpasswd, update secrets.env, re-deploy); accepted-risk register (open chat UI on LAN, on-node backups, dev overlay unencrypted); post-deploy smoke checklist (curl each host expecting redirect + valid-under-CA cert, 401 on admin paths without credentials, 200 with, `kubectl top pods` shows all pods within limits).

## Testing strategy

Manifests: yamllint + CI. Cluster behavior (cert issuance, middleware auth, securityContext compatibility) is only verifiable on a real cluster — the plan keeps each patch mechanical and includes the smoke checklist as the acceptance gate, run manually after server deploy. Dockerfile pinning: CI build proves the pinned tags exist. `.dockerignore`: verify `docker build` still succeeds for one representative image locally if docker is available; otherwise CI/next deploy.

## Sequencing

3 (k8s/container hardening — no behavioral risk, testable in dev) → 4 (Dependabot) → 1 (cert-manager + TLS) → 2 (auth) → 5 (docs). TLS before auth because the auth smoke test wants HTTPS in place.
