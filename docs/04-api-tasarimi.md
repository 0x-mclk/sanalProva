# 04 — Backend API Tasarımı

## 1. Genel sözleşme

- Taban yol: `/api/v1`. OpenAPI dokümanı `/openapi/v1.json` (yalnızca geliştirme ortamında public).
- JSON, `camelCase`. Tarih: ISO 8601 UTC.
- **Kimlik doğrulama:** Personel için HttpOnly + Secure + SameSite=Strict cookie. TV için `Authorization: Bearer <deviceToken>`.
- **CSRF:** Cookie kullanan değiştirici isteklerde `X-CSRF-TOKEN` başlığı (ASP.NET Core Antiforgery).
- **Kiracı:** İstekte organization/store ID'si **gönderilmez**; kullanıcının oturumundan ve seçili mağazadan (`X-Store-Id` başlığı, yetkisi doğrulanır) türetilir.
- **Hatalar:** RFC 9457 Problem Details. `code` alanı frontend'in Türkçe mesaj eşlemesi içindir:

```json
{
  "type": "https://sanalprova/errors/quota-exceeded",
  "title": "Günlük üretim kotası doldu",
  "status": 429,
  "code": "QUOTA_EXCEEDED",
  "traceId": "00-...",
  "errors": { }
}
```

- **Sayfalama:** `?page=1&pageSize=24` → `{ items, page, pageSize, total }`.
- **Idempotency:** Üretim başlatan uç noktalarda `Idempotency-Key` başlığı zorunlu (UUID, istemci üretir).
- **Rate limit:** Giriş (IP başına 5/dk), eşleştirme kodu (IP başına 10/dk), üretim (kullanıcı başına 10/dk).

## 2. Yetki matrisi

| Kaynak | Staff | Manager | Owner |
|---|---|---|---|
| Prova oturumu oluştur/yürüt | ✔ | ✔ | ✔ |
| Başkasının oturumunu görme (aynı mağaza) | ✔ | ✔ | ✔ |
| Katalog görüntüleme | ✔ | ✔ | ✔ |
| Katalog düzenleme | – | ✔ | ✔ |
| TV kaydetme / iptal | – | ✔ | ✔ |
| TV'ye oturum bağlama | ✔ | ✔ | ✔ |
| Kullanıcı yönetimi | – | mağaza içi | ✔ |
| Kullanım/maliyet raporu | – | ✔ | ✔ |
| Saklama/sağlayıcı ayarları | – | – | ✔ |

## 3. Uç noktalar

### 3.1 Kimlik

| Metot | Yol | Açıklama |
|---|---|---|
| POST | `/auth/login` | `{ email, password }` → cookie; yanıt: kullanıcı + mağazalar |
| POST | `/auth/logout` | |
| GET | `/auth/me` | Kullanıcı, roller, mağazalar |
| POST | `/auth/unlock` | `{ pin }` — ekran kilidini açar |
| GET | `/auth/csrf` | Antiforgery token |

### 3.2 Katalog

| Metot | Yol | Açıklama |
|---|---|---|
| GET | `/categories` | Aktif kategoriler, sıralı, kurallarıyla |
| POST/PUT/DELETE | `/categories/{id}` | Manager+ |
| GET | `/products?type=OutfitSet&categoryId=&q=&active=true` | Liste; her ürün için kapak görseli imzalı URL'i |
| GET | `/products/{id}` | Detay: varyantlar, görseller, takım bileşenleri |
| POST | `/products` | Manager+ |
| PUT | `/products/{id}` | |
| PATCH | `/products/{id}/status` | `{ isActive }` |
| POST | `/products/{id}/variants` · PUT/DELETE `/variants/{id}` | |
| POST | `/products/{id}/images` | `multipart/form-data`: file, variantId?, angle, kind, isAiReference |
| PATCH/DELETE | `/product-images/{id}` | |

**Örnek — ürün listesi öğesi**
```json
{
  "id": "0192...",
  "code": "BND-014",
  "name": "Zümrüt Bindallı Takım",
  "productType": "OutfitSet",
  "category": { "id": "...", "name": "Bindallı" },
  "coverImageUrl": "/files/...?exp=...&sig=...",
  "variants": [{ "id": "...", "colorName": "Bordo", "colorHex": "#6D1A2A", "isDefault": true }],
  "isReadyForAi": true
}
```
`isReadyForAi = false` ise (AI referans görseli yok) ürün seçim ekranında pasif görünür.

### 3.3 Prova oturumları

| Metot | Yol | Açıklama |
|---|---|---|
| POST | `/sessions` | `{ customerLabel? }` → `{ id, status }` |
| GET | `/sessions?status=Open&mine=false` | Geçmiş/aktif liste (görselsiz özet + küçük önizleme) |
| GET | `/sessions/{id}` | Tam durum: onay, fotoğraflar, sonuç sürümleri, aktif işler, bağlı TV |
| POST | `/sessions/{id}/consent` | `{ noticeVersion, processingConsent, foreignTransferConsent, extendedRetentionConsent, guardianConsent?, method }` |
| DELETE | `/sessions/{id}/consent` | Onay geri alındı → oturum hemen silinir |
| POST | `/sessions/{id}/close` | TV'den kaldırır, saklama süresini başlatır |
| DELETE | `/sessions/{id}` | Hemen sil (müşteri talebi) |
| GET | `/consent-notice/current` | Güncel aydınlatma metni + sürüm |

### 3.4 Fotoğraflar

| Metot | Yol | Açıklama |
|---|---|---|
| POST | `/sessions/{id}/photos` | `multipart`: file, angle. Onay yoksa `409 CONSENT_REQUIRED`. En fazla 15 MB, JPEG/PNG/HEIC. Yanıtta `qualityFlags` |
| DELETE | `/sessions/{id}/photos/{photoId}` | |
| PATCH | `/sessions/{id}/photos/{photoId}` | `{ isPrimary: true }` |

### 3.5 Üretim

| Metot | Yol | Açıklama |
|---|---|---|
| POST | `/sessions/{id}/generations` | İlk üretim (aşağıda) → `202 { jobId }` |
| POST | `/sessions/{id}/revisions` | Revizyon (aşağıda) → `202 { jobId }` |
| GET | `/jobs/{jobId}` | Durum (SignalR yedeği olarak polling için) |
| POST | `/jobs/{jobId}/cancel` | Yalnızca `Queued` iken |
| POST | `/jobs/{jobId}/retry` | `Failed` iş için yeni iş oluşturur |
| GET | `/sessions/{id}/images` | Sürüm ağacı |
| PATCH | `/images/{imageId}` | `{ isFavorite }` |

**İlk üretim isteği**
```json
// Hazır takım
{ "mode": "OutfitSet", "photoId": "...", "items": [{ "productId": "...", "variantId": "..." }] }

// Serbest kombin
{ "mode": "FreeCombination", "photoId": "...",
  "items": [
    { "productId": "salvar-...", "variantId": "..." },
    { "productId": "gomlek-...", "variantId": "..." },
    { "productId": "yelek-...",  "variantId": "..." },
    { "productId": "kemer-...",  "variantId": "..." }
  ] }
```
Sunucu doğrular: ürün aktif ve AI'ya hazır, kategori seçim kuralları, çakışan kategoriler, `OutfitSet` modunda tam 1 öğe.

**Revizyon isteği** (serbest metin yok — yalnızca yapılandırılmış seçenek)
```json
// Renk değişimi
{ "baseImageId": "...", "type": "Recolor",
  "targetCategoryId": "salvar-kat", "targetVariantId": "kirik-beyaz-varyant" }
// veya katalogda yoksa paletten:
{ "baseImageId": "...", "type": "Recolor", "targetCategoryId": "...", "paletteColorId": "kirik-beyaz" }

// Ürün değiştirme
{ "baseImageId": "...", "type": "ProductSwap",
  "targetCategoryId": "kemer-kat", "productId": "...", "variantId": "..." }

// Aynı kombinin farklı varyantı (toplu)
{ "baseImageId": "...", "type": "VariantPreview", "productId": "...", "variantIds": ["...", "..."] }
```
Sunucu, `Recolor` + katalogda gerçek varyant varsa işi `ProductSwap` olarak yürütür (K3). Yanıt hangi yolun seçildiğini döner: `{ jobId, resolvedType: "ProductSwap" }`.

**İş durumu**
```json
{
  "id": "...", "type": "FreeCombination", "status": "Running",
  "stage": "Generating",          // Preparing | Generating | Downloading | Validating
  "queuePosition": null,
  "createdAt": "...", "startedAt": "...",
  "resultImage": null,
  "error": null                   // { code: "PROVIDER_TIMEOUT", retryable: true }
}
```

### 3.6 Dosyalar

| Metot | Yol | Açıklama |
|---|---|---|
| GET | `/files/{key}?exp=&sig=` | Yalnızca geçerli HMAC imzasıyla; `Cache-Control: private, max-age=300`. Müşteri görselleri için TTL 5 dk, katalog için 1 saat |

### 3.7 TV (Display)
Personel tarafı:

| Metot | Yol | Açıklama |
|---|---|---|
| GET | `/displays` | Mağazanın TV'leri, çevrimiçi durumu, bağlı oturum |
| POST | `/displays/claim` | `{ code, name }` — Manager+. TV'yi mağazaya kaydeder |
| PATCH | `/displays/{id}` | Ad değiştir |
| DELETE | `/displays/{id}` | Cihazı iptal et (token geçersiz) |
| PUT | `/displays/{id}/session` | `{ sessionId }` — oturumu TV'ye bağla (başka oturuma bağlıysa `409`, `force=true` ile devral) |
| DELETE | `/displays/{id}/session` | TV'yi boşalt |
| PUT | `/displays/{id}/state` | `{ mode: "Single" \| "Compare", imageIds: [...] , expectedVersion? }` — görseller **bağlı oturuma ait olmak zorunda** (sonuç veya orijinal fotoğraf); Single=1, Compare=2 farklı görsel. Sürüm uyuşmazlığında `409 STATE_CONFLICT` |
| POST | `/displays/{id}/clear` | Ekranı boşaltır (oturum bağlı kalır) |

TV tarafı (anonim → cihaz token'ı):

| Metot | Yol | Açıklama |
|---|---|---|
| POST | `/display/pairing` | Kimliksiz, IP başına hız sınırlı. → `{ code: "482913" (6 hane), pairingSecret, expiresAt }` |
| GET | `/display/pairing/status` | `Authorization: Pairing <secret>` → `{ status: "Pending" \| "Claimed" \| "Expired" }`. Cihaz belirteci **ayrıca dönmez**: gizli değer, eşleşme anında cihaz belirtecine dönüşür (bkz. 06 §8) |
| GET | `/display/state` | Bearer deviceToken → mevcut durum + imzalı görsel URL'leri + `stateVersion` |

Ayrıntı: [06 — TV / SignalR](06-tv-signalr.md).

### 3.8 Yönetim

| Metot | Yol | Açıklama |
|---|---|---|
| GET/POST/PUT | `/admin/stores` | Owner |
| GET/POST/PUT | `/admin/users` | |
| GET | `/admin/usage?from=&to=&storeId=` | Üretim sayısı, başarı, süre, maliyet (kişisel veri yok) |
| GET/PUT | `/admin/settings` | Saklama süresi, kota, renk paleti |

### 3.9 Sistem

| Metot | Yol | Açıklama |
|---|---|---|
| GET | `/health/live` · `/health/ready` | DB + image-worker + depolama kontrolü |

## 4. SignalR hub'ları (özet)

| Hub | İstemci | Sunucu → istemci olayları |
|---|---|---|
| `/hubs/staff` | Personel (cookie) | `JobUpdated(job)`, `SessionUpdated(sessionId)`, `DisplayStatusChanged(display)`; istemci çağrıları `JoinSession(id)`, `JoinStore(storeId)` (rol sunucuda doğrulanır) |
| `/hubs/display` | TV (deviceToken) | `StateChanged({stateVersion, reason})`, `Cleared()`, `Revoked()`; istemci çağrısı `Heartbeat(stateVersion)` |

## 5. Python image-worker iç API'si

Yalnızca iç ağdan erişilebilir; paylaşılan gizli başlıkla korunur (`X-Internal-Key`).

| Metot | Yol | Girdi | Çıktı |
|---|---|---|---|
| POST | `/v1/segment` | görsel (multipart) + hedef (`{ "category": "Lower", "hint": "salvar", "points": [...]? }`) | maske PNG + `{ confidence, areaRatio }` |
| POST | `/v1/recolor` | görsel + maske + `{ targetHex, preserveTexture: true }` | görsel + kalite raporu `{ deltaE, edgeLeak, patternPreserved, passed }` |
| GET | `/health` | | `{ segmentation: "mock" \| "sam2" }` |

Servis durumsuzdur (stateless): girdiyi bellekte işler, diske yazmaz.
