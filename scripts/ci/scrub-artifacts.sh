#!/bin/bash
# Artifact/gunluk klasorundeki dosyalarda uretilen gizli degerlerden biri geciyorsa o dosyayi SILER (degeri yazdirmadan; yalnizca dosya adi ve sayi).
# Kaynaklar: .env degerleri + E2E_PASSWORD. Kullanim: bash scripts/ci/scrub-artifacts.sh <klasor>...
set -uo pipefail
cd "$(dirname "$0")/../.."
[ "$#" -ge 1 ] || { echo "kullanim: scrub-artifacts.sh <klasor>..." >&2; exit 2; }
secrets=()
if [ -f .env ]; then
  while IFS='=' read -r k v; do
    case "$k" in
      MSSQL_SA_PASSWORD|SP_MIGRATOR_PW|SP_APP_PW|STORAGE_SIGNING_KEY|IMAGE_WORKER_KEY) [ -n "$v" ] && secrets+=("$v") ;;
    esac
  done < .env
fi
[ -n "${E2E_PASSWORD:-}" ] && secrets+=("$E2E_PASSWORD")
removed=0
root="$(pwd -P)"
for dir in "$@"; do
  [ -d "$dir" ] || continue
  # Koruma: depo kokunu veya onun ustunu taramaz (.env ve kaynak dosyalari silinmesin); yalnizca depo ici alt klasorler
  abs="$(cd "$dir" && pwd -P)"
  case "$abs" in
    "$root"/*) ;;
    *) echo "reddedildi (depo ici alt klasor degil): $dir" >&2; continue ;;
  esac
  case "$abs" in
    "$root/.git"*|"$root/scripts"*|"$root/backend/src"*|"$root/frontend/src"*) echo "reddedildi (kaynak klasoru): $dir" >&2; continue ;;
  esac
  for v in "${secrets[@]}"; do
    while IFS= read -r f; do
      rm -f -- "$f"
      echo "gizli deger icerdigi icin silindi: $f"
      removed=$((removed + 1))
    done < <(grep -rlaF -- "$v" "$dir" 2>/dev/null || true)
  done
done
echo "temizleme tamam: $removed dosya silindi"
