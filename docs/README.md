# Sanal Prova — Aşama 0: Analiz ve Planlama

> **MSSQL geçiş notu (M3.10):** Veritabanı SQL Server 2025 Express'e taşındı. Bu belgedeki PostgreSQL / Npgsql / `SKIP LOCKED` / Testcontainers anlatımı **tarihseldir**; güncel durum için `CLAUDE.md` ve `docs/DEVAM-NOTU.md`. Kapsamlı yeniden yazım M4'te yapılacak.

> Durum: **Taslak — onay bekliyor** · Tarih: 2026-10-20

Bu klasör, yapay zekâ destekli sanal kıyafet prova sisteminin Aşama 0 çıktılarıdır. Kod yazılmadı, bağımlılık kurulmadı, ücretli API çağrılmadı.

## Doküman dizini

| # | Doküman | İçerik |
|---|---|---|
| 01 | [Gereksinim analizi](01-gereksinim-analizi.md) | Fonksiyonel / fonksiyonel olmayan gereksinimler, kapsam dışı, **eksik ve çelişkili kararlar** |
| 02 | [Sistem mimarisi ve klasör yapısı](02-sistem-mimarisi.md) | Bileşenler, iş akışı, iş kuyruğu, depolama, klasör yapısı |
| 03 | [Veri modeli](03-veri-modeli.md) | PostgreSQL ER modeli, tablolar, ilişkiler, indeksler |
| 04 | [Backend API tasarımı](04-api-tasarimi.md) | REST uç noktaları, sözleşmeler, hata formatı, yetkiler |
| 05 | [Frontend ekranları ve akışlar](05-frontend-ekranlar.md) | 15 ekran, kullanıcı akışları, PWA, tasarım sistemi |
| 06 | [TV / SignalR mimarisi](06-tv-signalr.md) | Eşleştirme, hub'lar, gruplar, yeniden bağlanma, izolasyon |
| 07 | [AI entegrasyon stratejisi](07-ai-entegrasyon.md) | FASHN, Gemini, SAM 2 + OpenCV, adapter yapısı, maliyet |
| 08 | [KVKK ve güvenlik](08-kvkk-guvenlik.md) | Onay akışı, saklama süreleri, yurt dışı aktarım, erişim |
| 09 | [Görev listesi](09-gorev-listesi.md) | Aşama 1–6 görevleri, her aşamanın çıkış kriteri |
| 10 | [Kabul kriterleri ve test planı](10-test-ve-kabul.md) | MVP kabul kriterleri, test piramidi, görsel kalite değerlendirmesi |
| 12 | [Tek kaftan giydirme: Seedream, GPT Image 2.5, Pruna](12-tek-kaftan-model-entegrasyonu.md) | **Güncel model seçimi**: doğrulanan API'ler, model/kalite ayarları, KVKK, onay bekleyen canlı test |
| 11 | [Aşama 4: AI entegrasyon planı, maliyet sınırları ve kontrollü test planı](11-ai-entegrasyon-plani-ve-test.md) | Doğrulanan API bilgileri, bütçe kapıları, test sonuçları, **onay bekleyen canlı test planı** |

## Özet: temel mimari kararlar

1. **Modüler monolit.** Tek ASP.NET Core API + tek ayrı Python görsel işleme servisi. Mikroservis yok.
2. **İş kuyruğu PostgreSQL'de.** AI işleri `generation_jobs` tablosunda tutulur, `BackgroundService` worker'ı `FOR UPDATE SKIP LOCKED` ile alır. Redis / Hangfire / RabbitMQ ilk sürümde yok.
3. **Sağlayıcıdan bağımsız iş kuralları.** Hangi AI modelinin kullanılacağına `GenerationStrategy` karar verir, yapılandırmadan okunur. Frontend sadece "hazır takım / kombin / revizyon" bilir.
4. **Prompt'lar sunucuda, yapılandırılmış veriden üretilir.** Kullanıcı hiçbir zaman prompt yazmaz; şablonlar sürümlüdür.
5. **Her çıktı bir sürüm.** Revizyonlar `generated_images.parent_image_id` ile ağaç oluşturur; hiçbir sonuç üzerine yazılmaz.
6. **TV sunucu-otoritatif.** TV'de ne gösterileceği veritabanında tutulur; SignalR yalnızca "durum değişti" bildirir. Yeniden bağlanan TV durumu REST'ten çeker.
7. **Görseller asla public değil.** Dosyalar `IImageStorageService` arkasında (önce yerel disk, sonra S3), istemciye kısa ömürlü imzalı URL ile verilir.
8. **Çok kiracılı (multi-tenant) hazır.** Her kayıt `organization_id` (ve gerekiyorsa `store_id`) taşır; EF Core global query filter ile izole edilir.
9. **Önce mock.** Tüm akış mock sağlayıcılarla uçtan uca çalışır; gerçek API'ler Aşama 4'te bayrakla açılır.

## Onayınızı beklediğim kararlar

Detayları [01 §5](01-gereksinim-analizi.md#5-eksik-çelişkili-ve-onay-gereken-kararlar) bölümünde. Özellikle:

- **K1** Serbest kombinde FASHN yerine Gemini Nano Banana Pro kullanımı (FASHN Try-On Max tek ürün görseli alıyor).
- **K2** Karmaşık revizyon için Nano Banana 2 Lite'ın uygunluğu (resmî dokümanda "çok adımlı düzenlemeye optimize değil" deniyor).
- **K3** Katalogda gerçek varyantı olmayan rengin "önizleme" etiketiyle gösterilmesi.
- **K4** Müşteri fotoğrafı saklama süresi varsayılanı (öneri: 24 saat).
- **K5** Kiracı yapısı: Firma → Şubeler, katalog firma seviyesinde.
- **K6** Figma taslaklarına erişim (bu oturumda Figma bağlantısı yetkilendirilmemiş).
