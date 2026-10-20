# 07 — AI Entegrasyon Stratejisi

## 1. Doğrulanan sağlayıcı bilgileri (2026-10-20 itibarıyla resmî dokümandan)

### FASHN — Try-On Max
Kaynak: docs.fashn.ai (`/api-reference/tryon-max`, `/api-overview/api-fundamentals`)

| Konu | Bilgi |
|---|---|
| Uç nokta | `POST https://api.fashn.ai/v1/run` → `{ id }`; durum: `GET /v1/status/{id}` |
| Kimlik | `Authorization: Bearer <key>` |
| Model | `model_name: "tryon-max"` — **Preview** durumunda (26 Ocak 2026) |
| Girdiler (`inputs`) | `product_image` (zorunlu, **tek görsel**), `model_image` (zorunlu), `prompt` (ops.), `resolution` `1k/2k/4k`, `generation_mode` `fast/balanced/quality`, `seed`, `num_images` 1–4, `output_format` `png/jpeg`, `return_base64` |
| Durumlar | `starting`, `in_queue`, `processing`, `completed`, `failed` |
| Süre | ~10 sn (fast+1k), ~25 sn (balanced+2k), ~55 sn (quality+4k) |
| Kredi / görsel | fast 1/2/3 · balanced 2/3/4 · quality 3/4/5 (1k/2k/4k). Başarısız tahmin kredi harcamaz. Yanıt başlığı: `x-fashn-credits-used` |
| Limitler | `/run` 50 istek/60 sn, `/status` 50 istek/10 sn, eşzamanlı 6 iş (varsayılan) |
| Girdi sınırları | ≤ 30 MiB, ≥ 15×15 px, en-boy 1:16 – 16:1 |
| Saklama | CDN çıktıları 3 gün; `return_base64: true` ile 60 dk |
| Webhook | Destekleniyor (parametre detayı Aşama 4'te Webhooks rehberinden doğrulanacak) |

**Sonuçlar:**
- FASHN web uygulamasındaki "Agent" özelliği API'de **yok** sayılacak; yalnızca yukarıdaki parametreler kullanılacak.
- Tek `product_image` → yalnızca **hazır takım** (komple takım fotoğrafı) için kullanılacak.
- KVKK için `return_base64: true` tercih edilecek (sağlayıcıda çıktı saklama 3 gün → 60 dk).
- Polling ile başlanacak (Aşama 4), webhook sonraya (sunucunun dışarıdan erişilebilir olması gerekir).

### Google Gemini — görsel modelleri (2026-10-20'de yeniden doğrulandı)
Kaynak: ai.google.dev/gemini-api/docs/image-generation, /pricing, /api/interactions-api

> **Düzeltme:** İlk taslakta `generateContent` varsayılmıştı. Resmî dokümana göre görsel üretimi **Interactions API** ile yapılır (`POST /v1beta/interactions`; girdi `input[]` içinde `text` + `image{mime_type,data}`, çıktı `steps[model_output].content[image].data`; yanıt şeması için `Api-Revision: 2026-05-20`). Adapter buna göre yazıldı. Ayrıntılı tablo, fiyatlar ve doğrulanamayan noktalar: [11](11-ai-entegrasyon-plani-ve-test.md#1-resmî-dokümandan-doğrulanan-bilgiler).

| Ad | Model ID | Not |
|---|---|---|
| Nano Banana Pro | `gemini-3-pro-image` | Premium; 1K/2K/4K; görsel başı 0,134 $ (1K/2K), 0,24 $ (4K) |
| Nano Banana 2.1 | `gemini-nano-banana-2.1` | Doküman yeni projeler için önerir; 1K/2K/4K; 0,0336 / 0,0504 / 0,113 $ |
| Nano Banana 2 | `gemini-3.1-flash-image` | Önceki nesil; karşılaştırma için |
| Nano Banana 2 Lite | `gemini-3.1-flash-lite-image` | **Yalnızca 1K**; çoklu referans ve çok adımlı düzenleme için optimize değil → kombinde kullanılmaz |

- Düzenleme: metin + görsel(ler). Piksel maskesi parametresi yok → "yalnızca ilgili bölge değişsin" garantisi modelden değil, Aşama 5'teki maskeli birleştirmeden gelecek (§5.3).
- Tüm çıktılarda SynthID filigranı; Gemini 3 görsel modellerinde düşünme kapatılamaz (ara görseller `thought` adımlarında, yok sayılır).
- **Ücretsiz katman kullanılmaz** (dört modelde de zaten yok); ücretli katman veri koşulları hukuki değerlendirmede (08).
- Hiçbir model canlı doğrulanmadı: `ModelCatalog.LiveVerified=false`.

### Bria FIBO Edit 1.5
Bu aşamada doğrulanmadı. Yalnızca alternatif `IImageEditProvider` olarak planlandı.

## 2. Görev → sağlayıcı eşlemesi (başlangıç)

| İş tipi | Varsayılan | Yedek | Neden |
|---|---|---|---|
| `OutfitSet` (hazır takım) | FASHN `tryon-max`, balanced, **1k** (2k alternatif; yönetici ayarı — bkz. 11 §3) | Gemini Pro | Kimlik/poz korumada uzmanlaşmış try-on modeli; tek ürün görseli yeterli |
| `FreeCombination` | Gemini `gemini-3-pro-image` | FASHN zincirleme (deneysel) | Çoklu referans görsel desteği |
| `ProductSwap` (revizyon) | Gemini `gemini-nano-banana-2.1` | Pro / Bria | Önceki sonuç + yeni ürün referansı; K2 nedeniyle Lite varsayılan değil |
| `Recolor` | image-worker (SAM 2 + OpenCV) | `ProductSwap` yolu (Gemini) | Ucuz, hızlı, deterministik; başarısızsa AI |
| `VariantPreview` | İlk üretimdeki sağlayıcı, varyant başına bir iş | — | Gerçek ürün varyantları |

Eşleme `appsettings` → `Generation:Strategies`'den okunur ([02 §8](02-sistem-mimarisi.md#8-yapılandırma-ve-gizli-anahtarlar)). Değişiklik kod gerektirmez.

## 3. Adapter yapısı (sade)

> Aşama 4 uygulaması: arayüzler `Infrastructure/Ai/Abstractions/Providers.cs` içindedir (`ProviderRequestOptions` model + parametreler + tohum + `ResumeRequestId` + `OnSubmitted`); `FashnTryOnProvider`, `GeminiProvider`, `CostEstimator`, `ModelCatalog`, `OutputValidator`, `BudgetGuard` eklendi. Aşağıdaki taslak imzalar yön göstericidir.

```csharp
// Infrastructure/Ai/Abstractions

public sealed record ImageInput(Stream Content, string ContentType, string? Role = null); // Role: "person", "garment:salvar"...

public sealed record ProviderResult(
    IReadOnlyList<byte[]> Images, string Provider, string Model,
    string? ProviderRequestId, ProviderUsage Usage);

public interface ITryOnProvider             // tek kişi + tek ürün görseli (FASHN)
{
    Task<ProviderResult> TryOnAsync(ImageInput person, ImageInput garment,
                                    TryOnOptions options, CancellationToken ct);
}

public interface IImageGenerationProvider  // kişi + çoklu ürün referansı → kompozisyon (Gemini)
{
    Task<ProviderResult> ComposeOutfitAsync(ImageInput person, IReadOnlyList<GarmentReference> garments,
                                            string prompt, GenerationOptions options, CancellationToken ct);
}

public interface IImageEditProvider        // önceki görsel + talimat (+ referanslar) → düzenleme
{
    Task<ProviderResult> EditAsync(ImageInput baseImage, string instruction,
                                   IReadOnlyList<ImageInput> references, EditOptions options, CancellationToken ct);
}

public interface IImageSegmentationService // image-worker istemcisi
{
    Task<SegmentationResult> SegmentAsync(ImageInput image, SegmentationTarget target, CancellationToken ct);
}

public interface IRecolorService           // image-worker istemcisi
{
    Task<RecolorResult> RecolorAsync(ImageInput image, byte[] mask, string targetHex, CancellationToken ct);
}

public interface IImageStorageService { /* bkz. 02 §6 */ }
```

- `GeminiProvider` hem `IImageGenerationProvider` hem `IImageEditProvider`'ı uygular (aynı API).
- Her arayüzün bir `Mock*` uygulaması vardır: girdi fotoğrafının üzerine ürün küçük resimlerini + "MOCK" filigranını koyar, yapılandırılabilir gecikme (3–8 sn) ve hata oranı ile. Böylece UI, kuyruk, SignalR ve TV gerçek API olmadan uçtan uca test edilir.
- Sağlayıcıyı seçen tek yer `GenerationStrategy`. İş kuralları (oturum, onay, kota, sürümleme) sağlayıcıyı bilmez.
- HTTP istemcileri `IHttpClientFactory` + Polly: zaman aşımı, yeniden deneme (429/5xx), devre kesici.
- Sağlayıcı hata kodları ortak bir `ProviderErrorCode` enum'una çevrilir: `InvalidInput`, `ContentRejected`, `RateLimited`, `Timeout`, `ProviderUnavailable`, `Unknown`.

## 4. Prompt yönetimi

- Kullanıcı prompt yazmaz. Prompt; ürün adı, kategori, `categories.ai_hint`, `products.ai_notes`, katman sırası ve varyant renginden **sunucuda** üretilir.
- Şablonlar `Infrastructure/Ai/Prompts/` altında sürümlü dosyalar (`free-combination.v1.txt`). Kullanılan sürüm `generation_jobs.prompt_template_version`'a yazılır → kalite karşılaştırması yapılabilir.
- İngilizce şablon (modeller genellikle daha tutarlı), ürün adları olduğu gibi.

**Örnek — serbest kombin şablonu (taslak)**
```
Image 1 is a full-body photo of a real person. Images 2..N are product photos of real garments from a store.
Dress the person in Image 1 with ALL of the following garments, layered in this order (inner to outer):
{{#each garments}}
- Image {{index}}: {{categoryName}} — {{productName}}{{#if colorName}}, color: {{colorName}}{{/if}}. {{aiHint}} {{aiNotes}}
{{/each}}
Strict requirements:
- Keep the person's face, hair, skin tone, body shape, pose, and the background exactly the same.
- Reproduce each garment's pattern, embroidery, color and fabric as faithfully as possible. Do not invent new ornaments.
- Do not add any garments or accessories that are not listed.
- Photorealistic, natural lighting matching Image 1. Output a single full-body image with the same framing.
```

## 5. Revizyon motoru

### 5.1 Karar akışı
```
Revizyon isteği (parça + "renk" | "başka ürün")
  ├─ "başka ürün"                         → ProductSwap (AI edit)
  └─ "renk"
       ├─ katalogda o renkte gerçek varyant var → ProductSwap (gerçek varyant görseliyle)
       └─ yok (palet rengi)                     → Recolor (SAM 2 + OpenCV)
                                                   ├─ kalite geçti → sonuç (is_color_preview = true)
                                                   └─ kalite kaldı → ProductSwap'a düş, renk talimatıyla (is_color_preview = true)
```

### 5.2 Basit renk değişimi (image-worker)
1. **Maske:** Hedef parça için segmentasyon.
   - *Aşama 5 MVP:* Personel sonuç görselinde parçaya **bir kez dokunur** → nokta istemi ile SAM 2 (`sam2.1` küçük model, CPU'da birkaç saniye — ölçülecek). Dokunma gerektirmesi kabul edilebilir ve en güvenilir yöntem.
   - *Sonra:* Kıyafet ayrıştırma (human parsing) modeliyle otomatik kaba bölge → SAM 2 ile iyileştirme. Model lisansı ticari kullanım için kontrol edilecek.
   - Başlangıçta `MockSegmentation` (önceden hazırlanmış test maskeleri) → GPU/model kurulumu gerekmez.
2. **Nakış koruma:** Maske içinde yüksek kenar yoğunluğu + metalik/doygun renk kümeleri (altın sırma, simli işleme) tespit edilip maskeden çıkarılır.
3. **Renk dönüşümü:** LAB uzayında; `a`, `b` kanalları hedef renge kaydırılır, `L` (parlaklık) kanalı ortalaması hedefe ölçeklenip **varyansı korunur** → kıvrım ve gölgeler kalır. Maske kenarı yumuşatılır (feather).
4. **Kalite kontrol (`passed` = hepsi):**
   - Maske alanı kategori için beklenen aralıkta (ör. şalvar görüntünün %8–40'ı).
   - Maske yüz bölgesine taşmıyor (yüz tespiti, OpenCV DNN/Haar).
   - Maske içi yapı korunumu: `L` kanalında SSIM ≥ eşik.
   - Hedef renge ulaşıldı: ΔE2000 ortalaması ≤ eşik.
   - Nakış korunumu: nakış maskesi piksel değişimi ≈ 0.
   Eşikler test setiyle kalibre edilecek.

### 5.3 Karmaşık revizyon ve "yalnızca ilgili bölge" garantisi
1. Gemini'ye önceki sonuç + yeni ürün referansı + yapılandırılmış talimat gönderilir ("Replace only the belt with the belt in Image 2. Keep everything else identical.").
2. Dönen görsel önceki görselle hizalanır (boyut aynı değilse yeniden ölçeklenir).
3. **Maskeli birleştirme:** Eski parça maskesi ∪ yeni parça maskesi (genişletilmiş + yumuşatılmış) içindeki pikseller yeni görselden, dışındakiler eski görselden alınır → yüz, arka plan ve diğer parçalar matematiksel olarak korunur.
4. Silüet çok değiştiyse (ör. kısa yelek → uzun kaftan), birleşim maskesi geniş olur; bu durum loglanır.
5. Maske alınamazsa birleştirme yapılmadan model çıktısı kullanılır (etiketlenir).

## 6. Görsel ön işleme ve doğrulama

- **Girdi:** EXIF yönü uygulanır ve tüm EXIF silinir; uzun kenar sağlayıcıya göre (FASHN 2k için ≈2048 px); sRGB; JPEG %92.
- **Çıktı doğrulama:** boyut > 0, tamamen düz/siyah değil, en-boy oranı makul, (ops.) yüz tespit edildi. Başarısızsa `Failed: InvalidOutput`, kredi harcandıysa kayda geçer.
- **Kimlik koruma sınırı:** Modeller yüzü değiştirebilir. Aşama 6'da yüz benzerliği ölçümü (yerel embedding, saklanmadan) ile otomatik uyarı değerlendirilecek — biyometrik veri işleme anlamına gelebileceği için önce hukuki görüş alınacak.

## 7. Maliyet ve kota

- Her sağlayıcı çağrısı `ai_usage_logs`'a yazılır: süre, kredi/token, tahmini USD.
- Birim fiyatlar yapılandırmada (`Generation:Pricing`); fatura ile ay sonu mutabakat.
- Kotalar: mağaza günlük üretim sınırı, oturum başına üst sınır (ör. 15 üretim), kullanıcı başına dakika sınırı. Kota aşımı `429 QUOTA_EXCEEDED`.
- Yönetici ekranında günlük maliyet ve başarı oranı.
- Maliyeti düşürme: önizleme için `fast`/1k, "TV'de göster" için `balanced`/2k seçeneği (Aşama 4 testinde kalite farkı değerlendirilecek); aynı (fotoğraf, seçim, model, seed) için önbellek.

## 8. Python image-worker

- FastAPI, OpenCV headless, NumPy, Pillow. **Varsayılan kurulum GPU/PyTorch içermez.**
- SAM 2 ayrı opsiyonel bağımlılık grubu (`pip install .[sam2]`) ve ayrı Docker imajı (`image-worker:sam2`); yokken `segmentation: mock`.
- Durumsuz; görselleri diske yazmaz; istek boyutu sınırı; iç anahtar ile korunur.
- Backend'den bağımsız test edilir: `pytest` + `tests/fixtures/` altında **lisanslı/izinli** test görselleri ve beklenen maskeler.
- Gelecekte GPU makinesine taşınması yalnızca `ImageWorker:BaseUrl` değişikliğidir.

## 9. Değerlendirme (benchmark) planı — Aşama 4/5

Gerçek müşteri fotoğrafı **kullanılmadan**, izin alınmış 10–15 manken/personel fotoğrafı × 10 ürün ile:

| Ölçüt | Yöntem |
|---|---|
| Kimlik korunumu | 1–5 puan, 2 değerlendirici |
| Poz/vücut korunumu | 1–5 |
| Desen/nakış sadakati | 1–5, ürün fotoğrafıyla yan yana |
| Renk doğruluğu | ΔE (ürün görseli ↔ sonuç, örnek bölge) |
| Artefakt | var/yok (eller, kenarlar, ekstra parça) |
| Süre ve maliyet | otomatik |

Karşılaştırılacaklar: FASHN fast/balanced/quality; Gemini Pro vs 2.1 vs 2 Lite (kombin ve ürün değiştirme); FASHN zincirleme vs Gemini (kombin). Sonuç raporu model seçimini belirler.
