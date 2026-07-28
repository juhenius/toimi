#!/usr/bin/env bash
# Exports the Toimi root CA certificate for trusting on client devices.
# See docs/ops/server-hardening.md for per-device trust instructions.
#
# Usage: scripts/export-ca.sh [output-file]
set -euo pipefail

OUT="${1:-toimi-ca.crt}"
# On the k3s server: KUBECTL="sudo k3s kubectl" scripts/export-ca.sh
KUBECTL="${KUBECTL:-kubectl}"
$KUBECTL get secret toimi-ca-key-pair -n cert-manager -o jsonpath='{.data.tls\.crt}' | base64 -d > "$OUT"
echo "CA certificate written to $OUT"
echo "Fingerprint: $(openssl x509 -in "$OUT" -noout -fingerprint -sha256)"
