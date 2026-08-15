#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

if [ $# -lt 2 ]; then
  echo "Usage: $0 <dev|server> <app>"
  echo "  <app> = a src/ project dir name, with or without the 'toimi.' prefix"
  echo "  Examples: $0 dev web   |   $0 server tools.koti   |   $0 dev toimi.tools.tietue"
  exit 1
fi

"$SCRIPT_DIR/render-config.sh" "$1"
CONFIG_FILE="$ROOT_DIR/config.env"
set -a; # shellcheck disable=SC1090
source "$CONFIG_FILE"; set +a
REGISTRY="${IMAGE_REGISTRY:?IMAGE_REGISTRY missing from config.env}"
# shellcheck disable=SC2016
SUBST='${TOIMI_HOST} ${ADMINER_HOST} ${QDRANT_HOST} ${IMAGE_REGISTRY} ${HOMEASSISTANT_BASE_URL} ${OPENAI_MODEL_FAST}'
command -v envsubst >/dev/null || { echo "ERROR: envsubst not installed (gettext)"; exit 1; }

ENV="$1"
APP_ARG="$2"

# Normalise: accept 'web', 'tools.koti', or full 'toimi.web'/'toimi.tools.koti'.
case "$APP_ARG" in
  toimi.*) APP_DIR="$APP_ARG" ;;
  *)       APP_DIR="toimi.$APP_ARG" ;;
esac

SRC_DIR="$ROOT_DIR/src/$APP_DIR"
DOCKERFILE="$SRC_DIR/Dockerfile"

if [ ! -f "$DOCKERFILE" ]; then
  echo "ERROR: not a deployable pod (no Dockerfile): src/$APP_DIR"
  exit 1
fi

# toimi.tools.koti -> toimi-tools-koti
IMAGE_NAME="${APP_DIR//./-}"
IMAGE="${REGISTRY}/${IMAGE_NAME}:latest"

echo "=== Building $IMAGE_NAME ==="
docker build -t "$IMAGE" -f "$DOCKERFILE" "$ROOT_DIR"   # context = repo root
docker push "$IMAGE"

OVERLAY_DIR="$ROOT_DIR/k8s/overlays/$ENV"
if [ ! -d "$OVERLAY_DIR" ]; then
  echo "ERROR: no overlay at k8s/overlays/$ENV"
  exit 1
fi

echo "Applying overlay ($ENV) with config.env substitution..."
kubectl kustomize "$OVERLAY_DIR" | envsubst "$SUBST" | kubectl apply -f -

kubectl rollout restart "deployment/$IMAGE_NAME" -n apps 2>/dev/null || true
kubectl rollout status  "deployment/$IMAGE_NAME" -n apps --timeout=120s 2>/dev/null || \
  echo "Note: check manually: kubectl get pods -n apps"

echo "=== $IMAGE_NAME deployed ==="
