#!/bin/bash
# Üretim ortam dosyası ve yedek parolası üretir (Pilot Faz A). Rastgele sırlar; DEĞERLER ASLA YAZDIRILMAZ. Var olan dosyaları EZMEZ (-f ile zorla).
# Kullanım: ./prod-env.sh <site-adresi> [--internal-ca] [-f]
#   site-adresi: alan adı (ör. prova.magaza.com). --internal-ca: Caddy iç CA'sı (Caddyfile.internal) — genel alan adı yoksa.
# Çıktılar (git'e GİRMEZ: .gitignore .env.* ve deploy/secrets/):
#   deploy/.env.prod (600)            tüm yapılandırma + sırlar (docker compose --env-file)
#   deploy/secrets/backup_passphrase  yedek şifreleme parolası (600). KAYBOLURSA YEDEKLER AÇILAMAZ: bir parola yöneticisine/çevrimdışı kasaya KOPYALAYIN.
set -euo pipefail
cd "$(dirname "$0")/.."
SITE="${1:-}"; shift || true
caddyfile="Caddyfile"; force=0
for a in "$@"; do
  case "$a" in
    --internal-ca) caddyfile="Caddyfile.internal" ;;
    -f) force=1 ;;
    *) echo "bilinmeyen seçenek: $a" >&2; exit 2 ;;
  esac
done
if ! [[ "$SITE" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$ ]]; then
  echo "kullanım: $0 <alan-adı> [--internal-ca] [-f] (alan adı örn. prova.magaza.com; IP veya 'localhost' üretimde anlamsız)" >&2; exit 2
fi
case "$SITE" in localhost|*.localhost|127.*|0.*) echo "SITE_ADDRESS bir yerel/IP adres olamaz" >&2; exit 2 ;; esac
if { [ -f .env.prod ] || [ -f secrets/backup_passphrase ]; } && [ $force != 1 ]; then
  echo ".env.prod veya secrets/backup_passphrase zaten var (ezilmedi). Zorlamak için -f (ESKİ SIRLAR KAYBOLUR: yedek parolası dahil!)." >&2; exit 1
fi
rnd() { echo "Aa1$(openssl rand -hex "${1:-16}")"; }   # Aa1: SQL Server parola karmaşıklığı (büyük+küçük+rakam)
umask 077
mkdir -p secrets
openssl rand -base64 48 | tr -d '\n' > secrets/backup_passphrase
cat > .env.prod <<ENV
# Sanal Prova ÜRETİM yapılandırması. GİT'E GİRMEZ. Sırlar rastgele üretildi; elle düzenlenebilen yerler aşağıda işaretli.
SITE_ADDRESS=$SITE
CADDYFILE=$caddyfile
DB_NAME=sanalprova
PROXY_SUBNET=172.31.250.0/24

MSSQL_SA_PASSWORD=$(rnd 20)
SP_MIGRATOR_PW=$(rnd 20)
SP_APP_PW=$(rnd 20)
SP_BACKUP_PW=$(rnd 20)
STORAGE_SIGNING_KEY=$(rnd 24)
SETUP_TOKEN_KEY=$(rnd 24)

# --- AI (varsayılan: MOCK, ücretli çağrı KAPALI). Gerçek AI için docs/19 "Ücretli AI'yı etkinleştirme" sırasını izleyin.
USE_MOCK_PROVIDERS=true
ALLOW_PAID_CALLS=false
BUDGET_DAILY_USD=3
BUDGET_MONTHLY_USD=60
BUDGET_MAX_JOB_USD=0.25
FAL_API_KEY=
PRUNA_API_KEY=
FASHN_API_KEY=
GEMINI_API_KEY=

# --- Yedek
BACKUP_DIR=./backups
BACKUP_RETENTION_FULL_DAYS=14
BACKUP_RETENTION_LOG_DAYS=7
ENV
chmod 600 .env.prod secrets/backup_passphrase
echo ".env.prod ve secrets/backup_passphrase oluşturuldu (değerler yazdırılmadı)."
echo "ÖNEMLİ: secrets/backup_passphrase dosyasının bir kopyasını sunucu DIŞINDA güvenli bir yere (parola yöneticisi/kasa) kaydedin; kaybı = yedeklerin kaybı."
echo "Sıradaki adım: ./scripts/make-sql-cert.sh"
