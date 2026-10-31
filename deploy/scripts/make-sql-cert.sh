#!/bin/bash
# SQL Server TLS için özel CA + sunucu sertifikası üretir (Pilot Faz A). API/ops bağlantıları bu CA ile sertifikayı DOĞRULAR (TrustServerCertificate yok).
# Çıktılar (git'e GİRMEZ): secrets/ca/ca.crt (istemcilere), secrets/ca/ca.key (SUNUCUDAN ÇIKARIP çevrimdışı saklayın), secrets/sqlserver-tls/server.crt|server.key.
# Sertifika ana makine adı: "sqlserver" (compose servis adı; bağlantılarda HostNameInCertificate=sqlserver). Geçerlilik: sunucu 2 yıl, CA 10 yıl.
# Kullanım: ./make-sql-cert.sh [-f]     (SQL Server konteyneri 'mssql' kullanıcısı (uid 10001) anahtarı okuyabilmeli: kök olarak çalıştırılırsa chown yapılır.)
set -euo pipefail
export MSYS_NO_PATHCONV=1   # Windows Git Bash yol dönüşümü -subj değerini bozmasın (Linux'ta etkisiz)
cd "$(dirname "$0")/.."
force=0; [ "${1:-}" = "-f" ] && force=1
if [ -f secrets/sqlserver-tls/server.crt ] && [ $force != 1 ]; then echo "sertifika zaten var (ezilmedi). Yenilemek için -f." >&2; exit 1; fi
umask 077
mkdir -p secrets/ca secrets/sqlserver-tls
# Geçici dizin secrets/ içinde (göreli yol: Windows Git Bash'te yerel openssl ile uyumlu; Linux'ta etkisiz)
tmp="secrets/.tmp.$$"; mkdir -p "$tmp"; trap 'rm -rf "$tmp"' EXIT
openssl genrsa -out secrets/ca/ca.key 4096 2> /dev/null
openssl req -x509 -new -nodes -key secrets/ca/ca.key -sha256 -days 3650 -subj "/O=Sanal Prova/CN=Sanal Prova SQL CA" -out secrets/ca/ca.crt
openssl genrsa -out secrets/sqlserver-tls/server.key 2048 2> /dev/null
openssl req -new -key secrets/sqlserver-tls/server.key -subj "/O=Sanal Prova/CN=sqlserver" -out "$tmp/server.csr"
cat > "$tmp/ext.cnf" <<EXT
basicConstraints=CA:FALSE
keyUsage=digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=DNS:sqlserver,DNS:localhost
EXT
openssl x509 -req -in "$tmp/server.csr" -CA secrets/ca/ca.crt -CAkey secrets/ca/ca.key -CAcreateserial -CAserial "$tmp/ca.srl" -out secrets/sqlserver-tls/server.crt -days 730 -sha256 -extfile "$tmp/ext.cnf" 2> /dev/null
openssl verify -CAfile secrets/ca/ca.crt secrets/sqlserver-tls/server.crt > /dev/null
chmod 644 secrets/ca/ca.crt secrets/sqlserver-tls/server.crt
chmod 600 secrets/ca/ca.key
if [ "$(id -u)" = 0 ]; then
  chown 10001:0 secrets/sqlserver-tls/server.key secrets/sqlserver-tls/server.crt
  chmod 600 secrets/sqlserver-tls/server.key
else
  # Kök değilsek konteynerdeki mssql (uid 10001) anahtarı okuyamaz: sahiplik elle devredilmeli
  chmod 640 secrets/sqlserver-tls/server.key
  chmod 755 secrets/sqlserver-tls
  echo "UYARI: kök olmadığınız için anahtarı sahiplenin: sudo chown 10001:0 secrets/sqlserver-tls/server.key && sudo chmod 600 secrets/sqlserver-tls/server.key (docs/19)." >&2
fi
echo "SQL TLS sertifikaları oluşturuldu (ca.key dosyasını sunucudan çıkarıp çevrimdışı saklayın)."
