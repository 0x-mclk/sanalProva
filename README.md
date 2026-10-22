# Sanal Prova

Mağaza içi, yapay zekâ destekli sanal kıyafet prova sistemi (bindallı, şalvar takımı, kaftan, kına ve özel gün kıyafetleri).

Mimari ve planlama: [docs/](docs/README.md) · Tasarım referansı: [docs/design/reference.png](docs/design/reference.png)

> **Durum:** Aşama 4b kodu tamamlandı (onay bekliyor; **gerçek AI çağrısı yapılmadı**, ücretli çağrılar varsayılan olarak kapalı) — tek kaftan/hazır takım giydirme için Seedream 5 Flash (varsayılan), GPT Image 2.5 ve Pruna adapter'ları, yönetici model/kalite ayarları, bütçe kapıları ve maliyet izleme hazır; FASHN/Gemini eski (pasif). Aşama 3 kodu tamamlandı — katalog, prova akışı ve **Smart TV eşleştirme / gerçek zamanlı görüntüleme** çalışıyor. Tüm AI sağlayıcıları **mock** modda; gerçek/ücretli API çağrısı yapılmaz ve üretilen görseller **DEMO** olarak işaretlenir. Gerçek AI (Aşama 4) ve revizyon motoru (Aşama 5) henüz yok; TV uygulaması gerçek bir mağaza TV'sinde henüz denenmedi.

## Bileşenler

| Klasör | Teknoloji | Port (yerel) |
|---|---|---|
| `backend/` | ASP.NET Core (.NET 10), EF Core + PostgreSQL, SignalR | 5080 |
| `frontend/` | React 19 + TypeScript + Vite PWA, Mantine | 5173 (personel `/`, TV `/tv`) |
| `image-worker/` | Python FastAPI + OpenCV (SAM 2 Aşama 5'te) | 8090 |
| `.local/` | Taşınabilir PostgreSQL 18 + veri klasörü (git'e girmez) | 5433 |

## Gereksinimler

- **.NET 10 SDK** — kullanıcı klasörüne kuruludur (`%LOCALAPPDATA%\Microsoft\dotnet`). Her yeni PowerShell oturumunda önce:
  ```powershell
  . ./scripts/dev-env.ps1
  ```
  (Bu, yalnızca o oturumun PATH'ini değiştirir. Sistemdeki .NET 8 etkilenmez.)
- **Node.js 22+**
- **Python 3.10+**
- **PostgreSQL** — `.local/pgsql` altında taşınabilir sürüm. Yeni bir makinede [EnterpriseDB Windows binaries](https://www.enterprisedb.com/download-postgresql-binaries) (zip) `.local/pgsql` klasörüne açılır.

## İlk kurulum (bir kez)

```powershell
. ./scripts/dev-env.ps1

# 1) Yerel geliştirme veritabanı: rastgele parolalar üretir ve bağlantı dizelerini,
#    imza anahtarını, iç servis anahtarını, demo parolasını dotnet user-secrets'a yazar.
./scripts/db.ps1 init

# 2) Migration (yalnızca sanalprova_dev veritabanına uygulanır)
cd backend
dotnet tool restore
dotnet ef database update -p src/SanalProva.Infrastructure -s src/SanalProva.Api
cd ..

# 3) Frontend bağımlılıkları
cd frontend; npm ci; cd ..

# 4) Görsel servis sanal ortamı (GPU/PyTorch kurulmaz)
cd image-worker
python -m venv .venv
.venv\Scripts\python -m pip install -e ".[dev]"
cd ..
```

## Çalıştırma (üç ayrı terminal)

```powershell
# Terminal 1 — veritabanı + API
. ./scripts/dev-env.ps1
./scripts/db.ps1 start
cd backend; dotnet run --project src/SanalProva.Api --launch-profile http

# Terminal 2 — görsel servis (iç anahtarı user-secrets'tan okur)
./scripts/image-worker.ps1

# Terminal 3 — frontend
cd frontend; npm run dev
```

- Personel uygulaması / geliştirme ekranı: <http://127.0.0.1:5173/>
- TV ekranı: <http://127.0.0.1:5173/tv>
- Sağlık: <http://127.0.0.1:5080/health/ready> · OpenAPI: <http://127.0.0.1:5080/openapi/v1.json>

İlk çalıştırmada (Development) demo veriler yüklenir: 1 firma, 1 şube, 7 kategori ve `elif@lal.local` (Staff), `yonetici@lal.local` (Manager), `sahip@lal.local` (Owner) hesapları. Demo parola:

```powershell
dotnet user-secrets list --project backend/src/SanalProva.Api | Select-String Seed:DevPassword
```

Demo katalog (16 ürün, "Örnek" etiketli stilize görseller) API ilk açıldığında yüklenir. Sıfırlamak için: `./scripts/db.ps1 reseed-demo` ve API'yi yeniden başlatın.

Veritabanını durdurmak için: `./scripts/db.ps1 stop`

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
cd backend; dotnet test --solution SanalProva.slnx           # birim + entegrasyon (sanalprova_test DB)
cd ../frontend; npm run typecheck; npm run lint; npm test; npm run build
cd ../image-worker; .venv\Scripts\python -m pytest
```

- Entegrasyon testleri yalnızca `sanalprova_test` veritabanında çalışır ve başlangıçta onu temizler. Bağlantı yoksa veritabanı testleri atlanır.
- `ImageWorkerContractTests` C#–Python sözleşmesini gerçek servisle sınar; image-worker çalışmıyorsa atlanır.

## Yapılandırma ve gizli değerler

Gizli değerler **asla** kaynak koda veya `appsettings*.json` dosyasına yazılmaz. Geliştirmede `dotnet user-secrets`, üretimde ortam değişkenleri kullanılır (`__` ayırıcı):

| Anahtar | Ortam değişkeni | Açıklama |
|---|---|---|
| `ConnectionStrings:Main` | `ConnectionStrings__Main` | PostgreSQL bağlantısı |
| `Storage:SigningKey` | `Storage__SigningKey` | İmzalı dosya URL'i HMAC anahtarı (≥32 karakter) |
| `ImageWorker:InternalKey` | `ImageWorker__InternalKey` | Backend ↔ image-worker paylaşılan anahtar |
| `Ai:Fashn:ApiKey` | `Ai__Fashn__ApiKey` | Aşama 4 |
| `Ai:Gemini:ApiKey` | `Ai__Gemini__ApiKey` | Aşama 4 |
| `Generation:UseMockProviders` | `Generation__UseMockProviders` | `true` iken hiçbir AI sağlayıcısı çağrılmaz |
| `Security:LoginRateLimitPerMinute` | `Security__LoginRateLimitPerMinute` | Giriş hız sınırı (varsayılan 5/dk/IP). Yalnızca otomatik tarayıcı testlerinde yükseltilir |
| `Retention:SweepIntervalSeconds` | `Retention__SweepIntervalSeconds` | Süresi dolan prova oturumlarını silme aralığı (varsayılan 900 sn) |

image-worker: `IMAGE_WORKER_INTERNAL_KEY`, `IMAGE_WORKER_SEGMENTATION` (`mock` \| `sam2`), `IMAGE_WORKER_MAX_UPLOAD_MB`.

Frontend'e hiçbir gizli değer verilmez; API anahtarları yalnızca backend'dedir.
