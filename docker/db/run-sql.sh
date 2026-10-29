#!/bin/bash
# db-provision / db-harden isleri icin ortak yurutucu (docker/db/Dockerfile imajinda, mssql-tools18 ile calisir).
# Kullanim: run-sql.sh provision | harden
#
# Asamalar: arac -> baglanti (sqlcmd girisi) -> hazirlik (sunucu baslangic islemleri GERCEKTEN bitti mi) -> provision|harden (betik, 1205'te sinirli yeniden deneme)
#
# NEDEN ayri 'hazirlik' asamasi (CI #3: Msg 1205 deadlock victim): SQL Server sysadmin (sa) girisini sistem veritabanlari kurulurken/yukseltilirken
# (ilk acilis, harmanlama degisikligi sonrasi msdb/model betikleri) de kabul eder. 'SELECT 1' ve TCP healthcheck'i "hazir" demek degildir; bu surede
# CREATE/ALTER DATABASE sistem islemleriyle kilit catismasina girebilir. Hazirlik kosulu (hepsi saglanmali, sabit bekleme YOK):
#   recovery = hata gunlugunde "Recovery is complete" (sp_readerrorlog)   online = tum veritabanlari ONLINE   msdb = msdb ONLINE
#
# Yeniden deneme: YALNIZCA Msg 1205 (deadlock kurbani) icin, sinirli (DEADLOCK_MAX_TRIES, varsayilan 5; ustel geri cekilme, en fazla 20 sn),
# her denemeden once hazirlik yeniden denetlenir. Betikler idempotenttir (IF DB_ID IS NULL / IF SUSER_ID IS NULL / ALTER ... SET / ALTER ROLE ADD MEMBER /
# DENY), tum betik bastan calistirilabilir. Baska hatalar (or. sozdizimi, yetki) YENIDEN DENENMEZ.
#
# Hata kodlari (asama bilgisiyle stderr'e; gizli degerler ASLA yazdirilmaz, sqlcmd ciktisi arindirilir):
#   2  bilinmeyen eylem          10 gerekli ortam degiskeni yok (yalniz adi)      20 sqlcmd bulunamadi
#   21 TCP baglantisi kurulamadi 22 TCP acik ama sqlcmd girisi/TLS basarisiz      23 sunucu baslangic islemleri tamamlanmadi (hazirlik)
#   30 provision.sql basarisiz (1205 tukenmesi dahil)                              31 sertlestirme betigi basarisiz
# Parolalar komut satirina verilmez: sqlcmd betik degiskenlerini ($(SP_MIGRATOR_PW) vb.) ortam degiskenlerinden okur; SA parolasi SQLCMDPASSWORD ile.
set -uo pipefail

SQLCMD="${SQLCMD:-/opt/mssql-tools18/bin/sqlcmd}"
HOST="${MSSQL_HOST:-sqlserver}"
PORT="${MSSQL_PORT:-1433}"
WAIT_ATTEMPTS="${WAIT_ATTEMPTS:-60}"            # sqlcmd girisi icin
WAIT_SLEEP="${WAIT_SLEEP:-2}"
READY_ATTEMPTS="${READY_ATTEMPTS:-90}"          # hazirlik kosulu icin (varsayilan en fazla 180 sn)
READY_SLEEP="${READY_SLEEP:-2}"
READY_RECHECK_ATTEMPTS="${READY_RECHECK_ATTEMPTS:-30}"   # her 1205 yeniden denemesinden once
DEADLOCK_MAX_TRIES="${DEADLOCK_MAX_TRIES:-5}"   # toplam deneme sayisi (ilk dahil)
DEADLOCK_BACKOFF="${DEADLOCK_BACKOFF:-2}"       # ilk bekleme (sn); her denemede iki katina cikar, en fazla 20

# Hazirlik sorgusu: recovery | online | msdb | sunucu calisma suresi (sn). sa (sysadmin) gerektirir; hata -> hazir degil sayilir.
READY_SQL="SET NOCOUNT ON; DECLARE @l TABLE (d datetime, p nvarchar(max), t nvarchar(max)); BEGIN TRY INSERT @l EXEC sys.sp_readerrorlog 0, 1, N'Recovery is complete'; END TRY BEGIN CATCH DECLARE @e int = ERROR_NUMBER(); END CATCH; SELECT CASE WHEN EXISTS (SELECT 1 FROM @l) THEN 1 ELSE 0 END, CASE WHEN NOT EXISTS (SELECT 1 FROM sys.databases WHERE state <> 0) THEN 1 ELSE 0 END, CASE WHEN DATABASEPROPERTYEX(N'msdb', N'Status') = N'ONLINE' THEN 1 ELSE 0 END, (SELECT DATEDIFF(SECOND, sqlserver_start_time, SYSDATETIME()) FROM sys.dm_os_sys_info);"

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

# 1) Baglanti: sqlcmd girisi (hata ciktisi YUTULMAZ, son denemeninki raporlanir)
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
echo "[asama=baglanti] giris basarili (deneme $i)"

# 2) Hazirlik: sunucu baslangic islemleri bitti mi (gercek kosul; sabit sleep yok)
ready_flags=""; ready_out=""
check_ready() { # 0 = hazir; ready_flags = recovery|online|msdb|uptime_sn
  local out code line
  out="$(run -l 5 -h -1 -W -s '|' -Q "$READY_SQL" 2>&1)"; code=$?
  ready_out="$out"
  if [ "$code" != 0 ]; then ready_flags="sorgu-hatasi"; return 1; fi
  line="$(printf '%s\n' "$out" | grep -E '^[01]\|[01]\|[01]\|-?[0-9]+$' | tail -n 1)"
  ready_flags="${line:-yanit-yok}"
  case "$line" in 1\|1\|1\|*) return 0 ;; *) return 1 ;; esac
}
wait_ready() { # wait_ready <etiket> <deneme-sayisi>
  local label="$1" attempts="$2" j
  for ((j = 1; j <= attempts; j++)); do
    if check_ready; then
      echo "[asama=hazirlik] $label: hazir (recovery|online|msdb|uptime_sn=$ready_flags, deneme $j)"
      return 0
    fi
    if [ $((j % 10)) = 0 ]; then echo "[asama=hazirlik] $label: bekleniyor, deneme $j/$attempts (recovery|online|msdb|uptime_sn=$ready_flags)"; fi
    sleep "$READY_SLEEP"
  done
  return 1
}
echo "[asama=hazirlik] sunucu baslangic islemlerinin bitmesi bekleniyor (en fazla $((READY_ATTEMPTS * READY_SLEEP)) sn)..."
if ! wait_ready ilk "$READY_ATTEMPTS"; then
  echo "[asama=hazirlik] son durum (recovery|online|msdb|uptime_sn=$ready_flags); son sorgu ciktisi:" >&2
  redact "$ready_out" | sed 's/^/    /' >&2; echo >&2
  die 23 hazirlik "SQL Server baslangic islemleri tamamlanmadi (recovery|online|msdb|uptime_sn=$ready_flags): sistem veritabanlari hazir olmadan provision calistirilmadi"
fi

# 3) Betik calistirici: ciktiyi arindirarak yazar; YALNIZCA Msg 1205'te sinirli yeniden deneme (betikler idempotent)
deadlock_exhausted=0
run_script() { # run_script <dosya> [sqlcmd ek argumanlari]; basarisizlikta sqlcmd cikis kodunu dondurur
  local file="$1"; shift
  local try=1 out code delay
  while :; do
    out="$(run "$@" -i "$file" 2>&1)"; code=$?
    if [ -n "$out" ]; then redact "$out"; echo; fi
    if [ "$code" = 0 ]; then return 0; fi
    if printf '%s' "$out" | grep -q 'Msg 1205,'; then
      if [ "$try" -lt "$DEADLOCK_MAX_TRIES" ]; then
        delay=$((DEADLOCK_BACKOFF * (1 << (try - 1)))); if [ "$delay" -gt 20 ]; then delay=20; fi
        echo "[asama=yeniden-deneme] Msg 1205 (deadlock kurbani): deneme $try/$DEADLOCK_MAX_TRIES basarisiz; $delay sn sonra betik bastan calistirilacak (betik idempotent): $(basename "$file")"
        sleep "$delay"
        wait_ready "yeniden deneme oncesi" "$READY_RECHECK_ATTEMPTS" || echo "[asama=hazirlik] uyari: hazirlik kosulu yeniden saglanamadi (recovery|online|msdb|uptime_sn=$ready_flags); yine de deneniyor"
        try=$((try + 1))
        continue
      fi
      deadlock_exhausted=1
    fi
    return "$code"
  done
}
fail_message() { # fail_message <betik-adi>
  if [ "$deadlock_exhausted" = 1 ]; then echo "$1 basarisiz: Msg 1205 (deadlock) $DEADLOCK_MAX_TRIES denemede de surdu (yukaridaki sqlcmd ciktilarina bakin)"; else echo "$1 basarisiz (yukaridaki sqlcmd ciktisina bakin)"; fi
}

case "$action" in
  provision)
    echo "[asama=provision] provision.sql calistiriliyor"
    run_script /docker/db/provision.sql || die 30 provision "$(fail_message provision.sql)"
    echo "provision tamam."
    ;;
  harden)
    for db in sanalprova_dev sanalprova_test sanalprova_e2e; do
      echo "[asama=harden] $db"
      run_script /sql/harden-migration-history.sql -v Database="$db" || die 31 "harden:$db" "$(fail_message harden-migration-history.sql)"
      run_script /sql/harden-append-only.sql -v Database="$db" || die 31 "harden:$db" "$(fail_message harden-append-only.sql)"
    done
    echo "harden tamam."
    ;;
esac
