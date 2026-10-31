#!/bin/bash
# Dağıtım ÖNCESİ ön kontrol (Pilot Faz A): yapılandırma ve sırlar güvenli ve tutarlı mı? Hata varsa sıfırdan farklı çıkar; docker compose up ÇALIŞTIRILMAZ.
# Kullanım: ./preflight.sh [--env <dosya>] [--secrets <klasör>]   (varsayılan: .env.prod, secrets/). Hiçbir değer yazdırılmaz.
set -uo pipefail
cd "$(dirname "$0")/.."
ENVF=".env.prod"; SECRETS="secrets"
while [ $# -gt 0 ]; do
  case "$1" in
    --env) ENVF="$2"; shift 2 ;;
    --secrets) SECRETS="$2"; shift 2 ;;
    *) echo "bilinmeyen seçenek: $1" >&2; exit 2 ;;
  esac
done
fail=0
ok() { printf '  TAMAM  %s\n' "$1"; }
bad() { printf '  HATA   %s\n' "$1"; fail=1; }
val() { sed -n "s/^$1=\(.*\)$/\1/p" "$ENVF" | head -n 1; }
perm_ok() {
  [ "${PREFLIGHT_SKIP_PERM:-0}" = 1 ] && return 0
  case "$(stat -c '%a' "$1")" in 600|400) return 0 ;; *) return 1 ;; esac
}

echo "[ön kontrol] $ENVF / $SECRETS"
if [ -f "$ENVF" ]; then ok "$ENVF var"; else bad "$ENVF yok (./scripts/prod-env.sh)"; exit 1; fi
if perm_ok "$ENVF"; then ok "$ENVF izni 600/400"; else bad "$ENVF izni 600/400 olmalı (chmod 600)"; fi
for v in SITE_ADDRESS CADDYFILE DB_NAME PROXY_SUBNET MSSQL_SA_PASSWORD SP_MIGRATOR_PW SP_APP_PW SP_BACKUP_PW STORAGE_SIGNING_KEY SETUP_TOKEN_KEY USE_MOCK_PROVIDERS ALLOW_PAID_CALLS; do
  if [ -n "$(val $v)" ]; then ok "$v tanımlı"; else bad "$v boş/eksik"; fi
done
# Yer tutucu / zayıf değer
for v in MSSQL_SA_PASSWORD SP_MIGRATOR_PW SP_APP_PW SP_BACKUP_PW STORAGE_SIGNING_KEY SETUP_TOKEN_KEY; do
  x="$(val $v)"
  if [ "${#x}" -lt 24 ] || printf '%s' "$x" | grep -qiE 'changeme|change-me|placeholder|example|password|secret|todo|xxxx|0000'; then bad "$v zayıf/yer tutucu"; fi
done
if [ "$(val STORAGE_SIGNING_KEY)" != "$(val SETUP_TOKEN_KEY)" ]; then ok "imza ve kurulum anahtarları farklı"; else bad "STORAGE_SIGNING_KEY ile SETUP_TOKEN_KEY aynı"; fi
# Ad ve ağ
site="$(val SITE_ADDRESS)"
if [[ "$site" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$ ]] && ! [[ "$site" =~ ^(localhost|127\.|0\.) ]] && ! [[ "$site" =~ ^[0-9.]+$ ]]; then ok "SITE_ADDRESS alan adı"; else bad "SITE_ADDRESS geçerli bir alan adı olmalı (IP/localhost değil)"; fi
case "$(val CADDYFILE)" in Caddyfile|Caddyfile.internal) ok "CADDYFILE geçerli" ;; *) bad "CADDYFILE Caddyfile veya Caddyfile.internal olmalı" ;; esac
db="$(val DB_NAME)"
if [[ "$db" =~ ^[A-Za-z0-9_]{3,60}$ ]] && ! [[ "$db" =~ ^(sanalprova_dev|sanalprova_test|sanalprova_e2e|sanalprova_lab_.*|master|tempdb|model|msdb)$ ]]; then ok "DB_NAME üretim adı"; else bad "DB_NAME geliştirme/test/lab/sistem adı olamaz"; fi
if [[ "$(val PROXY_SUBNET)" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}/[0-9]{1,2}$ ]]; then ok "PROXY_SUBNET CIDR"; else bad "PROXY_SUBNET geçerli bir CIDR olmalı"; fi
# AI kapıları tutarlılığı
mock="$(val USE_MOCK_PROVIDERS)"; paid="$(val ALLOW_PAID_CALLS)"
if [ "$mock" = true ] && [ "$paid" = false ]; then
  ok "AI: mock mod, ücretli çağrı kapalı (güvenli varsayılan)"
elif [ "$mock" = false ] && [ "$paid" = true ]; then
  ok "AI: GERÇEK mod açık (ücretli çağrılar etkin) — bilinçli karar mı? docs/19 kontrol listesi"
  if [ -n "$(val FAL_API_KEY)$(val PRUNA_API_KEY)$(val FASHN_API_KEY)$(val GEMINI_API_KEY)" ]; then ok "en az bir sağlayıcı anahtarı var"; else bad "gerçek mod açık ama hiçbir sağlayıcı anahtarı yok"; fi
else
  bad "USE_MOCK_PROVIDERS/ALLOW_PAID_CALLS tutarsız (ya mock+kapalı ya gerçek+açık olmalı)"
fi
# Sır dosyaları
if [ -s "$SECRETS/backup_passphrase" ]; then
  ok "yedek parolası var"
  if perm_ok "$SECRETS/backup_passphrase"; then ok "yedek parolası izni 600/400"; else bad "backup_passphrase izni 600/400 olmalı"; fi
else
  bad "$SECRETS/backup_passphrase yok"
fi
for f in ca/ca.crt sqlserver-tls/server.crt sqlserver-tls/server.key; do
  if [ -s "$SECRETS/$f" ]; then ok "$f var"; else bad "$SECRETS/$f yok (./scripts/make-sql-cert.sh)"; fi
done
if [ -s "$SECRETS/ca/ca.crt" ] && [ -s "$SECRETS/sqlserver-tls/server.crt" ]; then
  if openssl verify -CAfile "$SECRETS/ca/ca.crt" "$SECRETS/sqlserver-tls/server.crt" > /dev/null 2>&1; then ok "sunucu sertifikası CA ile doğrulanıyor"; else bad "sunucu sertifikası CA ile doğrulanamıyor"; fi
  if openssl x509 -in "$SECRETS/sqlserver-tls/server.crt" -noout -text 2> /dev/null | grep -q 'DNS:sqlserver'; then ok "sertifika SAN: sqlserver"; else bad "sertifikada DNS:sqlserver (SAN) yok"; fi
  if openssl x509 -in "$SECRETS/sqlserver-tls/server.crt" -noout -checkend $((30 * 86400)) > /dev/null 2>&1; then ok "sertifika en az 30 gün geçerli"; else bad "sertifikanın süresi 30 gün içinde doluyor/doldu"; fi
fi
if [ -f "$SECRETS/ca/ca.key" ]; then echo "  UYARI  secrets/ca/ca.key sunucuda duruyor: çevrimdışı saklayıp silin (yalnızca sertifika yenilerken gerekir)"; fi
# Docker (varsa) yapılandırma kontrolü
if command -v docker > /dev/null 2>&1; then
  if docker compose -f docker-compose.prod.yml --env-file "$ENVF" config -q > /dev/null 2>&1; then ok "docker compose config geçerli"; else bad "docker compose config HATALI"; fi
else
  echo "  ATLANDI  docker yok: docker compose config doğrulanmadı"
fi
if [ $fail = 0 ]; then
  echo "[ön kontrol] TAMAM: dağıtıma hazır."
else
  echo "[ön kontrol] BAŞARISIZ: yukarıdaki HATA satırlarını giderin." >&2
  exit 1
fi
