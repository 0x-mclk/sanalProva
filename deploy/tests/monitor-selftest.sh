#!/bin/bash
# monitor.sh yerel doğrulaması (Docker yok): sahte docker/curl/df ile disk, yedek yaşı, servis ve uygulama çıkış kodu eşlemeleri.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
pass=0; fail=0
check() { # ad beklenen-çıkış aranan-metin
  local name="$1" want="$2" needle="$3"
  out="$(cd "$here/.." && env MONITOR_SKIP_HTTP=1 PATH="$tmp/bin:$PATH" "${@:4}" bash scripts/monitor.sh --env "$tmp/env" --backups "$tmp/backups" 2>&1)"; code=$?
  if [ "$code" = "$want" ] && printf '%s' "$out" | grep -q -- "$needle"; then printf '  geçti  %s\n' "$name"; pass=$((pass+1)); else printf '  KALDI  %s (çıkış %s, beklenen %s)\n%s\n' "$name" "$code" "$want" "$out"; fail=$((fail+1)); fi
}
mkdir -p "$tmp/bin" "$tmp/backups/full" "$tmp/backups/log"
echo "SITE_ADDRESS=pilot.example.test" > "$tmp/env"
touch "$tmp/backups/full/placeholder"; rmdir "$tmp/backups/full" 2>/dev/null; mkdir -p "$tmp/backups/full/2026-11-01T0000Z" "$tmp/backups/log/2026-11-01T0000Z"

# sahte docker: ps → hepsi çalışıyor; run … platform status → FAKE_STATUS_CODE ile çıkar
cat > "$tmp/bin/docker" <<'EOF'
#!/bin/bash
case "$*" in
  *"ps --status running --services"*) echo "${FAKE_RUNNING:-sqlserver api web}" | tr " " "
" ;;
  *"platform status"*) echo "[TAMAM ] database: Bağlantı kuruldu (3 ms)."; [ "${FAKE_STATUS_CODE:-0}" = 11 ] && echo "[KRİTİK] disk: Depo diskinde %5 boş (1 GB)."; exit "${FAKE_STATUS_CODE:-0}" ;;
esac
EOF
chmod +x "$tmp/bin/docker"
cat > "$tmp/bin/df" <<'EOF'
#!/bin/bash
echo "Filesystem 1024-blocks Used Available Capacity Mounted"
echo "x 100 50 50 ${FAKE_DISK_PCT:-40}% /"
EOF
chmod +x "$tmp/bin/df"
now_touch() { touch "$1"; }
fresh() { mkdir -p "$tmp/backups/full/2026-11-01T0000Z" "$tmp/backups/log/2026-11-01T0000Z"; touch "$tmp/backups/full/2026-11-01T0000Z" "$tmp/backups/log/2026-11-01T0000Z"; }

fresh
check "her şey yolunda → 0" 0 "SONUÇ: sorun yok" MONITOR_DISK_PATHS="$tmp/backups"
check "disk %85 → uyarı (10)" 10 "UYARI.*disk:" MONITOR_DISK_PATHS="$tmp/backups" FAKE_DISK_PCT=85
check "disk %95 → kritik (11)" 11 "KRİTİK.*disk:" MONITOR_DISK_PATHS="$tmp/backups" FAKE_DISK_PCT=95
check "servis durmuş → kritik" 11 "KRİTİK.*servis:api" MONITOR_DISK_PATHS="$tmp/backups" FAKE_RUNNING="sqlserver web"
check "uygulama uyarısı (10) çıkışa yansır" 10 "SONUÇ: UYARI" MONITOR_DISK_PATHS="$tmp/backups" FAKE_STATUS_CODE=10
check "uygulama kritiği (11) satırları gösterir" 11 "KRİTİK.*disk: Depo" MONITOR_DISK_PATHS="$tmp/backups" FAKE_STATUS_CODE=11
touch -d '40 hours ago' "$tmp/backups/full/2026-11-01T0000Z"
check "tam yedek 40 saat → uyarı" 10 "yedek:tam.*40 saat" MONITOR_DISK_PATHS="$tmp/backups"
touch -d '60 hours ago' "$tmp/backups/full/2026-11-01T0000Z"
check "tam yedek 60 saat → kritik" 11 "KRİTİK.*yedek:tam" MONITOR_DISK_PATHS="$tmp/backups"
fresh; touch -d '45 minutes ago' "$tmp/backups/log/2026-11-01T0000Z"
check "günlük yedek 45 dk → uyarı" 10 "yedek:günlük.*45 dk" MONITOR_DISK_PATHS="$tmp/backups"
rm -rf "$tmp/backups/full"/*; fresh
mkdir -p "$tmp/backups/full/2026-11-02T0000Z.partial"
check "yarım (.partial) set yaş hesabına girmez" 0 "SONUÇ: sorun yok" MONITOR_DISK_PATHS="$tmp/backups"
rm -rf "$tmp/backups/full"/*
check "hiç tam yedek yok → kritik" 11 "hiç tam yedek yok" MONITOR_DISK_PATHS="$tmp/backups"
bad="$(cd "$here/.." && bash scripts/monitor.sh --yanlis 2>&1)"; badcode=$?
if [ "$badcode" = 2 ] && printf '%s' "$bad" | grep -q "bilinmeyen seçenek"; then echo "  geçti  bilinmeyen seçenek reddedilir"; pass=$((pass+1)); else echo "  KALDI  bilinmeyen seçenek reddedilir"; fail=$((fail+1)); fi
printf '\nSONUÇ: %d geçti, %d kaldı\n' "$pass" "$fail"
[ "$fail" = 0 ]
