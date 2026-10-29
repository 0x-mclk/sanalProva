#!/bin/bash
# Basarisizlik teshisi: servis durumlari + gunlukler. Gunlukler gizli degerlerden ARINDIRILARAK (deger -> ***) dosyaya yazilir;
# ekrana yalnizca arindirilmis son satirlar basilir. Kullanim: bash scripts/ci/compose-diagnostics.sh [cikti-dosyasi=compose-logs.txt]
set -uo pipefail
cd "$(dirname "$0")/../.."
out="${1:-compose-logs.txt}"
redact() {
  local data
  data="$(cat)"
  if [ -f .env ]; then
    while IFS='=' read -r k v; do
      case "$k" in
        MSSQL_SA_PASSWORD|SP_MIGRATOR_PW|SP_APP_PW|STORAGE_SIGNING_KEY|IMAGE_WORKER_KEY) [ -n "$v" ] && data="${data//"$v"/***}" ;;
      esac
    done < .env
  fi
  [ -n "${E2E_PASSWORD:-}" ] && data="${data//"$E2E_PASSWORD"/***}"
  printf '%s\n' "$data"
}
{ echo "== docker compose ps -a =="; docker compose ps -a; echo; echo "== gunlukler =="; docker compose logs --no-color --tail=400; } 2>&1 | redact > "$out"
echo "arindirilmis gunluk: $out ($(wc -l < "$out") satir)"
tail -n 120 "$out"
