#!/bin/bash
# Docker Compose smoke dogrulamalari (yigin `docker compose up -d --build api` ile ayaga kalkmis olmali).
# Kontroller: tek seferlik isler (provision/migrate/harden) 0 ile bitti; sqlserver/image-worker/api healthy; /health/ready Healthy;
# veritabani ayarlari (harmanlama, RCSI, uyumluluk 160) ve migration'lar uygulandi; image-worker ana makineye ACIK DEGIL;
# yayimlanan portlar yalnizca 127.0.0.1; sertlestirme etkin (sanalprova_app migration gecmisini ve mutabakat tablosunu degistiremez);
# uretilen gizli degerler compose gunluklerinde YOK.
# Gizli degerler .env'den okunur ve ASLA yazdirilmaz (set -x yok; ciktilar yalnizca durum metinleridir).
set -uo pipefail
cd "$(dirname "$0")/../.."
[ -f .env ] || { echo ".env yok: once scripts/compose-env.sh" >&2; exit 2; }
set -a; . ./.env; set +a

fail=0
summary() { if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then echo "$1" >> "$GITHUB_STEP_SUMMARY"; fi; }
ok()  { echo "OK   : $*"; summary "- OK: $*"; }
bad() { echo "HATA : $*" >&2; summary "- HATA: $*"; fail=1; }
summary "### Compose smoke"

# sqlcmd'yi arac imajinda (db-harden servisi) calistirir; parola komut satirina degil ortam degiskenine (adi gecirilir, degeri degil) verilir
sql() { # kullanim: sql <giris> <parola-degiskeni-adi> <veritabani> <sorgu>
  local user="$1" pwvar="$2" db="$3" query="$4"
  SQLCMDPASSWORD="${!pwvar}" docker compose run -T --rm --no-deps -e SQLCMDPASSWORD --entrypoint /opt/mssql-tools18/bin/sqlcmd db-harden \
    -S sqlserver -U "$user" -d "$db" -C -I -h -1 -W -Q "SET NOCOUNT ON; $query" 2>&1
}

# 1) Tek seferlik isler basariyla tamamlandi
for svc in db-provision db-migrate db-harden; do
  cid="$(docker compose ps -aq "$svc" 2>/dev/null | head -n1)"
  if [ -z "$cid" ]; then bad "$svc konteyneri bulunamadi"; continue; fi
  state="$(docker inspect -f '{{.State.Status}}' "$cid")"; code="$(docker inspect -f '{{.State.ExitCode}}' "$cid")"
  if [ "$state" = exited ] && [ "$code" = 0 ]; then ok "$svc tamamlandi (exit 0)"; else bad "$svc durumu: $state, cikis kodu: $code"; fi
done

# 2) Uzun yasayan servisler healthy
for svc in sqlserver image-worker api; do
  cid="$(docker compose ps -q "$svc" 2>/dev/null | head -n1)"
  health="none"
  if [ -n "$cid" ]; then health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$cid")"; fi
  if [ "$health" = healthy ]; then ok "$svc healthy"; else bad "$svc saglik durumu: $health"; fi
done

# 3) API hazir: veritabani + depolama + image-worker (ana makine portu uzerinden)
body="$(curl -fsS --max-time 15 http://127.0.0.1:8080/health/ready 2>/dev/null || true)"
if echo "$body" | grep -q '"status":"Healthy"'; then ok "api /health/ready Healthy (veritabani, depolama, image-worker)"; else bad "api /health/ready Healthy degil"; fi

# 4) Veritabanlari: harmanlama, RCSI, uyumluluk 160; migration'lar uygulanmis
dbs="$(sql sa MSSQL_SA_PASSWORD master "SELECT COUNT(*) FROM sys.databases WHERE name IN (N'sanalprova_dev', N'sanalprova_test', N'sanalprova_e2e') AND collation_name = N'Latin1_General_100_CI_AS_SC' AND is_read_committed_snapshot_on = 1 AND compatibility_level = 160" | tail -n1 | tr -d '[:space:]')"
if [ "$dbs" = 3 ]; then ok "3 veritabani: harmanlama Latin1_General_100_CI_AS_SC, RCSI acik, uyumluluk 160"; else bad "veritabani ayarlari beklenen gibi degil (eslesen: ${dbs:-?})"; fi
for db in sanalprova_dev sanalprova_test sanalprova_e2e; do
  n="$(sql sa MSSQL_SA_PASSWORD "$db" "SELECT COUNT(*) FROM dbo.__ef_migrations_history" | tail -n1 | tr -d '[:space:]')"
  if [[ "$n" =~ ^[0-9]+$ ]] && [ "$n" -gt 0 ]; then ok "$db: $n migration uygulandi"; else bad "$db: migration sayisi okunamadi/0 ($n)"; fi
done

# 5) Ag yuzeyi: image-worker ana makineye acik degil; yayimlanan portlar yalnizca loopback
if [ -z "$(docker compose port image-worker 8090 2>/dev/null)" ]; then ok "image-worker ana makineye acik degil"; else bad "image-worker ana makineye acilmis"; fi
for svc in sqlserver api; do
  cid="$(docker compose ps -q "$svc" 2>/dev/null | head -n1)"
  hosts="$(docker inspect -f '{{range $p, $b := .NetworkSettings.Ports}}{{range $b}}{{.HostIp}} {{end}}{{end}}' "$cid" 2>/dev/null)"
  if [ -n "$hosts" ] && [ -z "$(echo "$hosts" | tr ' ' '\n' | grep -v '^$' | grep -v '^127\.0\.0\.1$')" ]; then ok "$svc yalnizca 127.0.0.1 uzerinde yayimlaniyor"; else bad "$svc dis arayuze yayimlaniyor veya port bilgisi okunamadi ($hosts)"; fi
done

# 6) Sertlestirme: uygulama hesabi okuyabilir ama migration gecmisini / mutabakat tablosunu degistiremez
r="$(sql sanalprova_app SP_APP_PW sanalprova_test "SELECT COUNT(*) FROM dbo.__ef_migrations_history" | tail -n1 | tr -d '[:space:]')"
if [[ "$r" =~ ^[0-9]+$ ]]; then ok "sanalprova_app migration gecmisini okuyabiliyor"; else bad "sanalprova_app okuma basarisiz"; fi
d="$(sql sanalprova_app SP_APP_PW sanalprova_test "DELETE FROM dbo.__ef_migrations_history")"
if echo "$d" | grep -qi "permission"; then ok "sanalprova_app migration gecmisini SILEMIYOR (DENY etkin)"; else bad "sanalprova_app migration gecmisini silebildi veya beklenen hata alinmadi"; fi
u="$(sql sanalprova_app SP_APP_PW sanalprova_test "UPDATE dbo.ai_cost_reconciliations SET decision = decision")"
if echo "$u" | grep -qi "permission"; then ok "sanalprova_app mutabakat kayitlarini GUNCELLEYEMIYOR (append-only DENY etkin)"; else bad "mutabakat tablosunda UPDATE reddedilmedi"; fi

# 7) Uretilen gizli degerler gunluklerde gecmiyor
logs="$(docker compose logs --no-color 2>&1 || true)"
leak=0
for v in "$MSSQL_SA_PASSWORD" "$SP_MIGRATOR_PW" "$SP_APP_PW" "$STORAGE_SIGNING_KEY" "$IMAGE_WORKER_KEY"; do
  if [ -n "$v" ] && grep -qF -- "$v" <<<"$logs"; then leak=1; fi
done
if [ "$leak" = 0 ]; then ok "compose gunluklerinde gizli deger yok"; else bad "compose gunluklerinde gizli deger bulundu"; fi

if [ "$fail" = 0 ]; then echo "SMOKE TAMAM"; else echo "SMOKE BASARISIZ" >&2; fi
exit "$fail"
