#!/bin/bash
# Sanal Prova operasyon komutları (Pilot Faz A): ilk kurulum, sertleştirme, yedek, doğrulama, saklama, geri yükleme. Üretim ops imajının giriş noktasıdır
# (docker compose --profile ops run --rm ops <komut>); aynı betik, SQLCMD/yol ortam değişkenleriyle yerelde de sınanır (deploy/tests/ops-selftest.sh).
#
# KOMUTLAR
#   provision | harden | verify-prod | tls-check            İlk kurulum / en az yetki sertleştirmesi / yetki doğrulaması / SQL TLS doğrulaması
#   backup full | backup log                                Şifreli yedek seti (tam: veritabanı + görsel deposu + silme günlüğü; günlük: yalnızca işlem günlüğü)
#   verify <set-klasörü> [--deep]                           Sağlama toplamı + şifre çözme + (--deep) RESTORE VERIFYONLY
#   list | prune [--yes]                                    Setleri listele / saklama süresini aşanları sil (varsayılan kuru çalışma; en yeni N tam set hiç silinmez)
#   restore-db <tam-set> --target <yeni-ad> [--log <günlük-set> ...] [--stopat <zaman>] [--confirm]
#   restore-files <tam-set> --dest <boş-klasör> --confirm
#   post-restore --db <ad>                                  Karantina + tutarlılık denetimi (restore-db otomatik çağırır)
#
# GÜVENLİK: yedekler gpg simetrik (AES256, bütünlük korumalı) ile şifrelenir; parola dosyası (BACKUP_PASSPHRASE_FILE) 600/400 izinli olmalıdır; açık (şifresiz)
# .bak dosyaları paylaşılan SQL yedek biriminden şifrelendikten HEMEN sonra silinir. Hiçbir sır yazdırılmaz. Geri yükleme asla mevcut veritabanının/depo klasörünün
# ÜZERİNE YAZMAZ; üretime geçiş bilinçli, elle bir adımdır (docs/19).
set -uo pipefail

SQLCMD="${SQLCMD:-/opt/mssql-tools18/bin/sqlcmd}"
HOST="${MSSQL_HOST:-sqlserver}"
PORT="${MSSQL_PORT:-1433}"
SQL_DIR="${SQL_DIR:-/sql}"
SQL_BACKUP_DIR="${SQL_BACKUP_DIR:-/var/opt/mssql/backup}"
BACKUP_DIR="${BACKUP_DIR:-/backups}"
STORAGE_DIR="${STORAGE_DIR:-/data/storage}"
JOURNAL_FILE="${JOURNAL_FILE:-/data/journal/deleted-sessions.log}"
PASS_FILE="${BACKUP_PASSPHRASE_FILE:-/run/secrets/backup_passphrase}"
DB="${DB_NAME:-}"
KEEP_FULL_MIN="${BACKUP_KEEP_FULL_MIN:-3}"
RET_FULL="${BACKUP_RETENTION_FULL_DAYS:-14}"
RET_LOG="${BACKUP_RETENTION_LOG_DAYS:-7}"
PROTECTED_RE='^(sanalprova_dev|sanalprova_test|sanalprova_e2e|master|tempdb|model|msdb)$'

# Özel (700) klasör; yalnızca NTFS üzerindeki yerel sınamada (OPS_SKIP_PERM_CHECK=1) chmod hatası yok sayılır
mkdir_private() { mkdir -p "$1" && { chmod 700 "$1" 2> /dev/null || [ "${OPS_SKIP_PERM_CHECK:-0}" = 1 ]; }; }
log() { printf '[ops] %s\n' "$*"; }
die() { printf '[ops HATA] %s\n' "$2" >&2; exit "$1"; }

# ----------------------------------------------------------------- yardımcılar

need() { local v; for v in "$@"; do [ -n "${!v:-}" ] || die 10 "gerekli ortam değişkeni yok: $v"; done; }

# sqlcmd: sertifika DOĞRULANIR (-C yok). Kırılma durumu için OPS_SQL_TRUST_SERVER_CERT=1 (yalnızca bilinçli, geçici; runbook'ta uyarı var).
sql_file() { # sql_file <login> <parola> <dosya> [KEY=VALUE ...]
  local login="$1" password="$2" file="$3"; shift 3
  local args=(-S "$HOST,$PORT" -U "$login" -b -i "$SQL_DIR/$file")
  [ "${OPS_SQL_TRUST_SERVER_CERT:-0}" = 1 ] && args+=(-C)
  if [ $# -gt 0 ]; then args+=(-v "$@"); fi
  SQLCMDPASSWORD="$password" "$SQLCMD" "${args[@]}"
}
sql_query() { # sql_query <login> <parola> <sorgu>
  local args=(-S "$HOST,$PORT" -U "$1" -b -Q "$3")
  [ "${OPS_SQL_TRUST_SERVER_CERT:-0}" = 1 ] && args+=(-C)
  SQLCMDPASSWORD="$2" "$SQLCMD" "${args[@]}"
}

wait_sql() {
  local i
  for ((i = 1; i <= ${OPS_WAIT_ATTEMPTS:-60}; i++)); do
    if sql_query sa "$MSSQL_SA_PASSWORD" "SELECT 1" > /dev/null 2>&1; then return 0; fi
    sleep "${OPS_WAIT_SLEEP:-2}"
  done
  die 21 "SQL Server'a sa ile bağlanılamadı (TLS sertifikası doğrulaması dahil): 'ops tls-check' çalıştırın"
}

require_passphrase() {
  [ -s "$PASS_FILE" ] || die 11 "yedek parola dosyası yok/boş: $PASS_FILE"
  if [ "${OPS_SKIP_PERM_CHECK:-0}" != 1 ]; then
    local mode; mode="$(stat -c '%a' "$PASS_FILE")"
    case "$mode" in 600|400) ;; *) die 11 "parola dosyası izni $mode; 600 veya 400 olmalı: $PASS_FILE" ;; esac
  fi
  [ "$(wc -c < "$PASS_FILE")" -ge 24 ] || die 11 "yedek parolası çok kısa (en az 24 karakter)"
}

gpg_common=(--batch --yes --quiet --pinentry-mode loopback)
encrypt() { gpg "${gpg_common[@]}" --passphrase-file "$PASS_FILE" --symmetric --cipher-algo AES256 --s2k-mode 3 --s2k-digest-algo SHA512 --s2k-count 65011712 -o "$1"; } # stdin → dosya
decrypt() { gpg "${gpg_common[@]}" --passphrase-file "$PASS_FILE" --decrypt "$1"; }                                                                                     # dosya → stdout

timestamp() { date -u +%Y%m%dT%H%M%SZ; }
epoch_of() { local t="$1"; date -u -d "${t:0:4}-${t:4:2}-${t:6:2} ${t:9:2}:${t:11:2}:${t:13:2}" +%s; }

write_manifest() { # write_manifest <klasör> <tür> <ts> <veritabanı> <taban-tam-set>
  local dir="$1" kind="$2" ts="$3" db="$4" base="$5" f
  ( cd "$dir" && sha256sum -- *.gpg > manifest.sha256 )
  {
    printf '{\n  "kind": "%s",\n  "createdUtc": "%s",\n  "database": "%s",\n  "baseFullSet": "%s",\n  "files": [' "$kind" "$ts" "$db" "$base"
    local first=1
    for f in "$dir"/*.gpg; do
      [ $first = 1 ] && first=0 || printf ','
      printf '\n    {"name": "%s", "bytes": %s}' "$(basename "$f")" "$(wc -c < "$f")"
    done
    printf '\n  ]\n}\n'
  } > "$dir/manifest.json"
}

manifest_field() { sed -n "s/.*\"$2\": \"\\([^\"]*\\)\".*/\\1/p" "$1/manifest.json" | head -n 1; }

# ----------------------------------------------------------------- yedek

backup_set() { # backup_set full|log
  local kind="$1"; need DB_NAME SP_BACKUP_PW; require_passphrase
  DB="$DB_NAME"
  local ts final tmp bak base="-"
  ts="$(timestamp)"; final="$BACKUP_DIR/$kind/$ts"; tmp="$final.partial"
  mkdir_private "$BACKUP_DIR/$kind" || die 12 "yedek klasörü oluşturulamadı: $BACKUP_DIR/$kind"
  rm -rf "$tmp"; mkdir_private "$tmp" || die 12 "geçici set klasörü oluşturulamadı"
  trap 'rm -rf "$tmp"; [ -n "${bak:-}" ] && rm -f "$bak"' EXIT

  if [ "$kind" = full ]; then
    bak="$SQL_BACKUP_DIR/${DB}_full_${ts}.bak"
    log "tam yedek alınıyor (SQL Server → $bak)"
    sql_file sanalprova_backup "$SP_BACKUP_PW" backup-full.sql "Database=$DB" "BackupFile=$bak" || die 30 "SQL tam yedeği başarısız"
  else
    bak="$SQL_BACKUP_DIR/${DB}_log_${ts}.trn"
    base="$(ls -1 "$BACKUP_DIR/full" 2>/dev/null | grep -E '^[0-9]{8}T[0-9]{6}Z$' | sort | tail -n 1)"
    [ -n "$base" ] || die 30 "günlük yedeği için önce bir TAM yedek seti gerekir"
    log "günlük yedeği alınıyor (taban tam set: $base)"
    sql_file sanalprova_backup "$SP_BACKUP_PW" backup-log.sql "Database=$DB" "BackupFile=$bak" || die 30 "SQL günlük yedeği başarısız"
  fi

  # Açık (şifresiz) dosya: gzip + gpg ile şifrelenir, ardından paylaşılan birimden SİLİNİR
  local name="db.bak.gz.gpg"; [ "$kind" = log ] && name="log.trn.gz.gpg"
  gzip -c "$bak" | encrypt "$tmp/$name" || die 31 "veritabanı yedeği şifrelenemedi"
  rm -f "$bak"; bak=""

  if [ "$kind" = full ]; then
    log "görsel deposu arşivleniyor (veritabanı yedeğinden SONRA: depo ⊇ veritabanının bildiği dosyalar)"
    # tar, yazılan dosyalar nedeniyle 1 ("dosya değişti") dönebilir: bu bir hata değildir (2+ hatadır)
    set +o pipefail
    tar --warning=no-file-changed -C "$STORAGE_DIR" -cf - . | gzip | encrypt "$tmp/storage.tar.gz.gpg"
    local rc=("${PIPESTATUS[@]}"); set -o pipefail
    [ "${rc[0]}" -le 1 ] && [ "${rc[1]}" -eq 0 ] && [ "${rc[2]}" -eq 0 ] || die 32 "depo arşivi başarısız (tar=${rc[0]}, gzip=${rc[1]}, gpg=${rc[2]})"
    if [ -f "$JOURNAL_FILE" ]; then
      encrypt "$tmp/journal.gpg" < "$JOURNAL_FILE" || die 33 "silme günlüğü şifrelenemedi"
    else
      log "UYARI: silme günlüğü bulunamadı ($JOURNAL_FILE); yedeğe eklenmedi"
    fi
  fi

  write_manifest "$tmp" "$kind" "$ts" "$DB" "$base"
  verify_dir "$tmp" quick || die 34 "yeni yedek seti doğrulanamadı; set atıldı"
  mv "$tmp" "$final" || die 35 "set son konumuna taşınamadı"
  trap - EXIT
  log "TAMAM: $kind yedek seti hazır: $final"
}

# ----------------------------------------------------------------- doğrulama

verify_dir() { # verify_dir <klasör> [quick|deep]
  local dir="$1" mode="${2:-quick}" f
  [ -f "$dir/manifest.json" ] && [ -f "$dir/manifest.sha256" ] || { log "manifest yok: $dir"; return 1; }
  ( cd "$dir" && sha256sum --quiet -c manifest.sha256 ) || { log "SAĞLAMA TOPLAMI UYUŞMUYOR (dosya bozulmuş/değiştirilmiş): $dir"; return 1; }
  for f in "$dir"/*.gpg; do
    case "$(basename "$f")" in
      db.bak.gz.gpg|log.trn.gz.gpg) decrypt "$f" | gzip -t || { log "şifre çözme/gzip bütünlüğü BAŞARISIZ: $f"; return 1; } ;;
      storage.tar.gz.gpg) decrypt "$f" | gzip -dc | tar -tf - > /dev/null || { log "depo arşivi okunamadı: $f"; return 1; } ;;
      *) decrypt "$f" > /dev/null || { log "şifre çözme BAŞARISIZ: $f"; return 1; } ;;
    esac
  done
  if [ "$mode" = deep ]; then
    need MSSQL_SA_PASSWORD
    for f in "$dir"/db.bak.gz.gpg "$dir"/log.trn.gz.gpg; do
      [ -f "$f" ] || continue
      local tmp="$SQL_BACKUP_DIR/verify_$(timestamp).tmp"
      decrypt "$f" | gzip -dc > "$tmp" || { rm -f "$tmp"; return 1; }
      sql_query sa "$MSSQL_SA_PASSWORD" "RESTORE VERIFYONLY FROM DISK = N'$tmp' WITH CHECKSUM" > /dev/null
      local rc=$?; rm -f "$tmp"
      [ $rc = 0 ] || { log "RESTORE VERIFYONLY BAŞARISIZ: $f"; return 1; }
    done
  fi
  log "doğrulandı ($mode): $dir"
}

list_sets() {
  local kind d
  for kind in full log; do
    for d in $(ls -1 "$BACKUP_DIR/$kind" 2>/dev/null | grep -E '^[0-9]{8}T[0-9]{6}Z$' | sort); do
      [ -f "$BACKUP_DIR/$kind/$d/manifest.json" ] && printf '%s\t%s\t%s\n' "$kind" "$d" "$(du -sk "$BACKUP_DIR/$kind/$d" | cut -f1)KB"
    done
  done
}

prune_sets() { # prune_sets [--yes]
  local yes=0 now kind d age limit full_count
  [ "${1:-}" = "--yes" ] && yes=1
  now="$(date -u +%s)"
  for kind in full log; do
    limit=$RET_LOG; [ $kind = full ] && limit=$RET_FULL
    local sets; sets=($(ls -1 "$BACKUP_DIR/$kind" 2>/dev/null | grep -E '^[0-9]{8}T[0-9]{6}Z$' | sort))
    full_count=${#sets[@]}
    local idx=0
    for d in "${sets[@]}"; do
      idx=$((idx + 1))
      age=$(( (now - $(epoch_of "$d")) / 86400 ))
      # En yeni KEEP_FULL_MIN tam set ne olursa olsun korunur (tek bozuk yedekte geri dönüş olsun)
      if [ $kind = full ] && [ $((full_count - idx)) -lt "$KEEP_FULL_MIN" ]; then continue; fi
      if [ "$age" -gt "$limit" ]; then
        if [ $yes = 1 ]; then rm -rf "${BACKUP_DIR:?}/$kind/$d"; log "silindi: $kind/$d ($age gün)"; else log "silinecek (kuru çalışma): $kind/$d ($age gün)"; fi
      fi
    done
  done
  [ $yes = 1 ] || log "KURU ÇALIŞMA: gerçekten silmek için: prune --yes"
}

# ----------------------------------------------------------------- geri yükleme

restore_db() {
  need MSSQL_SA_PASSWORD; require_passphrase
  local set="" target="" stopat="" confirm=0 logs=()
  set="${1:-}"; shift || true
  while [ $# -gt 0 ]; do
    case "$1" in
      --target) target="${2:-}"; shift 2 ;;
      --log) logs+=("${2:-}"); shift 2 ;;
      --stopat) stopat="${2:-}"; shift 2 ;;
      --confirm) confirm=1; shift ;;
      *) die 2 "bilinmeyen seçenek: $1" ;;
    esac
  done
  [ -d "$set" ] && [ "$(manifest_field "$set" kind)" = full ] || die 2 "geçerli bir TAM yedek seti klasörü verin"
  [[ "$target" =~ ^[A-Za-z0-9_]{3,100}$ ]] || die 2 "--target yalnızca harf/rakam/alt çizgi (3–100) olmalı"
  [[ "$target" =~ $PROTECTED_RE ]] && die 2 "--target korumalı bir ad"
  local source_db; source_db="$(manifest_field "$set" database)"
  [ "$target" != "$source_db" ] || die 2 "--target üretim veritabanı adı olamaz: yeni bir ad verin (üretime geçiş elle yapılır)"
  local log_dir
  for log_dir in "${logs[@]}"; do [ "$(manifest_field "$log_dir" kind)" = log ] || die 2 "geçerli bir günlük seti değil: $log_dir"; done

  log "PLAN: '$set' (kaynak veritabanı: $source_db) → YENİ veritabanı '$target'; günlük seti: ${#logs[@]}; zaman: ${stopat:-son}"
  verify_dir "$set" quick || die 40 "tam set doğrulanamadı"
  for log_dir in "${logs[@]}"; do verify_dir "$log_dir" quick || die 40 "günlük seti doğrulanamadı: $log_dir"; done
  if [ $confirm != 1 ]; then log "KURU ÇALIŞMA: --confirm verilmedi, hiçbir şey geri yüklenmedi."; return 0; fi

  wait_sql
  local tmp_full="$SQL_BACKUP_DIR/restore_${target}_full.tmp" recovery=RECOVERY
  [ ${#logs[@]} -gt 0 ] && recovery=NORECOVERY
  trap 'rm -f "$SQL_BACKUP_DIR"/restore_'"$target"'_*.tmp' EXIT
  decrypt "$set/db.bak.gz.gpg" | gzip -dc > "$tmp_full" || die 41 "tam yedek çözülemedi"
  sql_file sa "$MSSQL_SA_PASSWORD" restore.sql "SourceDatabase=$source_db" "TargetDatabase=$target" "BackupFile=$tmp_full" "Recovery=$recovery" || die 42 "tam yedek geri yüklenemedi"
  rm -f "$tmp_full"
  local i=0 n=${#logs[@]} rec
  for log_dir in "${logs[@]}"; do
    i=$((i + 1)); rec=NORECOVERY; [ $i = $n ] && rec=RECOVERY
    local tmp_log="$SQL_BACKUP_DIR/restore_${target}_log$i.tmp"
    decrypt "$log_dir/log.trn.gz.gpg" | gzip -dc > "$tmp_log" || die 41 "günlük yedeği çözülemedi: $log_dir"
    sql_file sa "$MSSQL_SA_PASSWORD" restore-log.sql "TargetDatabase=$target" "BackupFile=$tmp_log" "Recovery=$rec" "StopAt=$stopat" || die 43 "günlük yedeği uygulanamadı: $log_dir"
    rm -f "$tmp_log"
  done
  trap - EXIT
  post_restore "$target"
  log "GERİ YÜKLEME TAMAM: '$target'. ÜRETİME GEÇİŞ BU KOMUTLA YAPILMAZ: docs/19 §geri yükleme adımları (API'yi durdur → reapply-deletions → ad değiştir/bağlantıyı çevir)."
}

post_restore() { # post_restore <veritabanı>
  local db="$1"
  [[ "$db" =~ ^[A-Za-z0-9_]{3,100}$ ]] && ! [[ "$db" =~ $PROTECTED_RE ]] || die 2 "geçersiz/korumalı veritabanı adı"
  need MSSQL_SA_PASSWORD; wait_sql
  sql_file sa "$MSSQL_SA_PASSWORD" post-restore-quarantine.sql "Database=$db" || die 44 "karantina betiği başarısız"
  sql_file sa "$MSSQL_SA_PASSWORD" post-restore-check.sql "Database=$db" "RunCheckDb=${RUN_CHECKDB:-1}" || die 45 "geri yükleme sonrası tutarlılık denetimi BAŞARISIZ: veritabanı üretime alınmamalı"
}

restore_files() {
  require_passphrase
  local set="${1:-}" dest="" confirm=0; shift || true
  while [ $# -gt 0 ]; do
    case "$1" in --dest) dest="${2:-}"; shift 2 ;; --confirm) confirm=1; shift ;; *) die 2 "bilinmeyen seçenek: $1" ;; esac
  done
  [ -d "$set" ] && [ "$(manifest_field "$set" kind)" = full ] || die 2 "geçerli bir TAM yedek seti klasörü verin"
  [ -n "$dest" ] || die 2 "--dest zorunlu"
  local abs_dest abs_storage; abs_dest="$(readlink -m "$dest")"; abs_storage="$(readlink -m "$STORAGE_DIR")"
  case "$abs_dest/" in "$abs_storage"/*) die 2 "--dest canlı depo klasörü veya altı olamaz (üzerine yazma yok)" ;; esac
  if [ -e "$dest" ] && [ -n "$(ls -A "$dest" 2>/dev/null)" ]; then die 2 "--dest boş değil: üzerine yazılmaz"; fi
  log "PLAN: '$set' deposu ve silme günlüğü → $dest"
  verify_dir "$set" quick || die 40 "set doğrulanamadı"
  if [ $confirm != 1 ]; then log "KURU ÇALIŞMA: --confirm verilmedi."; return 0; fi
  mkdir -p "$dest/storage" || die 12 "hedef oluşturulamadı"
  decrypt "$set/storage.tar.gz.gpg" | gzip -dc | tar -C "$dest/storage" -xf - || die 46 "depo açılamadı"
  if [ -f "$set/journal.gpg" ]; then decrypt "$set/journal.gpg" > "$dest/deleted-sessions.log" || die 46 "silme günlüğü açılamadı"; fi
  log "TAMAM: depo '$dest/storage', silme günlüğü '$dest/deleted-sessions.log' (günlük, canlı günlükle BİRLEŞTİRİLİR; üzerine yazılmaz: docs/19)"
}

# ----------------------------------------------------------------- kurulum

case "${1:-}" in
  provision)
    need MSSQL_SA_PASSWORD DB_NAME SP_MIGRATOR_PW SP_APP_PW SP_BACKUP_PW; wait_sql
    sql_file sa "$MSSQL_SA_PASSWORD" provision-production.sql "Database=$DB_NAME" "SP_MIGRATOR_PW=$SP_MIGRATOR_PW" "SP_APP_PW=$SP_APP_PW" "SP_BACKUP_PW=$SP_BACKUP_PW" || die 50 "provision-production.sql başarısız"
    log "provision TAMAM" ;;
  harden)
    need MSSQL_SA_PASSWORD DB_NAME; wait_sql
    sql_file sa "$MSSQL_SA_PASSWORD" harden-production.sql "Database=$DB_NAME" "AllowLoginlessUsers=0" || die 51 "harden-production.sql başarısız"
    sql_file sa "$MSSQL_SA_PASSWORD" verify-production.sql "Database=$DB_NAME" || die 52 "üretim yetki doğrulaması BAŞARISIZ"
    log "harden + verify TAMAM" ;;
  verify-prod)
    need MSSQL_SA_PASSWORD DB_NAME; wait_sql
    sql_file sa "$MSSQL_SA_PASSWORD" verify-production.sql "Database=$DB_NAME" || die 52 "üretim yetki doğrulaması BAŞARISIZ" ;;
  tls-check)
    need MSSQL_SA_PASSWORD
    [ "${OPS_SQL_TRUST_SERVER_CERT:-0}" != 1 ] || die 53 "tls-check, OPS_SQL_TRUST_SERVER_CERT=1 iken anlamsızdır (sertifika doğrulanmıyor)"
    out="$(sql_query sa "$MSSQL_SA_PASSWORD" "SET NOCOUNT ON; SELECT CAST(encrypt_option AS nvarchar(10)) FROM sys.dm_exec_connections WHERE session_id = @@SPID" 2>&1)" \
      || die 53 "SQL TLS bağlantısı kurulamadı (sertifika zinciri/ana makine adı doğrulanamadı): $(printf '%s' "$out" | head -n 3 | tr '\n' ' ')"
    printf '%s' "$out" | grep -qi 'TRUE' || die 53 "bağlantı şifreli görünmüyor: $out"
    log "SQL TLS TAMAM: bağlantı şifreli ve sertifika özel CA ile doğrulandı" ;;
  backup)
    case "${2:-}" in full|log) backup_set "$2" ;; *) die 2 "kullanım: backup full|log" ;; esac ;;
  verify)
    require_passphrase
    [ -d "${2:-}" ] || die 2 "kullanım: verify <set-klasörü> [--deep]"
    verify_dir "$2" "$([ "${3:-}" = "--deep" ] && echo deep || echo quick)" || die 47 "doğrulama BAŞARISIZ" ;;
  list) list_sets ;;
  prune) prune_sets "${2:-}" ;;
  restore-db) shift; restore_db "$@" ;;
  restore-files) shift; restore_files "$@" ;;
  post-restore)
    [ "${2:-}" = "--db" ] || die 2 "kullanım: post-restore --db <ad>"
    post_restore "${3:-}" ;;
  *) sed -n '2,15p' "$0"; exit 2 ;;
esac
