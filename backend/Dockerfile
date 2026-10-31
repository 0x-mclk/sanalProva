# syntax=docker/dockerfile:1
# Sanal Prova API + EF migration paketi (bundle). Bağlam: ./backend (bkz. docker-compose.yml).
# Notlar:
#  - global.json KOPYALANMAZ: yerel SDK sabitlemesi (10.0.402) imajdaki SDK ile çakışmasın.
#  - Gizli değer yok: bağlantı dizesi, imza anahtarı vb. çalışma zamanında ortam değişkeniyle gelir.
#  - Migration paketi linux-x64 içindir (x64 ana makine/CI); ARM ana makinede derlenmez.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props dotnet-tools.json ./
COPY src/ src/
RUN dotnet restore src/SanalProva.Api/SanalProva.Api.csproj
RUN dotnet publish src/SanalProva.Api/SanalProva.Api.csproj -c Release --no-restore -o /out/api /p:UseAppHost=false

# EF migration paketi: tek dosya, SDK gerektirmeden çalışır. Bağlamı AppDbContextFactory (tasarım zamanı fabrikası) üretir: uygulama
# yapılandırması / ConnectionStrings:Main gerekmez (ne derlemede ne çalışmada). Gerçek bağlantı çalışma anında --connection ile verilir (compose: db-migrate).
FROM build AS migrations
RUN dotnet tool restore
RUN dotnet ef migrations bundle --self-contained -r linux-x64 --force \
      --project src/SanalProva.Infrastructure --startup-project src/SanalProva.Api -o /out/efbundle

# Migration işi (compose: db-migrate). Giriş noktası paketin kendisidir: efbundle --connection "<dize>"
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS migrator
WORKDIR /app
COPY --from=migrations /out/efbundle /app/efbundle
USER app
ENTRYPOINT ["/app/efbundle"]

# API (son hedef; compose bunu 'api' hedefiyle derler)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /out/api ./
# Dosya deposu ve Data Protection anahtarları (çerez/antiforgery) kalıcı birimlere bağlanır; kullanıcı 'app' (root değil)
# Üretim (deploy/docker-compose.prod.yml): depo, anahtar halkası ve silme günlüğü ayrı kalıcı birimlerdir; yolların görüntüde 'app' sahipliğinde bulunması
# adlandırılmış birimin ilk açılışta yazılabilir olmasını sağlar
RUN mkdir -p /data/storage /data/dpkeys /data/journal /home/app/.aspnet/DataProtection-Keys \
    && chown -R app:app /data /home/app/.aspnet
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    Storage__LocalRoot=/data/storage
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "SanalProva.Api.dll"]
