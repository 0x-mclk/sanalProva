# Sanal Prova

> Veritabanı **SQL Server 2025 Express**'tir (M4.1'de bu belge güncellendi). Güncel durum ve riskler: `CLAUDE.md`, `docs/DEVAM-NOTU.md`.

Mağaza içi, yapay zekâ destekli sanal kıyafet prova sistemi (bindallı, şalvar takımı, kaftan, kına ve özel gün kıyafetleri).

Mimari ve planlama: [docs/](docs/README.md) · Tasarım referansı: [docs/design/reference.png](docs/design/reference.png)

> **Durum:** Aşama 4b kodu tamamlandı (onay bekliyor; **gerçek AI çağrısı yapılmadı**, ücretli çağrılar varsayılan olarak kapalı) — tek kaftan/hazır takım giydirme için Seedream 5 Flash (varsayılan), GPT Image 2.5 ve Pruna adapter'ları, yönetici model/kalite ayarları, bütçe kapıları ve maliyet izleme hazır; FASHN/Gemini eski (pasif). Aşama 3 kodu tamamlandı — katalog, prova akışı ve **Smart TV eşleştirme / gerçek zamanlı görüntüleme** çalışıyor. Tüm AI sağlayıcıları **mock** modda; gerçek/ücretli API çağrısı yapılmaz ve üretilen görseller **DEMO** olarak işaretlenir. Gerçek AI (Aşama 4) ve revizyon motoru (Aşama 5) henüz yok; TV uygulaması gerçek bir mağaza TV'sinde henüz denenmedi.

## Bileşenler

| Klasör | Teknoloji | Port (yerel) |
|---|---|---|
| `backend/` | ASP.NET Core (.NET 10), EF Core + SQL Server, SignalR | 5080 |
| `frontend/` | React 19 + TypeScript + Vite PWA, Mantine | 5173 (personel `/`, TV `/tv`) |
| `image-worker/` | Python FastAPI + OpenCV (SAM 2 Aşama 5'te) | 8090 |
| SQL Server 2025 Express | Örnek adı `SANALPROVA` (Windows hizmeti; repo içinde değil) | TCP 14330 (uygulama), paylaşılan bellek `.\SANALPROVA` (yönetim) |

> `.local/` altındaki `pgsql`, `pgdata`, `postgres*` eski PostgreSQL döneminden kalmadır; `backups/` yedekleri ve kurulum kayıtlarını içerir. Hiçbiri kullanılmaz, silinmez veya değiştirilmez (git'e girmez).

## Gereksinimler

- **.NET 10 SDK** — kullanıcı klasörüne kuruludur (`%LOCALAPPDATA%\Microsoft\dotnet`). Her yeni PowerShell oturumunda önce:
  ```powershell
  . ./scripts/dev-env.ps1
  ```
  (Bu, yalnızca o oturumun PATH'ini değiştirir. Sistemdeki .NET 8 etkilenmez.)
- **Node.js 22+**
- **Python 3.10+**
- **SQL Server 2025 Express** — **adlandırılmış örnek `SANALPROVA`**, Windows kimlik doğrulaması + karma mod (uygulama SQL girişleri için), TCP etkin ve sabit port `14330`. `sqlcmd` (ODBC 17+) PATH'te olmalı. `sa` hesabı ve gereksiz girişler `provision.sql` ile kapatılır. Diğer örnekler (ör. `SQLEXPRESS`) bu projeye ait değildir; betikler ve testler onlara bağlanmayı **reddeder**.
- Repoda sabitlenmiş araçlar: `dotnet tool restore` (`dotnet-ef`).

## Veritabanı

Üç ayrı veritabanı rolü vardır; hepsi aynı örnekte, aynı harmanlamayla (`Latin1_General_100_CI_AS_SC`, RCSI açık, uyumluluk 160):

| Veritabanı | Amaç | Kim kullanır |
|---|---|---|
| `sanalprova_dev` | Elle çalıştırılan geliştirme API'si | `ConnectionStrings:Main` (`sanalprova_app`), `ConnectionStrings:Migrator` |
| `sanalprova_test` | Entegrasyon testleri (her çalıştırmada verisi sıfırlanır) | `ConnectionStrings:Test` / `:TestMigrator` |
| `sanalprova_e2e`, `sanalprova_lab_<8hex>` | Geçici: izole E2E ve migration laboratuvarı (oluşturulur ve silinir) | `scripts/e2e-env.ps1`, migration testleri |

> **Bu makinede `sanalprova_dev` bilerek boştur** (şema yok) ve M5'e kadar boş kalacaktır; `db.ps1` ve testler ona dokunmaz. Elle çalıştırılabilir bir geliştirme veritabanı M4.3/M5'te ayrıca kurulacaktır. Doğrulama bugün testler + izole E2E ile yapılır.

### Yeni makinede ilk kurulum (bir kez, yönetici yetkisiyle)

1. SQL Server 2025 Express'i `SANALPROVA` adıyla kurun (karma mod, TCP 14330).
2. İki rastgele parola üretin ve **yalnızca o oturumun** ortam değişkenlerine koyun (kaynağa, günlüğe, belgeye yazmayın):
   ```powershell
   $env:SP_MIGRATOR_PW = '<rastgele-parola-1>'; $env:SP_APP_PW = '<rastgele-parola-2>'
   sqlcmd -S .\SANALPROVA -E -C -I -b -i scripts/mssql/provision.sql
   ```
   Betik idempotenttir; `sanalprova_dev` ve `sanalprova_test` veritabanlarını, en az yetkili `sanalprova_migrator` (DDL + okuma/yazma) ve `sanalprova_app` (yalnızca okuma/yazma) girişlerini oluşturur ve sunucuyu sıkılaştırır (`sa` kapatılır). **Mevcut makinede yeniden çalıştırmayın**: giriş parolalarını sıfırlar.
3. Gizli değerleri `dotnet user-secrets` ile kaydedin (API projesi; değerler **asla** repoya girmez). Bağlantı dizesi biçimi (parolalar yer tutucudur):
   ```powershell
   cd backend
   dotnet user-secrets set "ConnectionStrings:Main"        "Server=tcp:127.0.0.1,14330;Database=sanalprova_dev;User ID=sanalprova_app;Password=<parola-2>;Encrypt=True;TrustServerCertificate=True;Application Name=SanalProva.Api" --project src/SanalProva.Api
   dotnet user-secrets set "ConnectionStrings:Test"        "Server=tcp:127.0.0.1,14330;Database=sanalprova_test;User ID=sanalprova_app;Password=<parola-2>;Encrypt=True;TrustServerCertificate=True;Application Name=SanalProva.Tests" --project src/SanalProva.Api
   dotnet user-secrets set "ConnectionStrings:TestMigrator" "Server=tcp:127.0.0.1,14330;Database=sanalprova_test;User ID=sanalprova_migrator;Password=<parola-1>;Encrypt=True;TrustServerCertificate=True;Application Name=SanalProva.Tests" --project src/SanalProva.Api
   dotnet user-secrets set "Storage:SigningKey"      "<en az 32 karakter rastgele>" --project src/SanalProva.Api
   dotnet user-secrets set "ImageWorker:InternalKey" "<rastgele>" --project src/SanalProva.Api
   dotnet user-secrets set "Seed:DevPassword"        "<demo hesap parolası>" --project src/SanalProva.Api
   cd ..
   ```
4. Şemayı **yeni makinedeki** geliştirme veritabanına migrator hesabıyla uygulayın (bu makinede M5'e kadar uygulanmaz):
   ```powershell
   cd backend; dotnet tool restore
   dotnet ef database update -p src/SanalProva.Infrastructure -s src/SanalProva.Api --connection "<ConnectionStrings:Migrator değeri>"
   cd ..
   ```
5. Migration sonrası sertleştirme (migration geçmişi ve maliyet mutabakatı tabloları için `sanalprova_app` DENY'leri; önce kuru çalışma):
   ```powershell
   ./scripts/apply-history-hardening.ps1 -Database sanalprova_test -Hardening All          # kuru çalışma
   ./scripts/apply-history-hardening.ps1 -Database sanalprova_test -Hardening All -Apply   # uygular (onay sorar)
   ```
6. Frontend ve image-worker bağımlılıkları:
   ```powershell
   cd frontend; npm ci; cd ..
   cd image-worker; python -m venv .venv; .venv\Scripts\python -m pip install -e ".[dev]"; cd ..
   ```

### Günlük veritabanı komutları (`scripts/db.ps1`)

`db.ps1` Windows kimliğiyle `.\SANALPROVA` örneğine bağlanır; parola gerektirmez ve hiçbir gizli değer yazdırmaz. **Yalnızca `sanalprova_test`, `sanalprova_e2e` ve `sanalprova_lab_<8hex>`** hedeflenebilir; `sanalprova_dev`, `SQLEXPRESS` ve diğer her şey kodla reddedilir. Değiştiren komutlar varsayılan olarak **kuru çalışmadır** (`-Apply` gerekir).

```powershell
./scripts/db.ps1 status                                    # salt okunur: örnek, veritabanları, tablo/migration sayıları (dev'e bağlanmaz)
./scripts/db.ps1 migrate -Database sanalprova_test         # kuru çalışma: bekleyen migration'ları listeler
./scripts/db.ps1 migrate -Database sanalprova_test -Apply  # migration uygular (veritabanı önceden var olmalı; yaratmaz)
./scripts/db.ps1 reset   -Database sanalprova_test         # kuru çalışma: silinecek satır sayısı
./scripts/db.ps1 reset   -Database sanalprova_test -Apply  # tüm VERİYİ siler, şema + migration geçmişi kalır (onay sorar)
./scripts/db.ps1 script                                    # idempotent migration betiği (.local/migrations/), veritabanına bağlanmaz
```

Eski `init`, `start`, `stop`, `reseed-demo` komutları (PostgreSQL) kaldırılmıştır ve açıklayıcı bir hata verir. Sunucuyu Windows hizmeti (`MSSQL$SANALPROVA`) yönetir.

**Migration politikası:** yalnızca ileri yönlü (`Down` maliyet/mutabakat verisini siler); geri dönüş yedekten yapılır. Üretim/dağıtım için `./scripts/db.ps1 script` ile idempotent betik üretilir ve migrator hesabıyla `sqlcmd -I` ile uygulanır.

## Docker Compose ve CI

`docker-compose.yml` geliştirme/test için **yalıtılmış** bir yığın kurar: kendi SQL Server 2025 Express konteyneri (Windows'taki `SANALPROVA`, `SQLEXPRESS`, `SQLEXPRESS01` örneklerinden, dosyalarından ve portlarından bağımsız; kendi adlandırılmış birimi), API ve image-worker. Gerçek AI çağrısı yoktur (mock, ücretli çağrılar kapalı, anahtarlar boş). Gereksinim: Docker Desktop veya Docker Engine + Compose v2 (x64).

```powershell
./scripts/compose-env.ps1                 # rastgele parolalarla .env üretir (yazdırılmaz, git'e girmez); varsa ezmez
docker compose up -d --build api          # sqlserver → db-provision → db-migrate → db-harden → image-worker → api
docker compose ps                         # sağlık durumları (api: healthy)
curl http://127.0.0.1:8080/health/ready   # veritabanı + depolama + image-worker
docker compose down                       # durdurur, veri kalır
docker compose down -v                    # bu projenin birimlerini de SİLER (yalnızca sanalprova_* birimleri)
```

| Servis | Rol | Ana makineye açılış |
|---|---|---|
| `sqlserver` | SQL Server 2025 Express; birim `sqlserver-data` | yalnızca `127.0.0.1:14331` |
| `db-provision` → `db-migrate` → `db-harden` | Tek seferlik işler: veritabanları (`sanalprova_dev/test/e2e`) ve en az yetkili girişler → EF migration paketi → `DENY` sertleştirmeleri | yok |
| `image-worker` | FastAPI + OpenCV (durumsuz) | yok (yalnızca compose ağı) |
| `api` | ASP.NET Core; birimler `api-storage`, `api-dpkeys` | yalnızca `127.0.0.1:8080` |

- **Gizli değerler:** `.env` (git'e girmez) `scripts/compose-env.ps1` / `scripts/compose-env.sh` ile üretilir; şablon `.env.example`. Kaynakta ve imaj katmanlarında parola/anahtar yoktur; uygulama `sanalprova_app` (en az yetki) ile bağlanır, `sa` yalnızca tek seferlik işlerde kullanılır.
- **Demo tohumu varsayılan kapalıdır** (`SEED_ENABLED=false`): Compose ortamında veri yoktur. Elle denemek için `.env`'e `SEED_ENABLED=true` ve `SEED_DEV_PASSWORD` ekleyin (yalnızca Development).
- **Üretim:** `API_ENVIRONMENT=Production` çerezleri `Secure` yapar; TLS sonlandıran bir ters vekil gerekir (M4.3/canlıya çıkış kapsamı). Data Protection anahtarları `api-dpkeys` birimindedir.
- Migration paketi `linux-x64`tür; ARM ana makinede `db-migrate` derlenmez.
- **Harmanlama:** uygulama veritabanları `Latin1_General_100_CI_AS_SC` ile oluşturulur (smoke doğrular). Konteynerde sunucu harmanlaması bilerek değiştirilmez (`MSSQL_COLLATION` yok; ilk açılıştaki yeniden indeksleme sunucuyu çökertebiliyordu), sunucu/`tempdb` varsayılanı `SQL_Latin1_General_CP1_CI_AS`'dir.

**CI (GitHub Actions, `.github/workflows/ci.yml`)** — her işte rastgele ve maskelenmiş parolalar; AI çağrısı yok:

| İş | İçerik |
|---|---|
| `backend` | Release derleme (uyarılar hata) + birim testleri |
| `frontend` | typecheck, lint, test, build |
| `image-worker` | pytest |
| `compose-smoke` | Compose yığınını kurar ve doğrular (`scripts/ci/smoke-assert.sh`): tek seferlik işler exit 0, sqlserver/image-worker/api healthy, `/health/ready` Healthy, harmanlama/RCSI/uyumluluk 160, migration'lar uygulanmış, image-worker ana makineye açık değil, portlar yalnızca 127.0.0.1, `sanalprova_app` migration geçmişini/mutabakat tablosunu değiştiremiyor, günlüklerde gizli değer yok |
| `integration` (needs: `backend`, `compose-smoke`) | Aynı yığında MSSQL entegrasyon testlerini `sanalprova_test`'te çalıştırır |
| `e2e` (needs: `backend`, `frontend`, `compose-smoke`) | Compose ile SQL Server + şema, ardından Playwright (izole `sanalprova_e2e`) |

Tetikleyiciler: `push` (`main`, `feature/**`), `pull_request`, elle `workflow_dispatch` (Actions sekmesi). Aynı dal/PR'ın eski koşusu iptal edilir (`concurrency`). Smoke düşerse pahalı işler başlamaz.

**Gizli değerler günlüğe/artifact'a yazılmaz:** parolalar her koşuda üretilip maskelenir; hata günlükleri `scripts/ci/compose-diagnostics.sh` ile arındırılır (değer → `***`); artifact klasörleri `scripts/ci/scrub-artifacts.sh` ile taranıp gizli değer içeren dosyalar silinir; Playwright trace'i CI'da kapalıdır (giriş isteğindeki parolayı içerebilir); `docker compose config` yalnızca `--quiet` çalışır.

Kapsam sınırı: migration laboratuvarı testleri (`MigrationValidationTests` vb.) Windows `SANALPROVA` örneği gerektirir (`SANALPROVA_LAB_ADMIN`); Linux konteynerinde **atlanır** ve yerelde çalıştırılır.

## Çalıştırma

API şu an `sanalprova_dev` şemasını bekler; bu makinede o veritabanı M5'e kadar boş olduğundan **elle çalıştırma yeni bir geliştirme veritabanı kurulana kadar kullanılamaz** (bkz. "Yeni makinede ilk kurulum"). Kurulu bir makinede üç terminal:

```powershell
# Terminal 1 — API
. ./scripts/dev-env.ps1
cd backend; dotnet run --project src/SanalProva.Api --launch-profile http

# Terminal 2 — görsel servis (iç anahtarı user-secrets'tan okur)
./scripts/image-worker.ps1

# Terminal 3 — frontend
cd frontend; npm run dev
```

- Personel uygulaması / geliştirme ekranı: <http://127.0.0.1:5173/>
- TV ekranı: <http://127.0.0.1:5173/tv>
- Sağlık: <http://127.0.0.1:5080/health/ready> · OpenAPI: <http://127.0.0.1:5080/openapi/v1.json>

Development ortamında ilk çalıştırmada demo veriler yüklenir (`Seed:Enabled`): 1 firma, 1 şube, 7 kategori, 16 ürünlük "Örnek" etiketli demo katalog ve `elif@lal.local` (Staff), `yonetici@lal.local` (Manager), `sahip@lal.local` (Owner) hesapları; parola `Seed:DevPassword` değeridir. 
## Ekranlar (Aşama 2)

`/giris` · `/` ana sayfa · `/prova/yeni` (müşteri onayı) · `/prova/:id/fotograf` · `/tur` · `/takim` · `/kombin` · `/uretim/:jobId` · `/sonuc` · `/revizyon` (arayüz) · `/karsilastir` · `/gecmis` · `/tv-cihazlar` (yer tutucu) · `/katalog`, `/katalog/urun/:id`, `/katalog/kategoriler` (Manager+). Ekran görüntüleri: [docs/screenshots/asama-2/](docs/screenshots/asama-2/).

## API istemcisi (OpenAPI)

Frontend tipleri backend'in OpenAPI dokümanından üretilir (`frontend/openapi/openapi.json` → `frontend/src/shared/api/schema.d.ts`). Backend sözleşmesini değiştirdiyseniz:

```powershell
./scripts/gen-api-client.ps1   # API'yi geçici olarak başlatır, dokümanı ve tipleri yeniler
```

Kayıtlı doküman güncel değilse entegrasyon testi (`OpenApiContractTests`) kırılır.

## Testler

```powershell
. ./scripts/dev-env.ps1
cd backend; dotnet test --solution SanalProva.slnx           # birim + entegrasyon (sanalprova_test)
cd ../frontend; npm run typecheck; npm run lint; npm test; npm run build
cd ../image-worker; .venv\Scripts\python -m pytest
```

- **Entegrasyon testleri yalnızca `sanalprova_test`'te çalışır** ve başlangıçta onun verisini sıfırlar. `ApiFactory` iki kez korunur: bağlantı dizesinde veritabanı adı `sanalprova_test` değilse veya örnek `SQLEXPRESS` ise bağlanmadan reddeder; bağlandıktan sonra sunucunun bildirdiği veritabanı/örnek adını yeniden doğrular. Bağlantı yapılandırılmamışsa veritabanı testleri atlanır. Testlerde AI anahtarları etkisizdir (`Generation:Budget:AllowPaidCalls=false`); gerçek AI çağrısı yapılmaz.
- **Migration laboratuvarı testleri** (`MigrationValidationTests` vb.) her senaryoda kendi geçici `sanalprova_lab_<8hex>` veritabanını kurar ve siler; veritabanı oluşturmak sunucu yetkisi ister, bu yüzden yalnızca `SANALPROVA_LAB_ADMIN` verilirse çalışır (yoksa atlanır):
  ```powershell
  $env:SANALPROVA_LAB_ADMIN = 'Server=.\SANALPROVA;Integrated Security=True;TrustServerCertificate=True;Encrypt=False'
  ```
- `ImageWorkerContractTests` C#–Python sözleşmesini gerçek servisle sınar; image-worker çalışmıyorsa atlanır.
- **Tarayıcı E2E (Playwright)** izole çalışır: atılabilir `sanalprova_e2e` veritabanı + geçici depo, mock sağlayıcı. `playwright.config.ts` ortam hazır değilse veya veritabanı adı `sanalprova_e2e` değilse çalışmaz; çalışan sunucuyu yeniden kullanmaz.
  ```powershell
  . ./scripts/dev-env.ps1
  . ./scripts/e2e-env.ps1 up          # DB + migration + geçici depo + rastgele parola (yalnızca bu oturumda)
  cd frontend; npm run test:e2e; cd ..
  . ./scripts/e2e-env.ps1 down        # DB ve geçici depoyu siler
  ```
- Sözleşme: backend API'si değişince `./scripts/gen-api-client.ps1`; `OpenApiContractTests` kayma varsa kırılır.

## Yapılandırma ve gizli değerler

Gizli değerler **asla** kaynak koda veya `appsettings*.json` dosyasına yazılmaz. Geliştirmede `dotnet user-secrets`, üretimde ortam değişkenleri kullanılır (`__` ayırıcı):

| Anahtar | Ortam değişkeni | Açıklama |
|---|---|---|
| `ConnectionStrings:Main` | `ConnectionStrings__Main` | SQL Server bağlantısı (`sanalprova_app`, en az yetkili; biçim: "Veritabanı" bölümü). Migrator/Test bağlantıları yalnızca user-secrets'ta |
| `Storage:SigningKey` | `Storage__SigningKey` | İmzalı dosya URL'i HMAC anahtarı (≥32 karakter) |
| `ImageWorker:InternalKey` | `ImageWorker__InternalKey` | Backend ↔ image-worker paylaşılan anahtar |
| `Ai:Fashn:ApiKey` | `Ai__Fashn__ApiKey` | Aşama 4 |
| `Ai:Gemini:ApiKey` | `Ai__Gemini__ApiKey` | Aşama 4 |
| `Generation:UseMockProviders` | `Generation__UseMockProviders` | `true` iken hiçbir AI sağlayıcısı çağrılmaz |
| `Security:LoginRateLimitPerMinute` | `Security__LoginRateLimitPerMinute` | Giriş hız sınırı (varsayılan 5/dk/IP). Yalnızca otomatik tarayıcı testlerinde yükseltilir |
| `Retention:SweepIntervalSeconds` | `Retention__SweepIntervalSeconds` | Süresi dolan prova oturumlarını silme aralığı (varsayılan 900 sn) |

image-worker: `IMAGE_WORKER_INTERNAL_KEY`, `IMAGE_WORKER_SEGMENTATION` (`mock` \| `sam2`), `IMAGE_WORKER_MAX_UPLOAD_MB`.

Frontend'e hiçbir gizli değer verilmez; API anahtarları yalnızca backend'dedir.
