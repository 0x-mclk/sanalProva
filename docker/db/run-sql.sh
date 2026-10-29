#!/bin/bash
# db-provision / db-harden isleri icin ortak yurutucu (docker/db/Dockerfile imajinda, mssql-tools18 ile calisir).
# Kullanim: run-sql.sh provision | harden
#
# Hata kodlari (asama bilgisiyle birlikte stderr'e yazilir; gizli degerler ASLA yazdirilmaz, sqlcmd ciktisi arindirilir):
#   2  bilinmeyen eylem          10 gerekli ortam degiskeni yok (yalniz adi yazilir)   20 sqlcmd bulunamadi
#   21 TCP baglantisi kurulamadi 22 TCP acik ama sqlcmd girisi/TLS basarisiz           30 provision.sql basarisiz
#   31 sertlestirme betigi basarisiz
# Parolalar komut satirina verilmez: sqlcmd betik degiskenlerini ($(SP_MIGRATOR_PW) vb.) ortam degiskenlerinden okur; SA parolasi SQLCMDPASSWORD ile.
set -uo pipefail

SQLCMD="${SQLCMD:-/opt/mssql-tools18/bin/sqlcmd}"
HOST="${MSSQL_HOST:-sqlserver}"
PORT="${MSSQL_PORT:-1433}"
WAIT_ATTEMPTS="${WAIT_ATTEMPTS:-60}"
WAIT_SLEEP="${WAIT_SLEEP:-2}"

# Bilinen gizli degerleri *** ile degistirir
redact() {
  local s="$1" v
  for v in "${MSSQL_SA_PASSWORD:-}" "${SP_MIGRATOR_PW:-}" "${SP_APP_PW:-}"; do
    if [ -n "$v" ]; then s="${s//"$v"/***}"; fi
  done
  printf '%s' "$s"
}
die() { # die <kod> <asama> <mesaj>
  echo "[HATA kod=$1 asama=$2] $3" >&2
  exit "$1"
}

action="${1:-}"
case "$action" in
  provision|harden) ;;
  *) die 2 baslangic "bilinmeyen eylem: '${action:-<bos>}' (provision | harden)" ;;
esac

# Ortam degiskenleri: yalnizca ADLARI raporlanir
[ -n "${MSSQL_SA_PASSWORD:-}" ] || die 10 ortam "MSSQL_SA_PASSWORD tanimli degil"
if [ "$action" = provision ]; then
  for var in SP_MIGRATOR_PW SP_APP_PW; do
    [ -n "${!var:-}" ] || die 10 ortam "$var tanimli degil"
  done
  export SP_MIGRATOR_PW SP_APP_PW
fi
export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"

# Arac: yol ve surum (kanit icin bir kez yazdirilir)
[ -x "$SQLCMD" ] || die 20 arac "sqlcmd bulunamadi veya calistirilabilir degil: $SQLCMD"
echo "[asama=arac] $("$SQLCMD" -? 2>&1 | sed -n '1,2p' | tr '\n' ' ')"

run() { "$SQLCMD" -S "$HOST,$PORT" -U sa -C -I -b "$@"; }

# Baglanti bekleme: hata ciktisi YUTULMAZ, son denemeninki raporlanir
echo "[asama=baglanti] SQL Server bekleniyor ($HOST:$PORT, en fazla $((WAIT_ATTEMPTS * WAIT_SLEEP)) sn)..."
ready=0; last_out=""; last_code=0
for ((i = 1; i <= WAIT_ATTEMPTS; i++)); do
  last_out="$(run -l 5 -Q "SELECT 1" 2>&1)"; last_code=$?
  if [ "$last_code" = 0 ]; then ready=1; break; fi
  if [ $((i % 10)) = 0 ]; then echo "[asama=baglanti] deneme $i/$WAIT_ATTEMPTS: sqlcmd cikis=$last_code"; fi
  sleep "$WAIT_SLEEP"
done
if [ "$ready" != 1 ]; then
  if ( exec 3<>"/dev/tcp/$HOST/$PORT" ) 2>/dev/null; then tcp=acik; else tcp=kapali; fi
  echo "[asama=baglanti] son sqlcmd ciktisi (cikis=$last_code, tcp=$tcp):" >&2
  redact "$last_out" | sed 's/^/    /' >&2; echo >&2
  if [ "$tcp" = acik ]; then die 22 baglanti "TCP acik ama sqlcmd girisi/TLS basarisiz (yukaridaki son hata)"; fi
  die 21 baglanti "TCP baglantisi kurulamadi ($HOST:$PORT)"
fi
echo "[asama=baglanti] hazir (deneme $i)"

# Betik calistirici: ciktiyi arindirarak yazar, basarisizlikta kodu dondurur
run_script() { # run_script <dosya> [sqlcmd ek argumanlari]
  local file="$1"; shift
  local out code
  out="$(run "$@" -i "$file" 2>&1)"; code=$?
  if [ -n "$out" ]; then redact "$out"; echo; fi
  return "$code"
}

case "$action" in
  provision)
    echo "[asama=provision] provision.sql calistiriliyor"
    run_script /docker/db/provision.sql || die 30 provision "provision.sql basarisiz (yukaridaki sqlcmd ciktisina bakin)"
    echo "provision tamam."
    ;;
  harden)
    for db in sanalprova_dev sanalprova_test sanalprova_e2e; do
      echo "[asama=harden] $db"
      run_script /sql/harden-migration-history.sql -v Database="$db" || die 31 "harden:$db" "harden-migration-history.sql basarisiz"
      run_script /sql/harden-append-only.sql -v Database="$db" || die 31 "harden:$db" "harden-append-only.sql basarisiz"
    done
    echo "harden tamam."
    ;;
esac
