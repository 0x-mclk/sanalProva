#!/bin/bash
# Operasyon betikleri için yerel kendi kendine sınama (Pilot Faz A): Docker/SQL Server GEREKTİRMEZ. Gerçek gpg/tar/gzip/openssl kullanır; sqlcmd yerine bir SAHTE sqlcmd
# (yalnızca çağrılan betik adını/değişkenleri günlüğe yazar, yedek dosyası üretir). SQL tarafının gerçek doğrulaması backend/tests ProductionOpsLabTests'tedir (SQL Server lab).
# Kullanım: bash deploy/tests/ops-selftest.sh      Çıkış 0 = hepsi geçti.
set -uo pipefail
HERE="$(cd "$(dirname "$0")/.." && pwd)"
T="$(mktemp -d)"; trap 'rm -rf "$T"' EXIT
pass=0; failed=0
t() { # t <ad> <komut...>   komut başarılıysa geçer
  local name="$1"; shift
  if "$@" > "$T/out.txt" 2>&1; then pass=$((pass + 1)); printf '  geçti  %s\n' "$name"; else failed=$((failed + 1)); printf '  KALDI  %s\n' "$name"; sed 's/^/         | /' "$T/out.txt" | head -n 12; fi
}
tn() { # tn <ad> <komut...>  komut BAŞARISIZ olmalı
  local name="$1"; shift
  if "$@" > "$T/out.txt" 2>&1; then failed=$((failed + 1)); printf '  KALDI  %s (başarısız olmalıydı)\n' "$name"; head -n 5 "$T/out.txt" | sed 's/^/         | /'; else pass=$((pass + 1)); printf '  geçti  %s\n' "$name"; fi
}

# ------------------------------------------------------------------ ortam
export GNUPGHOME="$T/gnupg"; mkdir -p "$GNUPGHOME"; chmod 700 "$GNUPGHOME" 2> /dev/null || true
export OPS_SKIP_PERM_CHECK=1 PREFLIGHT_SKIP_PERM=1 OPS_WAIT_ATTEMPTS=2 OPS_WAIT_SLEEP=0
export SQL_DIR="$HERE/sql" SQL_BACKUP_DIR="$T/sqlbackup" BACKUP_DIR="$T/backups" STORAGE_DIR="$T/storage" JOURNAL_FILE="$T/journal/deleted-sessions.log"
export DB_NAME=sanalprova MSSQL_SA_PASSWORD="Aa1sa-sifresi-sifresi-sifresi" SP_BACKUP_PW="Aa1yedek-sifresi-sifresi-sifre" MSSQL_HOST=sqlserver
export BACKUP_PASSPHRASE_FILE="$T/pass"; export FAKE_LOG="$T/fakesql.log"
mkdir -p "$SQL_BACKUP_DIR" "$STORAGE_DIR/o1/s1/sessions/x1" "$T/journal"; : > "$FAKE_LOG"
printf 'Kq7vZ2mR9tW4yB6nE1cH8uJ3-test-yedek-parolasi' > "$BACKUP_PASSPHRASE_FILE"
head -c 5000 /dev/urandom > "$STORAGE_DIR/o1/s1/sessions/x1/photo.jpg"; echo 'katalog' > "$STORAGE_DIR/o1/catalog.txt"
printf '{"sessionId":"00000000-0000-0000-0000-000000000001"}\n' > "$JOURNAL_FILE"

cat > "$T/fake-sqlcmd" <<'FAKE'
#!/bin/bash
# Sahte sqlcmd: -i <dosya> ve -v K=V ... değişkenlerini günlüğe yazar; yedek betiklerinde BackupFile'ı üretir; restore betiklerinde dosyanın varlığını denetler.
file=""; declare -A vars; mode=""
args=("$@"); i=0
while [ $i -lt ${#args[@]} ]; do
  case "${args[$i]}" in
    -i) file="$(basename "${args[$((i + 1))]}")"; i=$((i + 2)) ;;
    -v) mode=v; i=$((i + 1)) ;;
    -S|-U|-Q) i=$((i + 2)) ;;
    -b|-C) i=$((i + 1)) ;;
    *=*) if [ "$mode" = v ]; then vars["${args[$i]%%=*}"]="${args[$i]#*=}"; fi; i=$((i + 1)) ;;
    *) i=$((i + 1)) ;;
  esac
done
[ -n "$file" ] && echo "$file|Database=${vars[Database]:-}|Target=${vars[TargetDatabase]:-}|Recovery=${vars[Recovery]:-}|StopAt=${vars[StopAt]:-}" >> "$FAKE_LOG"
case "$file" in
  backup-full.sql|backup-log.sql) head -c 20000 /dev/urandom > "${vars[BackupFile]}" ;;
  restore.sql|restore-log.sql) [ -s "${vars[BackupFile]}" ] || { echo "yedek dosyası yok/boş" >&2; exit 3; } ;;
esac
exit 0
FAKE
chmod +x "$T/fake-sqlcmd"; export SQLCMD="$T/fake-sqlcmd"
OPS="$HERE/ops/ops.sh"

# ------------------------------------------------------------------ prod-env.sh / make-sql-cert.sh / preflight.sh (kopya deploy dizininde)
D="$T/deploy"; mkdir -p "$D"; cp -r "$HERE/scripts" "$D/scripts"; cp "$HERE/docker-compose.prod.yml" "$D/"
( cd "$D" && bash scripts/prod-env.sh pilot.magaza.test > "$T/env-out.txt" 2>&1 )
t "prod-env: .env.prod ve yedek parolası oluşur" test -s "$D/.env.prod" -a -s "$D/secrets/backup_passphrase"
t "prod-env: çıktıya hiçbir sır yazılmaz" bash -c "! grep -qE 'Aa1[0-9a-f]{20}' '$T/env-out.txt' && ! grep -qF \"\$(cat '$D/secrets/backup_passphrase')\" '$T/env-out.txt'"
t "prod-env: 6 sır rastgele ve birbirinden farklı" bash -c "[ \$(grep -E '^(MSSQL_SA_PASSWORD|SP_MIGRATOR_PW|SP_APP_PW|SP_BACKUP_PW|STORAGE_SIGNING_KEY|SETUP_TOKEN_KEY)=' '$D/.env.prod' | cut -d= -f2 | sort -u | wc -l) = 6 ]"
t "prod-env: varsayılan AI kapalı (mock + ücretli çağrı kapalı)" grep -q -e '^USE_MOCK_PROVIDERS=true' -e '^ALLOW_PAID_CALLS=false' "$D/.env.prod"
tn "prod-env: var olan dosyaları ezmez" bash -c "cd '$D' && bash scripts/prod-env.sh pilot.magaza.test"
tn "prod-env: localhost/IP kabul etmez" bash -c "cd '$D' && bash scripts/prod-env.sh localhost -f"
tn "prod-env: geçersiz ad kabul etmez" bash -c "cd '$D' && bash scripts/prod-env.sh 'a b;c' -f"
( cd "$D" && bash scripts/make-sql-cert.sh > /dev/null 2>&1 )
t "make-sql-cert: sunucu sertifikası CA ile doğrulanır" openssl verify -CAfile "$D/secrets/ca/ca.crt" "$D/secrets/sqlserver-tls/server.crt"
t "make-sql-cert: SAN DNS:sqlserver" bash -c "openssl x509 -in '$D/secrets/sqlserver-tls/server.crt' -noout -text | grep -q 'DNS:sqlserver'"
tn "make-sql-cert: var olan sertifikayı ezmez" bash -c "cd '$D' && bash scripts/make-sql-cert.sh"
t "preflight: geçerli yapılandırma geçer" bash -c "cd '$D' && bash scripts/preflight.sh"
mut() { # mut <ad> <sed-ifadesi> : .env.prod kopyasını değiştirip preflight'ın BAŞARISIZ olmasını bekler
  cp "$D/.env.prod" "$D/.env.mut"; sed -i "$2" "$D/.env.mut"
  tn "preflight reddeder: $1" bash -c "cd '$D' && bash scripts/preflight.sh --env .env.mut"
}
mut "zayıf/yer tutucu sır" 's/^SP_APP_PW=.*/SP_APP_PW=Password123/'
mut "dev veritabanı adı" 's/^DB_NAME=.*/DB_NAME=sanalprova_dev/'
mut "localhost site adresi" 's/^SITE_ADDRESS=.*/SITE_ADDRESS=localhost/'
mut "IP site adresi" 's/^SITE_ADDRESS=.*/SITE_ADDRESS=10.0.0.5/'
mut "mock kapalı ama ücretli çağrı kapalı (tutarsız)" 's/^USE_MOCK_PROVIDERS=.*/USE_MOCK_PROVIDERS=false/'
mut "gerçek mod anahtarsız" 's/^USE_MOCK_PROVIDERS=.*/USE_MOCK_PROVIDERS=false/; s/^ALLOW_PAID_CALLS=.*/ALLOW_PAID_CALLS=true/'
mut "boş zorunlu değer" 's/^SETUP_TOKEN_KEY=.*/SETUP_TOKEN_KEY=/'
mut "geçersiz alt ağ" 's/^PROXY_SUBNET=.*/PROXY_SUBNET=kotu/'
rm -f "$D/.env.mut"
cp -r "$D/secrets" "$D/secrets.bak"; rm "$D/secrets/ca/ca.crt"
tn "preflight reddeder: CA sertifikası yok" bash -c "cd '$D' && bash scripts/preflight.sh"
rm -rf "$D/secrets"; mv "$D/secrets.bak" "$D/secrets"
t "preflight: geri konunca yeniden geçer" bash -c "cd '$D' && bash scripts/preflight.sh"

# ------------------------------------------------------------------ ops.sh: tam yedek
bash "$OPS" backup full > "$T/b1.txt" 2>&1
FULL1="$(ls -d "$BACKUP_DIR"/full/2* | head -n 1)"
t "backup full: şifreli set oluştu (db + depo + günlük + manifest)" bash -c "cd '$FULL1' && test -s db.bak.gz.gpg -a -s storage.tar.gz.gpg -a -s journal.gpg -a -s manifest.json -a -s manifest.sha256"
t "backup full: yarım (.partial) set kalmadı" bash -c "! ls '$BACKUP_DIR'/full | grep -q partial"
t "backup full: açık (şifresiz) .bak paylaşılan birimden silindi" bash -c "[ -z \"\$(ls -A '$SQL_BACKUP_DIR')\" ]"
t "backup full: şifreli dosyalarda açık metin yok" bash -c "! grep -aq 'katalog' '$FULL1/storage.tar.gz.gpg' && ! grep -aq sessionId '$FULL1/journal.gpg'"
t "backup full: SQL yedek betiği çağrıldı" grep -q '^backup-full.sql|Database=sanalprova' "$FAKE_LOG"
t "backup full: manifest veritabanı adını ve türü içerir" bash -c "grep -q '\"kind\": \"full\"' '$FULL1/manifest.json' && grep -q '\"database\": \"sanalprova\"' '$FULL1/manifest.json'"
t "verify: sağlam set doğrulanır" bash "$OPS" verify "$FULL1"
t "verify --deep: SQL VERIFYONLY yolu çalışır" bash "$OPS" verify "$FULL1" --deep
# bozulma / yanlış parola
cp -r "$FULL1" "$T/tampered"; printf 'X' | dd of="$T/tampered/storage.tar.gz.gpg" bs=1 seek=100 conv=notrunc 2> /dev/null
tn "verify: bozulmuş dosya (sağlama toplamı) reddedilir" bash "$OPS" verify "$T/tampered"
cp -r "$FULL1" "$T/tampered2"; ( cd "$T/tampered2" && printf 'X' | dd of=db.bak.gz.gpg bs=1 seek=100 conv=notrunc 2> /dev/null && sha256sum -- *.gpg > manifest.sha256 )
tn "verify: sağlama toplamı da yeniden yazılsa bile gpg bütünlüğü bozulmayı yakalar" bash "$OPS" verify "$T/tampered2"
printf 'baska-parola-baska-parola-baska' > "$T/otherpass"
tn "verify: yanlış parolayla açılamaz" env BACKUP_PASSPHRASE_FILE="$T/otherpass" bash "$OPS" verify "$FULL1"
tn "backup: parola dosyası yoksa çalışmaz" env BACKUP_PASSPHRASE_FILE="$T/yok" bash "$OPS" backup full
printf 'kisa' > "$T/shortpass"
tn "backup: kısa parola reddedilir" env BACKUP_PASSPHRASE_FILE="$T/shortpass" bash "$OPS" backup full

# ------------------------------------------------------------------ günlük yedeği ve zincir
sleep 1
bash "$OPS" backup log > "$T/b2.txt" 2>&1
LOG1="$(ls -d "$BACKUP_DIR"/log/2* | head -n 1)"
t "backup log: şifreli günlük seti ve taban tam set kaydı" bash -c "test -s '$LOG1/log.trn.gz.gpg' && grep -q '\"baseFullSet\": \"$(basename "$FULL1")\"' '$LOG1/manifest.json'"
t "verify: günlük seti doğrulanır" bash "$OPS" verify "$LOG1"
tn "backup log: tam yedek yokken reddedilir" env BACKUP_DIR="$T/bosyedek" bash "$OPS" backup log

# ------------------------------------------------------------------ depo geri yükleme (üzerine yazmaz)
t "restore-files: kuru çalışma hiçbir şey yazmaz" bash -c "bash '$OPS' restore-files '$FULL1' --dest '$T/r1' && [ ! -e '$T/r1' ]"
t "restore-files: boş hedefe birebir açar" bash -c "bash '$OPS' restore-files '$FULL1' --dest '$T/r1' --confirm && diff -r '$STORAGE_DIR' '$T/r1/storage' && cmp '$JOURNAL_FILE' '$T/r1/deleted-sessions.log'"
tn "restore-files: boş olmayan hedefin üzerine yazmaz" bash "$OPS" restore-files "$FULL1" --dest "$T/r1" --confirm
tn "restore-files: canlı depo klasörü (veya altı) hedef olamaz" bash "$OPS" restore-files "$FULL1" --dest "$STORAGE_DIR/yeni" --confirm

# ------------------------------------------------------------------ veritabanı geri yükleme (sahte sqlcmd ile betik sırası ve korumalar)
tn "restore-db: üretim veritabanı adına geri yüklenmez" bash "$OPS" restore-db "$FULL1" --target sanalprova --confirm
tn "restore-db: korumalı ad reddedilir" bash "$OPS" restore-db "$FULL1" --target sanalprova_dev --confirm
tn "restore-db: geçersiz ad reddedilir" bash "$OPS" restore-db "$FULL1" --target 'x;DROP' --confirm
tn "restore-db: günlük seti tam set yerine verilemez" bash "$OPS" restore-db "$LOG1" --target sanalprova_restore_1 --confirm
: > "$FAKE_LOG"
t "restore-db: kuru çalışma SQL çağırmaz" bash -c "bash '$OPS' restore-db '$FULL1' --target sanalprova_restore_1 && [ ! -s '$FAKE_LOG' ]"
t "restore-db: tam + günlük zinciri doğru sırayla (restore → restore-log → kullanıcı eşleme → karantina → denetim)" bash -c "bash '$OPS' restore-db '$FULL1' --target sanalprova_restore_1 --log '$LOG1' --stopat 2026-11-01T10:00:00 --confirm"
t "restore-db: SQL betik sırası ve değişkenleri" bash -c "
  mapfile -t lines < <(grep -v '^backup' '$FAKE_LOG')
  [[ \"\${lines[0]}\" == restore.sql*Target=sanalprova_restore_1*Recovery=NORECOVERY* ]] &&
  [[ \"\${lines[1]}\" == restore-log.sql*Target=sanalprova_restore_1*Recovery=RECOVERY*StopAt=2026-11-01T10:00:00 ]] &&
  [[ \"\${lines[2]}\" == post-restore-fix-users.sql*Database=sanalprova_restore_1* ]] &&
  [[ \"\${lines[3]}\" == post-restore-quarantine.sql*Database=sanalprova_restore_1* ]] &&
  [[ \"\${lines[4]}\" == post-restore-check.sql*Database=sanalprova_restore_1* ]]"
t "restore-db: geçici açık yedek dosyaları temizlendi" bash -c "[ -z \"\$(ls -A '$SQL_BACKUP_DIR')\" ]"
tn "post-restore: korumalı veritabanında çalışmaz" bash "$OPS" post-restore --db sanalprova_test

# ------------------------------------------------------------------ saklama süresi
for old in 20260101T000000Z 20260102T000000Z 20260103T000000Z 20260104T000000Z 20260105T000000Z; do mkdir -p "$BACKUP_DIR/full/$old" "$BACKUP_DIR/log/$old"; echo '{}' > "$BACKUP_DIR/full/$old/manifest.json"; echo '{}' > "$BACKUP_DIR/log/$old/manifest.json"; done
t "prune: kuru çalışma hiçbir şey silmez" bash -c "bash '$OPS' prune && [ -d '$BACKUP_DIR/full/20260101T000000Z' ]"
t "prune --yes: süresi geçen eski setleri siler" bash -c "bash '$OPS' prune --yes && [ ! -d '$BACKUP_DIR/log/20260101T000000Z' ]"
t "prune --yes: en yeni 3 tam set ve güncel setler korunur" bash -c "[ -d '$FULL1' ] && [ -d '$LOG1' ] && [ \$(ls -1 '$BACKUP_DIR/full' | wc -l) -ge 3 ]"
t "prune: eski setler silinse bile günlük 'taze' kalır" test -d "$LOG1"

echo
echo "SONUÇ: $pass geçti, $failed kaldı"
[ "$failed" = 0 ]
