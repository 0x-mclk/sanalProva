# 02 — Sistem Mimarisi ve Klasör Yapısı

## 1. Mimari yaklaşım: modüler monolit

Ekip küçük, yük düşük (~200 üretim/gün), asıl karmaşıklık AI entegrasyonunda. Bu yüzden:

- **Tek ASP.NET Core API** (REST + SignalR + arka plan worker aynı süreçte, ayrı ölçeklenebilir).
- **Tek Python görsel işleme servisi** (OpenCV, ileride SAM 2). Ayrı çünkü dil/kütüphane ekosistemi farklı ve ileride GPU makinesine taşınabilmeli.
- **PostgreSQL** hem iş verisi hem iş kuyruğu.
- Mikroservis, mesaj kuyruğu, Redis, Kubernetes **yok**. Gerçek ihtiyaç doğarsa eklenir; iç sınırlar buna hazırdır.

## 2. Bileşen diyagramı

```
 ┌──────────────────────┐        ┌──────────────────────┐
 │  Personel PWA        │        │  Smart TV (/tv)      │
 │  React + TS + Vite   │        │  aynı build, hafif   │
 │  telefon / tablet    │        │  ayrı giriş noktası  │
 └──────┬─────────┬─────┘        └─────┬──────────┬─────┘
   HTTPS│REST     │WSS SignalR    HTTPS│REST      │WSS SignalR
        │         │ /hubs/staff        │          │ /hubs/display
 ┌──────▼─────────▼────────────────────▼──────────▼─────────────┐
 │                 ASP.NET Core (SanalProva.Api)                │
 │  ┌───────────┐ ┌───────────┐ ┌───────────┐ ┌──────────────┐  │
 │  │ Auth &    │ │ Catalog   │ │ TryOn     │ │ Display (TV) │  │
 │  │ Tenancy   │ │           │ │ Sessions  │ │ Pairing      │  │
 │  └───────────┘ └───────────┘ └─────┬─────┘ └──────────────┘  │
 │                                    │ iş oluşturur            │
 │  ┌─────────────────────────────────▼──────────────────────┐  │
 │  │ Generation: JobQueue (PG) → GenerationWorker           │  │
 │  │   GenerationStrategy → Provider adapter'ları           │  │
 │  │   PromptBuilder · ImagePreprocessor · ResultValidator  │  │
 │  └───┬────────────┬───────────────┬───────────────┬───────┘  │
 │      │            │               │               │          │
 │  ┌───▼───┐  ┌─────▼────┐  ┌───────▼──────┐  ┌─────▼───────┐  │
 │  │Storage│  │ EF Core  │  │ RetentionJob │  │ AuditLog    │  │
 │  └───┬───┘  └─────┬────┘  └──────────────┘  └─────────────┘  │
 └──────┼────────────┼──────────────────────────────────────────┘
        │            │                 │ HTTP (iç ağ)      │ HTTPS
 ┌──────▼─────┐ ┌────▼──────┐   ┌──────▼───────────┐ ┌─────▼─────────────┐
 │ Yerel disk │ │PostgreSQL │   │ image-worker     │ │ FASHN API         │
 │ → S3 uyumlu│ │           │   │ Python FastAPI   │ │ Google Gemini API │
 └────────────┘ └───────────┘   │ OpenCV (+SAM 2)  │ │ (Bria - ops.)     │
                                └──────────────────┘ └───────────────────┘
```

## 3. Teknoloji seçimleri

| Katman | Seçim | Gerekçe |
|---|---|---|
| Backend | .NET 10 (LTS), ASP.NET Core Minimal API + endpoint grupları | LTS, sade, OpenAPI yerleşik |
| ORM | EF Core + Npgsql | İstendi; migration yönetimi |
| Kimlik | ASP.NET Core Identity, **HttpOnly cookie** | PWA aynı origin'den servis edilir; JWT'yi localStorage'da tutmaktan daha güvenli |
| Gerçek zamanlı | SignalR | İstendi; otomatik yeniden bağlanma |
| Doğrulama | FluentValidation | Okunabilir kurallar |
| Log | Serilog (JSON) + PII maskeleme | Yapılandırılmış log |
| Görsel ön işleme (.NET) | SixLabors.ImageSharp *(lisans kontrolü gerekli — ticari kullanımda ücretli olabilir; alternatif: SkiaSharp)* | EXIF temizleme, boyutlandırma |
| Frontend | React 19 + TypeScript + Vite | İstendi |
| UI kütüphanesi | **Mantine** | Dokunmatik dostu, tema sistemi güçlü, hazır form/modal/notification; Tailwind gerektirmez |
| Veri çekme | TanStack Query | Önbellek, yeniden deneme, iyimser güncelleme |
| Yönlendirme | React Router | |
| Formlar | react-hook-form + zod | |
| API istemcisi | OpenAPI'den otomatik üretim (orval) | Frontend–backend sözleşme uyumu |
| PWA | vite-plugin-pwa (Workbox) | |
| Görsel servis | Python 3.12, FastAPI, OpenCV (headless), NumPy | GPU bağımlılığı **yok**; SAM 2 opsiyonel ve ayrı profil |
| Geliştirme ortamı | Docker Compose (PostgreSQL + image-worker) | API ve web yerelde `dotnet run` / `vite` ile |

> Aşama 1'de tüm paket sürümleri o günkü kararlı sürümlere sabitlenecek.

## 4. Modüller (backend içi sınırlar)

Her modül kendi klasöründe: endpoint'ler, istek/yanıt DTO'ları, doğrulayıcılar, servis. Modüller birbirinin tablolarına doğrudan yazmaz; servis çağırır.

| Modül | Sorumluluk |
|---|---|
| **Identity & Tenancy** | Giriş, roller, kullanıcı-mağaza ilişkisi, `ITenantContext` (aktif organization/store) |
| **Catalog** | Kategoriler, ürünler, varyantlar, görseller, hazır takım bileşenleri |
| **TryOn** | Oturum, onay kaydı, müşteri fotoğrafları, seçimler, sonuç sürümleri |
| **Generation** | İş kuyruğu, worker, strateji, prompt üretimi, sağlayıcı adapter'ları, maliyet kaydı |
| **Display** | TV kayıt/eşleştirme, oturum-TV bağlama, TV durumu, DisplayHub |
| **Files** | `IImageStorageService`, imzalı URL, `stored_files` metadata |
| **Compliance** | Saklama/otomatik silme işi, audit log, veri silme talebi |

### Bilinçli olarak *yapılmayanlar*
- Repository pattern (EF Core `DbContext` zaten unit-of-work + repository).
- MediatR / CQRS altyapısı. Endpoint → servis metodu yeterli.
- Domain event bus. Gerektiğinde basit in-process event.
- Her şey için interface. Interface yalnızca **değiştirilebilir dış bağımlılıklar** için: AI sağlayıcıları, depolama, görsel servis, saat (`TimeProvider`).

## 5. AI üretim akışı (uçtan uca)

```
Personel "Üret"
  │ POST /sessions/{id}/generations   (Idempotency-Key başlığı)
  ▼
TryOn servisi: oturum açık mı? onay var mı? fotoğraf var mı? ürünler aktif mi? kota?
  │ generation_jobs INSERT (status=Queued)  → 202 Accepted {jobId}
  ▼
GenerationWorker (BackgroundService, N paralel slot)
  │ SELECT ... FOR UPDATE SKIP LOCKED  → status=Running, attempt++
  │ 1. GenerationStrategy: job tipi + yapılandırma → hangi provider/model
  │ 2. ImagePreprocessor: EXIF sil, yönü düzelt, boyutlandır, JPEG/PNG
  │ 3. PromptBuilder: ürün metadata'sı → sürümlü şablon → prompt
  │ 4. Provider.ExecuteAsync(...)  (FASHN: run + poll; Gemini: senkron)
  │ 5. Sonucu indir → Storage'a yaz → generated_images INSERT
  │ 6. ResultValidator: boyut, boş/siyah görsel, (ops.) yüz tespiti
  │ 7. ai_usage_logs INSERT (model, süre, kredi/maliyet)
  │ status=Succeeded
  ▼
SignalR StaffHub → group session:{id}  "JobUpdated"
  (Personel ekranı sonucu gösterir; personel "TV'ye gönder" der)
```

**Hata ve tekrar politikası**
- Geçici hata (HTTP 429, 5xx, zaman aşımı): üstel bekleme ile en fazla 3 deneme (`next_attempt_at`).
- Kalıcı hata (girdi doğrulama, içerik politikası reddi): tekrar yok, kullanıcıya anlamlı mesaj.
- Worker çökmesi: `Running` durumunda `lease_until` süresi geçen işler tekrar kuyruğa alınır.
- Sağlayıcı kesintisi: devre kesici (Polly) — art arda hatada sağlayıcı geçici olarak kapatılır, alternatif sağlayıcı tanımlıysa ona düşer.

## 6. Depolama stratejisi

- `IImageStorageService`: `SaveAsync(stream, key, contentType)`, `OpenReadAsync(key)`, `DeleteAsync(key)`, `GetSignedUrl(key, ttl)`.
- İlk uygulama: `LocalFileStorage` → `storage/` klasörü (web kökünün **dışında**). İmzalı URL = API'nin `/files/{key}?exp=..&sig=..` uç noktası (HMAC).
- Sonra: `S3Storage` (MinIO / AWS / Cloudflare R2 vb.) → doğrudan presigned URL.
- Anahtar şeması: `{orgId}/{storeId}/sessions/{sessionId}/{fileId}.jpg`, `{orgId}/catalog/{productId}/{fileId}.jpg`. Oturum klasörü tek seferde silinebilir.
- Her dosyanın metadata'sı `stored_files` tablosunda (boyut, sha256, içerik türü, saklama son tarihi).
- AI sağlayıcısına görsel **kısa ömürlü imzalı URL** veya **base64** ile gönderilir; kalıcı public URL yok.

## 7. Klasör yapısı

```
sanalprova/
├── docs/                              # Bu planlama dokümanları
│   └── design/                        # Figma dışa aktarımları (K6)
├── backend/
│   ├── SanalProva.sln
│   ├── src/
│   │   ├── SanalProva.Api/            # Host: Program.cs, DI, middleware, hub'lar, endpoint'ler
│   │   │   ├── Features/
│   │   │   │   ├── Auth/
│   │   │   │   ├── Catalog/           # Categories, Products, Variants, Images
│   │   │   │   ├── TryOn/             # Sessions, Photos, Selections, Results
│   │   │   │   ├── Generation/        # Job endpoint'leri + Worker
│   │   │   │   ├── Display/           # Pairing, DisplayHub, DisplayState
│   │   │   │   └── Admin/             # Stores, Users, Usage
│   │   │   ├── Hubs/                  # StaffHub, DisplayHub
│   │   │   ├── Infrastructure/        # ProblemDetails, tenancy middleware, rate limit
│   │   │   └── appsettings*.json
│   │   ├── SanalProva.Domain/         # Entity'ler, enum'lar, iş kuralları (bağımlılıksız)
│   │   └── SanalProva.Infrastructure/
│   │       ├── Persistence/           # AppDbContext, konfigürasyonlar, Migrations/
│   │       ├── Storage/               # LocalFileStorage, S3Storage
│   │       ├── Ai/
│   │       │   ├── Abstractions/      # ITryOnProvider, IImageGenerationProvider, IImageEditProvider
│   │       │   ├── Mock/
│   │       │   ├── Fashn/
│   │       │   ├── Gemini/
│   │       │   ├── Bria/              # Aşama 5, opsiyonel
│   │       │   └── Prompts/           # Sürümlü şablonlar (*.txt / *.json)
│   │       └── ImageWorker/           # Python servis istemcisi (IImageSegmentationService, IRecolorService)
│   └── tests/
│       ├── SanalProva.UnitTests/
│       └── SanalProva.IntegrationTests/   # WebApplicationFactory + Testcontainers PostgreSQL
├── frontend/
│   ├── package.json
│   ├── vite.config.ts
│   ├── index.html                     # Personel uygulaması
│   ├── tv.html                        # TV giriş noktası (ayrı, hafif bundle)
│   ├── public/                        # PWA ikonları, manifest
│   └── src/
│       ├── app/                       # Router, providers, tema
│       ├── tv/                        # TV uygulaması (pairing, display)
│       ├── features/
│       │   ├── auth/
│       │   ├── home/
│       │   ├── session/               # Yeni prova, onay, fotoğraf
│       │   ├── catalog-picker/        # Hazır takım + serbest kombin seçimi
│       │   ├── generation/            # Durum, sonuç, revizyon, karşılaştırma
│       │   ├── history/
│       │   ├── displays/              # TV eşleştirme ve yönetim
│       │   └── catalog-admin/         # Ürün yönetimi
│       ├── shared/
│       │   ├── api/                   # orval ile üretilen istemci + fetch sarmalayıcı
│       │   ├── realtime/              # SignalR bağlantı yönetimi
│       │   ├── ui/                    # Ortak bileşenler (ProductCard, ImageCompare...)
│       │   └── i18n/                  # tr.json
│       └── test/
├── image-worker/
│   ├── pyproject.toml
│   ├── app/
│   │   ├── main.py                    # FastAPI
│   │   ├── recolor.py                 # OpenCV LAB tabanlı renk değişimi
│   │   ├── segmentation/
│   │   │   ├── base.py
│   │   │   ├── mock.py
│   │   │   └── sam2.py                # opsiyonel, ayrı extra: [sam2]
│   │   └── quality.py                 # Maske ve renk değişimi kalite kontrolü
│   ├── tests/
│   │   └── fixtures/                  # Test görselleri (müşteri fotoğrafı DEĞİL)
│   └── Dockerfile
├── deploy/
│   ├── docker-compose.yml             # postgres, image-worker (geliştirme)
│   └── docker-compose.prod.yml        # Aşama 6
├── .editorconfig
├── .gitignore                         # storage/, .env, *.user
└── README.md
```

## 8. Yapılandırma ve gizli anahtarlar

- Geliştirme: `dotnet user-secrets`; üretim: ortam değişkenleri veya gizli anahtar yöneticisi.
- Frontend'e **hiçbir** AI anahtarı gitmez; `VITE_*` değişkenleri sadece public ayarlar içindir.
- Sağlayıcı seçimi örneği:

```json
"Generation": {
  "Strategies": {
    "OutfitSet":      { "Provider": "Fashn",  "Model": "tryon-max",          "Options": { "resolution": "2k", "generation_mode": "balanced" } },
    "FreeCombination":{ "Provider": "Gemini", "Model": "gemini-3-pro-image" },
    "ProductSwap":    { "Provider": "Gemini", "Model": "gemini-nano-banana-2.1" },
    "Recolor":        { "Provider": "ImageWorker", "Fallback": "ProductSwap" }
  },
  "UseMockProviders": true,
  "MaxParallelJobs": 4
}
```

## 9. Dağıtım (hedef — Aşama 6)

- Tek Linux VM (Türkiye'de barındırma tercih): Docker Compose ile `api`, `image-worker`, `postgres`, reverse proxy (Caddy/Nginx, otomatik TLS).
- Frontend statik dosyaları API tarafından veya reverse proxy'den aynı origin'de servis edilir (cookie + SignalR için basitlik).
- Yedekleme: PostgreSQL günlük yedek; müşteri görselleri **yedeklenmez** (kısa saklama, KVKK).
