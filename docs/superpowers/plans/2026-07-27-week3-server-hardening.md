# Week 3: Server Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the approved design in `docs/superpowers/specs/2026-07-27-week3-server-hardening-design.md`: container/k8s hardening, Dependabot, cert-manager TLS from a self-managed CA, Traefik basicAuth on admin surfaces, and the ops runbook.

**Architecture:** Pure infrastructure iteration — no C#/TS changes. Base manifests gain resources/securityContext (all envs); TLS and auth land as server-overlay patches so dev (kind) stays plain HTTP. cert-manager (Helm, pinned) issues certs from a bootstrapped in-cluster CA; Traefik Middlewares handle HTTPS redirect and admin basicAuth. CI gains a kustomize-render check since no local kubectl exists.

**Tech Stack:** Kustomize (patches, secretGenerator), cert-manager, Traefik CRDs (Middleware), Dependabot, GitHub Actions.

**Conventions:** yamllint (2-space indent, 200-char lines) on everything; `bash scripts/lint.sh` before each commit (dotnet format is a no-op here but the yaml step matters). NO local kubectl — validation is yamllint + the new CI kustomize job + the post-deploy smoke checklist. Commit convention `<type>(<scope>): <subject>`. Work from repo root; verify branch with `git branch --show-current`.

**Facts discovered during planning (do not re-derive):**
- Ruutu's ingress shares `${TOIMI_HOST}` (path `/ruutu`) with the web ingress → ONE cert secret (`toimi-web-tls`) covers both; only the web ingress carries the cert-manager annotation (two Certificates for one host would fight).
- envsubst allowlist (`SUBST=` in deploy.sh/dev-setup.sh/server-setup.sh) already covers `TOIMI_HOST`/`ADMINER_HOST`/`QDRANT_HOST` — TLS blocks reuse them, no allowlist change.
- Traefik middleware annotation syntax: `traefik.ingress.kubernetes.io/router.middlewares: <namespace>-<name>@kubernetescrd`. Middlewares + their secrets are namespace-scoped: one set in `apps`, one in `data`.
- Redirect pattern: main ingress pinned to `websecure` entrypoint + tls block; a companion `-http` ingress pinned to `web` with a redirectScheme middleware (redirect middleware on the same router as TLS would loop).
- .NET 10 images ship user `app` (env `APP_UID=1654`). Verify during Task 2 (`docker run --rm mcr.microsoft.com/dotnet/aspnet:10.0 sh -c 'id app'` if docker exists; else trust APP_UID and note it).
- Server overlay kustomizations currently have only `resources:` + secretGenerator — `patches:` sections are new.

---

## Task 1: `.dockerignore` + image pinning

**Files:**
- Create: `.dockerignore`
- Modify: `src/toimi.web/Dockerfile`, `src/toimi.tools.tietue/Dockerfile`, `src/toimi.tools.koti/Dockerfile`, `src/toimi.tools.verkko/Dockerfile`, `src/toimi.tools.ruutu/Dockerfile` (FROM lines)
- Modify: `infrastructure/base/adminer/deployment.yaml:20` (`adminer:latest`), `infrastructure/overlays/server/registry/deployment.yaml:20` (`registry:2`)

- [ ] **Step 1: Create `.dockerignore`** at repo root:

```
.git
.github
.worktrees
.playwright-mcp
**/bin
**/obj
**/node_modules
docs
k8s
infrastructure
scripts
samples
*.md
```

(Build context is the repo root for every Dockerfile; nothing in these paths is COPYed.)

- [ ] **Step 2: Resolve current patch tags and pin.** Look up the current tags: `curl -s https://mcr.microsoft.com/v2/dotnet/sdk/tags/list | jq -r '.tags[]' | grep -E '^10\.0\.[0-9]+$' | sort -V | tail -1` (same for `dotnet/aspnet`), and for node: `curl -s 'https://hub.docker.com/v2/repositories/library/node/tags?name=24.&page_size=100' | jq -r '.results[].name' | grep -E '^24\.[0-9]+\.[0-9]+-slim$' | sort -V | tail -1`. Then in all five Dockerfiles replace `sdk:10.0`→`sdk:10.0.<patch>`, `aspnet:10.0`→`aspnet:10.0.<patch>`, and in toimi.web also `node:24-slim`→`node:24.<x>.<y>-slim`. Pin `adminer:latest`→ current stable (`curl -s 'https://hub.docker.com/v2/repositories/library/adminer/tags?page_size=25' | jq -r '.results[].name'` — pick the newest plain semver, e.g. `adminer:5.x.y`) and `registry:2`→ newest `registry:2.8.x` (same technique, repo `library/registry`). If a registry query fails, use the most recent version you know and note it for Dependabot to correct.

- [ ] **Step 3: Verify one build if docker is available**: `docker build -f src/toimi.tools.verkko/Dockerfile -t verkko-pin-test . && docker rmi verkko-pin-test`. If docker is unavailable, note it — CI's next real deploy validates; the pinned tags at least must exist per the registry queries above.

- [ ] **Step 4: Lint + commit**

```bash
bash scripts/lint.sh
git add .dockerignore src/*/Dockerfile infrastructure/base/adminer/deployment.yaml infrastructure/overlays/server/registry/deployment.yaml
git commit -m "chore: pin base images and add .dockerignore"
```

---

## Task 2: Resources + securityContext on all deployments

**Files:**
- Modify: `k8s/base/web/deployment.yaml`, `k8s/base/tools-tietue/deployment.yaml`, `k8s/base/tools-koti/deployment.yaml`, `k8s/base/tools-verkko/deployment.yaml`, `k8s/base/tools-ruutu/deployment.yaml`
- Modify: `infrastructure/base/qdrant/deployment.yaml`, `infrastructure/base/adminer/deployment.yaml`, `infrastructure/overlays/server/registry/deployment.yaml`

- [ ] **Step 1: .NET app deployments.** In each of the five `k8s/base/*/deployment.yaml`, inside `spec.template.spec` add pod-level securityContext, and on the container add resources, container securityContext, tmp mount. Template (adjust resources per table below):

```yaml
    spec:
      securityContext:
        runAsNonRoot: true
        runAsUser: 1654  # `app` user in the .NET base images (APP_UID)
        seccompProfile:
          type: RuntimeDefault
      containers:
        - name: <existing>
          # ... existing image/ports/env unchanged ...
          resources:
            requests:
              cpu: 100m
              memory: 256Mi
            limits:
              cpu: 500m
              memory: 512Mi
          securityContext:
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            capabilities:
              drop:
                - ALL
          volumeMounts:
            - name: tmp
              mountPath: /tmp
      volumes:
        - name: tmp
          emptyDir: {}
```

Per-pod resources: **tietue** `requests 200m/512Mi, limits 1000m/1Gi`; **web, koti, verkko, ruutu** `100m/256Mi → 500m/512Mi`.

Note: if a deployment already has `volumeMounts`/`volumes` (check each file), merge rather than duplicate keys. ASP.NET needs writable `/tmp` (data protection keys land under /root or /home by default — with `readOnlyRootFilesystem` the app may also need `DOTNET_CLI_HOME=/tmp` and `ASPNETCORE_DATA_PROTECTION` fallback; to keep risk low, ALSO add env `- name: HOME` / `value: /tmp` to each container so anything writing to $HOME lands on the emptyDir).

- [ ] **Step 2: Infra deployments.**
  - **qdrant** (`infrastructure/base/qdrant/deployment.yaml`): resources `200m/512Mi → 1000m/1Gi`; container securityContext `allowPrivilegeEscalation: false` + `capabilities.drop: [ALL]`. Qdrant's official image runs as root by default and writes to its storage path (PVC) — set pod `securityContext: { seccompProfile: { type: RuntimeDefault } }` only, with a comment: `# qdrant image expects uid 0 unless storage ownership is migrated; runAsNonRoot deferred`. Do NOT set readOnlyRootFilesystem.
  - **adminer**: resources `50m/64Mi → 200m/256Mi`; the official image runs as `adminer` (uid 1000) already — set pod securityContext `runAsNonRoot: true`, `seccompProfile: RuntimeDefault`; container `allowPrivilegeEscalation: false`, `capabilities.drop: [ALL]`. No readOnlyRootFilesystem (php session tmp).
  - **registry** (`infrastructure/overlays/server/registry/deployment.yaml`): resources `50m/64Mi → 200m/256Mi` (file already has a `resources:` key at ~line 40 — REPLACE its contents, don't duplicate); container `allowPrivilegeEscalation: false` + `capabilities.drop: [ALL]`; comment that runAsNonRoot is skipped (image writes /var/lib/registry as root unless the PVC ownership is prepared).

- [ ] **Step 3: Lint + commit**

```bash
bash scripts/lint.sh
git add k8s/base infrastructure/base/qdrant infrastructure/base/adminer infrastructure/overlays/server/registry
git commit -m "feat(infra): resource limits and security contexts on all deployments"
```

---

## Task 3: CI kustomize-render check + Dependabot

**Files:**
- Modify: `.github/workflows/ci.yml` (yaml job)
- Create: `.github/dependabot.yml`

- [ ] **Step 1: Extend the CI `yaml` job** with kustomize render checks (ubuntu-latest runners ship kubectl):

```yaml
  yaml:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - run: pip install yamllint
      - run: yamllint -c .yamllint.yaml .
      - run: kubectl kustomize k8s/base > /dev/null
      - run: kubectl kustomize infrastructure/base > /dev/null
      - name: Render server overlays with dummy secrets
        run: |
          cp k8s/overlays/server/secrets.env.example k8s/overlays/server/secrets.env
          cp infrastructure/secrets.env.example infrastructure/overlays/server/secrets.env
          cp k8s/overlays/server/admin-auth.env.example k8s/overlays/server/admin-auth.env
          cp infrastructure/overlays/server/admin-auth.env.example infrastructure/overlays/server/admin-auth.env
          kubectl kustomize k8s/overlays/server > /dev/null
          kubectl kustomize infrastructure/overlays/server > /dev/null
```

NOTE: the `admin-auth.env.example` files are created in Task 6. To keep every commit green, in THIS task add the render step only for the base directories and the two `cp` lines for the existing secrets.env examples + overlay renders; Task 6 then appends its two `cp` lines when the files exist. (Check `infrastructure/secrets.env.example` location — it's at `infrastructure/secrets.env.example`, copied into the overlay dir.)

- [ ] **Step 2: Create `.github/dependabot.yml`:**

```yaml
version: 2
updates:
  - package-ecosystem: nuget
    directory: /
    schedule:
      interval: weekly
    groups:
      nuget-minor-patch:
        update-types: [minor, patch]
  - package-ecosystem: npm
    directory: /src/toimi.web/ClientApp
    schedule:
      interval: weekly
    groups:
      npm-minor-patch:
        update-types: [minor, patch]
  - package-ecosystem: docker
    directories:
      - /src/toimi.web
      - /src/toimi.tools.tietue
      - /src/toimi.tools.koti
      - /src/toimi.tools.verkko
      - /src/toimi.tools.ruutu
    schedule:
      interval: weekly
    groups:
      docker-minor-patch:
        update-types: [minor, patch]
  - package-ecosystem: github-actions
    directory: /
    schedule:
      interval: weekly
```

(`directories:` on one docker entry is supported by current Dependabot; if the schema validator in CI complains, split into five entries.)

- [ ] **Step 3: Lint + commit**

```bash
bash scripts/lint.sh
git add .github
git commit -m "ci: validate kustomize renders; add Dependabot for nuget, npm, docker, actions"
```

---

## Task 4: cert-manager install + CA bootstrap + export script

**Files:**
- Modify: `scripts/dev-setup.sh`, `scripts/server-setup.sh` (cert-manager Helm install)
- Create: `infrastructure/base/cert-manager/kustomization.yaml`, `.../selfsigned-issuer.yaml`, `.../ca-certificate.yaml`, `.../ca-issuer.yaml`
- Modify: `infrastructure/base/kustomization.yaml` (add `- cert-manager`)
- Create: `scripts/export-ca.sh`

- [ ] **Step 1: Helm install in both setup scripts.** Determine the current cert-manager chart version (`helm search repo jetstack/cert-manager` after adding the repo, or check https://cert-manager.io/docs/releases/ — pin the latest stable, e.g. `v1.18.x`). Insert after the Traefik/PostgreSQL installs (dev) and PostgreSQL install (server), following each script's existing style:

```bash
helm repo add jetstack https://charts.jetstack.io --force-update >/dev/null
helm repo update >/dev/null
helm upgrade --install cert-manager jetstack/cert-manager \
  --namespace cert-manager --create-namespace \
  --version v1.18.2 \
  --set crds.enabled=true \
  --wait
```

(server-setup.sh uses `sudo k3s kubectl` but plain `helm` — helm talks to the kubeconfig; check how the script configures KUBECONFIG for helm's postgres install and mirror it.)

- [ ] **Step 2: Bootstrap manifests.** `infrastructure/base/cert-manager/selfsigned-issuer.yaml`:

```yaml
apiVersion: cert-manager.io/v1
kind: ClusterIssuer
metadata:
  name: selfsigned-bootstrap
spec:
  selfSigned: {}
```

`ca-certificate.yaml`:

```yaml
apiVersion: cert-manager.io/v1
kind: Certificate
metadata:
  name: toimi-ca
  namespace: cert-manager
spec:
  isCA: true
  commonName: Toimi CA
  duration: 87600h  # 10 years
  privateKey:
    algorithm: RSA
    size: 4096
  secretName: toimi-ca-key-pair
  issuerRef:
    name: selfsigned-bootstrap
    kind: ClusterIssuer
```

`ca-issuer.yaml`:

```yaml
apiVersion: cert-manager.io/v1
kind: ClusterIssuer
metadata:
  name: toimi-ca-issuer
spec:
  ca:
    secretName: toimi-ca-key-pair
```

`kustomization.yaml` listing the three; add `- cert-manager` to `infrastructure/base/kustomization.yaml` resources. ORDERING NOTE: applying these requires cert-manager CRDs to exist — the setup scripts install cert-manager (Step 1) BEFORE applying the infrastructure kustomize (verify the script order puts the Helm install before the `kubectl kustomize infrastructure/overlays/...` apply; move it above if not). CI's kustomize-render check doesn't need the CRDs (client-side render).

- [ ] **Step 3: `scripts/export-ca.sh`** (executable, `set -euo pipefail`, SCRIPT_DIR/ROOT_DIR preamble like the other scripts):

```bash
#!/usr/bin/env bash
# Exports the Toimi root CA certificate for trusting on client devices.
# See docs/ops/server-hardening.md for per-device trust instructions.
set -euo pipefail

OUT="${1:-toimi-ca.crt}"
kubectl get secret toimi-ca-key-pair -n cert-manager -o jsonpath='{.data.tls\.crt}' | base64 -d > "$OUT"
echo "CA certificate written to $OUT"
echo "Fingerprint: $(openssl x509 -in "$OUT" -noout -fingerprint -sha256)"
```

`chmod +x scripts/export-ca.sh`.

- [ ] **Step 4: Lint + commit**

```bash
bash scripts/lint.sh
git add scripts infrastructure/base
git commit -m "feat(infra): cert-manager with a self-managed Toimi CA"
```

---

## Task 5: Server-overlay TLS (certs + HTTPS redirect)

**Files:**
- Create: `k8s/overlays/server/tls/` — `web-ingress-patch.yaml`, `ruutu-ingress-patch.yaml`, `redirect-middleware.yaml`, `web-http-ingress.yaml`
- Create: `infrastructure/overlays/server/tls/` — `adminer-ingress-patch.yaml`, `qdrant-ingress-patch.yaml`, `redirect-middleware.yaml`, `adminer-http-ingress.yaml`, `qdrant-http-ingress.yaml`
- Modify: `k8s/overlays/server/kustomization.yaml`, `infrastructure/overlays/server/kustomization.yaml`

- [ ] **Step 1: Redirect middlewares** (one per namespace). `k8s/overlays/server/tls/redirect-middleware.yaml`:

```yaml
apiVersion: traefik.io/v1alpha1
kind: Middleware
metadata:
  name: redirect-https
  namespace: apps
spec:
  redirectScheme:
    scheme: https
    permanent: true
```

Same content with `namespace: data` in `infrastructure/overlays/server/tls/redirect-middleware.yaml`.

- [ ] **Step 2: TLS patches.** `k8s/overlays/server/tls/web-ingress-patch.yaml` (strategic-merge patch):

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: toimi-web
  namespace: apps
  annotations:
    cert-manager.io/cluster-issuer: toimi-ca-issuer
    traefik.ingress.kubernetes.io/router.entrypoints: websecure
spec:
  tls:
    - hosts:
        - ${TOIMI_HOST}
      secretName: toimi-web-tls
```

`ruutu-ingress-patch.yaml` — same host, REUSES the secret, NO cert-manager annotation (the web ingress owns the Certificate):

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: toimi-tools-ruutu
  namespace: apps
  annotations:
    traefik.ingress.kubernetes.io/router.entrypoints: websecure
spec:
  tls:
    - hosts:
        - ${TOIMI_HOST}
      secretName: toimi-web-tls
```

`infrastructure/overlays/server/tls/adminer-ingress-patch.yaml` and `qdrant-ingress-patch.yaml`: same shape — `name: adminer`/`qdrant`, `namespace: data`, cert-manager annotation present on each (distinct hosts), hosts `${ADMINER_HOST}`/`${QDRANT_HOST}`, secretNames `adminer-tls`/`qdrant-tls`.

- [ ] **Step 3: Companion HTTP redirect ingresses.** `k8s/overlays/server/tls/web-http-ingress.yaml` (a resource, not a patch — covers both web `/` and ruutu `/ruutu` since it matches the whole host):

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: toimi-web-http-redirect
  namespace: apps
  annotations:
    traefik.ingress.kubernetes.io/router.entrypoints: web
    traefik.ingress.kubernetes.io/router.middlewares: apps-redirect-https@kubernetescrd
spec:
  ingressClassName: traefik
  rules:
    - host: ${TOIMI_HOST}
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: toimi-web
                port:
                  number: 80
```

Same pattern for `adminer-http-ingress.yaml` and `qdrant-http-ingress.yaml` in the infra overlay (`data-redirect-https@kubernetescrd`, respective hosts/services/ports — adminer service port 80, qdrant 6333).

- [ ] **Step 4: Wire kustomizations.** `k8s/overlays/server/kustomization.yaml` gains:

```yaml
resources:
  - ../../base
  - tls/redirect-middleware.yaml
  - tls/web-http-ingress.yaml

patches:
  - path: tls/web-ingress-patch.yaml
  - path: tls/ruutu-ingress-patch.yaml
```

(analogous for the infrastructure server overlay with its five files). Keep the existing secretGenerator untouched.

- [ ] **Step 5: Lint + commit** (CI's overlay render step now exercises these):

```bash
bash scripts/lint.sh
git add k8s/overlays/server infrastructure/overlays/server
git commit -m "feat(infra): TLS on all server ingresses via the Toimi CA with HTTPS redirect"
```

---

## Task 6: Admin basicAuth (server overlay)

**Files:**
- Create: `k8s/overlays/server/admin-auth.env.example`, `infrastructure/overlays/server/admin-auth.env.example`
- Create: `k8s/overlays/server/auth/middleware.yaml`, `k8s/overlays/server/auth/web-admin-ingress.yaml`
- Create: `infrastructure/overlays/server/auth/middleware.yaml`, `.../adminer-auth-patch.yaml`, `.../qdrant-auth-patch.yaml`
- Modify: both server `kustomization.yaml`s (secretGenerator entries + resources/patches), `.github/workflows/ci.yml` (the two deferred `cp` lines), `.gitignore` (ignore `admin-auth.env`)

- [ ] **Step 1: htpasswd env files.** `admin-auth.env.example` (identical content in both overlay dirs):

```
# Copy to admin-auth.env and replace with a real bcrypt htpasswd line:
#   htpasswd -nbB admin 'your-password'      (apache2-utils)
# or docker run --rm httpd:2.4-alpine htpasswd -nbB admin 'your-password'
users=admin:$2y$05$REPLACE_WITH_REAL_BCRYPT_HASH
```

Add `admin-auth.env` to `.gitignore` (alongside the existing secrets.env pattern — check how secrets.env is ignored and mirror it).

- [ ] **Step 2: secretGenerator entries.** In `k8s/overlays/server/kustomization.yaml`:

```yaml
secretGenerator:
  # ... existing toimi-secrets entry unchanged ...
  - name: admin-basic-auth
    namespace: apps
    envs:
      - admin-auth.env
    options:
      disableNameSuffixHash: true
```

Same in the infra overlay with `namespace: data`. (The htpasswd `$` characters are safe: secretGenerator base64-encodes values, so the envsubst pipeline never sees raw `$` in the rendered YAML.)

- [ ] **Step 3: Middlewares.** `k8s/overlays/server/auth/middleware.yaml`:

```yaml
apiVersion: traefik.io/v1alpha1
kind: Middleware
metadata:
  name: admin-auth
  namespace: apps
spec:
  basicAuth:
    secret: admin-basic-auth
```

Same with `namespace: data` in the infra overlay.

- [ ] **Step 4: Apply auth.**
  - `infrastructure/overlays/server/auth/adminer-auth-patch.yaml` / `qdrant-auth-patch.yaml` — annotation patches ADDING the middleware to the EXISTING websecure annotation set (strategic-merge on metadata.annotations merges keys; the entrypoints annotation from Task 5 must not be lost — put both patches' annotations in ONE patch file per ingress OR merge Task 5's and this task's annotations into a single combined patch per ingress. SIMPLEST: extend Task 5's `adminer-ingress-patch.yaml`/`qdrant-ingress-patch.yaml` with the extra annotation line instead of new files):

```yaml
    traefik.ingress.kubernetes.io/router.middlewares: data-admin-auth@kubernetescrd
```

  - `k8s/overlays/server/auth/web-admin-ingress.yaml` — new ingress for the admin paths only (longest-prefix wins in Traefik, so `/admin` + `/api/admin` route here, everything else stays on the open ingress):

```yaml
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: toimi-web-admin
  namespace: apps
  annotations:
    traefik.ingress.kubernetes.io/router.entrypoints: websecure
    traefik.ingress.kubernetes.io/router.middlewares: apps-admin-auth@kubernetescrd
spec:
  ingressClassName: traefik
  tls:
    - hosts:
        - ${TOIMI_HOST}
      secretName: toimi-web-tls
  rules:
    - host: ${TOIMI_HOST}
      http:
        paths:
          - path: /admin
            pathType: Prefix
            backend:
              service:
                name: toimi-web
                port:
                  number: 80
          - path: /api/admin
            pathType: Prefix
            backend:
              service:
                name: toimi-web
                port:
                  number: 80
```

- [ ] **Step 5: Wire kustomizations + CI.** Add `auth/middleware.yaml` (+ `auth/web-admin-ingress.yaml` in k8s) to the overlay `resources:`. Append the two deferred `cp ... admin-auth.env.example → admin-auth.env` lines to CI's overlay-render step (Task 3 note).

- [ ] **Step 6: Lint + commit**

```bash
bash scripts/lint.sh
git add k8s/overlays/server infrastructure/overlays/server .github/workflows/ci.yml .gitignore
git commit -m "feat(infra): basic auth on admin surfaces (adminer, qdrant, /admin)"
```

---

## Task 7: Ops runbook

**Files:**
- Create: `docs/ops/server-hardening.md`

- [ ] **Step 1: Write the runbook** covering, in order:
  1. **CA trust per device** — run `scripts/export-ca.sh`, then: macOS (Keychain Access → System → import, set Always Trust), iOS (AirDrop/mail the .crt → Settings → General → VPN & Device Management → install, then Settings → General → About → Certificate Trust Settings → enable), Android (Settings → Security → Encryption & credentials → Install a certificate → CA certificate), Linux (`sudo cp toimi-ca.crt /usr/local/share/ca-certificates/ && sudo update-ca-certificates`), ruutu display browser (depends on the display OS — trust at the OS level as above).
  2. **Admin credential rotation** — generate a new htpasswd line (`htpasswd -nbB admin '...'` or the docker fallback), update `users=` in BOTH `k8s/overlays/server/admin-auth.env` and `infrastructure/overlays/server/admin-auth.env`, re-run `scripts/deploy.sh server <any app>` (re-applies the overlay) and the infra apply; verify with the smoke checklist.
  3. **Accepted risks** — chat UI unauthenticated on the trusted LAN; backups on the node disk (off-site deferred, second consecutive deferral — revisit); dev overlay fully unencrypted/unauthenticated by design; qdrant + registry containers still run as root (image limitations, documented in their manifests).
  4. **Post-deploy smoke checklist** (run after every server deploy touching TLS/auth):

```
curl -sI http://$TOIMI_HOST/            # expect 301/308 → https
curl -sI --cacert toimi-ca.crt https://$TOIMI_HOST/          # expect 200
curl -sI --cacert toimi-ca.crt https://$TOIMI_HOST/admin     # expect 401
curl -sI --cacert toimi-ca.crt -u admin:PW https://$TOIMI_HOST/admin  # expect 200
curl -sI --cacert toimi-ca.crt https://$ADMINER_HOST/        # expect 401
curl -sI --cacert toimi-ca.crt https://$QDRANT_HOST/         # expect 401
kubectl top pods -n apps; kubectl top pods -n data           # all within limits
kubectl get certificates -A                                   # all Ready=True
```

  5. **First-deploy order**: run `scripts/server-setup.sh` (installs cert-manager, applies infra incl. CA bootstrap), wait for `kubectl get clusterissuer toimi-ca-issuer` Ready, then `scripts/deploy.sh server <apps>`, then smoke checklist.

- [ ] **Step 2: Lint + commit**

```bash
bash scripts/lint.sh
git add docs/ops/server-hardening.md
git commit -m "docs(ops): server hardening runbook (CA trust, credential rotation, smoke checklist)"
```

---

## Final verification

- [ ] `bash scripts/lint.sh` — passes.
- [ ] The kustomize render checks cannot run locally (no kubectl) — state clearly in the completion report that the push-triggered CI run and the post-deploy smoke checklist are the two outstanding acceptance gates.
- [ ] `git status` clean; commits follow convention.
- [ ] Completion report to the user MUST include: the smoke checklist location, the "trust the CA on your devices" action item, the htpasswd generation step needed before the server deploy, and the note that dev (kind) is intentionally unchanged.
