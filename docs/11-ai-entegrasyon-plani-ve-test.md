# 11 — Aşama 4: Gerçek AI Entegrasyonu — Plan, Maliyet Sınırları ve Kontrollü Test Planı

> **Güncelleme (2026-10-22):** Tek ürün giydirme modelleri değişti → [12](12-tek-kaftan-model-entegrasyonu.md) (Seedream 5 Flash, GPT Image 2.5, Pruna). Bu belgedeki FASHN/Gemini planı tarihçedir; bütçe kapıları, çift ücret koruması ve kayıt yapısı aynen geçerlidir. Canlı test planının güncel hali 12 §6'dadır.

> Durum: kod tamamlandı, **mock/sözleşme testleri geçti**; **hiçbir ücretli gerçek API çağrısı yapılmadı.** Gerçek API testi (§5) yalnızca proje sahibinin ayrıca vereceği onaydan sonra çalıştırılır.
> Doğrulama tarihi: 2026-10-20 (resmî dokümanlar). Fiyat ve model bilgisi değişebilir; çalıştırmadan önce yeniden kontrol edilir.

## 1. Resmî dokümandan doğrulanan bilgiler

| Konu | Doğrulanan bilgi | Kaynak |
|---|---|---|
| FASHN Try-On Max | `POST https://api.fashn.ai/v1/run`, `model_name: "tryon-max"`, **Preview** (26 Ocak 2026). Girdi: `product_image` (tek), `model_image` (data URI veya URL), `prompt`, `resolution` 1k/2k/4k, `generation_mode` fast/balanced/quality, `seed` 0–2³²−1, `num_images` 1–4, `output_format`, `return_base64` | docs.fashn.ai/api-reference/tryon-max |
| FASHN yoklama | `GET /v1/status/{id}`; durumlar `starting`, `in_queue`, `processing`, `completed`, `failed`; hata `{name, message}` | docs.fashn.ai/api-overview/api-fundamentals |
| FASHN hata adları | API: 400 BadRequest, 401 UnauthorizedAccess, 404 NotFound, 429 RateLimitExceeded / ConcurrencyLimitExceeded / OutOfCredits, 500 InternalServerError. Çalışma: ImageLoadError, ContentModerationError, InputValidationError, ThirdPartyError, UnavailableError, PipelineError | docs.fashn.ai/api-overview/error-handling |
| FASHN limitler | `/run` 50/60 sn, `/status` 50/10 sn, eşzamanlı 6; girdi ≤ 30 MiB, ≥ 15 px, oran 1:16–16:1 | api-fundamentals, tryon-max |
| FASHN kredi | fast 1/2/3 · balanced 2/3/4 · quality 3/4/5 (1k/2k/4k) **görsel başına**; başarısız tahmin kredi harcamaz; `x-fashn-credits-used` başlığı | tryon-max, api-fundamentals |
| FASHN fiyat | **0,075 USD/kredi** (on-demand); abonelik üst-yükleme 0,0675 / 0,060 / 0,0488; en az 100 kredi alınır; on-demand kredi 12 ay geçerli | help.fashn.ai/plans-and-pricing/api-pricing |
| FASHN saklama | CDN çıktıları 3 gün; `return_base64: true` ile 60 dk ve geçmişte yalnızca yer tutucu | tryon-max |
| Gemini istek biçimi | **Interactions API**: `POST https://generativelanguage.googleapis.com/v1beta/interactions`, başlık `x-goog-api-key`; `input` = `[{type:"text"}, {type:"image", mime_type, data(base64)}…]`; `response_format: {type:"image", aspect_ratio, image_size}`; `store`; `generation_config` | ai.google.dev/gemini-api/docs/image-generation, ai.google.dev/api/interactions-api |
| Gemini yanıt | `steps[]` içinde `type:"model_output"` → `content[]` → `type:"image"` (`data`, `mime_type`); `thought` adımlarında ara görseller olabilir; `status` (`completed`, `failed`, `incomplete`, `cancelled`…); `usage` (`total_input_tokens`, `total_output_tokens`, `total_thought_tokens`, `output_tokens_by_modality`…); `errors[]` | interactions-api |
| Gemini yanıt şeması sürümü | Mayıs 2026'da `outputs` → `steps`; **`Api-Revision: 2026-05-20`** başlığı yeni şemayı sabitler (eski şemanın 8 Haziran 2026'da kaldırılması duyurulmuştu) | interactions-breaking-changes-may-2026 |
| Gemini model kimlikleri | `gemini-3-pro-image` (Nano Banana Pro), `gemini-nano-banana-2.1` (2.1), `gemini-3.1-flash-image` (Nano Banana 2, önceki nesil), `gemini-3.1-flash-lite-image` (2 Lite: **yalnızca 1K**, çoklu referans ve çok adımlı düzenleme için optimize **değil**) | image-generation |
| Gemini çözünürlük | Pro ve 2.1: 1K/2K/4K (boyut değeri **büyük K**); 2: ayrıca 512; Lite: 1K | image-generation |
| Gemini referans sayısı | En çok 14 (Pro: 6 nesne + 5 karakter + 3 stil; 2.1/2: 10 nesne + 4 karakter; Lite: 14 nesne — tablo düzleştirilmiş okundu) | image-generation |
| Gemini fiyat (standart, ücretli) | Pro: giriş 2,00 $/1M jeton, çıktı metin+düşünme 12,00, **görsel 0,134 $ (1K/2K), 0,24 $ (4K)**. 2.1: giriş 1,50, metin 7,50, görsel 30 $/1M → **0,0336 (1K) / 0,0504 (2K) / 0,113 (4K)**. 2: giriş 0,50, metin 3,00, görsel 60 $/1M → 0,067/0,101/0,151. Lite: giriş 0,25, metin 1,50, görsel 30 $/1M → 0,0336 (1K). Dört modelde de **ücretsiz katman yok** | ai.google.dev/gemini-api/docs/pricing |
| Gemini düşünme | Gemini 3 görsel modellerinde düşünme **kapatılamaz**; ara görseller üretilebilir (son görsel nihai) | image-generation |
| SynthID | Tüm çıktılarda SynthID filigranı | image-generation |

### Doğrulanamayan / varsayım olan noktalar (çalıştığı iddia edilmez)
1. **Hiçbir model gerçek çağrıyla doğrulanmadı** (`LiveVerified=false`); yönetici raporunda her model "Canlı doğrulanmadı" etiketiyle görünür.
2. Gemini'nin **güvenlik/ret yanıtının tam şekli** dokümanda tanımlı değil: görselsiz `model_output` → `ContentRejected`, `status=incomplete` → `InvalidOutput`, `errors[]` içinde "safety/blocked/prohibited" geçen kod → `ContentRejected` olarak **varsayıldı**.
3. `aspect_ratio` desteklenen liste Pro/2.1 için dokümanda tam verilmemiş (Lite için verilmiş: 1:1, 3:2, 2:3, 3:4, 4:3, 4:5, 5:4, 9:16, 16:9, 21:9). Kişi fotoğrafına en yakın oran seçilir; model reddederse `400 → InvalidInput`.
4. `thinking_level` Pro için dokümanda belirtilmemiş; yalnızca yapılandırmada açıkça verilirse gönderilir.
5. `x-fashn-credits-used` başlığının `run` mı `status` yanıtında geldiği net değil; ikisi de okunur, yoksa kredi tablosundan hesaplanır ("bildirilen maliyet" boş kalır).
6. `total_output_tokens`'ın düşünme jetonlarını içerip içermediği belirsiz; maliyet tahmini ikisini toplayarak **güvenli tarafta** hesaplanır.
7. FASHN Try-On Max **Preview**: davranış/fiyat değişebilir. Gemini model sayfası Preview/GA durumu belirtmiyor.
8. Gemini **ücretli katman veri kullanım koşulları** ve yurt dışı aktarım için hukuki değerlendirme (08) sürüyor; müşteri fotoğrafıyla canlı kullanım **bu değerlendirme tamamlanmadan açılmamalıdır**.

## 2. Mimari (modüler)

```
GenerationStrategy (yapılandırma) → anahtarlı DI: Mock | Fashn | Gemini
  ITryOnProvider            → FashnTryOnProvider   (hazır takım)
  IImageGenerationProvider  → GeminiProvider       (çok parçalı kombin)
  IImageEditProvider        → GeminiProvider       (revizyon; motor Aşama 5)
  ImageWorker (SAM 2 + OpenCV) → dokunulmadı; renk revizyonu Aşama 5
GenerationProcessor: kapılar → sağlayıcı çağrısı → doğrulama → kayıt → bildirim → TV
```

Model eşlemesi `Generation:Strategies` içindedir (kod değişmez): `OutfitSet` → FASHN `tryon-max` (**balanced + 1k varsayılan**, balanced + 2k alternatif; yönetici ayarı, §3) · `FreeCombination` → `gemini-3-pro-image` (2K) · `ProductSwap`/`VariantPreview` → `gemini-nano-banana-2.1` (2K; K2 varsayılanı) · Lite yalnızca tek-referanslı basit revizyon **adayı**.

**Ücretli çağrı kapıları (hepsi geçmeden sağlayıcıya tek bayt gitmez):**
1. `Generation:UseMockProviders=false` (varsayılan **true**),
2. `Generation:Budget:AllowPaidCalls=true` (varsayılan **false**),
3. API anahtarı (ortam değişkeni/user-secrets; kaynağa yazılmaz),
4. Model, `ModelCatalog` içinde ve iş tipi için uygun (ör. Lite kombinde reddedilir; bilinmeyen kimlik reddedilir),
5. Bütçe: iş başı, günlük, aylık sınır (§3) — çağrının **en kötü durum** maliyetiyle birlikte.

**Hata sınıflandırma** (ortak `GenerationErrorCode`): `InvalidInput`, `ContentRejected` (yeniden denenmez), `RateLimited`/`Timeout`/`ProviderUnavailable` (üstel geri çekilmeyle ≤ 3 deneme), `QuotaExceeded` (FASHN kredisi bitti), `InvalidOutput` (yeniden denenmez), `BudgetExceeded`, `ProviderNotConfigured` (yeni). Hata iletileri sabittir; istek/yanıt gövdesi, anahtar ve görsel iletiye/günlüğe girmez.

**İptal:** kuyruktaki **ve çalışan** iş iptal edilebilir. Çalışan işte worker sağlayıcı bekleyişini bırakır, sonucu atar; çağrı başlamışsa sağlayıcı yine de ücretlendirebileceğinden **en kötü durum tahmini** kullanım kaydına yazılır (bütçe güvenli tarafta kalır).

**Müşteri görselinin sağlayıcıya iletimi:** satır içi base64 (kalıcı/public URL yok); Gemini `store:false`; FASHN `return_base64:true` (60 dk); EXIF depoya girerken silinmiş; giden HTTP istemcisinin günlüğü kapalı; yalnızca onayı (yurt dışı aktarım dahil) alınmış oturumlar işlenir (mevcut sunucu kuralı).

**Çıktı:** `OutputValidator` (çözümlenebilir, düz/siyah değil, çerçeve oranı fotoğrafla uyumlu) → `ImageStore` (EXIF'siz JPEG, oturum önekli anahtar) → `GeneratedImage` (`IsDemo=false` yalnızca gerçek sağlayıcıda; mock **her zaman DEMO**). İş bitince personel SignalR olayı alır; TV bu oturumun bir sonucunu **tekli gösteriyorsa** kendiliğinden en yeni sonuca geçer (boş ekran, karşılaştırma ve orijinal fotoğraf gösterimi değişmez).

## 3. Maliyet tahmini ve sınırlar

Tahminler yapılandırmadaki birim fiyatlardan hesaplanır (`Generation:Pricing`); fatura sağlayıcıdadır, ay sonu mutabakat yapılır.

| İş | Çağrı başı tahmin (başarılı) | Çağrı öncesi en kötü durum (kapı) |
|---|---|---|
| FASHN `tryon-max` **balanced/1k — varsayılan** (hazır takım) | 2 kredi × 0,075 = **0,15 $** (hata = 0 $) | 0,15 $ |
| FASHN `tryon-max` balanced/2k — alternatif | 3 kredi × 0,075 = **0,225 $** | 0,225 $ |
| Gemini Pro 2K, 5 parça kombin | ≈ **0,13–0,20 $** (görsel 0,134 $ + giriş ≈ 0,008 $ + düşünme) | 0,1976 $ |
| Gemini 2.1 2K revizyon | ≈ **0,05–0,08 $** | ≈ 0,07 $ |
| Gemini 2 Lite 1K basit revizyon | ≈ **0,035–0,04 $** | ≈ 0,04 $ |

**Kalite/çözünürlük ayarı (fiyat/performans):** Varsayılan **Dengeli + 1K = 2 kredi (≈ 0,15 $)**; Dengeli + 2K (3 kredi, ≈ 0,225 $) alternatiftir. Yönetici ekranında (**/maliyet → Hazır takım kalitesi**, `GET/PUT /admin/ai-quality`, Manager+) firma düzeyinde 1K/2K ve fast/balanced/quality seçilir (4K sunulmaz). Seçim sonraki hazır takım üretimlerinde FASHN `resolution`/`generation_mode` parametrelerini belirler; **maliyet tahmini, bütçe kapısı (en kötü durum) ve kullanım kaydı seçilen kaliteye göre hesaplanır**. Ayar yoksa sunucu yapılandırması (`Generation:Strategies:OutfitSet:Options`) geçerlidir. Ucuz seçeneğin yüz, kumaş, nakış ve ürün sadakatini koruduğu **varsayılmaz**: canlı testte 1K ile 2K aynı girdi ve tohumla karşılaştırılır (§5, §6); fark kabul edilemezse varsayılan 2K'ya çekilir.

**Varsayılan sınırlar** (`Generation:Budget`, firma başına; değiştirilebilir):

| Sınır | Varsayılan | Davranış |
|---|---|---|
| `AllowPaidCalls` | **false** | Gerçek sağlayıcı seçili olsa bile çağrı yok |
| `MaxJobCostUsd` | 0,50 $ | Bir işin (denemeler dahil) toplamı; aşılacaksa çağrı yapılmaz |
| `DailyLimitUsd` | 10 $ (UTC günü) | `BudgetExceeded`, ücret doğmaz |
| `MonthlyLimitUsd` | 150 $ (UTC ayı) | aynı |
| Mevcut kotalar | şube günlük 200 üretim, oturum başı 15 | değişmedi |

Eşzamanlı işlerde aşım en fazla `MaxParallelJobs × çağrı başı en kötü maliyet` kadar olabilir (kilitsiz denetim; bilinçli sadelik). Yönetici ekranı **AI kullanım ve maliyet** (`/maliyet`, API `GET /admin/usage`): mod (Mock/Gerçek), bütçe durumu, çağrı/başarı/yeniden deneme, süre, kredi, jeton, tahmini ve (varsa) bildirilen maliyet, model bazında dağılım, yapılandırılmış modellerin doğrulama durumu. Kişisel veri içermez.

### Gereksiz tekrar ve çift ücret önlemleri
- **Idempotency-Key** (mevcut) ve **istek parmak izi**: gerçek sağlayıcıda aynı fotoğraf + aynı seçim + aynı iş tipi için süren/başarılı iş varsa yeni iş açılmaz, mevcut iş döner; bilerek yeni varyasyon için `allowVariation=true`.
- **Tahmin kimliği hemen saklanır** (FASHN): zaman aşımı/çökme sonrası yeniden denemede aynı tahmin sorgulanır, yeni ücretli istek açılmaz. 60 dk sonra tahmin kaybolmuşsa **otomatik yeniden gönderilmez**.
- Geçersiz çıktı (ücret doğmuş olabilir) **otomatik yeniden denenmez**; maliyeti kaydedilir.
- Başarısız FASHN tahmini ücretsizdir → yeni tahmin açılır (bu güvenli).
- **Bilinen kalan risk:** Gemini'de yanıt gelmeden bağlantı/zaman aşımı olursa istek Google'a ulaşmış olabilir; yeniden deneme ikinci ücret doğurabilir (en fazla birkaç sent). FASHN `/run` yanıtı kaybolursa aynı belirsizlik geçerlidir.

## 4. Test sonuçları (ücretsiz)

| Katman | Kapsam | Sonuç |
|---|---|---|
| Birim | FASHN sözleşmesi (istek alanları, `return_base64` zorlaması, tohum, kredi, yoklama, devam etme, 404, zaman aşımı, 6 HTTP + 7 çalışma hatası eşlemesi, CDN izin listesi, anahtar sızıntısı yok) | geçti |
| Birim | Gemini sözleşmesi (uç nokta, başlıklar, `store:false`, giriş sırası = prompt sırası, 5 parça, `thought` görseli yok sayma, jeton maliyeti, görselsiz yanıt, `status` varyantları, 9 HTTP hatası, model/çözünürlük/boyut/parça sınırları) | geçti |
| Birim | Maliyet tahmini, model kataloğu (Lite kombinde reddi), çıktı doğrulama, prompt şablonları | geçti |
| Entegrasyon (gerçek DB, **sahte** sağlayıcı) | Ücretli çağrı kapısı, başarılı gerçek üretim (IsDemo=false, maliyet kaydı), günlük/iş başı bütçe, zaman aşımı → aynı tahmin, başarısız tahmin → yeni tahmin, geçersiz çıktı maliyeti, 5 parça Gemini, uygunsuz model, çalışan işi iptal, yönetici maliyet raporu, yinelenen istek koruması, TV'nin yeni sonuca geçmesi | geçti |
| E2E (Playwright) | Aşama 3 personel–TV senaryosu (otomatik TV güncellemesiyle) | geçti |
| **Canlı** | `LiveAiTests` — **atlandı** (onay + anahtar + test görseli gerekir) | çalıştırılmadı |

## 5. Kontrollü gerçek API test planı — **ONAY BEKLİYOR**

> **Bu bölümdeki hiçbir adım proje sahibinin ayrı, açık onayı olmadan çalıştırılmaz.**

**Ön koşullar (hepsi sağlanmadan başlanmaz):**
1. Proje sahibinin yazılı onayı ve harcama tavanı (öneri: **toplam 1,00 USD**).
2. API anahtarları yalnızca ortam değişkeni olarak: `Ai__Fashn__ApiKey`, `Ai__Gemini__ApiKey` (Gemini için **ücretli katman** ve faturalama etkin; ücretsiz katman kullanılmaz).
3. `testdata/ai-live/` içinde **izinli veya sentetik** görseller (gerçek müşteri fotoğrafı yok): `person.jpg` (boydan, izinli model/personel), `set.jpg` (hazır takım ürün fotoğrafı), `salvar.jpg`, `gomlek.jpg`, `yelek.jpg`, `kaftan.jpg`, `kemer.jpg`, `kemer-yeni.jpg`. Klasör git'e girmez.
4. Hukuki değerlendirme: test görselindeki kişinin yazılı izni (aktarım dahil).
5. Fatura panolarına erişim (FASHN, Google AI Studio/Cloud Billing) — mutabakat için.

**Adımlar ve tahmini maliyet** (her adım önceki adımın kaydı incelenmeden başlamaz):

| # | Adım | Çağrı | Tahmini | Başarı ölçütü |
|---|---|---|---|---|
| 1a | Tek hazır takım — FASHN `tryon-max`, **balanced/1k (varsayılan)** | 1 | 0,15 $ | Sonuç döner; kredi = 2; `x-fashn-credits-used` okunur; çıktı doğrulayıcıdan geçer; görsel kalite puanı (§6) |
| 1b | **Aynı girdi, aynı tohum** — balanced/2k karşılaştırması | 1 | 0,225 $ | Kredi = 3; 1K ile 2K'nın yüz/kumaş/nakış/ürün sadakati farkı yan yana puanlanır; karar: varsayılan 1K kalsın mı |
| 2 | 5 parçalı kombin — Gemini Pro 2K | 1 | ≈ 0,13–0,20 $ | Görsel döner; `usage` jeton alanları gerçek şemayla eşleşir; parçalar karışmaz; ekstra aksesuar yok |
| 3 | İlk sonuç üzerinde tek parça (kemer) revizyonu — Gemini 2.1 2K | 1 | ≈ 0,05–0,08 $ | Yalnızca kemer değişir (ham model çıktısı; maskeli birleştirme Aşama 5) |
| 3b | (İsteğe bağlı) aynı revizyon Lite 1K karşılaştırması | 1 | ≈ 0,04 $ | Lite'ın yeterliliği; karşılaştırma kaydı |
| 4 | Uygulama ve TV'ye ulaşma (mağaza TV'si olmadan Playwright'ta) | 1 FASHN (1K) veya 1 Gemini | ≤ 0,20 $ | Üretim bitince personel SignalR olayı; tekli gösteren TV'nin kendiliğinden güncellenmesi; gerçek sonucun DEMO işaretli olmaması |
| 5 | Hata/zaman aşımı/maliyet sınırı | 0 (ücretsiz): geçersiz anahtar ile 401/403 eşlemesi; `MaxWaitSeconds` kısaltılarak zaman aşımı + devam etme; bütçeyi 0,01 $ yaparak `BudgetExceeded` | 0 $ | Hatalar doğru kodlara eşlenir; sağlayıcı çağrılmadan bütçe reddi |

**Toplam tahmin:** adım 1a–4 ≈ **0,70–0,85 $**; yeniden deneme payıyla tavan **1,00 $**.

**Çalıştırma** (onaydan sonra, PowerShell):
```powershell
$env:SANALPROVA_LIVE_AI = 'ODEME-ONAYLANDI'
$env:SANALPROVA_LIVE_AI_BUDGET_USD = '1.00'   # varsayılan adımlar: fashn (1K), fashn-2k, gemini-combo, gemini-edit
$env:Ai__Fashn__ApiKey = '<anahtar>'; $env:Ai__Gemini__ApiKey = '<anahtar>'
cd backend; dotnet test --solution SanalProva.slnx --filter-class "*LiveAiTests"
```
Koşucu her çağrıdan önce en kötü durum tahminini kalan bütçeyle karşılaştırır ve aşılacaksa çağrı yapmaz. Çıktılar ve `kayit.md`/`kayit.json` `docs/ai-benchmark/runs/<tarih>/` altına yazılır.

**Durdurma ölçütleri:** ilk çağrıda beklenmeyen şema/hata → dur, ayrıntıyı incele; maliyet tahminin 2 katını aşarsa → dur; içerik reddi → girdiyi gözden geçir, tekrar deneme.

**Test sonrası:** sağlayıcı panolarından gerçek harcama alınır, `kayit.md` ile karşılaştırılır; `LiveVerified` bayrakları (`ModelCatalog`) yalnızca başarılı adımlarda, kanıtla (kayıt) `true` yapılır; §1'deki varsayımlar güncellenir.

## 6. Görsel kalite değerlendirme kaydı

Her sonuç için iki değerlendirici (1–5) ve notlar. Kayıt şablonu `docs/ai-benchmark/kayit-sablonu.md`; koşucu aynı sütunlarla `kayit.md` üretir.

| Ölçüt | Soru |
|---|---|
| Kimlik | Yüz, saç, ten aynı mı? (garanti verilmez; modeller değiştirebilir) |
| Poz/vücut | Poz, ellerin durumu, vücut oranı korunmuş mu? |
| Nakış/desen | Altın nakış, motif ve doku ürün fotoğrafına benziyor mu? Uydurma süs var mı? |
| Renk | Ürün rengiyle uyumlu mu? (ΔE ölçümü Aşama 6) |
| Parça ayrımı (kombin) | Beş parça karışmadan, doğru yerde ve bir kez mi? |
| Gereksiz öğe | Taç, ayakkabı, takı, ek kıyafet var mı? |
| Çerçeve | Aynı kadraj/oran mı? |

Kabul: ortalama ≥ 3,5 ve "gereksiz öğe/karışma" yok → model o iş için aday; aksi halde prompt/parametre iyileştirilir veya model değiştirilir. Kullanıcıya hiçbir zaman "birebir aynı" vaadi verilmez (arayüzdeki yapay zekâ önizlemesi uyarısı korunur).

## 7. Kalan eksikler
- Canlı doğrulama yapılmadı (§5); modeller "Canlı doğrulanmadı".
- Gemini güvenlik yanıtı şekli ve `aspect_ratio` listesi doğrulanacak (§1).
- Webhook yok (yoklama yeterli); FASHN eşzamanlılık sınırı 6 — `MaxParallelJobs` 4 (aşılmaz).
- Maliyet sınırlarında kilitsiz denetim (küçük aşım payı).
- Aşama 5 (SAM 2 + OpenCV revizyon, maskeli birleştirme) başlamadı; `POST /revisions` hâlâ 501.
- **Gerçek Smart TV uyumluluk testi açık** (Aşama 3'ten devam; docs/09 §3.0).
- Hukuki: yurt dışı aktarım/sağlayıcı sözleşmesi (08) tamamlanmadan müşteri fotoğrafıyla canlı kullanım yok.
