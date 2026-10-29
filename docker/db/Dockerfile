# syntax=docker/dockerfile:1
# db-provision / db-harden icin modern sqlcmd istemcisi.
# NEDEN ayri imaj: 'mcr.microsoft.com/mssql-tools:latest' 2017-10-31'de (Ubuntu 16.04, ODBC 13, sqlcmd 14) derlenmis ve guncellenmemistir;
# SQL Server 2025 ile desteklenmeyen sekiz yillik bir istemcidir. SQL Server 2025 imajinda ise sqlcmd garantisi yoktur.
# Baz: Ubuntu 24.04 (SQL Server 2025 imajiyla ayni), Docker Hub hiz siniri olmasin diye MCR yansisindan. Paket: Microsoft deposundan mssql-tools18 (sqlcmd 18, ODBC 18; varsayilan sifreli baglanti: betik -C ile sertifikaya guvenir).
FROM mcr.microsoft.com/mirror/docker/library/ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
    && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/ubuntu/24.04/prod noble main" > /etc/apt/sources.list.d/mssql-release.list \
    && apt-get update \
    && ACCEPT_EULA=Y apt-get install -y --no-install-recommends mssql-tools18 \
    && apt-get purge -y --auto-remove curl gnupg \
    && rm -rf /var/lib/apt/lists/*
RUN useradd --system --uid 10002 --no-create-home tools
USER tools
ENV PATH="${PATH}:/opt/mssql-tools18/bin"
