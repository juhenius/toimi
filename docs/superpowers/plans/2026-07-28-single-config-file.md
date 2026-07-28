# Single Root Config File Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the five hand-edited config/secret files (`config.env` + two `secrets.env` + two `admin-auth.env`) with one gitignored root `toimi.env`, generated into the per-overlay files by a `scripts/render-config.sh` prestep — eliminating the postgres-password duplication and the manual htpasswd step.

**Architecture:** Kustomize `secretGenerator` and the `kustomize | envsubst | apply` pipeline are unchanged. A new render step reads one root `toimi.env` and writes `config.env` + the per-overlay `secrets.env`/`admin-auth.env` files as gitignored build artifacts, **composing** the three DB connection strings from a single `POSTGRES_PASSWORD` and **deriving** the admin htpasswd from `ADMIN_PASSWORD`. The three setup/deploy scripts call the render step first, then continue with their existing source/SUBST/grep logic.

**Tech Stack:** Bash, Kustomize secretGenerator, envsubst, htpasswd (apache2-utils or the `httpd` docker image), GitHub Actions.

**Conventions:** `set -euo pipefail`, `SCRIPT_DIR`/`ROOT_DIR` preamble matching the existing scripts. shellcheck-clean (run `docker run --rm -v "$PWD":/repo:ro koalaman/shellcheck:stable /repo/scripts/<file>` — shellcheck isn't installed locally; docker is). yamllint on CI edits. `bash scripts/lint.sh` before each commit. Verify branch with `git branch --show-current`.

**Key facts (verified, do not re-derive):**
- The three scripts each do `source "$ROOT_DIR/config.env"` (non-secret vars) + `SUBST='${TOIMI_HOST} ${ADMINER_HOST} ${QDRANT_HOST} ${IMAGE_REGISTRY} ${HOMEASSISTANT_BASE_URL} ${OPENAI_MODEL}'`, then grep `postgres-password` from `infrastructure/overlays/<env>/secrets.env` for `helm --set auth.postgresPassword` + DB init.
- App secrets (`k8s/overlays/<env>/secrets.env`) feed the `toimi-secrets` secretGenerator (apps ns): keys `openai-api-key`, `ha-bearer-token`, `toimi-connection-string`, `ruutu-connection-string`, `tietue-connection-string`, `ntfy-base-url`, `ntfy-topic`, `ntfy-username`, `ntfy-password`.
- Connection string shape: `Host=postgresql.data.svc.cluster.local;Port=5432;Database=<db>;Username=postgres;Password=<POSTGRES_PASSWORD>` for db ∈ {toimi, ruutu, tietue}.
- `admin-auth.env` (server only, both overlays) feeds the `admin-basic-auth` secretGenerator: one key `users=admin:<bcrypt>`.
- Dev has NO admin-auth (dev overlays have no auth secretGenerator). `infrastructure/overlays/dev/secrets.env` is read ONLY by dev-setup's grep (no infra secretGenerator consumes it).
- `.gitignore` already ignores `**/secrets.env`, `**/admin-auth.env`, `/config.env` (keeping the `.example` files). Generated files stay gitignored.
- CI's `yaml` job renders the server overlays after `cp`-ing the `.example` files to real names.

**Design decisions locked in:**
- `toimi.env` holds UPPER_SNAKE keys; `render-config.sh` maps them to the kebab-case keys the manifests expect.
- Deterministic outputs (config.env, secrets.env) are regenerated every run. The **non-deterministic bcrypt** admin-auth is generated only when the target file is absent (stable salt across deploys, no churn, no docker/htpasswd needed on every `deploy.sh`); rotating the admin password = edit `toimi.env` and delete the two `admin-auth.env` files, then re-run.
- The five superseded `.example` files are deleted; `toimi.env.example` is the single documented input.

---

## Task 1: `toimi.env.example` + `scripts/render-config.sh`

**Files:**
- Create: `toimi.env.example`
- Create: `scripts/render-config.sh` (executable)

- [ ] **Step 1: Create `toimi.env.example`**

```bash
# Toimi single config source. Copy to toimi.env (gitignored) and fill in real values.
#   cp toimi.env.example toimi.env
# scripts/render-config.sh generates config.env and the per-overlay secret files
# from this one file, so the postgres password and admin credential are set ONCE.

# --- Ingress hostnames (must resolve to your cluster's Traefik entrypoint) ---
TOIMI_HOST=toimi.test
ADMINER_HOST=adminer.toimi.test
QDRANT_HOST=qdrant.toimi.test

# --- Image registry that deploy.sh pushes to and pods pull from ---
IMAGE_REGISTRY=localhost:5000

# --- Home Assistant (koti REQUIRES a reachable instance) ---
HOMEASSISTANT_BASE_URL=http://homeassistant.local:8123
HA_BEARER_TOKEN=PLACEHOLDER

# --- OpenAI ---
OPENAI_MODEL=gpt-4o
OPENAI_API_KEY=PLACEHOLDER

# --- PostgreSQL (set ONCE; render-config composes every connection string from it) ---
POSTGRES_PASSWORD=changeme-in-production

# --- ntfy push notifications ---
NTFY_BASE_URL=https://ntfy.example.com
NTFY_TOPIC=toimi
NTFY_USERNAME=PLACEHOLDER
NTFY_PASSWORD=PLACEHOLDER

# --- Admin basic-auth (server only; render-config runs htpasswd for you) ---
ADMIN_USER=admin
ADMIN_PASSWORD=change-me
```

- [ ] **Step 2: Create `scripts/render-config.sh`**

```bash
#!/usr/bin/env bash
# Generates config.env and the per-overlay secret files from the single root
# toimi.env, so the postgres password and admin credential are defined ONCE.
# Called by dev-setup.sh / server-setup.sh / deploy.sh before they read config.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

ENV="${1:?Usage: render-config.sh <dev|server>}"
case "$ENV" in
  dev | server) ;;
  *) echo "ERROR: env must be 'dev' or 'server', got '$ENV'" >&2; exit 1 ;;
esac

SRC="$ROOT_DIR/toimi.env"
if [ ! -f "$SRC" ]; then
  echo "ERROR: $SRC not found. Copy it: cp toimi.env.example toimi.env" >&2
  exit 1
fi

# shellcheck disable=SC1090
set -a; source "$SRC"; set +a

: "${POSTGRES_PASSWORD:?POSTGRES_PASSWORD missing from toimi.env}"

GEN_HEADER="# GENERATED by scripts/render-config.sh from toimi.env — DO NOT EDIT."

# --- config.env (non-secret vars sourced by the setup/deploy scripts) ---
cat > "$ROOT_DIR/config.env" <<EOF
$GEN_HEADER
TOIMI_HOST=${TOIMI_HOST}
ADMINER_HOST=${ADMINER_HOST}
QDRANT_HOST=${QDRANT_HOST}
IMAGE_REGISTRY=${IMAGE_REGISTRY}
HOMEASSISTANT_BASE_URL=${HOMEASSISTANT_BASE_URL}
OPENAI_MODEL=${OPENAI_MODEL}
EOF

# --- App secrets (toimi-secrets secretGenerator) ---
conn() { echo "Host=postgresql.data.svc.cluster.local;Port=5432;Database=$1;Username=postgres;Password=${POSTGRES_PASSWORD}"; }
APP_SECRETS="$ROOT_DIR/k8s/overlays/$ENV/secrets.env"
mkdir -p "$(dirname "$APP_SECRETS")"
cat > "$APP_SECRETS" <<EOF
$GEN_HEADER
openai-api-key=${OPENAI_API_KEY}
ha-bearer-token=${HA_BEARER_TOKEN}
toimi-connection-string=$(conn toimi)
ruutu-connection-string=$(conn ruutu)
tietue-connection-string=$(conn tietue)
ntfy-base-url=${NTFY_BASE_URL}
ntfy-topic=${NTFY_TOPIC}
ntfy-username=${NTFY_USERNAME}
ntfy-password=${NTFY_PASSWORD}
EOF

# --- Infra secret (postgres-password; read by the setup scripts' grep) ---
INFRA_SECRETS="$ROOT_DIR/infrastructure/overlays/$ENV/secrets.env"
mkdir -p "$(dirname "$INFRA_SECRETS")"
cat > "$INFRA_SECRETS" <<EOF
$GEN_HEADER
postgres-password=${POSTGRES_PASSWORD}
EOF

# --- Admin basic-auth (server only; both overlays share the same htpasswd) ---
if [ "$ENV" = "server" ]; then
  : "${ADMIN_USER:?ADMIN_USER missing from toimi.env}"
  : "${ADMIN_PASSWORD:?ADMIN_PASSWORD missing from toimi.env}"
  K8S_AUTH="$ROOT_DIR/k8s/overlays/server/admin-auth.env"
  INFRA_AUTH="$ROOT_DIR/infrastructure/overlays/server/admin-auth.env"

  # bcrypt salt is random, so regenerate only when absent (stable across deploys,
  # no docker/htpasswd needed on every deploy). Rotate = delete these + re-run.
  if [ ! -f "$K8S_AUTH" ] || [ ! -f "$INFRA_AUTH" ]; then
    if command -v htpasswd >/dev/null 2>&1; then
      LINE="$(htpasswd -nbB "$ADMIN_USER" "$ADMIN_PASSWORD")"
    elif command -v docker >/dev/null 2>&1; then
      LINE="$(docker run --rm httpd:2.4-alpine htpasswd -nbB "$ADMIN_USER" "$ADMIN_PASSWORD")"
    else
      echo "ERROR: need 'htpasswd' (apache2-utils) or 'docker' to generate admin auth" >&2
      exit 1
    fi
    printf '%s\nusers=%s\n' "$GEN_HEADER" "$LINE" > "$K8S_AUTH"
    printf '%s\nusers=%s\n' "$GEN_HEADER" "$LINE" > "$INFRA_AUTH"
    echo "Generated admin-auth for user '$ADMIN_USER'."
  fi
fi

echo "Rendered config for '$ENV' from toimi.env."
```

Make executable: `chmod +x scripts/render-config.sh`.

- [ ] **Step 3: Verify it renders and the overlays still build**

```bash
cp toimi.env.example toimi.env
bash scripts/render-config.sh server
# Files exist with GENERATED header + correct content:
grep -q "Password=changeme-in-production" k8s/overlays/server/secrets.env && echo "conn OK"
grep -q "^postgres-password=changeme-in-production" infrastructure/overlays/server/secrets.env && echo "pg OK"
grep -qE "^users=admin:\\\$2[aby]\\\$" k8s/overlays/server/admin-auth.env && echo "htpasswd OK"
diff <(grep -v '^#' infrastructure/overlays/server/admin-auth.env) <(grep -v '^#' k8s/overlays/server/admin-auth.env) && echo "auth files identical OK"
# Overlays render (docker kubectl; no local kubectl):
docker run --rm -v "$PWD":/repo:ro bitnami/kubectl:latest kustomize /repo/k8s/overlays/server > /dev/null && echo "k8s render OK"
docker run --rm -v "$PWD":/repo:ro bitnami/kubectl:latest kustomize /repo/infrastructure/overlays/server > /dev/null && echo "infra render OK"
# Also dev:
bash scripts/render-config.sh dev
docker run --rm -v "$PWD":/repo:ro bitnami/kubectl:latest kustomize /repo/k8s/overlays/dev > /dev/null && echo "dev render OK"
```

Expected: all the `echo` markers print, both renders succeed. Then remove the throwaway generated files so they don't get committed: `rm -f toimi.env config.env k8s/overlays/{dev,server}/secrets.env infrastructure/overlays/{dev,server}/secrets.env k8s/overlays/server/admin-auth.env infrastructure/overlays/server/admin-auth.env` (all gitignored anyway — confirm `git status` shows only the two new tracked files).

- [ ] **Step 4: shellcheck, lint, commit**

```bash
docker run --rm -v "$PWD":/repo:ro koalaman/shellcheck:stable /repo/scripts/render-config.sh
bash scripts/lint.sh
git add toimi.env.example scripts/render-config.sh
git commit -m "feat(config): single-source toimi.env with a render-config generator"
```

---

## Task 2: Wire `render-config.sh` into the setup/deploy scripts

**Files:**
- Modify: `scripts/dev-setup.sh`, `scripts/server-setup.sh`, `scripts/deploy.sh`

- [ ] **Step 1: dev-setup.sh**

Immediately after the `SCRIPT_DIR`/`ROOT_DIR` preamble and BEFORE the `CONFIG_FILE`/`source config.env` block, add:

```bash
"$SCRIPT_DIR/render-config.sh" dev
```

Then DELETE the now-stale `CONFIG_FILE` existence check's error message about copying `config.env.example` (config.env is now always generated by the render step — keep the `source "$ROOT_DIR/config.env"` line, since render-config just produced it). Also remove the later "WARNING: ... not found ... cp infrastructure/secrets.env.example ..." and "WARNING: Service secrets not found ... cp k8s/secrets.env.example ..." blocks (lines ~101-107 and ~163-166) — those files are now always generated; the grep for `postgres-password` still works against the generated `infrastructure/overlays/dev/secrets.env`. Read the current file and make these edits precisely, keeping all cluster/helm/apply logic intact.

- [ ] **Step 2: server-setup.sh**

Same pattern: add `"$SCRIPT_DIR/render-config.sh" server` right after the preamble, before the config source. Remove the stale "copy config.env.example" hint and the two "WARNING: ... cp ...secrets.env.example" blocks (lines ~78-80 and ~131-136). Keep the `INFRA_SECRETS` grep (line ~74-76) — it reads the generated file. Keep everything else (k3s, helm, cert-manager, infra apply, deploy-all) intact.

- [ ] **Step 3: deploy.sh**

deploy.sh sources config.env at the top, then checks `$# -lt 2`. Reorder so render runs first: after the preamble, parse the env arg and render before sourcing. Replace the top block:

```bash
CONFIG_FILE="$ROOT_DIR/config.env"
if [ ! -f "$CONFIG_FILE" ]; then
  echo "ERROR: $CONFIG_FILE not found. Copy it: cp config.env.example config.env"
  exit 1
fi
set -a; # shellcheck disable=SC1090
source "$CONFIG_FILE"; set +a
```

with:

```bash
if [ $# -lt 2 ]; then
  echo "Usage: $0 <dev|server> <app>"
  exit 1
fi
"$SCRIPT_DIR/render-config.sh" "$1"
set -a; # shellcheck disable=SC1090
source "$ROOT_DIR/config.env"; set +a
```

and remove the now-duplicate `$# -lt 2` check further down (read the file — it's around line 19; delete the second copy so there's exactly one). Keep `REGISTRY=...`, `SUBST=...`, the envsubst check, and everything below unchanged.

- [ ] **Step 2 note / Step 4: shellcheck, lint, commit**

```bash
docker run --rm -v "$PWD":/repo:ro koalaman/shellcheck:stable /repo/scripts/dev-setup.sh /repo/scripts/server-setup.sh /repo/scripts/deploy.sh
bash scripts/lint.sh
# Sanity: a full render+source path works end to end for deploy's arg shape
cp toimi.env.example toimi.env
( set -a; source toimi.env; set +a; bash scripts/render-config.sh server >/dev/null; grep -q "^IMAGE_REGISTRY=" config.env && echo "config.env OK" )
rm -f toimi.env config.env k8s/overlays/{dev,server}/secrets.env infrastructure/overlays/{dev,server}/secrets.env k8s/overlays/server/admin-auth.env infrastructure/overlays/server/admin-auth.env
git status   # confirm only the three scripts are staged-worthy; no generated files
git add scripts/dev-setup.sh scripts/server-setup.sh scripts/deploy.sh
git commit -m "refactor(scripts): render per-overlay config from toimi.env before deploy"
```

---

## Task 3: CI, cleanup, docs

**Files:**
- Modify: `.github/workflows/ci.yml` (render step)
- Delete: `config.env.example`, `k8s/secrets.env.example`, `k8s/overlays/server/secrets.env.example`, `infrastructure/secrets.env.example`, `k8s/overlays/server/admin-auth.env.example`, `infrastructure/overlays/server/admin-auth.env.example`
- Modify: `.gitignore` (add `/toimi.env`)
- Modify: `docs/ops/server-hardening.md`, `docs/ops/disaster-recovery.md`, `README.md` (if it documents the old copy-the-example flow — grep first)

- [ ] **Step 1: CI render step**

In `.github/workflows/ci.yml`, replace the "Render server overlays with dummy secrets" step body (the four `cp *.example` lines) with:

```yaml
      - name: Render server overlays from toimi.env
        run: |
          sudo apt-get update && sudo apt-get install -y apache2-utils
          cp toimi.env.example toimi.env
          scripts/render-config.sh server
          kubectl kustomize k8s/overlays/server > /dev/null
          kubectl kustomize infrastructure/overlays/server > /dev/null
```

(`apache2-utils` provides `htpasswd` so the render doesn't need docker in CI. Keep the existing `kubectl kustomize k8s/base` / `infrastructure/base` steps above it unchanged.)

- [ ] **Step 2: Delete the superseded `.example` files + gitignore**

```bash
git rm config.env.example k8s/secrets.env.example k8s/overlays/server/secrets.env.example infrastructure/secrets.env.example k8s/overlays/server/admin-auth.env.example infrastructure/overlays/server/admin-auth.env.example
```

In `.gitignore`, add `/toimi.env` next to the existing `/config.env` line. (The `**/secrets.env`, `**/admin-auth.env`, and `/config.env` ignores stay — those files are now generated artifacts. The `!**/secrets.env.example` / `!**/admin-auth.env.example` negations can stay harmlessly or be removed since the files are gone; leave them to keep the diff small.)

- [ ] **Step 3: Update docs**

`grep -rn "secrets.env.example\|admin-auth.env.example\|config.env.example\|cp .*secrets.env\|htpasswd -nbB" docs/ README.md` and rewrite each hit to the new one-file flow:
- Setup becomes: `cp toimi.env.example toimi.env`, edit it, run `scripts/server-setup.sh` (or dev). No manual per-overlay copies, no manual htpasswd.
- `docs/ops/server-hardening.md` §2 (admin credential rotation): the new flow is "edit `ADMIN_PASSWORD` in `toimi.env`, delete the two generated `admin-auth.env` files, re-run `scripts/deploy.sh server <app>` (and the infra apply)". Update it.
- `docs/ops/disaster-recovery.md` and README: adjust any "copy the example" instructions to the single `toimi.env`.
Keep the edits accurate to what the scripts now do; don't invent flags that don't exist.

- [ ] **Step 4: Verify CI-equivalent render locally, lint, commit**

```bash
# Mirror the CI step locally (docker htpasswd fallback stands in for apache2-utils):
cp toimi.env.example toimi.env && bash scripts/render-config.sh server
docker run --rm -v "$PWD":/repo:ro bitnami/kubectl:latest kustomize /repo/k8s/overlays/server > /dev/null && echo "render OK"
rm -f toimi.env config.env k8s/overlays/{dev,server}/secrets.env infrastructure/overlays/{dev,server}/secrets.env k8s/overlays/server/admin-auth.env infrastructure/overlays/server/admin-auth.env
yamllint -c .yamllint.yaml .github/workflows/ci.yml
bash scripts/lint.sh
git add .github/workflows/ci.yml .gitignore docs README.md
git commit -m "chore(config): point CI + docs at toimi.env; drop the old example files"
```

---

## Final verification

- [ ] `git status` clean — NO generated files committed (`toimi.env`, `config.env`, any `secrets.env`/`admin-auth.env`); only `toimi.env.example`, `scripts/render-config.sh`, the three edited scripts, the CI/gitignore/docs edits, and the six deletions.
- [ ] `bash scripts/lint.sh` passes; shellcheck clean on all four scripts (via docker).
- [ ] A fresh `cp toimi.env.example toimi.env && scripts/render-config.sh server` produces renderable overlays (docker kubectl), then clean up.
- [ ] Completion report: the new one-file setup flow (`cp toimi.env.example toimi.env`, edit, run setup), that the postgres password + admin credential are now set once, and the admin-rotation flow (edit + delete the two `admin-auth.env` + re-render).
