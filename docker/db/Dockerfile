# syntax=docker/dockerfile:1
# db-provision / db-harden icin modern sqlcmd istemcisi.
# NEDEN ayri imaj: 'mcr.microsoft.com/mssql-tools:latest' 2017-10-31'de (Ubuntu 16.04, ODBC 13, sqlcmd 14) derlenmis ve guncellenmemistir;
# SQL Server 2025 ile desteklenmeyen sekiz yillik bir istemcidir. SQL Server 2025 imajinda ise sqlcmd garantisi yoktur.
# Baz: Ubuntu 24.04 (SQL Server 2025 imajiyla ayni), Docker Hub hiz siniri olmasin diye MCR yansisindan. Paket: Microsoft deposundan mssql-tools18 (sqlcmd 18, ODBC 18; varsayilan sifreli baglanti: betik -C ile sertifikaya guvenir).
#
# libgssapi-krb5-2 ACIKCA kurulur (CI #2 hatasi: "Can't open lib '.../libmsodbcsql-18.7.so.1.1' : file not found"):
#   msodbcsql18 surucusu libgssapi_krb5.so.2'ye baglidir (ELF DT_NEEDED) ama paketi Depends alaninda YALNIZCA libkrb5-3 ilan eder; libgssapi-krb5-2
#   o kapanista yoktur ve taban Ubuntu imajinda da yoktur. Eskiden yalnizca 'curl' (libcurl4t64/libssh-4) uzerinden geliyordu ve asagidaki
#   'apt-get purge --auto-remove curl gnupg' onu da siliyordu. Acik kurulum paketi "elle kurulmus" isaretler: purge silmez.
FROM mcr.microsoft.com/mirror/docker/library/ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
    && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/ubuntu/24.04/prod noble main" > /etc/apt/sources.list.d/mssql-release.list \
    && apt-get update \
    && ACCEPT_EULA=Y apt-get install -y --no-install-recommends mssql-tools18 libgssapi-krb5-2 \
    && apt-get purge -y --auto-remove curl gnupg \
    && rm -rf /var/lib/apt/lists/*
RUN useradd --system --uid 10002 --no-create-home tools
COPY verify-odbc.sh /usr/local/bin/verify-odbc.sh
USER tools
ENV PATH="${PATH}:/opt/mssql-tools18/bin"
# Derleme-zamani kontrol: surucu + sqlcmd yuklenebilir mi (son durum, purge sonrasi, root olmayan kullanici). Basarisizsa imaj derlenmez.
RUN sh /usr/local/bin/verify-odbc.sh
