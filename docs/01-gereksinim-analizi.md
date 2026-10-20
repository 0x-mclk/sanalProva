# 01 — Gereksinim Analizi

## 1. Mevcut durum

- Çalışma klasörü (`sanalprova/`) **boş**; mevcut kod, git deposu veya tasarım dosyası yok.
- Figma taslakları klasörde değil. Bu oturumda Figma bağlantısı yetkilendirilmemiş; taslaklar ya dışa aktarılıp `docs/design/` altına konmalı ya da claude.ai bağlayıcı ayarlarından Figma yetkilendirilmeli.
- Proje sıfırdan (greenfield) kurulacak.

## 2. Paydaşlar ve aktörler

| Aktör | Cihaz | Amaç |
|---|---|---|
| **Personel** (Staff) | Telefon / tablet (PWA) | Fotoğraf çeker, ürün seçer, üretimi başlatır, TV'ye gönderir |
| **Mağaza yöneticisi** (Manager) | Tablet / PC | Katalog, TV cihazları, personel, raporlar |
| **Firma sahibi** (Owner) | PC | Şubeler, kotalar, saklama politikası, sağlayıcı ayarları |
| **Müşteri** | — (uygulamayı doğrudan kullanmaz) | Fotoğrafı çekilir, onay verir, sonucu TV'de görür |
| **Smart TV** (Display) | TV tarayıcısı | Kendisine atanmış oturumun görsellerini gösterir |
| **AI sağlayıcıları** | Harici | FASHN, Google Gemini, (ops.) Bria |

## 3. Fonksiyonel gereksinimler

Öncelik: **M** = MVP'de zorunlu, **S** = MVP'de olmalı, **L** = sonraki sürüm.

### 3.1 Kimlik ve yetki

| ID | Gereksinim | Öncelik |
|---|---|---|
| FR-AUTH-1 | Personel e-posta/kullanıcı adı + parola ile giriş yapar | M |
| FR-AUTH-2 | Roller: Owner, Manager, Staff. Yetkiler rol ve mağaza bazlıdır | M |
| FR-AUTH-3 | Personel yalnızca atandığı mağaza(lar)ın verisini görür | M |
| FR-AUTH-4 | Hızlı mağaza içi kullanım için uzun oturum + boşta kalınca kilit (PIN ile açma) | S |
| FR-AUTH-5 | Başarısız giriş denemelerinde kilitleme | M |

### 3.2 Prova oturumu

| ID | Gereksinim | Öncelik |
|---|---|---|
| FR-SES-1 | Personel yeni prova oturumu başlatır (isteğe bağlı müşteri takma adı: "Ayşe H.", masa no vb.) | M |
| FR-SES-2 | **KVKK aydınlatma gösterilir ve açık rıza alınmadan fotoğraf yüklenemez** | M |
| FR-SES-3 | Boydan fotoğraf kamera ile çekilir veya galeriden yüklenir; açı etiketi (FRONT varsayılan) | M |
| FR-SES-4 | Fotoğraf kalite ön kontrolü: çözünürlük, tek kişi, tam boy, bulanıklık uyarısı | S |
| FR-SES-5 | Oturum durumları: Açık → Kapandı → (süre dolunca) Silindi | M |
| FR-SES-6 | Personel oturumu kapatır; kapatılan oturumun görselleri TV'den kalkar | M |

### 3.3 Hazır takım

| ID | Gereksinim | Öncelik |
|---|---|---|
| FR-SET-1 | Katalogdaki hazır takımlar büyük görselle listelenir, kategori/arama ile filtrelenir | M |
| FR-SET-2 | Takımın renk varyantı seçilebilir | M |
| FR-SET-3 | "Üret" ile tek AI işi oluşturulur (FASHN Try-On Max) | M |

### 3.4 Serbest kombin

| ID | Gereksinim | Öncelik |
|---|---|---|
| FR-CMB-1 | Kategoriler (Şalvar, Gömlek, Yelek, Kaftan, Kemer) dinamik olarak katalogdan gelir; yeni kategori kodsuz eklenir | M |
| FR-CMB-2 | Her kategoriden en fazla N ürün (varsayılan 1) seçilir; kural kategori üzerinde tanımlıdır | M |
| FR-CMB-3 | Uyumsuz kombinasyon uyarısı (ör. kaftan + yelek aynı katmanda çakışma) — kurallar kategori metadata'sından | S |
| FR-CMB-4 | "Üret" ile tek AI işi oluşturulur (Gemini Nano Banana Pro, çoklu referans) | M |
| FR-CMB-5 | Hazır takım + tekil ürün karışımı (ör. takım + farklı kemer) | L |

### 3.5 Üretim ve sonuç

| ID | Gereksinim | Öncelik |
|---|---|---|
| FR-GEN-1 | Üretim asenkron; ekranda canlı durum (Sırada / İşleniyor / Tamamlandı / Hata) | M |
| FR-GEN-2 | Aynı istek iki kez gönderilirse tek iş oluşur (idempotency) | M |
| FR-GEN-3 | Hata durumunda Türkçe, eyleme dönük mesaj ve "Tekrar dene" | M |
| FR-GEN-4 | Sonuç ekranında "AI önizlemesidir; beden ve nakış birebir garanti edilmez" ibaresi | M |
| FR-GEN-5 | İş iptal edilebilir (sağlayıcıya gönderilmeden önce) | S |
| FR-GEN-6 | Mağaza başına günlük/aylık üretim kotası | S |

### 3.6 Revizyon

| ID | Gereksinim | Öncelik |
|---|---|---|
| FR-REV-1 | Personel sonuç üzerinden revizyon seçer: **renk değiştir** / **ürün değiştir** / **varyant göster** — serbest metin yok | M |
| FR-REV-2 | Renk değişimi: önce katalogda gerçek varyant var mı bakılır; varsa o ürünle yeniden giydirme, yoksa SAM 2 + OpenCV renk önizlemesi | M |
| FR-REV-3 | Renk değişiminde maske/kalite kontrolü başarısızsa otomatik olarak AI edit yoluna düşülür veya kullanıcıya bildirilir | M |
| FR-REV-4 | Ürün değiştirme: önceki sonuçtan türetilen AI edit | M |
| FR-REV-5 | Her revizyon yeni sürümdür; sürüm ağacı/zaman çizelgesi gösterilir; eski sürüm silinmez | M |
| FR-REV-6 | İki sürüm yan yana / kaydırıcı ile karşılaştırılır | M |

### 3.7 Smart TV

| ID | Gereksinim | Öncelik |
|---|---|---|
| FR-TV-1 | TV tarayıcıda `/tv` adresini açar, ekranda geçici eşleştirme kodu (6 karakter, 5 dk) ve QR görür | M |
| FR-TV-2 | Yönetici kodu girerek TV'yi mağazaya kaydeder (bir kez) | M |
| FR-TV-3 | Personel oturumu bir TV'ye "bağlar"; aynı anda bir TV yalnızca bir oturumu gösterir | M |
| FR-TV-4 | Personel gösterilen görseli seçer, karşılaştırma modunu açar/kapatır | M |
| FR-TV-5 | Bağlantı koparsa otomatik yeniden bağlanma ve son durumu geri yükleme | M |
| FR-TV-6 | Boşta (oturum yok) markalı bekleme ekranı; müşteri görseli asla boşta kalmaz | M |
| FR-TV-7 | Oturum kapanınca / X dk etkileşim yoksa TV otomatik temizlenir | M |

### 3.8 Katalog yönetimi

| ID | Gereksinim | Öncelik |
|---|---|---|
| FR-CAT-1 | Ürün CRUD: kod, ad, kategori, açıklama, tip (Tekil / Hazır takım), aktif/pasif | M |
| FR-CAT-2 | Renk varyantları (ad, renk kodu, varyant kodu) | M |
| FR-CAT-3 | Görseller: varyant + açı (FRONT/BACK/LEFT/RIGHT) + tür (ürün düz çekim / mankende / detay) | M |
| FR-CAT-4 | Bir görsel "AI referansı" olarak işaretlenir (üretimde kullanılacak) | M |
| FR-CAT-5 | Kategori yönetimi: ad, sıra, katman sırası, giyim bölgesi, seçim kuralı | M |
| FR-CAT-6 | Hazır takımın bileşenleri (opsiyonel bağ: takım ⟶ tekil ürünler) | S |
| FR-CAT-7 | Toplu içe aktarma (CSV + ZIP görsel) | L |

### 3.9 Geçmiş ve raporlama

| ID | Gereksinim | Öncelik |
|---|---|---|
| FR-HIS-1 | Personel saklama süresi dolmamış oturumları listeler | M |
| FR-HIS-2 | Yönetici: üretim sayısı, başarı oranı, maliyet, en çok denenen ürünler (görselsiz, anonim) | S |

## 4. Fonksiyonel olmayan gereksinimler

| Alan | Hedef |
|---|---|
| **Performans** | API p95 < 300 ms (AI hariç). Hazır takım üretimi p50 < 30 sn, kombin < 60 sn. TV'ye ulaşma < 2 sn (sonuç hazır olduktan sonra) |
| **Kullanılabilirlik** | Bir prova ≤ 6 dokunuşla üretime ulaşır. Dokunma hedefleri ≥ 48 px. Türkçe, eldivensiz/ayakta tek elle kullanım |
| **Güvenilirlik** | AI sağlayıcısı hata verirse uygulama çökmez; iş yeniden denenir ya da anlamlı hata gösterilir. Worker yeniden başlarsa yarım kalan işler kurtarılır |
| **Güvenlik** | API anahtarları yalnızca backend'de. Tüm görseller kimlik doğrulamalı ve kısa ömürlü URL ile. OWASP ASVS L2 hedefi |
| **Gizlilik** | KVKK uyumu; varsayılan saklama 24 saat; loglarda kişisel veri yok |
| **Sürdürülebilirlik** | Sağlayıcı değişimi yalnızca yeni adapter + yapılandırma ile |
| **Ölçeklenebilirlik** | MVP: 1 firma, 1–3 mağaza, mağaza başına 1–3 TV, ~200 üretim/gün. Mimari çok firmaya genişleyebilir |
| **Uyumluluk** | Personel: iOS Safari 16+, Android Chrome son 2 sürüm. TV: Samsung Tizen / LG webOS / Android TV tarayıcıları (erken test edilecek) |
| **Gözlemlenebilirlik** | Yapılandırılmış log (Serilog), sağlık kontrolü uç noktaları, iş başına süre ve maliyet metrikleri |

## 5. Eksik, çelişkili ve onay gereken kararlar

### K1 — FASHN Try-On Max serbest kombine uygun değil (**doğrulandı**)
Resmî dokümana göre `tryon-max` girdi olarak **tek** `product_image` + `model_image` alıyor. Çoklu ürün (şalvar + gömlek + yelek + kemer) tek çağrıda giydirilemez.
- **Hazır takım:** Takımın tamamını gösteren tek ürün fotoğrafı (manken/düz çekim) varsa FASHN uygundur. → **Katalogda her hazır takım için "AI referansı" olacak tek bir komple fotoğraf zorunlu olmalı.**
- **Serbest kombin:** Gemini Nano Banana Pro (14'e kadar referans görsel) mantıklı. Alternatif olarak FASHN'ı ürün başına zincirleme çağırmak (4 ürün = 4 çağrı, artan bozulma ve maliyet) **önerilmez** ama karşılaştırma testinde denenecek.
- **Onay:** Bu dağılımı kabul ediyor musunuz?

### K2 — "Nano Banana 2 Lite" karmaşık revizyona en iyi aday olmayabilir
Resmî Gemini dokümanında Nano Banana 2 Lite (`gemini-3.1-flash-lite-image`) yalnızca 1K çıktı veriyor ve "çoklu referans ve çok adımlı ardışık düzenlemeye optimize değil" deniyor. Ürün değiştirme hem yeni ürün referansı hem önceki görsel gerektiriyor.
- **Öneri:** Varsayılan olarak Nano Banana 2.1 (`gemini-nano-banana-2.1`) veya Pro; Lite'ı ucuz seçenek olarak benchmark'a dahil et. Model yapılandırmadan seçilecek, karar Aşama 4 kalite testinde verilecek.

### K3 — Katalogda olmayan renk
"Şalvarı kırık beyaz yap" isteğinde mağazada kırık beyaz şalvar **yoksa**, gösterilen ürün satılamayacak bir ürün olur.
- **Öneri:** (a) Gerçek varyant varsa o varyantla yeniden giydir. (b) Yoksa yalnızca yöneticinin izin verdiği "renk paleti"nden seçim, sonuçta **"Renk önizlemesi — stokta olmayabilir"** etiketi.

### K4 — Saklama süresi ve "Prova geçmişi"
Geçmiş ekranı uzun saklamayı, KVKK ise veri minimizasyonunu gerektirir.
- **Öneri:** Varsayılan 24 saat (aynı gün dönüş). Müşteri ayrıca onay verirse en fazla 30 gün. Firma bazında ayarlanabilir, üst sınır kodda sabit.

### K5 — Kiracı yapısı
"Çok mağaza" iki anlama gelebilir: aynı firmanın şubeleri veya farklı firmalar (SaaS).
- **Öneri:** `Organization` (firma, kiracı) → `Store` (şube). Katalog firma seviyesinde paylaşılır, oturumlar/TV'ler şube seviyesinde. Bu ikisini de karşılar.

### K6 — Figma taslakları
Erişilemedi. Aşama 1'de tasarım token'ları (renk, tipografi, boşluk) için taslaklara ihtiyaç var. Tasarımlar gelene kadar marka renklerinden (bordo, krem, kırık beyaz, altın) geçici token seti kullanılacak.

### K7 — Müşteri kimliği
Müşteri hesabı / telefon numarası istenmiyor. **Öneri:** Müşteri tablosu tutulmaz; oturumda yalnızca isteğe bağlı takma ad. (Sonradan "sonucu WhatsApp'a gönder" istenirse ayrı onay ve ayrı tasarım gerekir.)

### K8 — Çocuk müşteriler
Kına/sünnet kıyafetlerinde çocuk müşteri olabilir. **Öneri:** 18 yaş altı için veli onayı onay ekranında ayrı kutucuk; politika hukuk danışmanıyla netleştirilmeli.

### K9 — Çevrimdışı çalışma
PWA çevrimdışı açılabilir ama AI üretimi internet gerektirir. **Öneri:** Çevrimdışıyken uygulama açılır, katalog önbellekten görüntülenir, "Üret" devre dışı ve bilgi mesajı gösterilir. Müşteri fotoğrafları cihazda **önbelleğe alınmaz**.

### K10 — Model sürümleri değişken
FASHN `tryon-max` "Preview" durumunda; Gemini model kimlikleri sık değişiyor. Model kimlikleri koda değil yapılandırmaya yazılacak; her üretimde kullanılan model/sürüm kaydedilecek.

### K11 — Bria FIBO Edit 1.5
Bu aşamada resmî dokümanı doğrulanmadı. Aşama 4'te yalnızca adapter iskeleti, Aşama 5'te karşılaştırma testi önerilir.

### K12 — Barındırma ve veri konumu
Sunucunun Türkiye'de mi bulutta mı olacağı belirsiz. KVKK yurt dışı aktarım değerlendirmesini etkiler (bkz. [08](08-kvkk-guvenlik.md)). **Öneri:** Uygulama + veritabanı Türkiye'deki bir sağlayıcıda; AI sağlayıcıları için ayrı aktarım değerlendirmesi.

## 6. Kapsam dışı (ilk sürüm)

- 360° / çok açılı üretim (veri modeli hazır, üretim yok)
- 3D model, beden ölçümü, beden önerisi
- Müşteri hesabı, e-posta/WhatsApp ile sonuç paylaşımı
- Stok ve satış (POS) entegrasyonu
- Çoklu dil (altyapı i18n'e uygun kurulacak, sadece Türkçe metin)
- Mobil native uygulama (PWA yeterli)

## 7. Varsayımlar

- Mağazada stabil internet var (≥ 20 Mbps yükleme).
- Ürün fotoğrafları mağaza tarafından düzgün ışıkta, düz/manken üzerinde çekilecek.
- TV'ler modern bir tarayıcı (Chromium tabanlı, ES2019+) çalıştırabiliyor.
- Aynı anda mağaza başına en fazla ~3 aktif prova oturumu.
