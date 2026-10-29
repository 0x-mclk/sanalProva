#!/bin/sh
# ODBC surucusu + sqlcmd GERCEKTEN yuklenebilir mi (SQL Server gerekmez). docker/db/Dockerfile derlemesinde calisir: hata = imaj derlenmez.
# Kontroller: (1) surucu dosyasi var, (2) ldd ile eksik paylasilan kutuphane yok (surucu + sqlcmd), (3) surucu odbcinst'e kayitli,
# (4) sqlcmd surucuyu yukleyip kapali bir porta baglanmayi dener: beklenen sonuc bir BAGLANTI hatasidir, "Can't open lib" DEGIL.
# Gizli deger icermez, kullanmaz.
set -eu
DRV_DIR="${ODBC_DRIVER_DIR:-/opt/microsoft/msodbcsql18/lib64}"
SQLCMD="${SQLCMD:-/opt/mssql-tools18/bin/sqlcmd}"
fail() { echo "[verify-odbc HATA] $1" >&2; exit 1; }

set -- "$DRV_DIR"/libmsodbcsql-*.so.*
[ -e "$1" ] || fail "ODBC surucu dosyasi yok: $DRV_DIR/libmsodbcsql-*.so.*"
drv="$1"
echo "[verify-odbc] surucu: $drv"
[ -x "$SQLCMD" ] || fail "sqlcmd yok veya calistirilabilir degil: $SQLCMD"

for f in "$drv" "$SQLCMD"; do
  missing="$(ldd "$f" 2>&1 | grep 'not found' || true)"
  if [ -n "$missing" ]; then
    echo "$missing" >&2
    fail "$f icin eksik paylasilan kutuphane (yukaridaki satirlar)"
  fi
done
echo "[verify-odbc] ldd: surucu ve sqlcmd icin eksik kutuphane yok"

odbcinst -q -d 2>&1 | grep -qx '\[ODBC Driver 18 for SQL Server\]' || fail "surucu odbcinst'e kayitli degil (odbcinst -q -d)"
echo "[verify-odbc] surucu kaydi: ODBC Driver 18 for SQL Server"

out="$("$SQLCMD" -S 127.0.0.1,1 -C -l 1 -Q "SELECT 1" 2>&1 || true)"
case "$out" in
  *"Can't open lib"*|*"Data source name not found"*|*"file not found"*)
    echo "$out" >&2
    fail "surucu yuklenemedi (sqlcmd ciktisi yukarida)" ;;
esac
echo "[verify-odbc] surucu yuklendi (beklenen baglanti hatasi: $(echo "$out" | head -n 1 | cut -c1-120))"
