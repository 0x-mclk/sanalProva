#!/bin/bash
# Compose servisi 'healthy' olana kadar bekler. Kullanim: bash scripts/ci/wait-healthy.sh <servis> [deneme=120]  (5 sn aralik)
# Hicbir ortam degiskeni veya gizli deger yazdirilmaz; yalnizca servis adi ve durum.
set -uo pipefail
cd "$(dirname "$0")/../.."
svc="${1:?kullanim: wait-healthy.sh <servis> [deneme]}"
tries="${2:-120}"
status="none"
for _ in $(seq 1 "$tries"); do
  cid="$(docker compose ps -q "$svc" 2>/dev/null || true)"
  status="none"
  if [ -n "$cid" ]; then
    status="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$cid" 2>/dev/null || echo none)"
  fi
  case "$status" in
    healthy) echo "$svc: healthy"; exit 0 ;;
    unhealthy|exited|dead) break ;;   # beklemenin anlami yok: erken ve acik basarisizlik
  esac
  sleep 5
done
echo "$svc saglikli olmadi (son durum: $status)" >&2
exit 1
