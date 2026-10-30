#!/bin/bash
# Docker Compose için rastgele parolalarla .env üretir (CI/Linux). Değerler ASLA yazdırılmaz; CI'da GITHUB_ENV'e de yazılır ve maskelenir.
# Varsa .env'i EZMEZ (-f ile zorla). Kullanım: scripts/compose-env.sh [-f]
set -euo pipefail
cd "$(dirname "$0")/.."
if [ -f .env ] && [ "${1:-}" != "-f" ]; then echo ".env zaten var (ezilmedi)."; exit 0; fi
rnd() { echo "Aa1$(openssl rand -hex "${1:-16}")"; }   # Aa1 öneki: büyük+küçük+rakam karmaşıklık kuralı
SA="$(rnd 16)"; MIG="$(rnd 16)"; APP="$(rnd 16)"; SIGN="$(rnd 24)"; SETUP="$(rnd 24)"; IMG="$(rnd 16)"
umask 077
cat > .env <<ENV
MSSQL_SA_PASSWORD=$SA
SP_MIGRATOR_PW=$MIG
SP_APP_PW=$APP
STORAGE_SIGNING_KEY=$SIGN
SETUP_TOKEN_KEY=$SETUP
IMAGE_WORKER_KEY=$IMG
ENV
if [ -n "${GITHUB_ENV:-}" ]; then
  for v in "$SA" "$MIG" "$APP" "$SIGN" "$SETUP" "$IMG"; do echo "::add-mask::$v"; done
  { echo "SP_APP_PW=$APP"; echo "SP_MIGRATOR_PW=$MIG"; echo "STORAGE_SIGNING_KEY=$SIGN"; echo "SETUP_TOKEN_KEY=$SETUP"; } >> "$GITHUB_ENV"
fi
echo ".env olusturuldu (degerler yazdirilmadi)."
