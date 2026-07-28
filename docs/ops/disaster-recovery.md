# Disaster recovery

Nightly backups land on the `backups` PVC in the `data` namespace:
`/backups/postgres/<db>-<date>.dump` (14 days) and `/backups/qdrant/<collection>-<date>.snapshot` (7 days),
produced by the `postgres-backup` (02:00) and `qdrant-backup` (02:30) CronJobs.

> **Limitation:** these backups live on the same node disk as the databases.
> They protect against dropped tables, bad migrations, and corruption — NOT
> against disk failure. Off-site replication (S3 or rsync to another machine)
> is the planned upgrade; until then, copy dumps off the node manually after
> significant data changes: `kubectl cp` from any pod mounting the PVC.
> The backup CronJob pins `postgres:17-alpine`; if the PostgreSQL server major
> version is ever upgraded (unpinned Bitnami chart), bump the CronJob image to
> match — `pg_dump`'s client major must be ≥ the server major.

## Restore PostgreSQL

1. Find the dump: run a pod with the PVC mounted and `ls /backups/postgres/`.
2. Stop writers: `kubectl scale deploy -n apps toimi-tools-tietue toimi-web toimi-tools-ruutu --replicas=0`.
3. Recreate the DB and restore:
   `dropdb`/`createdb` (or `DROP/CREATE DATABASE` via psql), then
   `pg_restore -h postgresql.data.svc.cluster.local -U postgres -d <db> /backups/postgres/<db>-<date>.dump`.
4. Scale the services back up. EF migrations run on startup and are no-ops on a current dump.

## Restore Qdrant

Per collection: `POST /collections/<name>/snapshots/upload` with the snapshot file
as a multipart upload, or copy the snapshot into the qdrant pod and use
`PUT /collections/<name>/snapshots/recover` to recover from a snapshot already
on the server (see Qdrant snapshot docs for exact parameters and body).

## Rebuild Qdrant without snapshots

Qdrant is derived data. For each semantically-indexed type (memory, skill, ...):
`POST /api/admin/tietue/semantic/reconcile/<type>` from the /admin panel host.
This enqueues re-embedding of every missing entity (OpenAI cost applies) and
prunes orphaned vectors. The outbox worker drains the queue within minutes.

## Verification cadence

Run `scripts/verify-backup.sh <env>` monthly. It restores the newest dump of each
database into a scratch `<db>_verify` database, asserts tables exist, and drops it.
