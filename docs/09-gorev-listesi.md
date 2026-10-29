# 09 — Geliştirme Aşamaları ve Görev Listesi

> **Güncelleme (M4.1):** Veritabanı SQL Server 2025 Express'tir. Bu belgenin ilgili yerleri güncellendi; kalan eski ifadeler tarihseldir. Güncel durum: `CLAUDE.md`, `docs/DEVAM-NOTU.md`.

Her aşama küçük, test edilebilir görevlere bölünmüştür. **Her aşama sonunda:** testler çalıştırılır, kısa rapor (yapılan, test sonuçları, açık sorunlar) hazırlanır, onay alınmadan sonraki aşamaya geçilmez.

---

## Aşama 0 — Analiz ve planlama ✅ (bu doküman seti)

Çıkış: Dokümanların onayı + [01 §5](01-gereksinim-analizi.md#5-eksik-çelişkili-ve-onay-gereken-kararlar) kararları.

---

## Aşama 1 — Proje iskeleti

| # | Görev | Çıktı / doğrulama |
|---|---|---|
| 1.1 | Git deposu, `.gitignore`, `.editorconfig`, kök README | `git status` temiz |
| 1.2 | Yerel SQL Server 2025 Express (`SANALPROVA`) + `scripts/db.ps1`; Docker Compose M4.2'de | DB erişilebilir (`db.ps1 status`) |
| 1.3 | Backend çözümü: `Api`, `Domain`, `Infrastructure`, test projeleri | `dotnet build` |
| 1.4 | Serilog, ProblemDetails, sağlık uç noktaları, OpenAPI | `/health/ready` 200 |
| 1.5 | EF Core + SQL Server, `AppDbContext`, snake_case, ilk migration (organizations, stores, users) | Migration uygulanır |
| 1.6 | Identity + cookie auth + roller, seed: demo firma, mağaza, 3 kullanıcı | Giriş entegrasyon testi |
| 1.7 | `ITenantContext` + global query filter + izolasyon testi | İki firma verisi birbirini görmüyor (test) |
| 1.8 | Frontend: Vite + React + TS, Mantine, tema token'ları, Router, TanStack Query, i18n | `npm run build` |
| 1.9 | PWA (manifest, service worker kabuk önbelleği) | Lighthouse PWA kontrolü |
| 1.10 | OpenAPI → orval istemci üretimi | Tip güvenli istemci |
| 1.11 | Giriş ekranı + Ana sayfa iskeleti + `/auth/me` | Tarayıcıda giriş → ana sayfa |
| 1.12 | `tv.html` boş giriş noktası | `/tv` açılır |
| 1.13 | CI (GitHub Actions veya eşdeğeri): build + test + lint | Yeşil pipeline |
| 1.14 | `IImageStorageService` + `LocalFileStorage` + imzalı URL uç noktası | Birim + entegrasyon testi |

**Çıkış kriteri:** Personel giriş yapıp ana sayfayı görür; backend–frontend–SQL Server bağlantısı çalışır; CI yeşil.

---

## Aşama 2 — Katalog ve prova akışı (mock AI)

| # | Görev |
|---|---|
| 2.1 | Katalog entity'leri + migration (categories, products, variants, images, set components) |
| 2.2 | Katalog API (CRUD, görsel yükleme, `isReadyForAi` hesaplama) |
| 2.3 | Katalog yönetim ekranları (14, 15, kategori yönetimi) |
| 2.4 | Seed: 5 kategori, ~12 demo ürün, demo görseller (lisanslı/kendi çekimimiz) |
| 2.5 | Oturum + onay + fotoğraf entity'leri ve API |
| 2.6 | Ekranlar 3–4: yeni prova, KVKK onayı, kamera/yükleme, kalite uyarısı |
| 2.7 | Ekran 5–6: hazır takım ve serbest kombin seçimi (kategori kuralları) |
| 2.8 | `generation_jobs` kuyruğu + `GenerationWorker` + lease/retry |
| 2.9 | `GenerationStrategy` + Mock provider'lar (Try-On, Generation, Edit) |
| 2.10 | StaffHub + `JobUpdated`; ekran 7 (üretim durumu) |
| 2.11 | `generated_images` sürümleme; ekran 8 (sonuç) |
| 2.12 | Revizyon API + ekran 9 (mock ile; renk / ürün değiştirme seçim akışı) |
| 2.13 | Ekran 10 (karşılaştırma) ve 11 (geçmiş) |
| 2.14 | `RetentionJob` (otomatik silme) + audit log |
| 2.15 | Kota kontrolü (temel) |

**Çıkış kriteri:** Mock AI ile hazır takım ve serbest kombin akışı uçtan uca çalışır; revizyonlar yeni sürüm oluşturur; süresi dolan oturum silinir.

---

## Aşama 3 — TV senkronizasyonu

> **Durum: kod tamamlandı, kullanıcı onayı bekliyor.** 3.0 (gerçek mağaza TV'sinde deneme) **yapılamadı**: bu ortamda fiziksel TV yok. TV uygulaması Chromium ≥ 56 (CSS değişkenleri) varsayar; gerçek cihaz denemesi Aşama 6 yayın hazırlığında zorunludur. Ayrıntı: [06 §8](06-tv-signalr.md#8-aşama-3-uygulama-notları).

| # | Görev |
|---|---|
| 3.0 | **Gerçek mağaza TV'sinde uyumluluk denemesi** (WebSocket, ES2019, localStorage) — risk erken kapatılır |
| 3.1 | `display_devices`, `display_pairings` + migration |
| 3.2 | Eşleştirme API'si + cihaz token kimlik doğrulaması |
| 3.3 | DisplayHub, gruplar, heartbeat, çevrimiçi durumu |
| 3.4 | TV uygulaması: eşleştirme kodu/QR, bekleme, tekli, karşılaştırma modları |
| 3.5 | Ekran 12: TV yönetimi, kod girme / QR okutma |
| 3.6 | "TV'ye gönder", "TV'de karşılaştır", oturumu bağla/devral |
| 3.7 | Yeniden bağlanma, durum geri yükleme, uzun kopmada görsel gizleme |
| 3.8 | Otomatik temizleme (oturum kapanışı, boşta kalma) |
| 3.9 | İzolasyon testleri: iki oturum, iki TV, yanlış TV'ye gönderim denemeleri |

**Çıkış kriteri:** Aynı mağazada 2 TV + 2 eşzamanlı oturum ile görseller her zaman doğru TV'de; ağ kesilip gelince TV doğru durumu gösterir.

---

## Aşama 4 — Gerçek AI entegrasyonu

> **Aşama 4b (2026-10-22): tek kaftan modelleri** — Seedream 5 Flash Edit, GPT Image 2.5 Sunburst Edit, Pruna P-Image Try-On adapter'ları, yönetici model/kalite ayarları, tek kaftan akışı: kod tamamlandı, mock/sözleşme testleri geçti; **canlı test onay bekliyor** ([12 §6](12-tek-kaftan-model-entegrasyonu.md#6-canlı-test-planı--onay-bekliyor)). Beş parçalı kombin gerçek modda kapalı. Gerçek Smart TV uyumluluk testi (3.0) açık. Native istemciler için token tabanlı kimlik doğrulama açık görev.

> **Durum: kod tamamlandı (mock/sözleşme testleri geçti), kullanıcı onayı bekliyor.** 4.0'daki hukuki/sözleşme değerlendirmesi ve **4.9 benchmark/canlı doğrulama yapılmadı** (ücretli çağrı onayı gerekir; plan: [11 §5](11-ai-entegrasyon-plani-ve-test.md#5-kontrollü-gerçek-api-test-planı--onay-bekliyor)). Gerçek Smart TV uyumluluk testi (3.0) açık görev olarak duruyor. Webhook ve Polly devre kesici yapılmadı (yoklama + üstel geri çekilme + bütçe kapısı yeterli bulundu; devre kesici Aşama 6'ya).

| # | Görev |
|---|---|
| 4.0 | **Ön koşul:** Sağlayıcı sözleşmeleri / KVKK aktarım değerlendirmesi başlatıldı; API anahtarları ve bütçe limiti belirlendi |
| 4.1 | Fiyatları ve FASHN webhook detaylarını resmî kaynaktan doğrula, yapılandırmaya yaz |
| 4.2 | `FashnTryOnProvider` (run + poll, `return_base64`, hata eşleme, kredi kaydı) |
| 4.3 | `GeminiProvider` (compose + edit, model ID yapılandırmadan) |
| 4.4 | Prompt şablonları v1 + PromptBuilder birim testleri |
| 4.5 | Görsel ön işleme (EXIF temizleme, boyutlandırma) ve çıktı doğrulama |
| 4.6 | Polly: zaman aşımı, retry, devre kesici; yedek sağlayıcıya düşme |
| 4.7 | `ai_usage_logs`, maliyet raporu ekranı, kotalar |
| 4.8 | Sağlayıcı sözleşme testleri (kayıtlı yanıtlarla, ücretsiz) |
| 4.9 | **Benchmark** (07 §9) — sınırlı bütçeyle, izinli test fotoğraflarıyla; rapor ve model seçimi kararı |

**Çıkış kriteri:** Hazır takım ve serbest kombin gerçek sağlayıcılarla çalışır; hatalar kullanıcıya anlamlı yansır; maliyet her iş için kayıtlı; benchmark raporu onaylandı.

---

## Aşama 5 — Revizyon motoru

| # | Görev |
|---|---|
| 5.1 | image-worker: FastAPI iskeleti, `/health`, iç anahtar |
| 5.2 | `recolor.py` (LAB dönüşümü, nakış maskesi hariç tutma) + kalite metrikleri + pytest |
| 5.3 | Segmentasyon: mock → SAM 2 (opsiyonel imaj), dokunarak nokta istemi |
| 5.4 | Backend istemcisi + `Recolor` iş tipi + kalite kontrol düşme yolu |
| 5.5 | `ProductSwap` (Gemini edit) + maskeli birleştirme |
| 5.6 | `VariantPreview` (toplu varyant) |
| 5.7 | Renk paleti yönetimi + "renk önizlemesi" etiketleri |
| 5.8 | (Ops.) Bria FIBO Edit adapter'ı ve karşılaştırma |
| 5.9 | Eşik kalibrasyonu (test seti ile) |

**Çıkış kriteri:** Renk değişimi test setinde hedef başarı oranına ulaşır ve başarısız olduğunda güvenli şekilde AI'ya düşer; ürün değiştirmede yüz/arka plan piksel bazında korunur.

---

## Aşama 6 — Test ve yayın hazırlığı

| # | Görev |
|---|---|
| 6.1 | Playwright E2E: tam prova akışı + TV (iki tarayıcı bağlamı) |
| 6.2 | Güvenlik: OWASP kontrol listesi, bağımlılık taraması, yetki/izolasyon testleri, dış pentest (önerilir) |
| 6.3 | Performans: eşzamanlı 10 üretim, API yük testi (k6), TV gecikmesi ölçümü |
| 6.4 | KVKK kontrol listesi (08 §8), silme doğrulaması |
| 6.5 | Mağazada kullanılabilirlik testi (2–3 personel, gerçek tablet + TV), dokunuş sayısı ölçümü |
| 6.6 | Hata senaryoları: sağlayıcı kapalı, internet yok, TV kapandı, kota doldu |
| 6.7 | Üretim dağıtımı: compose prod, TLS, yedekleme, izleme, alarm |
| 6.8 | Personel kullanım kılavuzu (kısa, görsel) ve operasyon runbook'u |

**Çıkış kriteri:** [10 — Kabul kriterleri](10-test-ve-kabul.md) karşılandı; pilot mağazada canlı.
