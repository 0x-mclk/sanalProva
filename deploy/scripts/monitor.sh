#!/bin/bash
# Pilot asgari izleme (Faz C): servis sağlığı, disk, yedek yaşı, site erişimi ve uygulama kontrolleri (veritabanı, depo diski, takılı işler, belirsiz maliyet, bütçe, kredi).
# SALT OKUNUR: hiçbir şeyi değiştirmez, hiçbir sır yazdırmaz. Cron ile çalıştırın; çıkış kodu: 0 sorun yok, 10 uyarı, 11 kritik (uyarı iletimi dış sistemin işidir: docs/19 §11).
# Kullanım: ./monitor.sh [--env <dosya>] [--backups <klasör>]
# Ortam (testler için ezilebilir): MONITOR_SKIP_DOCKER=1 (docker denetimlerini atla), MONITOR_SKIP_HTTP=1, MONITOR_DISK_PATHS="yol1 yol2"
set -uo pipefail
cd "$(dirname "$0")/.."
ENVF=".env.prod"; BACKUPS="backups"
while [ $# -gt 0 ]; do
  case "$1" in
    --env) ENVF="$2"; shift 2 ;;
    --backups) BACKUPS="$2"; shift 2 ;;
    *) echo "bilinmeyen seçenek: $1" >&2; exit 2 ;;
  esac
done

worst=0
report() { # seviye(0/1/2) ad mesaj
  local tag="TAMAM "; [ "$1" = 1 ] && tag="UYARI "; [ "$1" = 2 ] && tag="KRİTİK"
  printf '[%s] %s: %s\n' "$tag" "$2" "$3"
  [ "$1" -gt "$worst" ] && worst="$1"
  return 0
}
val() { sed -n "s/^$1=\(.*\)$/\1/p" "$ENVF" 2>/dev/null | head -n 1; }
dc() { docker compose -f docker-compose.prod.yml --env-file "$ENVF" "$@"; }

# 1) Servisler
if [ "${MONITOR_SKIP_DOCKER:-0}" != 1 ]; then
  running="$(dc ps --status running --services 2>/dev/null)"
  for svc in sqlserver api web; do
    if grep -qx "$svc" <<<"$running"; then report 0 "servis:$svc" "çalışıyor"; else report 2 "servis:$svc" "çalışmıyor"; fi
  done
fi

# 2) Disk doluluğu (yüzde): Docker veri kökü ve yedek klasörü; ≥80 uyarı, ≥90 kritik
disk_level() { # yüzde → seviye
  if [ "$1" -ge 90 ]; then echo 2; elif [ "$1" -ge 80 ]; then echo 1; else echo 0; fi
}
paths="${MONITOR_DISK_PATHS:-/var/lib/docker $BACKUPS}"
for p in $paths; do
  [ -e "$p" ] || continue
  pct="$(df -P "$p" 2>/dev/null | awk 'NR==2 {gsub("%","",$5); print $5}')"
  if [ -z "$pct" ]; then report 1 "disk:$p" "okunamadı"; else report "$(disk_level "$pct")" "disk:$p" "%$pct dolu"; fi
done

# 3) Yedek yaşı: tam set ≤ 26 sa (≥ 50 sa kritik), günlük seti ≤ 30 dk (≥ 3 sa kritik); klasör değişiklik zamanından
newest_age_min() { # klasör → en yeni alt klasörün yaşı (dk) veya boş
  local newest; newest="$(ls -1dt "$1"/*/ 2>/dev/null | grep -v '\.partial/$' | head -n 1)"
  [ -n "$newest" ] || return 1
  echo $(( ( $(date +%s) - $(stat -c %Y "$newest") ) / 60 ))
}
if age="$(newest_age_min "$BACKUPS/full")"; then
  lvl=0; [ "$age" -ge 1560 ] && lvl=1; [ "$age" -ge 3000 ] && lvl=2
  report "$lvl" "yedek:tam" "son tam set $((age / 60)) saat önce"
else report 2 "yedek:tam" "hiç tam yedek yok ($BACKUPS/full)"; fi
if age="$(newest_age_min "$BACKUPS/log")"; then
  lvl=0; [ "$age" -ge 30 ] && lvl=1; [ "$age" -ge 180 ] && lvl=2
  report "$lvl" "yedek:günlük" "son günlük seti $age dk önce"
else report 1 "yedek:günlük" "günlük yedek seti yok ($BACKUPS/log)"; fi

# 4) Site erişimi (dışarıdan görünen adres)
site="$(val SITE_ADDRESS)"
if [ "${MONITOR_SKIP_HTTP:-0}" != 1 ] && [ -n "$site" ]; then
  if curl -fsS -o /dev/null --max-time 10 "https://$site/" 2>/dev/null; then report 0 "site" "https://$site/ erişilebilir"; else report 2 "site" "https://$site/ erişilemiyor"; fi
fi

# 5) Uygulama kontrolleri (veritabanı, depo diski, takılı iş, belirsiz maliyet, bütçe, kredi)
if [ "${MONITOR_SKIP_DOCKER:-0}" != 1 ]; then
  out="$(dc run --rm --no-deps -T api platform status 2>&1)"; code=$?
  printf '%s\n' "$out" | grep -E '^\[(TAMAM|UYARI|KRİTİK)' || true
  case "$code" in
    0) ;;
    10) [ "$worst" -lt 1 ] && worst=1 ;;
    11) worst=2 ;;
    *) report 2 "uygulama" "platform status çalışmadı (çıkış $code)" ;;
  esac
fi

case "$worst" in
  0) echo "SONUÇ: sorun yok"; exit 0 ;;
  1) echo "SONUÇ: UYARI var"; exit 10 ;;
  *) echo "SONUÇ: KRİTİK sorun var"; exit 11 ;;
esac
