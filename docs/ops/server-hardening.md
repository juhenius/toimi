# Server hardening runbook

Covers the outcomes of the week-3 server-hardening effort (container/k8s
hardening, cert-manager TLS from a self-managed CA, Traefik basicAuth on
admin surfaces): trusting the CA on client devices, rotating the admin
credential, the accepted-risk list, and the post-deploy smoke checklist.
Companion to `docs/ops/disaster-recovery.md` (backups/restore); this doc is
about transport trust and access control, not data recovery.

## 1. CA trust per device

TLS on the server overlay is issued by a self-managed in-cluster CA
(`toimi-ca-issuer`, bootstrapped from the `toimi-ca-key-pair` secret in the
`cert-manager` namespace), not a public CA — so every client device needs the
CA certificate installed to avoid trust warnings.

Export the certificate from the cluster:

```bash
scripts/export-ca.sh                       # writes ./toimi-ca.crt
# on the k3s server itself, kubectl means sudo k3s kubectl:
KUBECTL="sudo k3s kubectl" scripts/export-ca.sh
```

Then trust `toimi-ca.crt` on each device:

- **macOS**: open in Keychain Access → drag into the `System` keychain →
  double-click the entry → `Trust` → `Always Trust`.
- **iOS**: get the file onto the device (AirDrop or mail it to yourself) →
  `Settings → General → VPN & Device Management` → install the profile, then
  `Settings → General → About → Certificate Trust Settings` → enable full
  trust for the Toimi CA.
- **Android**: `Settings → Security → Encryption & credentials → Install a
  certificate → CA certificate` → select `toimi-ca.crt`.
- **Linux**: `sudo cp toimi-ca.crt /usr/local/share/ca-certificates/ && sudo
  update-ca-certificates`.
- **ruutu display browser**: trust depends on the display's OS — apply the
  matching instructions above at the OS level (ruutu itself has no
  certificate store of its own).

## 2. Admin credential rotation

The admin basicAuth password lives once in `toimi.env` (`ADMIN_PASSWORD`).
`scripts/render-config.sh server` derives a bcrypt htpasswd line from it and
writes it into two gitignored files (`admin-auth.env`, one per Traefik
namespace) — `k8s/overlays/server/` guards the web `/admin` + `/api/admin`
paths, `infrastructure/overlays/server/` guards adminer and qdrant. Because
the bcrypt salt is random, the render step regenerates these files only when
they are absent, so rotation is a delete-then-re-render:

1. Edit `ADMIN_PASSWORD` in `toimi.env`.

2. Delete both generated htpasswd files so the next render re-derives them:

   ```bash
   rm -f k8s/overlays/server/admin-auth.env \
         infrastructure/overlays/server/admin-auth.env
   ```

   (One admin password, enforced by two separate Traefik Middlewares/
   namespaces — the render step writes the same freshly-generated line to
   both.)

3. Re-render and re-apply:
   - `k8s/overlays/server` (the `admin-basic-auth` secret + `/admin` guard):
     `scripts/deploy.sh server <any-app>` runs render-config first, then
     re-applies the whole overlay, so any app works, e.g.
     `scripts/deploy.sh server web`.
   - `infrastructure/overlays/server` (adminer/qdrant guard): re-run
     `scripts/server-setup.sh`. It renders config first and is idempotent by
     design (`helm upgrade --install` for PostgreSQL/cert-manager, `k3s`
     restart only if already installed, kustomize apply for the rest) — safe
     to run end-to-end on a live server for this purpose; it does not touch
     `k8s/overlays/server` or restart application deployments.

4. Verify with the smoke checklist below, in particular the `-u admin:PW`
   and unauthenticated `401` lines.

## 3. Accepted risks

- **Chat UI unauthenticated on the trusted LAN.** The main `toimi-web`
  ingress (chat, SSE, SignalR) carries no basicAuth — only `/admin` and
  `/api/admin` do. Anyone on the LAN can reach the assistant, which is
  already able to do most things an authenticated admin session could do
  (it drives the same tool servers). Accepted: the admin surfaces (raw DB
  access via adminer, raw vector access via qdrant, and the admin dashboard)
  are the higher-value targets and are the ones gated.
- **Traefik `PathPrefix` matching is case-sensitive; ASP.NET routing is
  case-insensitive.** The `/admin`/`/api/admin` guard on `toimi-web-admin`
  matches those exact-case prefixes on the RAW path, but ASP.NET routes the
  DECODED path case-insensitively — so a case-variant (`GET /Api/admin/summary`,
  `/ADMIN`) or percent-encoded (`/api/%61dmin`) request falls through to the
  open `toimi-web` ingress yet still reaches the admin endpoints.
  **Mitigated app-side (`AdminPathGuard` middleware in `toimi.web`):** any
  request whose decoded path is an admin path but whose raw target does not
  start with the exact lowercase literal now gets a 404, so non-canonical
  requests never reach the admin endpoints. The Traefik-side limitation
  itself remains (its router still only matches the exact-case prefix), but
  the bypass is closed at the application layer, independent of the server's
  Traefik version.
- **Backups live on the same node disk as the databases** (see
  `docs/ops/disaster-recovery.md`) — protects against bad migrations/
  corruption, not disk failure. Off-site replication is still deferred; this
  is the second consecutive deferral and should be revisited before a third.
- **Dev overlay (kind) is fully unencrypted and unauthenticated by design.**
  No TLS, no basicAuth — hardening is server-overlay-only so local dev stays
  frictionless. Do not port any of this back to `k8s/overlays/dev` or
  `infrastructure/overlays/dev`.
- **qdrant and registry containers still run as root.** Both official images
  write to their storage paths as uid 0 by default; `runAsNonRoot` is
  deferred until PVC ownership is migrated. `allowPrivilegeEscalation: false`
  and `capabilities.drop: [ALL]` are applied regardless. Documented inline in
  `infrastructure/base/qdrant/deployment.yaml` and
  `infrastructure/overlays/server/registry/deployment.yaml`.

## 4. Post-deploy smoke checklist

Run after every server deploy that touches TLS, auth, or ingress
(`$TOIMI_HOST`/`$ADMINER_HOST`/`$QDRANT_HOST` are the values from
`config.env`; `PW` is the current admin password; `toimi-ca.crt` from
step 1):

```bash
curl -sI http://$TOIMI_HOST/                                           # expect 301/308 → https
curl -sI --cacert toimi-ca.crt https://$TOIMI_HOST/                    # expect 200
curl -sI --cacert toimi-ca.crt https://$TOIMI_HOST/admin                # expect 401
curl -sI --cacert toimi-ca.crt -u admin:PW https://$TOIMI_HOST/admin    # expect 200
curl -sI --cacert toimi-ca.crt https://$ADMINER_HOST/                   # expect 401
curl -sI --cacert toimi-ca.crt https://$QDRANT_HOST/                    # expect 401
curl -sI http://$ADMINER_HOST/                                          # expect 301/308 → https
curl -sI http://$QDRANT_HOST/                                           # expect 301/308 → https
curl -sI --cacert toimi-ca.crt https://$TOIMI_HOST/api/admin/summary    # expect 401
curl -sI --cacert toimi-ca.crt https://$TOIMI_HOST/Api/admin/summary    # expect 404 (path guard)
curl -sI --cacert toimi-ca.crt https://$TOIMI_HOST/ruutu/               # expect 200 (no auth — display surface)
kubectl top pods -n apps; kubectl top pods -n data                      # all within limits
kubectl get certificates -A                                             # all Ready=True
```

On the k3s server, `kubectl` above means `sudo k3s kubectl` or
`export KUBECONFIG=/etc/rancher/k3s/k3s.yaml` first (see the note in
`docs/ops/disaster-recovery.md`).

## 5. First-deploy order

1. `scripts/server-setup.sh` — installs cert-manager, PostgreSQL, and applies
   the infrastructure overlay (including the CA bootstrap: the self-signed
   `ClusterIssuer`, the `toimi-ca` `Certificate`, and the `toimi-ca-issuer`
   that signs everything else).
2. Wait for the CA issuer to be ready:
   `kubectl get clusterissuer toimi-ca-issuer` → `READY: True`.
3. `scripts/deploy.sh server <app>` (or `scripts/deploy-all.sh server`) for
   each application pod.
4. Run the smoke checklist (section 4).
