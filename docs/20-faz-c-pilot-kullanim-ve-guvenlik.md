# 20 — Pilot Hazırlık Faz C: tablet güvenliği, kredi görünürlüğü, platform finansı, katalog başlangıcı, izleme

Kapsam: docs/18 §6 "Faz C". Bu belge ne yapıldığını, güvenlik sınırlarını ve **bilinçli sınırlamaları** anlatır. Ödeme (M5.5) yoktur; AI çağrısı yapılmaz.

## 1. Ekran kilidi ve PIN

| Konu | Karar |
| --- | --- |
| Boşta kilit | `Security:IdleLockSeconds` (varsayılan 300 sn; 60–3600 arası sıkıştırılır). Süre dolunca **içerik DOM'dan kaldırılır** (geçmiş provalar, müşteri fotoğrafları, yönetim ekranları), sorgu önbelleği temizlenir (kimlik hariç). Etkileşim (dokunma/tuş/kaydırma) süreyi sıfırlar; tablet uykudan dönünce hemen denetlenir. Elle kilit: Ana ekran hesap menüsü → "Ekranı kilitle". |
| Kilit bayrağı | `sessionStorage` (sayfa yenileme/adrese doğrudan gitme kilidi AÇMAZ; sekme kapanınca düşer). |
| PIN | 4–8 rakam; tek rakam tekrarı ve ardışık diziler (1234, 4321) reddedilir. Identity hasher'ıyla `users.pin_hash` sütununda (düz metin yok). Belirleme/değiştirme **hesap parolasını** ister. PIN'i olmayan hesap kilidi **parolayla** açar (PIN'li hesap parolayla AÇAMAZ: PIN atlatılamaz). |
| Doğrulama | **Sunucuda** (`POST /account/unlock`), kullanıcı başına hız sınırlı (`Security:UnlockRateLimitPerMinute`, 10/dk; IP'den bağımsız). 5 ardışık hatalı PIN/parola (`users.pin_failed_attempts`, atomik artış) → kullanıcının **tüm oturumları kapatılır** (güvenlik damgası yenilenir), sayaç sıfırlanır, PIN korunur; yanıt `PIN_LOCKED_OUT`. Başarı sayacı sıfırlar. |
| Pasife alma / parola sıfırlama | PIN **silinir**, oturumlar düşer (mevcut `ActiveUserCookieValidator`). Yeniden etkinleştirilen hesap eski PIN ile değil parolayla girer. |
| Yetki değişimi | Kilit yalnızca arayüzdür; rol/şube yetkisi **her istekte** veritabanından okunur (rol düşürülen Manager kilit açık olsa bile `INSUFFICIENT_ROLE` alır; testle kanıtlı). |
| Denetim | `Account.PinSet`, `Account.PinLockout` (kişisel veri yok). |

**Bilinçli sınırlar (dürüst):** (1) Kilit bir **arayüz kalkanıdır**: oturum çerezi açık kalır; çerezi çalabilen biri API'yi doğrudan çağırabilir (çerez HttpOnly + SameSite=Strict; ağ üzerinde TLS). Sunucu tarafı "kilitli oturum" durumu yoktur. (2) Hesabın birden çok cihazı varsa her cihazın kilidi bağımsızdır; sunucu yalnızca hatalı deneme sayacını paylaşır. (3) Boşta süresi cihaz saatine/zamanlayıcıya bağlıdır; JS çalışmayan bir tarayıcıda uygulanmaz. (4) TV uygulaması kilitlenmez (müşteri ekranıdır; yalnızca bağlı oturumun sonucunu gösterir).

## 2. Fotoğraf çekim deneyimi

- Çekim öncesi **rehber kartı** (silüet + kısa ipuçları: mesafe, ışık, giysi, tek kişi); kamera üstündeki kesikli çerçeve zaten vardı.
- Çekim sonrası **personel onayı** (üç madde: tek kişi/tam boy, dik ve önden duruş, net ve aydınlık). Üçü işaretlenmeden "Devam Et" ve kıyafet sayfalarındaki "Üret" açılmaz. Onay fotoğraf kimliğine bağlıdır (fotoğraf değişince geçersiz), `sessionStorage`'dadır.
- **Sınır:** bu bir iş akışı kontrolüdür, sunucuda kayıt/zorlama YOKTUR (sunucu fotoğrafı yine yön/çözünürlük/netlik açısından denetler; kişi sayısı ve tam boy otomatik denetlenmez). Sunucu tarafı onay kaydı istenirse ayrı bir iştir (migration + üretim kabulüne kapı).
- **Kredi/maliyet bilgisi** (kıyafet seçimi sayfaları): `GET /credits/quote` → "Bu üretim X kredi harcar (kalan A → B); başarısız olursa iade edilir"; yetersizse uyarı ve "Üret" kapalı; **mock modda** "Demo üretim: kredi harcanmaz ve sonuç gerçek yapay zekâ çıktısı değildir". Mevcut model/kalite seçicisindeki USD tahmini korunur. Sunucu kabulde tarifeyi yeniden hesaplar (arayüz yalnızca bilgilendirir).
- KVKK onay akışı değişmedi (onay olmadan fotoğraf yüklenemez, sunucu zorlar).

## 3. Kredi görünürlüğü

- `GET /credits/balance` (Staff/Manager/Owner, şube başlığı gerekir): **yalnızca** `available`, `isLow`, `isEmpty`, `enforced`, `live`. Defter, rezerve, tarife ve belirsiz bloke bilgisi YOK; başka firmanın verisi görünmez (firma filtresi).
- Düşük kredi: `Credits:LowBalanceThreshold` (varsayılan 10, 0 = kapalı) veya kredi 0 → ana ekranda uyarı. **Yalnızca gerçek (ücretli) modda** gösterilir (mock modda üretim kredi harcamaz). Üretimi engellemez; engelleme zaten kabulde `INSUFFICIENT_CREDITS` (402).
- Owner'ın `/yonetim/kredi` ekranı ve `GET /credits`, `/credits/ledger` (yalnızca Owner) değişmedi.

## 4. Platform yönetimi

- **/platform/finans**: işletme bazlı kredi (bakiye/rezerve/kullanılabilir, düşük kredi), **etkin** bütçe sınırları (min(platform tavanı, firma)), bugün/ay harcama, açık rezervasyon (USD), bekleyen belirsiz vaka sayısı, bütçe seviyesi; ayrıca izleme kontrolleri (§6). API: `GET /platform/finance`, `GET /platform/monitoring`.
- **/platform/mutabakat**: `OutcomeUncertain` (durum `Open`) vakaları; karar penceresi mevcut `POST /platform/cost-cases/{id}/decisions` API'sini kullanır (Idempotency-Key diyalog açılışında üretilir ve ağ yeniden denemesinde aynı kalır; `expectedSequence`; çakışma 409 açıklanır; tutar yalnızca "ücretlendi" kararında). Karar silinemez, düzeltme yeni karardır.
- **Yetki:** tüm uçlar `RequirePlatformAdmin` (her istekte veritabanından doğrulanır); Staff/Manager/Owner → 403, anonim → 401 (testli). Kredi ve maliyet defterleri append-only (tetikleyici + tek yazıcı) — bu aşamada yeni yazma yolu eklenmedi (finans uçları salt okunur; mutabakat yazımı önceki serviste).
- Bütçe/kredi **yazma** hâlâ yalnızca operatör komutlarındadır (`platform budget|credits|paid-ai`).

## 5. Katalog başlangıç desteği

Katalog yöneticilerinin (Manager+) ürün listesinde **başlangıç kontrol listesi** (kategori var mı, ilk ürün, AI referans görseli, en az 3 provaya hazır aktif ürün) ve **ürün fotoğrafı çekim rehberi** görünür; ilerleme mevcut katalog verisinden hesaplanır (yeni API/yazma yok); tamamlanınca küçülür. Rehberdeki sınırlar koddan gelir (kısa kenar ≥200 px, ≤10 MB, 1600 px üstü küçültülür).

## 6. Asgari izleme

`PilotMonitoring` (Infrastructure, salt okunur) kontrolleri: `database` (bağlantı + gecikme), `disk` (depo diski: <%20 uyarı, <%10 kritik), `ai-mode`, `jobs` (takılı kuyruk/çalışan iş), `uncertain-cases` (24 sa uyarı, 72 sa kritik), `budget:*` (%80 uyarı, ≥%100 kritik; sınırı 0 olan firmada harcama kritik), `credits:*` (ücretli AI açık firmada düşük kredi). Üç tüketici: platform API/ekranı, `platform status` komutu (çıkış 0/10/11), `deploy/scripts/monitor.sh` (servis, disk, yedek yaşı, site + `platform status`; cron). Ayrıntı ve cron: [docs/19 §11](19-uretim-dagitim-runbook.md). **Uyarı iletimi (e-posta/mesaj) yoktur**: dış sistem/cron gerekir.

## 7. Testler (yerel)

Backend `PilotFazCTests` (PIN politikası/saklama/sayaç/oturum kapatma/pasife alma/rol düşürme/hız sınırı, kredi görünürlüğü ve firma izolasyonu, platform yetkisi, finans özeti, izleme eşikleri, `platform status`), React (`lock.test.tsx`, `finance.test.tsx`, `photoGuide.test.tsx`, `catalogStarter.test.tsx`), Playwright `lock.spec.ts` (+ flow/onboarding güncellemeleri), `deploy/tests/monitor-selftest.sh`. Migration: `PinLockout` (yalnızca `users.pin_failed_attempts int NOT NULL DEFAULT 0` ekler; ileri yönlü).

## 8. Kalan / yapılmayanlar

Sunucu tarafı fotoğraf onay kaydı; sunucu tarafı "kilitli oturum" durumu; MFA/e-posta; dış uyarı iletimi; TV kilidi; gerçek Docker'da monitor.sh çalıştırması; Faz B (hukuk, canlı AI testi, gerçek TV) kod dışıdır.
