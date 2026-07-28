#!/usr/bin/env bash
# Restores the newest backup of each database into a scratch <db>_verify database,
# sanity-checks it, and drops it. Run monthly (see docs/ops/disaster-recovery.md).
#
# Usage: scripts/verify-backup.sh [dev|server]
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

ENV="${1:-dev}"
"$SCRIPT_DIR/render-config.sh" "$ENV"
INFRA_SECRETS="$ROOT_DIR/infrastructure/overlays/$ENV/secrets.env"
PG_PASSWORD=$(grep '^postgres-password=' "$INFRA_SECRETS" | cut -d= -f2-)

DATABASES=(tietue toimi ruutu)
FAILURES=0

# psql against the postgres pod. kubectl exec keeps the password out of pod specs.
psql_admin() {
  kubectl exec -n data svc/postgresql -- env PGPASSWORD="$PG_PASSWORD" psql -U postgres "$@"
}

# Pod spec for an ephemeral pod with the backups PVC mounted at /backups.
# PGPASSWORD comes from the chart-created 'postgresql' Secret via secretKeyRef, so
# the password never appears in the pod spec, process args, or `kubectl describe`.
# $1 is embedded in a JSON string: it must not contain double quotes or backslashes.
pod_overrides() {
  cat <<EOF
{
  "apiVersion": "v1",
  "spec": {
    "restartPolicy": "Never",
    "containers": [
      {
        "name": "verify",
        "image": "postgres:17-alpine",
        "command": ["sh", "-c", "$1"],
        "env": [
          {
            "name": "PGPASSWORD",
            "valueFrom": {"secretKeyRef": {"name": "postgresql", "key": "postgres-password"}}
          }
        ],
        "volumeMounts": [{"name": "backups", "mountPath": "/backups"}]
      }
    ],
    "volumes": [{"name": "backups", "persistentVolumeClaim": {"claimName": "backups"}}]
  }
}
EOF
}

run_in_backup_pod() {
  kubectl run "backup-verify-$RANDOM" --rm -i --quiet --restart=Never -n data \
    --image=postgres:17-alpine --overrides="$(pod_overrides "$1")"
}

for DB in "${DATABASES[@]}"; do
  echo "=== $DB ==="
  LATEST=$(run_in_backup_pod "ls -1 /backups/postgres/$DB-*.dump 2>/dev/null | sort | tail -n 1" 2>/dev/null \
    | grep '^/backups/' | tail -n 1 || true)
  if [ -z "$LATEST" ]; then
    echo "FAIL: no backup found for $DB"
    FAILURES=$((FAILURES + 1))
    continue
  fi
  echo "Newest dump: $LATEST"

  psql_admin -c "DROP DATABASE IF EXISTS ${DB}_verify;" >/dev/null
  psql_admin -c "CREATE DATABASE ${DB}_verify;" >/dev/null

  if ! run_in_backup_pod "pg_restore -h postgresql.data.svc.cluster.local -U postgres -d ${DB}_verify $LATEST"; then
    echo "FAIL: pg_restore of $LATEST into ${DB}_verify failed"
    FAILURES=$((FAILURES + 1))
    psql_admin -c "DROP DATABASE IF EXISTS ${DB}_verify;" >/dev/null
    continue
  fi

  TABLES=$(psql_admin -tA -d "${DB}_verify" \
    -c "SELECT count(*) FROM information_schema.tables WHERE table_schema='public';" \
    | tr -d '[:space:]' || true)
  if [ "$TABLES" -gt 0 ] 2>/dev/null; then
    echo "PASS: ${DB}_verify restored with $TABLES table(s)"
  else
    echo "FAIL: ${DB}_verify has no tables (count: '$TABLES')"
    FAILURES=$((FAILURES + 1))
  fi
  psql_admin -c "DROP DATABASE ${DB}_verify;" >/dev/null
done

echo ""
if [ "$FAILURES" -gt 0 ]; then
  echo "=== verify-backup FAILED ($FAILURES failure(s)) ==="
  exit 1
fi
echo "=== verify-backup PASSED ==="
