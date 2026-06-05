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

CLUSTER_NAME="toimi"
REGISTRY_NAME="kind-registry"
REGISTRY_PORT="5000"

RESET=false
if [ "${1:-}" = "--reset" ]; then
  RESET=true
fi

echo "=== Dev Environment Setup ==="

# --- Prerequisites ---
for cmd in docker kind kubectl helm envsubst; do
  command -v "$cmd" &>/dev/null || { echo "ERROR: $cmd not installed"; exit 1; }
done

# --- Reset (destroy existing cluster + registry) ---
if [ "$RESET" = true ]; then
  echo "Resetting dev environment..."
  kind delete cluster --name "$CLUSTER_NAME" 2>/dev/null || true
  docker rm -f "$REGISTRY_NAME" 2>/dev/null || true
  echo "Cluster and registry destroyed."
fi

# --- Local registry (docker container) ---
if docker inspect "$REGISTRY_NAME" &>/dev/null; then
  if [ "$(docker inspect -f '{{.State.Running}}' "$REGISTRY_NAME")" != "true" ]; then
    echo "Starting registry container..."
    docker start "$REGISTRY_NAME"
  else
    echo "Registry already running."
  fi
else
  echo "Creating registry container..."
  docker run -d --restart=always -p "127.0.0.1:${REGISTRY_PORT}:5000" --network bridge --name "$REGISTRY_NAME" registry:2
fi

# --- kind cluster ---
if kind get clusters 2>/dev/null | grep -q "^${CLUSTER_NAME}$"; then
  echo "kind cluster '$CLUSTER_NAME' already exists."
else
  echo "Creating kind cluster..."
  kind create cluster --name "$CLUSTER_NAME" --config "$ROOT_DIR/infrastructure/kind/cluster-config.yaml"
fi

# Connect registry to kind network
docker network connect kind "$REGISTRY_NAME" 2>/dev/null || true

# Configure containerd to resolve registry address -> kind-registry:5000
REGISTRY_DIR="/etc/containerd/certs.d/localhost:${REGISTRY_PORT}"
for node in $(kind get nodes --name "$CLUSTER_NAME"); do
  docker exec "$node" mkdir -p "$REGISTRY_DIR"
  cat <<TOML | docker exec -i "$node" cp /dev/stdin "$REGISTRY_DIR/hosts.toml"
[host."http://${REGISTRY_NAME}:5000"]
TOML
done

# Document local registry
cat <<EOF | kubectl apply -f -
apiVersion: v1
kind: ConfigMap
metadata:
  name: local-registry-hosting
  namespace: kube-public
data:
  localRegistryHosting.v1: |
    host: "localhost:${REGISTRY_PORT}"
    help: "https://kind.sigs.k8s.io/docs/user/local-registry/"
EOF

# --- Traefik ingress (kind needs hostPort to use the kind extraPortMappings) ---
echo "Installing Traefik..."
helm repo add traefik https://traefik.github.io/charts --force-update >/dev/null
helm repo update >/dev/null
helm upgrade --install traefik traefik/traefik \
  --namespace traefik \
  --create-namespace \
  --version "39.0.7" \
  --set "ports.web.hostPort=80" \
  --set "ports.websecure.hostPort=443" \
  --set "service.type=ClusterIP" \
  --wait

# --- Read secrets ---
INFRA_SECRETS="$ROOT_DIR/infrastructure/overlays/dev/secrets.env"
if [ -f "$INFRA_SECRETS" ]; then
  PG_PASSWORD=$(grep '^postgres-password=' "$INFRA_SECRETS" | cut -d= -f2-)
else
  echo "WARNING: $INFRA_SECRETS not found, using default password"
  echo "  Copy from template: cp infrastructure/secrets.env.example infrastructure/overlays/dev/secrets.env"
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

# --- Infrastructure (Kustomize) ---
echo "Applying infrastructure (with config.env substitution)..."
kubectl kustomize "$ROOT_DIR/infrastructure/overlays/dev" | envsubst "$SUBST" | \
  { kubectl apply --server-side -f - 2>/dev/null || kubectl apply -f -; }

# --- Database init (imperative by nature) ---
echo "Waiting for PostgreSQL..."
kubectl rollout status statefulset/postgresql --namespace data --timeout=120s

echo "Ensuring databases exist..."
for DB_NAME in muistio muistutin ajastin toimi ruutu; do
  kubectl exec -n data svc/postgresql -- env PGPASSWORD="$PG_PASSWORD" \
    psql -U postgres -tc "SELECT 1 FROM pg_database WHERE datname = '$DB_NAME'" | grep -q 1 || \
    kubectl exec -n data svc/postgresql -- env PGPASSWORD="$PG_PASSWORD" \
    psql -U postgres -c "CREATE DATABASE $DB_NAME;"
done

# --- Check secrets ---
if [ ! -f "$ROOT_DIR/k8s/overlays/dev/secrets.env" ]; then
  echo ""
  echo "WARNING: Service secrets not found. Copy from template:"
  echo "  cp k8s/secrets.env.example k8s/overlays/dev/secrets.env"
  echo "  # Then edit with real values"
fi

# --- Deploy all services (on reset) ---
if [ "$RESET" = true ]; then
  echo "Deploying all services..."
  "$SCRIPT_DIR/deploy-all.sh" dev
fi

echo ""
echo "=== Dev environment ready ==="
echo ""
echo "Registry: localhost:${REGISTRY_PORT}"
echo ""
echo "Add to /etc/hosts:"
echo "  127.0.0.1  ${TOIMI_HOST} ${ADMINER_HOST} ${QDRANT_HOST}"
