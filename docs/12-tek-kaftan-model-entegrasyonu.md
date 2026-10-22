# 12 — Tek Kaftan / Hazır Takım Giydirme: Seedream, GPT Image 2.5, Pruna

> Durum: kod tamamlandı, mock ve sözleşme testleri geçti. **Hiçbir ücretli API çağrısı yapılmadı.**
> Canlı test proje sahibinin onayını bekliyor (§6). Doğrulama tarihi: 2026-10-22.

Kapsam: yalnızca **tek kaftan** (tüm vücut tekil ürün) ve **hazır takım** giydirme. Beş parçalı kombin gerçek modda **kapalı**; yalnızca mock modda DEMO olarak çalışır.

## 1. Resmî kaynaklardan doğrulanan bilgiler

| | Seedream 5 Flash Edit | GPT Image 2.5 Sunburst Edit | Pruna P-Image Try-On |
|---|---|---|---|
| Sağlayıcı | fal.ai | fal.ai | Pruna (doğrudan) |
| Kimlik | `bytedance/seedream/v5/flash/edit` (`fal-ai/` önekli adres 404 döner) | `openai/gpt-image-2.5/sunburst/edit` | `Model: p-image-try-on` başlığı |
| Kaynak | `queue.fal.run` OpenAPI şeması (indirildi), fal Queue dokümanı, model sayfası | aynı | `docs.api.pruna.ai/openapi.yaml`, FAQ, Quickstart, DPA |
| Kimlik doğrulama | `Authorization: Key <FAL_KEY>` | aynı | `apikey: <KEY>` |
| Akış | `POST queue.fal.run/{model}` → `request_id`; `GET {sahip/uygulama}/requests/{id}/status` (`IN_QUEUE`, `IN_PROGRESS`, `COMPLETED`); `GET …/requests/{id}`; iptal `PUT …/cancel` | aynı | `POST /v1/files` (yükleme) → `POST /v1/predictions` → `GET /v1/predictions/status/{id}` (`starting`, `processing`, `succeeded`, `failed`, `canceled`) → `generation_url` indirme |
| Girdi | `prompt`, `image_urls` (≤ 10; URL veya **data URI**), `image_size` (`auto_1K`/`auto_2K`…; 1024²–2048²), `num_images` 1–6, `output_format`, `sync_mode`, `enable_safety_checker`. **Seed yok** | `prompt`, `image_urls` (≤ 16), `quality` auto/low/medium/high/xhigh/max (**varsayılan high**), `image_size` (varsayılan auto), `num_images`, `output_format`, `output_compression`, `sync_mode`, `mask_url`, `background` | `person_image`, `garment_images` (≤ 11, ≤ 6 önerilir), `turbo`, `seed`, `prompt` (deneysel), `output_format`, `output_quality`, `preserve_input_size`. **Base64 desteklenmez** |
| Çıktı | `images[].url` (`sync_mode: true` ile data URI) | aynı | `generation_url` (API anahtarıyla indirilir) |
| Fiyat | **0,027 $/görsel** (fal model sayfası) | Kalite × boyut tablosu. Ek olarak referans görsel başı ≈ 0,008 $. Kullanıcı ölçümü (`image_size: auto`): low **0,0135 $**, medium **0,0173 $**, high ort. **0,0391 $** | Standart **0,015 $** (ilk parça), turbo **0,008 $**/parça |
| İstek başı gerçek maliyet bildirimi | **Yok** (yalnızca `metrics.inference_time`) | Yok | Yok |
| Saklama | `X-Fal-Store-IO: 0` yük saklamayı kapatır; `sync_mode` çıktıyı geçmişte tutmaz | aynı | Yüklenen dosyalar **30 dk** sonra otomatik silinir; **silme uç noktası yok**. Çıktı **24 saat** erişilebilir (FAQ: bağlantı herkese açık) |

**Doğrulanamayan / varsayım:**
- Hiçbir model bu projede canlı çağrıyla doğrulanmadı (`LiveVerified=false`; yönetici ekranında "Canlı doğrulanmadı").
- fal'ın başarısız istek `error_type` listesi alınamadı: anahtar kelimeyle sınıflandırılıyor.
- fal, istek başına ücret alanı döndürmüyor. Bu yüzden maliyet **tahmin** olarak kaydediliyor (`actual_cost_usd = NULL`); mutabakat fatura ile yapılır.
- GPT Image ölçümleri `image_size: auto` ile alındı: sabit çözünürlüğün garantili fiyatı değil. Bilgi olarak saklanır ("ölçülen"); tahmin formülü değiştirmez (§2a).
- `image_size: auto` çıktısının kişi fotoğrafının boyutunu izlediği varsayılır (fal: "infer from input images"); canlı testte doğrulanacak.
- Pruna şemasında yükleme süresi açıklamada 30 dk, örnek yanıtta 24 saat görünüyor; temkinli yorum: **en geç 24 saat**.

## 2. Mimari ve değişen dosyalar

- **Model kataloğu** — `Infrastructure/Ai/TryOn/TryOnModels.cs`:
  - Model anahtarları, sağlayıcı kimliği, kaliteler (tahmini ve en kötü durum maliyeti, sağlayıcı parametreleri), fiyat kaynağı.
  - FASHN `fashn-tryon-max` **eski (pasif)**: seçilemez, kodu ve testleri korunur. Gemini yalnızca beş parçalı kombin içindi; gerçek modda kapalı.
- **Politika** — `TryOnPolicyResolver`: firma ayarından etkin modeller, izinli kaliteler ve varsayılanlar. Kayıtlı ayar geçersizse güvenli varsayılana düşer.
- **Adapter'lar** (her sağlayıcı bağımsız):
  - `Ai/Fal/FalQueueClient.cs`: ortak kuyruk istemcisi.
  - `Ai/Fal/FalTryOnProviders.cs`: `FalSeedreamTryOnProvider`, `FalGptImageTryOnProvider`.
  - `Ai/Pruna/PrunaTryOnProvider.cs`.
  - `Ai/ProviderHttp.cs`: zaman aşımı, data URI, alan adı izin listesi, maliyet.
- **İşleyici** — `GenerationProcessor`:
  - İşte sabitlenmiş `ModelKey`/`Quality` varsa ilgili sağlayıcıyı ve parametreleri kullanır.
  - Talimatı (prompt) sunucuda üretir: `PromptBuilder.BuildSingleGarment`, sürüm `single-garment.v1`. Pruna'ya serbest metin gönderilmez (`pruna-tryon.v1`).
  - Bütçe kapısı modelin **en kötü durum** maliyetini kullanır.
  - Kullanım kaydı model ve kaliteyi içerir. Gerçek modda kombin işi reddedilir (ikinci savunma hattı).
- **Veri** — migration `Phase4bTryOnModels`:
  - `generation_jobs.model_key`, `generation_jobs.quality`, `ai_usage_logs.quality`.
  - Firma ayarları JSON'una `DefaultTryOnModel`, `EnabledTryOnModels`, `AllowedTryOnQualities`, `DefaultTryOnQualities` eklendi.
- **Tek kaftan** — `CombinationRules.IsSingleTryOnItem`: hazır takım **veya** tüm vücut (`BodyZone.FullBody`) tekil ürün. `GET /products?tryOn=true` seçim listesi.
- **API:**
  - `GET/PUT /api/v1/admin/ai-models` (Manager+): etkin modeller, izinli kaliteler, varsayılanlar.
  - `GET /api/v1/ai/try-on-options` (Staff): izinli seçenekler; web ve ileride native istemciler seçim ekranını buradan kurar.
  - `POST /sessions/{id}/generations` gövdesine `modelKey`, `quality` eklendi. İzinsizse `MODEL_NOT_ALLOWED` veya `QUALITY_NOT_ALLOWED`; bütçe sınırını aşan kalite için `QUALITY_OVER_BUDGET`; gerçek modda kombin için `FREE_COMBINATION_DISABLED`.
  - Rapor (`/admin/usage`) model + kalite bazında.
- **Frontend:**
  - Ürün seçimi "Hazır takım ve kaftanlar"; AI modeli ve kalite seçici yalnızca izinli seçenekleri gösterir.
  - Yönetici `/maliyet` ekranında "Tek ürün giydirme modelleri" bölümü.
  - Kombin kartı gerçek modda "henüz etkin değil" olarak kapalı.
- **Varsayılanlar:**
  - Model: **Seedream 5 Flash**.
  - GPT Image kalitesi: **medium** (high varsayılan değil). Şemadaki altı kalitenin tamamı desteklenir (§2a); başlangıçta etkin olanlar low/medium/high.
  - Pruna: standart (turbo seçilebilir).
- **Korunanlar:** Kuyruk, bütçe, iptal, çift ücret koruması, çıktı doğrulama, DEMO ayrımı, SignalR ve Smart TV aynen korunuyor. Sonuç, sonuç ekranında ve TV'de görünür; TV tekli sonuç gösteriyorsa yeni sonuca kendiliğinden geçer.

## 2a. GPT Image 2.5 kaliteleri ve maliyet hesabı

Şema (`queue.fal.run` OpenAPI, 2026-10-22 yeniden doğrulandı): `quality` ∈ `auto`, `low`, `medium`, `high`, `xhigh`, `max`; şema varsayılanı **high**. Kalite her istekte **açıkça** gönderilir; şema varsayılanına bırakılmaz.

| Kalite | Etiket | Başlangıçta | Tipik tahmin | En kötü durum |
|---|---|---|---|---|
| `auto` | Otomatik (model seçer) | kapalı · yüksek maliyet | ≈ 0,075 $ (high gibi) | 0,423 $ (max sütunu) |
| `low` | Düşük | açık | ≈ 0,025 $ | 0,034 $ |
| `medium` | Orta (**varsayılan**) | açık | ≈ 0,034 $ | 0,048 $ |
| `high` | Yüksek | açık | ≈ 0,075 $ | 0,123 $ |
| `xhigh` | Çok yüksek | kapalı · yüksek maliyet | ≈ 0,118 $ | 0,200 $ |
| `max` | Maksimum | kapalı · yüksek maliyet | ≈ 0,240 $ | 0,423 $ |

- **Formül** (`GptImagePricing`, fal model sayfası "Pricing"): çıktı (kalite × boyut tablosu) × görsel sayısı + her referans görsel (≤ 1024² ≈ 0,008 $, 3840×2160 ≈ 0,012 $; arası alana göre doğrusal) + prompt (5 $/1M jeton, ≈ 4 karakter/jeton). Toplam 0,0001 $'a yukarı yuvarlanır.
  - Tablo dışı çıktı boyutu, piksel sayısına göre bir üst satırdan fiyatlanır; 4K üstü orantılı büyütülür.
  - En kötü durum o satıra kadarki **en pahalı** satırı alır (tablo boyutla tekdüze değil: 1024×1024, 1024×1536'dan pahalı). Prompt için en az 0,003 $ ayrılır.
  - `auto`: tahmin high, en kötü durum max sütunu (fal: "cost can reach the max column").
- **Nerede hangi girdi:**
  - **Ekranda** (yönetici/personel): "tipik" = 1536×2048 fotoğraf + 1200×1600 ürün görseli + orta prompt. "En fazla" = yükleme sınırındaki en büyük görseller (2048² müşteri, 1600² katalog) + uzun prompt.
  - **Bütçe kapısı** (çağrı öncesi): gerçek girdi görsellerinin boyutu ve gerçek prompt uzunluğuyla en kötü durum.
  - **Kullanım kaydı** (çağrı sonrası): gerçek **çıktı** boyutuyla tahmin.
- **Ölçülen tutarlar** (low 0,0135 / medium 0,0173 / high 0,0391 $) formülün tipik tahmininin altında. Formül temkinli tarafta kalır. Canlı testten sonra gerekirse `Generation:Pricing:TryOnEstimatedUsd["gpt-image-2.5:medium"]` gibi ezmelerle kalibre edilir; ezme görsel başına sabit tutardır.
- **İzinler:** Yönetici `/maliyet` ekranında her kaliteyi ayrı açar/kapatır ve varsayılanı seçer (`PUT /admin/ai-models`). Personel yalnızca izinli kaliteleri görür; sunucu her istekte yeniden denetler (`QUALITY_NOT_ALLOWED`).
- **Bütçe:** Gerçek modda en kötü durumu iş başı sınırı (`Generation:Budget:MaxJobCostUsd`, varsayılan 0,50 $) aşan kalite:
  - Seçicide "bütçe sınırı aşılıyor" olarak devre dışı görünür.
  - API isteği `QUALITY_OVER_BUDGET` (409) ile reddeder; iş kuyruğa alınmaz.
  - İşleyicideki bütçe kapısı ikinci savunma hattıdır.
  - Varsayılan 0,50 $ sınırla altı kalitenin hepsi sığar; günlük/aylık sınırlar ayrıca geçerlidir.
- **İşlem geçmişi:**
  - İş ve kullanım kaydında `model_key` ve `quality` saklanır (migration gerekmedi; değerler mevcut sütunlara sığıyor).
  - API'de `JobDto.ModelLabel`, `GeneratedImageDto.AiModel` ve `SessionSummary.ResultAiModel` gösterilir (ör. "GPT Image 2.5 · Maksimum"). Bu etiket sonuç, üretim, karşılaştırma ve geçmiş ekranlarında görünür.
  - Demo sonuçta "Demo sonuç · seçilen model: …" yazar.
  - Maliyet raporu kaliteyi Türkçe etiketle gösterir.

## 3. Çift ücret ve hata yönetimi
- **İstek kimliği:** fal `request_id` ve Pruna tahmin `id`'si alınır alınmaz kaydedilir. Zaman aşımı veya çökme sonrası yeniden denemede **aynı istek sorgulanır**, yeni ücretli istek açılmaz. Kimlik artık bulunamazsa (404, süresi doldu) otomatik yeniden gönderilmez.
- **Parmak izi:** Aynı fotoğraf, aynı ürün, aynı model ve kalite için süren veya başarılı iş varsa yeni iş açılmaz. Model veya kalite farkı bilinçli yeni istektir.
- **İptal:** fal'da `PUT …/cancel` en iyi çabayla denenir. Pruna'da iptal uç noktası yok. Çağrı başlamışsa en kötü durum maliyeti kaydedilir.
- **Hata eşlemeleri:**
  - 401/403 → `ProviderNotConfigured`.
  - 400/422 → `InvalidInput`; içerik politikası → `ContentRejected` (yeniden denenmez).
  - 429 → `RateLimited`; 5xx → `ProviderUnavailable`; 504 → `Timeout`. Bu üçü yeniden denenir.
  - Geçersiz çıktı yeniden denenmez.
- **Kalan küçük risk:** Gönderim yanıtı gelmeden bağlantı koparsa istek sağlayıcıya ulaşmış olabilir. Yeniden deneme ikinci ücrete yol açabilir (görsel başı en fazla birkaç sent).

## 4. Gizlilik ve KVKK
- **fal.ai (ABD):**
  - Görseller data URI olarak gider; fal deposuna yükleme yapılmaz.
  - `X-Fal-Store-IO: 0` ve `sync_mode: true` kullanılıyor.
  - Çıktı yalnızca fal medya alan adlarından ve anahtar gönderilmeden indirilir.
  - fal'ın veri işleme sözleşmesi ve yurt dışı aktarım değerlendirmesi hukuki onay bekliyor.
- **Pruna:**
  - Base64 desteklenmediği için müşteri fotoğrafı ve ürün görseli Pruna'ya **geçici dosya olarak yüklenir**. Otomatik silme 30 dk (en geç 24 saat).
  - **Elle silme mümkün değil.** Üretilen görsel Pruna'da **24 saat** erişilebilir ve FAQ'ya göre bağlantısı herkese açık.
  - Pruna GDPR DPA'sı var, ancak saklama süresi bizim kontrolümüzde değil.
  - Alternatif: bizim kısa ömürlü (5 dk) imzalı bağlantımızı vermek. Bu, sunucunun internetten erişilebilir olmasını gerektirir ve çıktının 24 saatlik saklanmasını yine değiştirmez.
  - **Öneri:** Pruna'yı müşteri fotoğrafıyla açmadan önce aydınlatma metnine ve hukuki değerlendirmeye bu saklama sürelerini ekleyin. Yönetici panelinden Pruna kapatılabilir.
- **Diğer kurallar:**
  - Onaysız fotoğraf yüklenemez; yurt dışı aktarım onayı zorunlu (mevcut kural).
  - API anahtarları yalnızca backend'de (`Ai__Fal__ApiKey`, `Ai__Pruna__ApiKey` ortam değişkeni veya user-secrets); kaynağa yazılmaz.
  - İstek ve yanıt gövdeleri ile görseller günlüğe yazılmaz.

## 5. Native Android/iOS hazırlığı
- Seçenekler, model ayarları ve üretim tamamen REST/JSON ve OpenAPI'de tipli. İstemci model/kalite listesini sunucudan alır; kurallar sunucuda uygulanır.
- **Eksik:** Kimlik doğrulama tarayıcıya göre (HttpOnly çerez + CSRF). Native istemciler için token tabanlı oturum (kısa ömürlü erişim + yenileme belirteci) ayrı bir aşamada eklenmeli. AI ve TV altyapısı buna bağlı değil.

## 6. Canlı test planı — **ONAY BEKLİYOR**

Koşucu: `LiveAiTests` (varsayılan **atlanır**).
- **Kapı:** `SANALPROVA_LIVE_AI=ODEME-ONAYLANDI`, `Ai__Fal__ApiKey`, `Ai__Pruna__ApiKey`.
- **Girdiler:** `testdata/ai-live/person.jpg` ve `kaftan.jpg`. İzinli test görselleri kullanılır; klasör git'e girmez.
- **Bütçe tavanı:** `SANALPROVA_LIVE_AI_BUDGET_USD` (varsayılan 0,50 $). Her çağrıdan önce en kötü durum maliyeti kalan bütçeyle karşılaştırılır.

| Adım | Model / kalite | Tahmini | En kötü |
|---|---|---|---|
| `seedream` | Seedream 5 Flash, auto_2K | 0,027 $ | 0,027 $ |
| `gpt-medium` | GPT Image 2.5 medium | ≈ 0,017–0,034 $ (ölçülen–formül) | ≤ 0,048 $ |
| `pruna` | Pruna standart | 0,015 $ | 0,015 $ |
| isteğe bağlı `gpt-low` / `gpt-high` / `pruna-turbo` | | ≈ 0,025 / 0,075 / 0,008 $ | ≤ 0,034 / 0,123 / 0,008 $ |
| isteğe bağlı, pahalı `gpt-auto` / `gpt-xhigh` / `gpt-max` | | ≈ 0,075 / 0,118 / 0,240 $ | ≤ 0,423 / 0,200 / 0,423 $ |

- **Varsayılan üç adım:** tahmini **≈ 0,06–0,08 $**, en kötü ≈ 0,09 $.
- **Low/high/turbo eklenince (altı adım):** ≈ 0,15 $ civarı, en kötü ≈ 0,26 $.
- **Pahalı üç adım** (auto/xhigh/max) en kötü durumda birlikte ≈ 1,05 $ eder. 0,50 $ tavanı aşar; bu adımlar için ayrı onay ve daha yüksek tavan (`SANALPROVA_LIVE_AI_BUDGET_USD`) gerekir. Tavan aşılacaksa koşucu çağrı yapmadan durur.
- Bütçe koruması her adımda gerçek test görsellerinin boyutuyla en kötü durumu hesaplar.
- Çıktılar ve `kayit.md` dosyası `docs/ai-benchmark/runs/` altına yazılır; yüz, poz, kumaş, nakış ve ürün sadakati burada puanlanır.
- Uygulama ve TV'ye ulaşmayı ayrıca test etmek için `AllowPaidCalls=true` ile tek bir Seedream üretimi yeterli (≈ 0,027 $).

## 7. Kalan eksikler
- Canlı doğrulama yok (§6). fal hata türleri ve gerçek maliyet bildirimi yok, maliyet tahmin olarak kaydediliyor.
- GPT Image `image_size: auto` çıktı boyutu ve `auto` kalitesinin gerçekte hangi kaliteyi seçtiği doğrulanmadı (§2a varsayımları).
- Pruna için saklama ve silme kontrolü bizde değil (§4); imzalı bağlantı modu yalnızca belgelendi, uygulanmadı.
- Beş parçalı kombin gerçek modda kapalı. Ayrı aşamada test edilecek.
- Native istemciler için token tabanlı kimlik doğrulama yok.
- Gerçek Smart TV uyumluluk testi hâlâ açık. Aşama 5 (revizyon motoru) başlamadı.
