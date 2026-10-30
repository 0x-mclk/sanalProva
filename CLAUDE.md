# CLAUDE.md — Sanal Prova

Mağaza içi, yapay zekâ destekli sanal kıyafet prova sistemi (bindallı, şalvar takımı, kaftan, kına ve özel gün kıyafetleri). Personel telefon/tablet ile müşterinin boydan fotoğrafını çeker, hazır takım veya serbest kombin seçer, AI sonucu üretir; sonuç ileride Smart TV'de gösterilecek. Ticari kullanıma yönelik: güvenlik, KVKK, sürdürülebilirlik önceliklidir. **Dil: kullanıcıyla ve arayüzde Türkçe.**

## Çalışma kuralları (kullanıcıyla anlaşma)

- **Aşama sırası ve onay:** Her aşama bittiğinde dur, rapor ver, **kullanıcı onayı almadan sonrakine geçme.** Aşamadan önce alt görev sırasını göster.
- Ücretli AI API çağrısı yapma. Gerçek müşteri verisi kullanma. Gereksiz bağımlılık ekleme. API anahtarlarını kaynağa yazma.
- Migration'ı yalnızca izole lab/test veritabanlarına (`sanalprova_lab_*`, `sanalprova_test`, `sanalprova_e2e`) uygula (önce hedefi doğrula). `sanalprova_dev` M5'e kadar **boş** kalır; kullanıcının açık izni olmadan dokunma. Eski PostgreSQL verilerine, yedeklere ve gerçek storage klasörüne dokunma.
- `docs/` belgelerini ve `docs/design/reference.png` görselini silme. Tasarım dili yalnızca referanstan gelir; kendi UI dilini icat etme.
- Mock/demo sonucu asla gerçek AI çıktısı gibi sunma; yapılmamış özelliği (TV, revizyon motoru) bitmiş gibi gösterme.
- Her modülden sonra build + test çalıştır; hataları gerçek nedenine kadar izle.

## Durum

| Aşama | Durum |
| --- | --- |
| 0 Analiz ve planlama | ✅ tamamlandı (`docs/`) |
| 1 Proje iskeleti | ✅ tamamlandı |
| 2 Katalog ve prova akışı (mock AI) | ✅ tamamlandı |
| 3 TV senkronizasyonu | ✅ kod tamamlandı (gerçek mağaza TV'sinde deneme **yapılmadı**, Aşama 6'da zorunlu) — Aşama 4'e geçiş için kullanıcı onayı bekleniyor |
| 4 Gerçek AI entegrasyonu (FASHN, Gemini) | ✅ kod tamamlandı (mock/sözleşme testleri geçti); **canlı API testi yapılmadı, ücretli çağrılar kapalı** — canlı test ve Aşama 5 için kullanıcı onayı bekleniyor ([docs/11](docs/11-ai-entegrasyon-plani-ve-test.md)) |
| 4b Tek kaftan modelleri (Seedream 5 Flash, GPT Image 2.5, Pruna) | ✅ kod tamamlandı, mock/sözleşme testleri geçti; **canlı test onay bekliyor** ([docs/12](docs/12-tek-kaftan-model-entegrasyonu.md)) |
| 5 Revizyon motoru (Gemini edit, SAM 2 + OpenCV) | bekliyor (onay gerekir) |
| M5.1 Kontrollü onboarding (a–i) | ✅ yerelde tamamlandı ve doğrulandı (birim 266, entegrasyon 324, React 127, Playwright 10/10); **toplu GitHub CI doğrulaması kullanıcıda**; sözleşme metni TASLAK ([docs/13](docs/13-m5-1-kontrollu-onboarding.md), [docs/14](docs/14-platform-operator-komutlari.md)). Sıradaki: M5.2 şube/personel yönetimi (onay bekliyor) |
| M3 MSSQL geçişi ve doğrulama (M3.1–M3.10) | ✅ tamamlandı; ayrıntı ve açık riskler: [docs/DEVAM-NOTU.md](docs/DEVAM-NOTU.md). Sıradaki: M4 (onay bekliyor) |
| 6 Test ve yayın hazırlığı | bekliyor |

Onaylı kararlar: K1 hazır takım = FASHN Try-On Max, serbest kombin = Gemini Nano Banana Pro; K2 revizyon varsayılanı = `gemini-nano-banana-2.1` (Lite/Pro ile karşılaştırma mimarisi korunur, model kimlikleri Aşama 4'te gerçek API'den doğrulanır); K3 katalogda olmayan renk = "Renk önizlemesi — stokta olmayabilir"; K4 saklama 24 saat, onayla en fazla 30 gün (yapılandırılabilir, kodda sabit üst sınır); K5 Firma → Şube (katalog firma, oturum/TV şube seviyesinde); K8 18 yaş altı veli onayı.

## Belgeler (önce bunları oku)

Plan `docs/` altında; ayrıntı orada, burada yinelenmez. Başlangıç: [docs/README.md](docs/README.md).

| Belge | Konu |
| --- | --- |
| [01-gereksinim-analizi](docs/01-gereksinim-analizi.md) | Gereksinimler, eksik/çelişkili kararlar (K1–K12) |
| [02-sistem-mimarisi](docs/02-sistem-mimarisi.md) | Bileşenler, iş akışı, klasör yapısı |
| [03-veri-modeli](docs/03-veri-modeli.md) | ER modeli, tablolar |
| [04-api-tasarimi](docs/04-api-tasarimi.md) | Uç noktalar, hata biçimi, yetki matrisi |
| [05-frontend-ekranlar](docs/05-frontend-ekranlar.md) | 15 ekran, akışlar, PWA |
| [06-tv-signalr](docs/06-tv-signalr.md) | **Aşama 3 tasarımı**: eşleştirme, hub'lar, yeniden bağlanma |
| [07-ai-entegrasyon](docs/07-ai-entegrasyon.md) | **Aşama 4–5 tasarımı**: FASHN/Gemini doğrulanmış bilgiler, adapter'lar, revizyon |
| [08-kvkk-guvenlik](docs/08-kvkk-guvenlik.md) | KVKK, saklama, yurt dışı aktarım |
| [09-gorev-listesi](docs/09-gorev-listesi.md) | Aşama 1–6 görevleri ve çıkış kriterleri |
| [10-test-ve-kabul](docs/10-test-ve-kabul.md) | Kabul kriterleri, test planı |
| [11-ai-entegrasyon-plani-ve-test](docs/11-ai-entegrasyon-plani-ve-test.md) | **Aşama 4**: doğrulanan API bilgileri, bütçe kapıları, onay bekleyen canlı test planı |
| [13-m5-1-kontrollu-onboarding](docs/13-m5-1-kontrollu-onboarding.md) | **M5.1 tasarımı** (uygulandı): platform yöneticisi, onboarding, kurulum bağlantısı, hizmet sözleşmesi, ücretli AI kapısı |
| [14-platform-operator-komutlari](docs/14-platform-operator-komutlari.md) | **Operatör komutları**, arayüz, SignalR ve `PaidAiEnabled` yarış sınırları |
| [12-tek-kaftan-model-entegrasyonu](docs/12-tek-kaftan-model-entegrasyonu.md) | **Güncel modeller**: Seedream/GPT Image/Pruna, model-kalite ayarları, KVKK (Pruna saklama), canlı test planı |

`docs/design/reference.png` = Figma referansı (dört ekran). `docs/screenshots/asama-2/` = Aşama 2 ekran görüntüleri.

## Mimari özet

Modüler monolit + ayrı Python servisi. Mikroservis, Redis, mesaj kuyruğu yok.

- `backend/` — ASP.NET Core (**.NET 10**, Minimal API), EF Core + **SQL Server 2025 Express** (örnek `.\SANALPROVA`, snake_case, RCSI açık), SignalR, Identity (HttpOnly çerez + CSRF). `Domain` (bağımlılıksız) / `Infrastructure` / `Api`. Merkezi paket sürümleri `Directory.Packages.props`, `TreatWarningsAsErrors` açık.
- `frontend/` — React 19 + TS + Vite PWA, Mantine, TanStack Query, openapi-fetch. İki giriş noktası: personel (`index.html`) ve TV (`tv.html`, Mantine'siz, küçük paket).
- `image-worker/` — Python FastAPI + OpenCV (GPU/PyTorch yok). Renk değişimi ve SAM 2 için sözleşme ve iskelet var, motor Aşama 5'te.
- `docker-compose.yml` + `backend/Dockerfile` (api, migration paketi) + `image-worker/Dockerfile` + `docker/db/` (konteyner provision/harden): yalıtılmış SQL Server konteyneri, `.env` rastgele parolalarla `scripts/compose-env.ps1|sh` ile üretilir; ``.github/workflows/ci.yml` (backend, frontend, image-worker, compose-smoke, MSSQL entegrasyon, Playwright E2E; `scripts/ci/*.sh` bash ile çağrılır, gizli değerler günlük/artifact'a yazılmaz). **M4.2'de bu makinede Docker yoktu: Compose ve CI fiilen çalıştırılmadı** (ayrıntı `docs/DEVAM-NOTU.md`).
- `scripts/` — `db.ps1` (**MSSQL**: `status` / `migrate` / `reset` / `script`; yalnızca `sanalprova_test`, `sanalprova_e2e`, `sanalprova_lab_<8hex>`; `sanalprova_dev` ve diğer örnekler kodla reddedilir; değiştirenler varsayılan kuru çalışma), `dev-env.ps1`, `e2e-env.ps1` (izole E2E veritabanı up/down), `gen-api-client.ps1`, `gen-migration-script.ps1` (idempotent SQL), `apply-history-hardening.ps1` (dry-run varsayılan), `mssql/` (provision + sertleştirme SQL'leri), `image-worker.ps1`, `generate_icons.py`. Eski PostgreSQL komutları (`init/start/stop/reseed-demo`) kaldırıldı.
- `.local/` (eski taşınabilir PostgreSQL 18 verisi — **dokunulmaz**, yedekler ve `migrations/` çıktıları da burada), `storage/` (yerel dosya deposu) git'e girmez.

Temel kararlar:

- **Firma izolasyonu:** `ITenantOwned` + EF global query filter. **Şube izolasyonu:** `X-Store-Id` başlığı + `StoreAccessFilter` (rol kontrolü) + `IStoreOwned` filtresi. Filtre değerleri sorgu anında `TenantContext`'ten okunur (ara katman/endpoint filtresi doldurur). Katalog yönetimi: firmadaki herhangi bir şubede Manager+ (`OrganizationRoleFilter`).
- **Sağlayıcıdan bağımsız üretim:** `GenerationStrategy` (yapılandırmadan) iş tipine göre sağlayıcı/model seçer; adapter'lar `ITryOnProvider`, `IImageGenerationProvider`, `IImageEditProvider`; mock modda bile yapılandırılmış model adı kaydedilir. Kullanıcı prompt yazmaz (`PromptBuilder` sunucuda üretir).
- **İş kuyruğu SQL Server'da:** `generation_jobs`, tek deyimde `UPDATE … OUTPUT` + `UPDLOCK, READPAST` ile talep (eski `FOR UPDATE SKIP LOCKED` karşılığı), `attempt` çit belirteci, lease/yeniden kuyruğa alma, geçici hata geri çekilmesi; sürüm numarası, şube kabulü ve firma bütçesi için `sp_getapplock` (`AppLocks`). `GenerationWorker` + `GenerationProcessor` (Infrastructure), bildirim `IJobNotifier` (SignalR uygulaması Api'de).
- **Sunucu-otoritatif durum:** SignalR yalnızca "değişti" bildirir; istemci REST'ten çeker, yoklama yedeği vardır.
- **Görseller:** hiçbir zaman public değil; HMAC imzalı 5 dk (müşteri) / 1 saat (katalog) URL. Yükleme SkiaSharp ile yeniden kodlanır (EXIF silinir, magic byte doğrulanır, küçük resim üretilir). Anahtar şeması oturum önekiyle (`SessionPurger` tek seferde siler).
- **KVKK:** onay olmadan fotoğraf yüklenemez (sunucu zorunlu kılar); `SessionPurger` + `RetentionJob` süresi dolanı kalıcı siler; denetim kaydı kişisel veri içermez.
- **Sözleşme:** frontend tipleri OpenAPI'den üretilir (`frontend/openapi/openapi.json` → `src/shared/api/schema.d.ts`). Backend sözleşmesi değişince `./scripts/gen-api-client.ps1` çalıştır; `OpenApiContractTests` kayma varsa kırılır. Hata biçimi RFC 9457 + `code` alanı; frontend `src/shared/i18n/tr.ts` Türkçe eşler.
- **Tasarım token'ları tek dosyada:** `frontend/src/shared/theme/tokens.ts` (Mantine teması ve `--sp-*` CSS değişkenleri buradan türer).

## Aşama 2'de gelenler

Katalog API'si (kategori, ürün, varyant, görsel, hazır takım bileşenleri; AI referans görseli ürün+varyant+açı başına tek), oturum/onay/fotoğraf/taslak seçim/kapat/sil, üretim hattı (mock sağlayıcı, DEMO filigranlı çıktı, sürümleme, kota, iptal, tekrar dene, idempotency), saklama temizliği, revizyon **arayüzü** (motor yok: sunucu 501), prova geçmişi, katalog yönetim ekranları, demo katalog (16 ürün, `Örnek` etiketli çizilmiş görseller; `OrganizationSettings.IsDemoCatalog`). TV yalnızca "Henüz bağlı değil" yer tutucusu.

## Aşama 3'te gelenler

TV eşleştirme (6 haneli kod, HMAC'li, tek kullanımlık, kullanıcı başına hatalı deneme kilidi + IP hız sınırları), ayrı `DeviceToken` kimlik şeması, `DisplayHub` (TV) ve `StaffHub.JoinStore`, sunucu-otoritatif durum sürümü (`DisplayStateService` tek yazma noktası), gizlilik temizliği (oturum kapanışı/silme/onay geri alma/saklama süresi → TV boşalır ve bağlantı kopar; fotoğraf silme → ekran temizlenir; boşta 15 dk), personel TV ekranı + "TV'ye gönder / karşılaştır / temizle / bağlantıyı kes", TV uygulaması (`frontend/src/tv`), Playwright E2E (`frontend/e2e`). Tasarım sapmaları ve çoklu sunucu notları: [docs/06 §8](docs/06-tv-signalr.md#8-aşama-3-uygulama-notları). Ekran görüntüleri: `docs/screenshots/asama-3/`.

## Aşama 4'te gelenler

`FashnTryOnProvider` (run + yoklama, `return_base64`, tahmin kimliği hemen saklanır → zaman aşımında aynı tahmin sorgulanır), `GeminiProvider` (**Interactions API**, `store:false`, `Api-Revision` sabit; kombin + düzenleme), `ModelCatalog` (doğrulanmış kimlikler; hiçbiri `LiveVerified` değil; Lite kombinde reddedilir), `CostEstimator` + `BudgetGuard` (iş/gün/ay sınırı, en kötü durum tahmini), `OutputValidator`, hata eşleme tabloları, çalışan işi iptal, istek parmak izi ile yinelenen isteği engelleme (`allowVariation`), maliyet kaydı (`actual_cost_usd`, `provider_request_id`), yönetici ekranı `/maliyet` (`GET /admin/usage`), TV'nin yeni sonuca kendiliğinden geçmesi (yalnızca tekli sonuç gösteriyorsa), `LiveAiTests` (onay kapılı, varsayılan atlanır).
**Fiyat/performans:** FASHN varsayılanı **balanced + 1k (2 kredi ≈ 0,15 $)**; 2k (3 kredi) alternatif. Yönetici `/maliyet` ekranından (`GET/PUT /admin/ai-quality`, firma ayarı `OrganizationSettings.FashnResolution/FashnGenerationMode`) değiştirir; maliyet tahmini ve bütçe kapısı seçime göre hesaplanır. 1K/2K sadakat farkı **canlı testte** karşılaştırılacak (kalite kanıtlanmadan ucuz seçenek yeterli sayılmaz).
**Ücretli çağrı kapıları (hepsi gerekli):** `Generation:UseMockProviders=false` + `Generation:Budget:AllowPaidCalls=true` + API anahtarı (`Ai__Fashn__ApiKey`, `Ai__Gemini__ApiKey` ortam değişkeni; kaynağa yazılmaz) + bütçe. Varsayılanlar kapalıdır. **Proje sahibinin ayrı onayı olmadan gerçek API çağrısı yapılmaz.**

## Aşama 4b'de gelenler (tek kaftan modelleri)

`TryOnModels` kataloğu + `TryOnPolicyResolver` (firma ayarı: etkin modeller, izinli kaliteler, varsayılanlar), `FalQueueClient` + `FalSeedreamTryOnProvider` + `FalGptImageTryOnProvider`, `PrunaTryOnProvider`, `GET/PUT /admin/ai-models`, `GET /ai/try-on-options`, üretim isteğinde `modelKey`/`quality` (işe sabitlenir; parmak izine dahil), tek kaftan akışı (`CombinationRules.IsSingleTryOnItem`, `/products?tryOn=true`), gerçek modda kombin kapalı (`Generation:LiveFreeCombinationEnabled=false`), kullanım kaydında kalite. Varsayılan model **Seedream 5 Flash**; GPT Image varsayılan kalite **medium**; GPT'nin altı kalitesi (auto/low/medium/high/xhigh/max) desteklenir, auto/xhigh/max varsayılan kapalı (yönetici açar). GPT maliyeti `GptImagePricing` ile kalite × çıktı boyutu + referans görseller + prompt + görsel sayısından hesaplanır (docs/12 §2a); en kötü durumu iş başı sınırı aşan kalite `QUALITY_OVER_BUDGET` ile reddedilir. FASHN eski/pasif (kod korunur). Anahtarlar: `Ai__Fal__ApiKey`, `Ai__Pruna__ApiKey`.

## M5.1'de gelenler (kontrollü onboarding)

- **Operatör CLI'si:** `dotnet SanalProva.Api.dll platform bootstrap|reissue-link|revoke|reset-access|paid-ai` (`PlatformCommands`): web sunucusu başlamaz, HTTP'den çağrılamaz, `--confirm` yoksa yazmaz; bağlantı yalnızca stdout'a bir kez. `IsPlatformAdmin` ve `PaidAiEnabled`'ı yazan TEK yer bu sınıftır (kaynak taraması testleri).
- **Platform API/ekranları:** `POST/GET /platform/organizations` (Idempotency-Key, bağlantı yalnızca ilk yanıtta), `POST …/owner/setup-link`, `POST /account/setup-password` (anonim; consume + şifre + denetim tek transaction); React: `/platform`, `/platform/yeni`, `/platform/:id`, `/hesap/sifre-belirle`, `/sozlesme`.
- **Token:** 256 bit CSPRNG, DB'de `HMAC-SHA256(Security:SetupTokenKey)`; **`Security:SetupTokenKey` zorunlu** (≥32 karakter; user-secrets/ortam; compose/CI/E2E `.env` üretir). Token yalnızca URL fragment'ında/bellekte; token taşıyan çağrılar `useMutation` KULLANMAZ.
- **Hizmet sözleşmesi:** `ServiceAgreement` (sürümlü, SHA-256 birim testle sabit; metin değişirse sürüm artırılır), `organization_agreement_acceptances` append-only. **Zorlama `AgreementEnforcementMiddleware`'dir ve VARSAYILANDIR**: kimlik isteyen her uç nokta (REST + StaffHub), açıkça `AllowWithoutAgreement` taşımıyorsa 403 `AGREEMENT_ACCEPTANCE_REQUIRED` alır; muaf/anonim liste `AgreementTests`'te sabit. Müşteri KVKK onayından tamamen ayrı.
- **Ücretli AI kapısı:** `OrganizationSettings.PaidAiEnabled` (varsayılan false, fail-closed): kabulde (`/generations`, `/retry`) ve `BudgetGuard.ReserveAsync` içinde; mock mod etkilenmez. Test fixture'ı varsayılanı **true** verir (`TestData.CreateTenantAsync(paidAiEnabled:)`).
- **Fixture'lar:** `TestData.CreateTenantAsync` güncel sözleşmeyi kabul edilmiş yazar (`acceptAgreement:false` ile kapatılır); demo tohumu ve E2E `isolation-seed.sql` de öyle.
- **Tuzaklar:** (1) ikinci test sunucusu için `ApiTestBase.DerivedHost` kullanın: `WithWebHostBuilder` üretim işçisini bırakırsa başka testlerin işlerini kapıp yarıda bırakır. (2) Aynı Idempotency-Key ile eşzamanlı isteklerde "slug/e-posta alınmış" kararından önce tekrar yeniden değerlendirilir (aksi halde 409). (3) Aynı sekmede ikinci kurulum bağlantısı yalnızca `hashchange` üretir (router yakalamaz): `SetupPasswordPage` bu olayı dinler. (4) Playwright onboarding senaryosu operatör CLI'sini çalıştırır: API'nin önceden derlenmiş olması (`dotnet build backend/src/SanalProva.Api`) ve `SANALPROVA_E2E_SETUP_TOKEN_KEY` gerekir (`e2e-env.ps1 up` ve CI üretir). (5) Süreç testi API dll'ini test yapılandırmasına (Debug/Release) göre arar. (6) Platform yöneticisinin hiçbir firmada mağaza rolü yoktur: katalog/oturum uçları 403.

## Yerel çalıştırma

Ayrıntı: [README.md](README.md). Özet (PowerShell, .NET 10 kullanıcı klasöründe kurulu — her oturumda önce `. ./scripts/dev-env.ps1`):

```powershell
# Dev veritabanı (sanalprova_dev) M5'e kadar boş: elle çalıştırma için önce yeni bir geliştirme veritabanı kurulumu gerekir (README 'Veritabanı'). Bugün doğrulama: testler + izole E2E (aşağıda).
./scripts/db.ps1 status                                  # MSSQL durumu (salt okunur)
cd backend; dotnet run --project src/SanalProva.Api --launch-profile http   # :5080
./scripts/image-worker.ps1                               # :8090 (isteğe bağlı; Aşama 5)
cd frontend; npm run dev                                 # :5173, /tv → TV girişi
```

Tarayıcı E2E (Playwright; personel + TV ayrı tarayıcılar, API ve Vite'ı kendisi başlatır) **izole** çalışır: atılabilir `sanalprova_e2e` SQL Server veritabanı + geçici depo, mock sağlayıcı; dev/test/PostgreSQL'e dokunmaz. PowerShell'de: `. ./scripts/dev-env.ps1; . ./scripts/e2e-env.ps1 up; cd frontend; npm run test:e2e; cd ..; . ./scripts/e2e-env.ps1 down` (ortam yoksa config hata verir; Windows kimliği SANALPROVA'da yönetici olmalı). Ayrıntı: `docs/DEVAM-NOTU.md`.
Testler: `cd backend; dotnet test --solution SanalProva.slnx` (entegrasyon testleri gerçek `sanalprova_test` DB kullanır; `dotnet test` .NET 10'da `--solution`/`--project` ister) · `cd frontend; npm run typecheck; npm run lint; npm test; npm run build` · `cd image-worker; .venv\Scripts\python -m pytest`.
**Migration doğrulama (M3.2):** `MigrationValidationTests` her senaryoda kendi geçici `sanalprova_lab_<8hex>` veritabanını kurar ve siler (SANALPROVA örneği, dev/test DB'lere dokunmaz). Veritabanı oluşturmak sunucu yetkisi ister; bu yüzden **isteğe bağlıdır** ve yalnızca `SANALPROVA_LAB_ADMIN` verilirse çalışır, aksi halde atlanır: `$env:SANALPROVA_LAB_ADMIN='Server=.\SANALPROVA;Integrated Security=True;TrustServerCertificate=True;Encrypt=False'` (Windows kimliği SANALPROVA'da yönetici olmalı; TCP yerine paylaşılan bellek `.\SANALPROVA`). Dağıtım betiği: `./scripts/gen-migration-script.ps1` (idempotent, `.local/migrations/` altına yazar, DB'ye bağlanmaz). **Politika: migration yalnızca ileri yönlüdür** — `Down` mutabakat/maliyet verisini siler (testle kanıtlı); geri dönüş yedekten yapılır.

**Veri bütünlüğü testleri (M3.3):** `DatabaseIntegrityTests` + `RowFactory` (şema kataloğundan geçerli satır üretir; tüm FK'lar ve tüm enum sütunları veri odaklı sınanır). Tuzaklar: Türkçe collation yalnızca `products.name`'de (`kina` ≠ `Kına`; farklı collation'lı sütunları aynı ifadede karşılaştırmak 468 hatası verir); SQL Server benzersiz indekste NULL = NULL sayar (filtresiz indekste tek NULL, PostgreSQL'den farklı); enum CHECK'leri CI'dır; decimal fazla basamağı sessizce yuvarlar; `sys.database_permissions`'ı migrator göremez (yönetici bağlantısı gerekir); `STRING_AGG` katalog görünümü sütunlarında `COLLATE DATABASE_DEFAULT` ister.

**Tenant tutarlılığı (M3.4):** çocuk satırın firması (ve iş/TV için şubesi) ebeveyniyle aynı olmalıdır; bunu SQL Server zorlar. `organization_id` sütunları korunur; ebeveynlerde `UNIQUE (id, organization_id)` (+ oturumda `(id, store_id, organization_id)`), çocuklarda bileşik FK. SET NULL ilişkileri (iş↔maliyet satırı, TV↔oturum) bileşik FK'da SET NULL kullanamaz (firma sütunu NOT NULL; SQL Server tanımda reddeder): tek sütunlu SET NULL FK + bileşik NO ACTION eşi. Oturuma bağlı tablolarda (fotoğraf, onay, seçim, kalem, sonuç görseli) şube kapsamı EF filtresinde **oturum üzerinden yapısaldır** (`ISessionOwned`/`ISelectionOwned`). Migration `TenantConsistency` önce çapraz firma/şube verisi arar ve varsa 51200 hatasıyla durur. Test verisi üreten her yer tutarlı firma/şube kullanmalıdır (`RowFactory` ebeveynden devralır). Filtre atlayan/ham SQL kullanımı `TenantArchitectureTests` envanterinde gerekçelendirilmelidir. RLS bilinçli olarak yok (belgelenen kalan SELECT riskleri raporda).

**KVKK silme (M3.5):** `SessionPurger` sırası: TV sıfırla → depo (oturum öneki) → DB (cascade, kilitlenmede yeniden dener) → `stored_files` (kimlik + **depolama anahtarı öneki**) → **ikinci depo geçişi** → denetim kaydı. Worker, oturum silinmişken ücretli yanıt alırsa sonucu atar ve maliyeti `job_id` NULL ile yazar (`SaveOrKeepCostAsync`/`JobGoneAsync`); dosya yazılıp kayıt işlenemezse dosyayı siler (Finalize + fotoğraf yükleme). `OrphanSweeper` (RetentionJob içinde; payı `Retention:OrphanGraceMinutes`=60 dk): oturumu olmayan depo klasörleri ve kullanılmayan `stored_files` kayıtlarını siler. Silme testleri `KvkkDeletionTests`: tüm tablolarda uniqueidentifier sütunları katalogdan taranır (tek istisna `audit_logs.entity_id`).

**Yük ölçümü (M3.6):** `LoadMeasurementTests` (90 iş / 6 firma / 16 worker; kota yarışları) ve `PaidProviderPipelineTests` bütçe/kilit testleri doğruluk değişmezlerini zorunlu kılar, hız/kilitlenme/aşım değerlerini `%TEMP%\sanalprova_m36_*.json` dosyalarına yazar (kilitlenme sayacı `SANALPROVA_LAB_ADMIN` ister). **Bütçe kapısı atomiktir:** `BudgetGuard.ReserveAsync` firma başına `sp_getapplock` + tek transaction içinde kesinleşmiş harcama + diğer işlerin **açık rezervasyonlarını** (`generation_jobs.reserved_cost_usd`: Running; Queued+kimlik/niyet; maliyet kaydı yazılmamış Cancelled) toplayıp sınırlara karşı denetler ve yeni rezervasyonu aynı transaction'da yazar (ikinci defter yok; iş maliyet kaydıyla bittiğinde rezervasyon kendiliğinden kapanır, Succeeded/Failed işler sayılmaz → çift sayım yok). Kapsam: firma başına günlük/aylık (UTC) + iş başına; platform veya şube düzeyi limit yok. Kilit alınamazsa iş ücretsiz yeniden kuyruğa girer. Ölçüm: 0,50 $ limit / 0,15 $ çağrı / 16 paralel iş → tam 3 çağrı, 0 aşım.

**Idempotency (M3.7):** `POST /sessions/{id}/generations` anahtarı **oturum başına** benzersizdir; yeniden oynatma ilk yanıtın aynısını döner (tamamlanmışsa `resultImageId` dahil; oturum kapalı olsa da). Aynı anahtar farklı gövdeyle (fotoğraf/model/kalite/gerçek modda varyasyon) → **422 `IDEMPOTENCY_KEY_REUSED`**. Başarısız işe bağlı anahtar yeni deneme için kullanılamaz (yeni anahtar veya `/jobs/{id}/retry`). `/retry` **isteğe bağlı** `Idempotency-Key` alır (M3.8): aynı anahtar aynı yeni işi döner (eşzamanlı çift tıklama/ağ yeniden denemesi mock modda da tek iş), özgün işin anahtarı veya farklı istek → 422 `IDEMPOTENCY_KEY_REUSED`; anahtarsız eski istemci çalışır (mock modda dedupe yok, gerçek modda `ux_generation_jobs_active_dedupe` tek iş garanti eder). Frontend: başlatma anahtarı seçim/model değişince yenilenir, ağ hatasında korunur; retry anahtarı başarısız iş başınadır (`GeneratingPage`). Tekrar istekleri iş açmadığı için ikinci bütçe rezervasyonu/gönderim oluşmaz.

Demo hesaplar: `elif@lal.local` (Staff), `yonetici@lal.local` (Manager), `sahip@lal.local` (Owner); parola `dotnet user-secrets list --project backend/src/SanalProva.Api` içindeki `Seed:DevPassword`. Gizli değerlerin hepsi user-secrets'ta (geliştirme ortamı kurulumu M4'te yeniden yazılacak; E2E parolayı kendisi üretir). Giriş hız sınırı 5/dk/IP; çok girişli otomasyonda `Security__LoginRateLimitPerMinute` yükseltilir.

## Dikkat edilecek tuzaklar (yaşanmış hatalar)

- **Enum'lar DB'de metin:** SQL `MAX/ORDER BY` alfabetik olur; rol/enum karşılaştırmalarını bellekte yap.
- **Yeni varlığı gezinti özelliğiyle eklemek EF'te "Modified" sanılır** (anahtar önceden üretildiği için): açıkça `db.X.Add(...)` kullan.
- **Aynı oturumda paralel üretim:** sürüm numarası `sp_getapplock` (`AppLocks.SessionVersion`) olmadan çakışır.
- **`WebApplicationFactory` ana makineyi eşzamanlı ilk erişimlerde birden çok kez kurar:** `ApiFactory.InitializeAsync` içinde `_ = Services` ile tek seferde oluşturulur; aksi halde birden çok worker bildirimleri kaybettirir.
- **Entegrasyon testleri:** hata enjeksiyonu (`TestFaults`) fotoğraf özetine bağlı; hata testleri benzersiz fotoğraf (`seed`) kullanmalı. SignalR testleri WebSocket taşıması kullanır (long polling TestServer'da güvenilmez).
- **React:** çocuk efektleri ebeveynden önce çalışır; ilk sorgulardan önce gerekli global durum (`X-Store-Id`) `useLayoutEffect` ile ayarlanır. `QueryClient.clear()` aktif `useMe` gözlemcisini koparır (`removeQueries` + `resetQueries` kullan). `openapi-fetch` gövdeyi tükettiği için hata ayrıntısı `unwrap` içinde `error` nesnesinden okunur.
- **TV belirteci = eşleştirme gizli değeri** (claim anında hash'i cihaza taşınır); düz belirteç saklanmaz. Kod hash'lendiği için hatalı tahmin kayda bağlanamaz: sınır kullanıcı başınadır (`ClaimAttemptLimiter`, bellek içi).
- **Aynı kodu aynı anda kullanan iki istek:** cihaz `token_hash` benzersiz indeksi ve koşullu `UPDATE` ikisini ayırır; benzersizlik hatası "kod geçersiz" sayılır.
- **TV durumunu yalnızca `DisplayStateService.MutateAsync` değiştirir** (sürüm + eşzamanlılık belirteci). `last_seen_at` `ExecuteUpdate` ile yazılır, yoksa boşta temizleme/sürüm bozulur. Yanıttaki sürüm eşzamanlı yazımlarda "sonraki" olabilir; testler `başlangıç + N` ile doğrular.
- **Hub testleri:** .NET SignalR istemcisi WebSocket'te belirteci başlıkla gönderir (özel `WebSocketFactory` ile başlık kaybolur) — testler tarayıcı gibi `access_token` sorgu parametresi ekler; istemciye `JsonStringEnumConverter` eklenmeli (sunucu enum'ları metin gönderir); `StartAsync` bittiğinde `OnConnected` (grup üyeliği) henüz bitmemiş olabilir → `DisplayPresence` ile bekle.
- **TV CSS'i eski tarayıcıya göre yazılır** (flex `gap`, `aspect-ratio`, `inset` yok). TV görsel `key`'i `image.id` olmalı (imza her çekmede değişir, URL değil).
- **react-hooks v7 lint:** `useMemo(fn, [])` yalnızca satır içi fonksiyonla; efekt gövdesinde senkron `setState` yasak (geri çağrı içinde olmalı).
- **Bash heredoc'ta apostrof/uzun metin içeren dosya yazmak kırılgan:** Write ile dosyaya yaz, betiği dosyadan çalıştır.
- **Gemini görsel üretimi `generateContent` değil Interactions API'dir** (`/v1beta/interactions`, `Api-Revision: 2026-05-20`, çıktı `steps[model_output].content[image]`); `thought` adımlarındaki ara görseller sonuç değildir. Gemini güvenlik-ret yanıt şekli dokümanda yok (varsayım, canlı testte doğrulanacak).
- **Çift ücret:** başarısız FASHN tahmini ücretsiz (yeni tahmin açılabilir), ama **zaman aşımında aynı tahmin sorgulanmalı** (`ProviderRequestId`); geçersiz çıktıyı otomatik yeniden deneme yok; iptal edilen/çağrısı başlamış iş en kötü durum maliyetiyle kaydedilir.
- **Entegrasyon testlerinde "gerçek sağlayıcı" yolu:** ana test host'u mock'tur ve paralel worker'ı iş kapar. Ayarları değiştirmek yerine `PaidProviderPipelineTests` kendi `ServiceCollection`'ını kurar, işi "Running" yazar ve `GenerationProcessor.ProcessClaimedAsync` ile çalıştırır. Gerçek-mod HTTP doğrulaması için `RealModeApiFactory` (worker yoklaması 1 saat) kullanılır.
- **C# test verisi:** enterpolasyonlu ham dizede (`$$` + üç tırnak) `}}` ile biten JSON için bir `$` daha gerekir; `Options(...)` adlı yardımcı `Options.Create`'i gölgeler.
- **fal kuyruk adresleri:** gönderim tam model yoluna (`bytedance/seedream/v5/flash/edit`), durum/sonuç/iptal **sahip/uygulama** yoluna (`bytedance/seedream/requests/{id}/status`) gider (doküman: tam yol 405). Seedream kimliğinde `fal-ai/` öneki YOK (404). fal ve Pruna istek başına maliyet döndürmez: maliyet tahmindir.
- **GPT Image fiyat tablosu boyutla tekdüze değil** (1024×1024, 1024×1536'dan pahalı): en kötü durum o boyuta kadarki en pahalı satırı alır. `auto` kalitesi en kötü durumda max sütunudur. Yapılandırmadaki fiyat ezmesi yalnızca tahmini/en kötüyü ayrı ayrı ezer; en kötü durum tahminden küçük olamaz.
- **Pruna base64 kabul etmez:** görseller `/v1/files`'a geçici yüklenir (otomatik silme, elle silme yok). KVKK notu docs/12 §4.
- **İki test sunucusu aynı DB'yi paylaşırken** (`RealModeApiFactory`) aynı depolama kökünü kullanmalı; yoksa ana worker diğerinin işini "girdi görseli yok" ile başarısız eder.
- **VS Code NETSDK1045 (Problems panelinde ~220 hata):** C# Dev Kit proje sistemi SDK'yı VS Code sürecinin **PATH**'inden bulur (`dotnet.dotnetPath` yalnızca dil sunucusunun çalışma zamanını değiştirir). Sistemde yalnızca SDK 8 varken projeler yüklenemiyordu; çözüm .NET 10 SDK'nın sistem genelinde kurulmasıydı (2026-10-22). Proje yüklenmezken görülen CS0518/CS0246 ve "gereksiz using" (IDE0005) kayıtları bunun yan etkisidir; CLI build'i etkilemez.
- **EF araçları ve migration paketi `AppDbContextFactory` (tasarım zamanı fabrikası) kullanır:** uygulama host'u/`ConnectionStrings:Main`/user-secrets yüklenmez; bağlantı her zaman `--connection` ile verilir (verilmezse yer tutucu erişilemez porta gider). Fabrikanın model ayarları `AddInfrastructure` ile aynı kalmalı (`AppDbContextFactoryTests` bekleyen model değişikliğini yakalar).
- **Docker SQL Server'da `MSSQL_COLLATION` kullanılmaz** (CI #7: ilk açılışta sunucu harmanlaması değişimi sistem veritabanlarını yeniden indeksler, iç deadlock ile 3434 → sunucu kapanır). Harmanlama **veritabanı** düzeyinde (`CREATE DATABASE … COLLATE Latin1_General_100_CI_AS_SC`); Docker'da sunucu/`tempdb` `SQL_Latin1_General_CP1_CI_AS` (Windows SANALPROVA'da ikisi de `Latin1_General_100_CI_AS_SC`). Metin sütunlu `#temp` tablosu yazılırsa `COLLATE DATABASE_DEFAULT` şart.
- SkiaSharp 4: `SKPath` değiştirici yöntemleri kullanımdan kalkıyor (yalnızca dev `DemoArt.cs` dosyasında CS0618 bastırıldı).
- Windows PowerShell 5.1 (`RandomNumberGenerator.Fill` yok, `-Environment` yok), `.ps1` dosyaları UTF-8 BOM + CRLF olmalı; bash heredoc'ta çok dosya yazmak kırılgan — dosyaları Write ile yaz.
- `.NET 10` Minimal API: form endpoint'lerinde `.DisableAntiforgery()` (grup filtresi zaten CSRF'i doğrular); `AddValidation()` DataAnnotations'ı çalıştırır; bağlama hataları `BadRequestExceptionHandler` ile 4xx döner.

## Sıradaki işler

**Aşama 3, 4 ve 4b kodu tamamlandı.** 4b canlı testi onay bekliyor (docs/12 §6): ilk aşama yalnızca Seedream + GPT Image medium (≈ 0,05–0,06 $); anahtarlar API projesinin User Secrets kaydında (`Ai:Fal:ApiKey`); Pruna ayrı kapı (`SANALPROVA_LIVE_PRUNA=SENTETIK-GORSEL`) ve sentetik görsel ister. Aşama 4'ün canlı API testi onay bekliyor (docs/11 §5); Aşama 5 (SAM 2 + OpenCV + maskeli birleştirme) onaysız başlanmaz. Açık: gerçek mağaza TV'sinde uyumluluk denemesi (WebSocket/SSE, ES2019, CSS değişkenleri, `localStorage`), QR ile eşleştirme (bağımlılık gerektirir, yapılmadı), çoklu sunucu için SignalR backplane. Aşağıdaki Aşama 3 listesi tarihçedir.

**Aşama 3 — TV senkronizasyonu** (tasarım: [06-tv-signalr](docs/06-tv-signalr.md), görevler: [09 §Aşama 3](docs/09-gorev-listesi.md)):

0. Mağazadaki gerçek TV tarayıcısında uyumluluk denemesi (WebSocket, ES2019, localStorage) — risk önce kapatılır.
1. `display_devices` / `display_pairings` varlıkları migration'da var; eşleştirme API'si (geçici kod 5 dk, QR, kod/secret hash, tek seferlik cihaz belirteci), cihaz belirteci kimlik doğrulaması (`DisplayHub` şu an `DisplayDevice` politikasıyla tüm bağlantıları reddediyor), `DisplayHub` grupları, heartbeat, çevrimiçi durumu.
2. TV uygulaması (`frontend/src/tv`, şu an iskelet): kod/QR → bekleme → tekli/karşılaştırma modları, yeniden bağlanma + durum geri yükleme, uzun kopmada görseli gizleme, otomatik temizleme.
3. Personel tarafı: TV yönetimi ekranı (şu an yer tutucu), "TV'ye gönder / TV'de karşılaştır", oturumu bağla/devral (`expectedVersion` ile eşzamanlı değişiklik).
4. İzolasyon testleri: iki oturum × iki TV, yanlış TV'ye gönderim denemeleri.
Bu aşamada ayrıca: çok sunuculu bildirim için backplane ihtiyacı not edildi; ana sayfadaki/akıştaki "TV bağlı değil" yer tutucularını gerçek duruma bağla.

**Sonrası:** Aşama 4 (ön koşul: sağlayıcı sözleşmesi/KVKK aktarım değerlendirmesi, fiyatlar ve model kimlikleri resmî kaynaktan doğrulanır, benchmark) → Aşama 5 → Aşama 6 (Playwright E2E, güvenlik, performans, KVKK kontrol listesi; aşama 2 tarayıcı senaryosu bu aşamaya taşınabilir).

Açık notlar (Aşama 3 eklemeleri): TV uygulaması PWA servis çalışanı kaydı alıyor (önbellek yok, zararsız; istenirse `tv.html`'den kaldırılır); imzalı URL'ler günlüğe yazılmıyor (istek günlüğü yalnızca yol) ama bunun için ayrı otomatik test yok;
Açık notlar: müşteri bilgilendirme metni taslaktır (hukuki onay şart); başarısız yüklemelerden kalan yetim dosyalar için ayrı temizlik yok; HEIC desteklenmiyor; ImageSharp yerine SkiaSharp (MIT) seçildi.
