#!/bin/sh
# Daily Postgres backup. Mounted into hook-backup container, run by cron.
# Writes pg_dump tarballs to /backups, prunes older than RETENTION_DAYS.

set -eu

BACKUP_DIR="${BACKUP_DIR:-/backups}"
RETENTION_DAYS="${RETENTION_DAYS:-14}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
TARGET="${BACKUP_DIR}/hook-${STAMP}.dump.gz"

mkdir -p "${BACKUP_DIR}"

echo "[backup] start ${STAMP} target=${TARGET}"

pg_dump --format=custom --no-owner --no-acl --compress=9 --file=/tmp/hook.dump
gzip -c /tmp/hook.dump > "${TARGET}"
rm -f /tmp/hook.dump

# Prune
find "${BACKUP_DIR}" -name 'hook-*.dump.gz' -type f -mtime "+${RETENTION_DAYS}" -delete

echo "[backup] done size=$(du -h "${TARGET}" | cut -f1)"
