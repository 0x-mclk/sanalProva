# syntax=docker/dockerfile:1
# Operasyon imajı (Pilot Faz A): sqlcmd 18 + gpg + tar/gzip. Yedek, doğrulama, geri yükleme, ilk kurulum ve sertleştirme komutlarını çalıştırır (deploy/ops/ops.sh).
# Yalnızca isteğe bağlı çalışır (compose profili "ops"); sürekli çalışan bir servis DEĞİLDİR. Gizli değer yok: çalışma zamanında ortam/sır dosyasıyla gelir.
FROM mcr.microsoft.com/mirror/docker/library/ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gnupg tar gzip coreutils \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
    && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/ubuntu/24.04/prod noble main" > /etc/apt/sources.list.d/mssql-release.list \
    && apt-get update \
    && ACCEPT_EULA=Y apt-get install -y --no-install-recommends mssql-tools18 libgssapi-krb5-2 \
    && apt-get purge -y --auto-remove curl \
    && rm -rf /var/lib/apt/lists/*
COPY ops/ops.sh /usr/local/bin/ops
COPY sql/ /sql/
RUN chmod 0755 /usr/local/bin/ops && /opt/mssql-tools18/bin/sqlcmd -? > /dev/null && gpg --version > /dev/null
ENV PATH="${PATH}:/opt/mssql-tools18/bin"
# Kök kullanıcı: SQL Server'ın paylaşılan yedek biriminde yazdığı dosyaları okuyabilmek ve geri yükleme dizinlerine yazabilmek için (konteyner yalnızca isteğe bağlı çalışır, ağda yalnızca "backend" ağındadır)
ENTRYPOINT ["/usr/local/bin/ops"]
