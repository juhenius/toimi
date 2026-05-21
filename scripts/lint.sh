#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

FIX=false
if [ "${1:-}" = "--fix" ]; then
  FIX=true
fi

ERRORS=0

# --- C# (dotnet format) ---
echo "=== C# ==="
for SLN in "$ROOT_DIR"/*.sln; do
  [ -f "$SLN" ] || continue
  SLN_NAME=$(basename "$SLN")
  echo "Checking $SLN_NAME..."
  if [ "$FIX" = true ]; then
    dotnet format "$SLN" --verbosity minimal
  else
    dotnet format "$SLN" --verify-no-changes --verbosity minimal || ERRORS=$((ERRORS + 1))
  fi
done

# --- YAML (yamllint) ---
if command -v yamllint &>/dev/null; then
  echo ""
  echo "=== YAML ==="
  yamllint -c "$ROOT_DIR/.yamllint.yaml" "$ROOT_DIR" || ERRORS=$((ERRORS + 1))
else
  echo ""
  echo "=== YAML (skipped: yamllint not installed) ==="
  echo "  Install: pip install yamllint"
fi

# --- Shell (shellcheck) ---
if command -v shellcheck &>/dev/null; then
  echo ""
  echo "=== Shell ==="
  find "$ROOT_DIR/scripts" -name "*.sh" -print0 | xargs -0 shellcheck || ERRORS=$((ERRORS + 1))
else
  echo ""
  echo "=== Shell (skipped: shellcheck not installed) ==="
  echo "  Install: brew install shellcheck"
fi

echo ""
if [ "$ERRORS" -gt 0 ]; then
  echo "=== Lint failed ($ERRORS error(s)) ==="
  exit 1
else
  echo "=== Lint passed ==="
fi
