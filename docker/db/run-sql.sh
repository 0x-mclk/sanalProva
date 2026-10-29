#!/bin/bash
# db-provision / db-harden işleri için ortak yürütücü (mcr.microsoft.com/mssql-tools imajında çalışır).
# Kullanım: run-sql.sh provision | harden
# Parolalar yalnızca ortam değişkenlerinden okunur ve ASLA yazdırılmaz (set -x yok; sqlcmd çıktısı yalnızca PRINT/hata satırlarıdır).
set -euo pipefail

SQLCMD=/opt/mssql-tools/bin/sqlcmd
HOST="${MSSQL_HOST:-sqlserver}"
export SQLCMDPASSWORD="${MSSQL_SA_PASSWORD:?MSSQL_SA_PASSWORD gerekli}"

run() { "$SQLCMD" -S "$HOST" -U sa -C -I -b "$@"; }

echo "SQL Server bekleniyor ($HOST)..."
for i in $(seq 1 60); do
  if run -Q "SELECT 1" >/dev/null 2>&1; then ready=1; break; fi
  sleep 2
done
[ "${ready:-0}" = 1 ] || { echo "SQL Server 120 sn icinde hazir olmadi." >&2; exit 1; }

case "${1:?kullanim: run-sql.sh provision|harden}" in
  provision)
    : "${SP_MIGRATOR_PW:?SP_MIGRATOR_PW gerekli}" "${SP_APP_PW:?SP_APP_PW gerekli}"
    run -v SP_MIGRATOR_PW="$SP_MIGRATOR_PW" SP_APP_PW="$SP_APP_PW" -i /docker/db/provision.sql
    echo "provision tamam."
    ;;
  harden)
    for db in sanalprova_dev sanalprova_test sanalprova_e2e; do
      run -v Database="$db" -i /sql/harden-migration-history.sql
      run -v Database="$db" -i /sql/harden-append-only.sql
    done
    echo "harden tamam."
    ;;
  *)
    echo "bilinmeyen eylem: $1" >&2
    exit 2
    ;;
esac
