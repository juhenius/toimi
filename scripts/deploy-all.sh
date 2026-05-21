#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

ENV="${1:?Usage: deploy-all.sh <dev|server>}"

echo "=== Deploying all pods ($ENV) ==="
DEPLOYED=0

# A pod = a src/*/ dir containing a Dockerfile.
# Fail-fast: set -e + no '|| true' means a failed pod aborts the rest (intentional).
for DOCKERFILE in "$ROOT_DIR"/src/*/Dockerfile; do
  [ -f "$DOCKERFILE" ] || continue
  APP_DIR="$(basename "$(dirname "$DOCKERFILE")")"   # e.g. toimi.tools.koti
  echo ""
  echo "--- $APP_DIR ---"
  "$SCRIPT_DIR/deploy.sh" "$ENV" "$APP_DIR"
  DEPLOYED=$((DEPLOYED + 1))
done

if [ "$DEPLOYED" -eq 0 ]; then
  echo "No pods found (no src/*/Dockerfile)."
  exit 1
fi
echo ""
echo "=== Deployed $DEPLOYED pod(s) ==="
