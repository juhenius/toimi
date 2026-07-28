#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

CONFIG_FILE="$ROOT_DIR/config.env"
if [ ! -f "$CONFIG_FILE" ]; then
  echo "ERROR: $CONFIG_FILE not found. Copy it: cp config.env.example config.env"
  exit 1
fi
set -a; # shellcheck disable=SC1090
source "$CONFIG_FILE"; set +a
# shellcheck disable=SC2016
SUBST='${TOIMI_HOST} ${ADMINER_HOST} ${QDRANT_HOST} ${IMAGE_REGISTRY} ${HOMEASSISTANT_BASE_URL} ${OPENAI_MODEL}'

RESET=false
if [ "${1:-}" = "--reset" ]; then
  RESET=true
fi

echo "=== Server Setup ==="

# --- Prerequisites ---
for cmd in curl helm envsubst; do
  command -v "$cmd" &>/dev/null || { echo "ERROR: $cmd not installed"; exit 1; }
done

# --- Reset (destroy existing k3s installation) ---
if [ "$RESET" = true ]; then
  echo "Resetting server environment..."
  if [ -x /usr/local/bin/k3s-uninstall.sh ]; then
    /usr/local/bin/k3s-uninstall.sh
    echo "k3s uninstalled."
  else
    echo "k3s not installed, skipping uninstall."
  fi
fi

# --- k3s configuration files (declarative, copied from repo) ---
K3S_VERSION="v1.35.4+k3s1"

sudo mkdir -p /etc/rancher/k3s
sudo cp "$ROOT_DIR/infrastructure/k3s/config.yaml"     /etc/rancher/k3s/config.yaml
sudo cp "$ROOT_DIR/infrastructure/k3s/registries.yaml" /etc/rancher/k3s/registries.yaml

# --- k3s install ---
if command -v k3s &>/dev/null; then
  echo "k3s already installed; restarting to pick up config..."
  sudo systemctl restart k3s
else
  echo "Installing k3s $K3S_VERSION..."
  curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION="$K3S_VERSION" sh -
fi

# Poll until the API is up and at least one node is registered, then wait for Ready.
# Without this, `kubectl wait --all` against an empty node list errors out with
# "no matching resources found" rather than waiting.
echo "Waiting for k3s API to register node..."
for _ in $(seq 1 60); do
  sudo k3s kubectl get node 2>/dev/null | grep -q . && break
  sleep 1
done
sudo k3s kubectl wait --for=condition=ready node --all --timeout=120s

export KUBECONFIG=/etc/rancher/k3s/k3s.yaml

if ! command -v helm &>/dev/null; then
  echo "Installing Helm..."
  curl -sfL https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 | bash
fi

# --- Read secrets ---
INFRA_SECRETS="$ROOT_DIR/infrastructure/overlays/server/secrets.env"
if [ -f "$INFRA_SECRETS" ]; then
  PG_PASSWORD=$(grep '^postgres-password=' "$INFRA_SECRETS" | cut -d= -f2-)
else
  echo "WARNING: $INFRA_SECRETS not found, using default password"
  echo "  Copy from template: cp infrastructure/secrets.env.example infrastructure/overlays/server/secrets.env"
  PG_PASSWORD="changeme-in-production"
fi

# --- PostgreSQL (Helm) ---
echo "Installing PostgreSQL..."
helm repo add bitnami https://charts.bitnami.com/bitnami --force-update >/dev/null
helm repo update >/dev/null
helm upgrade --install postgresql bitnami/postgresql \
  --namespace data \
  --create-namespace \
  --values "$ROOT_DIR/infrastructure/base/helm/postgresql-values.yaml" \
  --set auth.postgresPassword="$PG_PASSWORD" \
  --wait

# --- cert-manager (Helm; must precede the infra apply — ClusterIssuer/Certificate need its CRDs) ---
echo "Installing cert-manager..."
helm repo add jetstack https://charts.jetstack.io --force-update >/dev/null
helm repo update >/dev/null
helm upgrade --install cert-manager jetstack/cert-manager \
  --namespace cert-manager \
  --create-namespace \
  --version "v1.21.0" \
  --set crds.enabled=true \
  --wait

# --- Infrastructure (Kustomize) ---
echo "Applying infrastructure (with config.env substitution)..."
INFRA_MANIFESTS=$(sudo k3s kubectl kustomize "$ROOT_DIR/infrastructure/overlays/server" | envsubst "$SUBST")
# cert-manager's webhook can lag a few seconds behind helm --wait (caBundle
# propagation), transiently rejecting Certificate/ClusterIssuer applies — retry.
for attempt in 1 2 3; do
  if echo "$INFRA_MANIFESTS" | sudo k3s kubectl apply --server-side -f - 2>/dev/null \
    || echo "$INFRA_MANIFESTS" | sudo k3s kubectl apply -f -; then
    break
  fi
  if [ "$attempt" -eq 3 ]; then
    echo "ERROR: infrastructure apply failed after 3 attempts" >&2
    exit 1
  fi
  echo "Apply failed (cert-manager webhook settling?); retrying in 5s..."
  sleep 5
done

# Wait for registry to be Ready before deploy-all.sh tries to push to it.
echo "Waiting for registry..."
sudo k3s kubectl rollout status deployment/registry --namespace infra --timeout=120s

# --- Database init ---
echo "Waiting for PostgreSQL..."
sudo k3s kubectl rollout status statefulset/postgresql --namespace data --timeout=120s

# --- Check secrets ---
if [ ! -f "$ROOT_DIR/k8s/overlays/server/secrets.env" ]; then
  echo ""
  echo "WARNING: Service secrets not found. Copy from template:"
  echo "  cp k8s/secrets.env.example k8s/overlays/server/secrets.env"
  echo "  # Then edit with real values"
fi

SERVER_IP=$(hostname -I | awk '{print $1}')

# --- Deploy all services (on reset) ---
if [ "$RESET" = true ]; then
  echo "Deploying all services..."
  "$SCRIPT_DIR/deploy-all.sh" server
fi

echo ""
echo "=== Server setup complete ==="
echo ""
echo "Server IP:  ${SERVER_IP}"
echo "Registry:   ${IMAGE_REGISTRY} (NodePort 31500)"
echo ""
echo "Configure DNS so these resolve to ${SERVER_IP}:"
echo "  ${TOIMI_HOST}  ${ADMINER_HOST}  ${QDRANT_HOST}"
