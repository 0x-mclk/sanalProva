# 10 — MVP Kabul Kriterleri ve Test Planı

> **MSSQL geçiş notu (M3.10):** Veritabanı SQL Server 2025 Express'e taşındı. Bu belgedeki PostgreSQL / Npgsql / `SKIP LOCKED` / Testcontainers anlatımı **tarihseldir**; güncel durum için `CLAUDE.md` ve `docs/DEVAM-NOTU.md`. Kapsamlı yeniden yazım M4'te yapılacak.

## 1. MVP kapsamı

MVP = Aşama 1–5 tamamlanmış, tek firma / tek pilot mağaza, 1–2 TV.

## 2. Kabul kriterleri

### Prova akışı
- [ ] **AC-1** Personel giriş yapıp ≤ 6 dokunuşla hazır takım üretimini başlatabiliyor.
- [ ] **AC-2** Onay kutuları işaretlenmeden fotoğraf yüklenemiyor (UI ve API).
- [ ] **AC-3** Hazır takım sonucu p50 ≤ 30 sn, serbest kombin p50 ≤ 60 sn içinde ekranda.
- [ ] **AC-4** Serbest kombinde kategoriler katalogdan dinamik geliyor; yeni kategori kod değişikliği olmadan seçilebiliyor.
- [ ] **AC-5** Kategori seçim kuralları ve çakışmalar uygulanıyor (UI uyarısı + API reddi).
- [ ] **AC-6** Aynı "Üret" isteği iki kez gönderildiğinde tek iş oluşuyor.
- [ ] **AC-7** Sağlayıcı hatasında kullanıcı Türkçe anlamlı mesaj ve "Tekrar dene" görüyor; uygulama çökmüyor.

### Revizyon
- [ ] **AC-8** Her revizyon yeni sürüm oluşturuyor; önceki sürümler erişilebilir ve karşılaştırılabilir.
- [ ] **AC-9** Katalogda gerçek varyant varsa renk değişimi gerçek varyantla yapılıyor.
- [ ] **AC-10** Palet rengi değişiminde sonuç "Renk önizlemesi" etiketi taşıyor.
- [ ] **AC-11** Renk değişimi kalite kontrolünden geçemezse otomatik olarak AI yoluna düşüyor veya kullanıcı bilgilendiriliyor; bozuk görsel gösterilmiyor.
- [ ] **AC-12** Ürün değiştirme revizyonunda maske dışındaki pikseller önceki sürümle aynı (maske başarılıysa).

### TV
- [ ] **AC-13** TV kod/QR ile 1 dakikadan kısa sürede eşleştiriliyor.
- [ ] **AC-14** 2 TV + 2 eşzamanlı oturum testinde 100 gönderimin 100'ü doğru TV'de.
- [ ] **AC-15** TV'nin bağlı olmadığı oturumun görseline erişimi API düzeyinde reddediliyor.
- [ ] **AC-16** Sonuç "TV'ye gönder"den sonra ≤ 2 sn içinde TV'de.
- [ ] **AC-17** Ağ 30 sn kesilip geri gelince TV son doğru durumu gösteriyor.
- [ ] **AC-18** Oturum kapanınca TV ≤ 2 sn içinde bekleme ekranına dönüyor.

### Katalog
- [ ] **AC-19** Yönetici bir ürünü (varyant + görsel + AI referansı) 3 dakikadan kısa sürede ekleyebiliyor.
- [ ] **AC-20** AI referans görseli olmayan ürün seçim ekranında seçilemiyor ve yönetici panelinde uyarı veriyor.

### Güvenlik ve KVKK
- [ ] **AC-21** Farklı firma/mağaza verisi hiçbir uç noktadan görünmüyor (otomatik test).
- [ ] **AC-22** Saklama süresi dolan oturumun fotoğrafları, sonuçları ve dosyaları siliniyor (otomatik test + depolama kontrolü).
- [ ] **AC-23** Loglarda görsel içeriği, token, imzalı URL veya müşteri takma adı yok (log tarama testi).
- [ ] **AC-24** AI anahtarları frontend paketinde yok (build çıktısı taraması).
- [ ] **AC-25** İmzası geçersiz veya süresi dolmuş dosya URL'i 403 dönüyor.
- [ ] **AC-26** UI'da beden/birebir doğruluk iddiası yok; sorumluluk metni sonuç ve TV ekranında görünüyor.

### Kalite (görsel)
- [ ] **AC-27** Benchmark setinde ortalama kimlik korunumu ≥ 4/5, desen sadakati ≥ 3.5/5 (eşikler Aşama 4 raporunda onaylanacak).

## 3. Test piramidi

| Seviye | Araç | Kapsam |
|---|---|---|
| Birim (.NET) | xUnit, FluentAssertions, NSubstitute | Kategori seçim kuralları, strateji seçimi, PromptBuilder, imzalı URL, kota, eşleştirme kodu üretimi, revizyon karar akışı |
| Entegrasyon (.NET) | WebApplicationFactory + **Testcontainers PostgreSQL** | API sözleşmeleri, yetki matrisi, kiracı izolasyonu, kuyruk (lease, retry, SKIP LOCKED eşzamanlılığı), retention, SignalR hub'ları (test istemcisi) |
| Sağlayıcı sözleşme | Kaydedilmiş HTTP yanıtları (WireMock.Net) | FASHN/Gemini istek biçimi, hata eşleme, polling — **ücretli çağrı yok** |
| Birim (frontend) | Vitest + React Testing Library + MSW | Kombin seçimi, hata eşleme, form doğrulama, SignalR olay → önbellek güncelleme |
| Python | pytest | Renk değişimi, nakış maskesi, kalite metrikleri (altın görseller / golden images), API |
| E2E | Playwright | Tam prova akışı (mock AI), revizyon, TV eşleştirme ve gönderme (iki tarayıcı bağlamı), yeniden bağlanma (ağ kesme simülasyonu) |
| Görsel kalite | Manuel değerlendirme rubriği (07 §9) | Model seçimi ve regresyon |
| Performans | k6 + özel betik | API p95, eşzamanlı üretim, TV gecikmesi |
| Güvenlik | OWASP ZAP baseline, bağımlılık taraması, manuel yetki testleri | |
| Kullanılabilirlik | Mağazada gözlem | Dokunuş sayısı, hata, süre, personel geri bildirimi |

## 4. Kritik hata senaryoları

| Senaryo | Beklenen davranış |
|---|---|
| FASHN 5xx / zaman aşımı | 3 deneme, sonra Failed + "Tekrar dene"; yedek tanımlıysa Gemini |
| İçerik politikası reddi | Tekrar denenmez; "Bu fotoğrafla üretim yapılamadı, farklı bir fotoğraf deneyin" |
| Worker üretim ortasında çöker | Lease süresi dolunca iş yeniden kuyruğa girer; kullanıcı ekranında durum bozulmaz |
| İnternet kesik (personel) | Çevrimdışı bandı, Üret pasif, mevcut ekran korunur |
| TV kapandı | Personel ekranında "TV çevrimdışı"; gönderme kuyruğa alınmaz, uyarı verilir |
| İki personel aynı TV'yi değiştirir | `expectedVersion` → 409 → yenile önerisi |
| Kota doldu | 429 + açıklayıcı mesaj; yönetici bilgilendirilir |
| Bulanık / çok kişili fotoğraf | Uyarı + "Yine de devam et" seçeneği |
| Onay geri alındı | Oturum, dosyalar silinir; TV temizlenir; audit kaydı |
| Maske yüz bölgesine taştı | Renk değişimi reddedilir → AI yolu |

## 5. Test verisi politikası

- Testlerde **gerçek müşteri fotoğrafı kullanılmaz.** Yazılı izinli personel/manken fotoğrafları veya lisanslı stok görseller.
- Ürün görselleri mağazanın kendi çekimleri.
- Benchmark görselleri ayrı, erişimi kısıtlı klasörde; repoya girmez (yalnızca küçük, izinli fixture'lar).

## 6. Aşama raporu şablonu

Her aşama sonunda:
1. Tamamlanan görevler
2. Test sonuçları (geçen / kalan, kapsam)
3. Bilinen sorunlar ve riskler
4. Sapmalar (plandan farklı yapılanlar ve nedeni)
5. Sonraki aşama için onay gereken kararlar
