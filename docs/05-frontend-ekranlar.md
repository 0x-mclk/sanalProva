# 05 — React PWA Ekranları ve Kullanıcı Akışları

## 1. Uygulama yapısı

Tek Vite projesi, **iki giriş noktası**:

| Giriş | Yol | Hedef cihaz | Not |
|---|---|---|---|
| `index.html` | `/` | Telefon / tablet (personel) | PWA, kurulabilir |
| `tv.html` | `/tv` | Smart TV tarayıcısı | Ayrı, küçük bundle; Mantine'in yalnızca gerekli parçaları; eski TV tarayıcıları için `build.target: es2019` + gerekirse `@vitejs/plugin-legacy` |

Ortak kod `src/shared/` altında. TV paketine katalog yönetimi, formlar vb. **girmez**.

## 2. Tasarım sistemi

Figma taslakları gelince güncellenecek geçici token seti:

| Token | Değer (öneri) | Kullanım |
|---|---|---|
| `--color-bordo-700` | `#6D1A2A` | Birincil eylem, başlık |
| `--color-bordo-900` | `#3F0E18` | TV arka planı, koyu yüzey |
| `--color-krem-50` | `#FBF7EF` | Uygulama arka planı |
| `--color-kirik-beyaz` | `#F5F0E6` | Kart yüzeyi |
| `--color-altin-500` | `#B8913A` | Vurgu, seçili durum, ince çerçeve |
| `--color-altin-300` | `#D9BE7C` | İkincil vurgu |
| Başlık fontu | Serif (ör. Cormorant Garamond / Playfair Display) | Premium his |
| Gövde fontu | Sans (ör. Inter / Manrope — Türkçe karakter desteği doğrulanacak) | Okunabilirlik |
| Dokunma hedefi | ≥ 48 px, birincil butonlar 56 px | |
| Köşe | 12–16 px | |

İlkeler: büyük ürün görselleri (kart görseli ekran genişliğinin ≥ %45'i tablet dikeyde), ekranda tek birincil eylem, teknik terim yok ("model", "prompt", "seed" asla görünmez), her bekleme durumunda ilerleme göstergesi, kontrast WCAG AA.

## 3. Ekran listesi

| # | Ekran | Rota | Rol | Ana bileşenler |
|---|---|---|---|---|
| 1 | Personel girişi | `/giris` | herkes | E-posta, parola, "beni hatırla"; PIN kilidi ekranı ayrı modal |
| 2 | Ana sayfa | `/` | Staff+ | "Yeni Prova" büyük butonu, açık oturumlar, TV durumları, mağaza seçici |
| 3 | Yeni prova başlatma | `/prova/yeni` | Staff+ | Takma ad (ops.), **KVKK aydınlatma + onay kutuları**, "Devam" |
| 4 | Fotoğraf çekme/yükleme | `/prova/:id/fotograf` | Staff+ | Kamera önizleme + boy çerçevesi kılavuzu, çek/galeriden yükle, kalite uyarıları, yeniden çek |
| 5 | Hazır takım seçimi | `/prova/:id/takim` | Staff+ | Kategori sekmeleri, büyük görsel ızgarası, varyant renk çipleri, "Üret" |
| 6 | Serbest kombin | `/prova/:id/kombin` | Staff+ | Kategori şeridi (dinamik), her kategoriden seçim, alt "kombin çekmecesi" (seçilenler), çakışma uyarısı, "Üret" |
| 7 | Üretim durumu | `/prova/:id/uretim/:jobId` | Staff+ | Aşama göstergesi (Hazırlanıyor → Üretiliyor → Kontrol), müşteri fotoğrafı bulanık arka plan, iptal, hata + tekrar dene |
| 8 | Sonuç | `/prova/:id/sonuc/:imageId` | Staff+ | Tam ekran görsel, "TV'ye gönder", "Değişiklik iste", "Karşılaştır", favori, sorumluluk notu |
| 9 | Revizyon | `/prova/:id/revizyon/:imageId` | Staff+ | Adım 1: hangi parça (kombindeki kategoriler) → Adım 2: "Renk" / "Başka ürün" → Adım 3: varyant/ürün seçimi → "Uygula" |
| 10 | Karşılaştırma | `/prova/:id/karsilastir` | Staff+ | İki sürüm seçimi, yan yana / kaydırıcı, "TV'de karşılaştır" |
| 11 | Prova geçmişi | `/gecmis` | Staff+ | Oturum listesi (süresi dolmamış), sürüm zaman çizelgesi, silme |
| 12 | TV eşleştirme | `/tv-cihazlar` | Manager+ (kayıt), Staff (bağlama) | Kayıtlı TV'ler + çevrimiçi göstergesi, "Yeni TV ekle" (kod girişi / QR okut), oturuma bağla |
| 13 | TV görüntüleme | `/tv` (tv.html) | TV cihazı | Eşleştirme kodu ekranı → bekleme ekranı → tekli görsel / karşılaştırma |
| 14 | Katalog yönetimi | `/katalog` | Manager+ | Filtre, arama, aktif/pasif, "AI'ya hazır değil" uyarı rozeti |
| 15 | Ürün ekle/düzenle | `/katalog/urun/:id?` | Manager+ | Temel bilgiler, tip, varyantlar, görsel yükleme (açı + tür + AI referansı), takım bileşenleri |
| (+) | Kategori yönetimi | `/katalog/kategoriler` | Manager+ | Sıra, katman, giyim bölgesi, seçim kuralı, çakışmalar |
| (+) | Ayarlar / Kullanım | `/yonetim` | Manager/Owner | Kullanıcılar, kota, saklama, maliyet raporu |

## 4. Ana akışlar

### 4.1 Hazır takım (hedef: ≤ 6 dokunuş)
```
Ana sayfa ─[Yeni Prova]→ Onay ─[Onaylıyorum + Devam]→ Fotoğraf ─[Çek]→[Kullan]
  → Mod seçimi ─[Hazır Takım]→ Takım ızgarası ─[ürün]→[renk]→[Üret]
  → Üretim durumu (≈10–30 sn) → Sonuç ─[TV'ye Gönder]
```

### 4.2 Serbest kombin
```
Fotoğraf → Mod seçimi ─[Kombin Oluştur]
  → Kategori: Şalvar ─[ürün + renk]  → Gömlek ─[...] → Yelek → Kemer
     (alt çekmecede seçilenler küçük kartlar; çıkarmak için dokun)
  → [Üret] → Durum → Sonuç
```
Kategori sırası `categories.sort_order`'dan gelir; yeni kategori eklenince ekran kod değişmeden günceller.

### 4.3 Revizyon
```
Sonuç ─[Değişiklik İste]
  → "Hangi parça?"   (sonuçtaki kombin parçaları: Şalvar, Kemer, ...)
  → "Ne değişsin?"   [Rengi]  [Başka model]
      Rengi    → katalogdaki varyantlar (gerçek) + ayrı bölüm "Renk önizlemesi" (palet, etiketli)
      Başka model → aynı kategorideki ürünler
  → [Uygula] → Durum → Yeni sürüm (v3) → otomatik "v2 ↔ v3 karşılaştır" önerisi
```

### 4.4 TV
```
TV: /tv açılır → kod "K7M-4QX" + QR gösterir
Yönetici: TV Cihazlar → [Yeni TV] → kodu girer / QR okutur → ad verir → kaydedildi
TV: bekleme ekranına geçer (mağaza logosu)
Personel: Sonuç → [TV'ye Gönder] → TV seç (tek TV varsa otomatik) → TV sonucu gösterir
Personel: Karşılaştırma → [TV'de karşılaştır] → TV iki görseli yan yana gösterir
Personel: Oturumu kapat → TV bekleme ekranına döner
```

## 5. Durum yönetimi

- **Sunucu durumu:** TanStack Query (oturum, katalog, işler). SignalR olayları ilgili sorguyu `invalidate` eder veya önbelleği günceller — iki ayrı veri kaynağı tutulmaz.
- **Yerel UI durumu:** Kombin seçimi gibi geçici durumlar için React state / küçük bir Zustand store. Müşteri verisi `localStorage`'a **yazılmaz**.
- **Kamera:** Önce `getUserMedia` (arka kamera, yüksek çözünürlük, boy çerçevesi kılavuzu); desteklenmezse `<input type="file" accept="image/*" capture="environment">`.
- **Yükleme:** İstemci tarafında uzun kenar 2048 px'e küçültme + JPEG %90 (yükleme süresi), sunucu yine doğrular ve yeniden işler. EXIF (konum vb.) sunucuda silinir.

## 6. PWA davranışı

| Konu | Karar |
|---|---|
| Manifest | Ad "Sanal Prova", tema bordo, `display: standalone`, dikey + yatay |
| Önbellek | Uygulama kabuğu (precache). Katalog küçük görselleri `StaleWhileRevalidate`. **`/files/*` müşteri görselleri ve API yanıtları önbelleğe alınmaz** |
| Güncelleme | Yeni sürüm hazır olduğunda "Güncelle" bildirimi; prova sırasında zorla yenileme yapılmaz |
| Çevrimdışı | Kabuk açılır, "İnternet bağlantısı yok" bandı, Üret butonu pasif |

## 7. Erişilebilirlik ve dil

- Tüm metinler `i18n/tr.json`'da; ileride çoklu dil için hazır.
- Hata kodu → Türkçe mesaj eşlemesi tek dosyada (`errors.tr.ts`).
- Ekran okuyucu etiketleri, odak sırası, `prefers-reduced-motion` desteği.

## 8. Sorumluluk metinleri (UI)

- Sonuç ekranı alt bilgi: *"Bu görsel yapay zekâ ile oluşturulmuş bir önizlemedir. Beden uyumu, kalıp ve nakış detayları gerçek üründen farklı olabilir."*
- Renk önizlemesi rozeti: *"Renk önizlemesi — bu renk stokta olmayabilir."*
- TV ekranının köşesinde küçük "Yapay zekâ önizlemesi" etiketi.
