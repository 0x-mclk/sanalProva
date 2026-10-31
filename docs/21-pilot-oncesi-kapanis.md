# 21 — Pilot öncesi eksiklerin kapatılması

Kapsam: docs/20 "bilinçli sınırlar" ile docs/19 "bilinen sınırlar" listelerinden kod ile kapatılabilenler. Gerçek AI, gerçek Docker dağıtımı ve fiziksel TV/tablet doğrulaması BU aşamaya dahil değildir (bkz. §8).

## 1. Sunucu tarafı ekran kilidi

| Konu | Karar |
| --- | --- |
| Durumun sahibi | **Sunucu.** Girişte `auth_sessions` satırı üretilir (`id`, `user_id`, `created_at`, `last_interaction_at`, `locked_at`); çerezin `sp_sid` claim'i bu satıra bağlıdır (`SignInWithClaimsAsync`). Satırı olmayan veya `sp_sid` taşımayan çerez geçersizdir (çıkış yapılmış/çalınmış çerez bir daha çalışmaz). |
| Zorlama | `ScreenLockMiddleware`: kimlik isteyen ve `ScreenLockExempt` işareti taşımayan HER uç nokta (REST + hub negotiate/WebSocket) kilitli oturumda **423 `SCREEN_LOCKED`** alır; bağlama/CSRF/rol filtrelerinden önce. Varsayılan zorlamadır: yeni uç nokta unutulsa bile açık kalamaz. Muaf liste mimari testle sabit: `GET /auth/me`, `POST /auth/logout`, `GET/POST /account/lock`, `POST /account/unlock`. |
| Kilitleme | `POST /account/lock` (elle/boşta; idempotent) oturumu kilitler ve **yalnızca o oturumun** hub bağlantılarını keser. İstemci etkileşim sinyali (`POST /account/activity`, ≥20 sn aralıkla) `last_interaction_at`'i günceller; **istemci durur/kilitlemeyi unutursa** sunucu, `Security:IdleLockSeconds` + 60 sn tolerans sonra oturumu kendisi kilitli sayar ve kalıcı yazar. |
| Açma | `POST /account/unlock` (PIN veya PIN'siz hesap için parola; hız sınırlı; 5 hatada tüm oturumlar kapanır — docs/20). Başarıda yalnızca BU oturumun kilidi açılır. |
| Atlatma | Sayfa yenileme, doğrudan API çağrısı ve ikinci sekme aynı çerezle aynı satırı paylaşır → 423. Arayüz kilit durumunu sunucudan okur (15 sn yoklama + herhangi bir 423 yanıtı); durum bilinmeden içerik çizilmez; sunucuya ulaşılamayan kilit yerel bayrakla korunur ve yakınsayana kadar yeniden denenir. Başka cihazdaki oturum (aynı kullanıcı) etkilenmez. |
| Korunanlar | `/sessions`, fotoğraflar, geçmiş, katalog (okuma+yazma), yönetim, kredi/maliyet, platform finans/mutabakat, TV yönetimi, üretim. |
| Bilinçli kalan | Müşteri fotoğraflarının **imzalı URL**'leri (5 dk, çerezsiz) kilitlenince geçersiz kılınmaz: URL'yi önceden kopyalayan, süre bitene kadar görüntüyü çekebilir. Çerez çalınırsa PIN gerekmez **kilit açılmadığı sürece** API 423 verir; ama çalınmış çerezle PIN denenebilir (5 hata → tüm oturumlar kapanır). TV cihaz belirteçleri kilitten bağımsızdır (ayrı kimlik). |

## 2. Fotoğraf uygunluk onayı (sunucu)

- `session_photos.staff_approved_at` / `staff_approved_by_user_id`. Onay **fotoğraf satırına** bağlıdır; yeniden çekim yeni satır üretir (eski silinir) → önceki onay yeni fotoğrafa geçmez.
- `POST /sessions/{id}/photos/{photoId}/approval` üç beyanın (`singlePersonFullBody`, `frontalUprightPose`, `sharpAndWellLit`) HEPSİ true olmalı; aksi 400 `APPROVAL_INCOMPLETE`. Kapalı oturum 409, KVKK onayı yoksa 409, başka oturum/şube/firma fotoğrafı 404 (varlık sızdırılmaz). `DELETE …/approval` onayı kaldırır. Denetim: `Photo.StaffApproved|StaffApprovalRevoked`.
- `POST /sessions/{id}/generations` ve `/jobs/{id}/retry`: onaysız fotoğraf **409 `PHOTO_NOT_APPROVED`** — ücretli kapı, bütçe/kredi rezervasyonu, kota ve iş oluşturmadan ÖNCE (mock modda da). Frontend yalnızca sunucudaki kaydı gösterir.
- **Otomatik kişi sayısı/tam boy analizi YOKTUR**; onay personelin sorumluluk beyanıdır (arayüz de bunu açıkça söyler). KVKK müşteri onayı akışı değişmedi.

## 3. SignalR yetki güvenliği

- **Kayıt defteri** bağlantı başına kullanıcı, `sp_sid` ve abone olunan şube/oturum gruplarını izler.
- **Anında kesme** (aynı süreçte): pasife alma, parola sıfırlama, **rol/şube ataması değişimi**, **şube pasife alma** (o şubeye abone herkes), kilitleme/çıkış (yalnızca o oturum), 5 hatalı PIN (kullanıcı). Yeniden bağlanan istemci abonelikleri sunucuda yeniden doğrulatır.
- **Süpürücü** (`StaffConnectionSweeper`, 30 sn, `Security:HubRevalidateSeconds`, 0 = kapalı): hesap etkin mi + güvenlik damgası aynı mı + oturum satırı var/kilitsiz mi + abone olunan her şubede rol var ve şube etkin mi + abone olunan her oturum için rol var ve oturum silinmemiş mi; değilse keser. **Başka süreçte yapılan değişiklikleri** (operatör CLI komutları, elle veritabanı düzeltmesi) en geç bir periyotta yakalar — CLI web sunucusuna sinyal gönderemez.
- Hub yöntemleri (`JoinSession/JoinStore`) her çağrıda oturumun kilitsiz/geçerli olduğunu doğrular, değilse bağlantıyı keser.
- **Çok sunuculu (gelecek, bu aşamada geliştirilmedi):** kayıt defteri ve süpürücü bellek içidir; birden çok API örneğinde (a) SignalR backplane (Redis/SQL) ya da yapışkan oturum, (b) abone izleme kaydının ortak depoya taşınması (kilit/atama değişikliğini başka örneğin bağlantılarına da iletmek için yayın), (c) `Security:HubRevalidateSeconds`'in kısa tutulması gerekir. Pilot tek sunucudur (docs/06 §8).

## 4. Test kararlılığı — `CreditSystemTests` aralıklı başarısızlığı

**Kök neden (kanıtlı):** ana test sunucusunun (mock) `GenerationWorker`'ı, aynı veritabanını paylaşan `RealModeApiFactory`'nin kabul ettiği **gerçek mod** işi kuyruktan kapıp mock sağlayıcıyla tamamlıyordu; kredi rezervasyonu bu sırada tüketildiği için `(bakiye, rezerve) = (1,1)` doğrulaması yük altında aralıklı düşüyordu (yalnız çalıştırınca geçiyordu). `Mock_isci_gercek_modda_kabul_edilmis_isi_almaz` testi filtre geçici devre dışıyken gerçek `Queued → Succeeded` ile **düşürülerek** doğrulandı. **Düzeltme** testi tekrar çalıştırmak değil üretim koduna yönelik: `generation_jobs.admitted_live` (kabul anındaki mod) eklendi ve işçi **yalnızca kendi modundaki işi** alır. Bu aynı zamanda gerçek bir güvenlik özelliğidir: mock kabul edilmiş (ücretli kapıdan geçmemiş) iş sonradan gerçek sağlayıcıya, gerçek kabul edilmiş (kredi/bütçe ayrılmış) iş mock'a (DEMO çıktıya kredi düşürerek) gitmez. `NULL` = alan öncesi eski satır (her iki mod). İdempotency, kredi ve maliyet testleri değişmeden geçer.

## 5. Üretim güvenliği

- **CSP** (`frontend/caddy/common.caddy`): `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; font-src 'self' data:; connect-src 'self' wss://{host}; worker-src 'self'; manifest-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`. Betik tarafı katıdır (`unsafe-inline/eval` yok); **stil için `unsafe-inline` bilinçli istisna** (Mantine/React stil öznitelikleri). TV girişindeki satır içi yedek betikler harici dosyaya taşındı (`/tv-nomodule.js`, `/tv-watchdog.js`; ES5). Doğrulama: `DeployArtifactsTests` (politika/inline betik yokluğu) + Playwright `csp.spec.ts` (derlenmiş arayüzü Caddy'deki politika başlığıyla küçük bir Node sunucusunda sunar, personel girişi/ana ekran/prova sayfaları/TV girişi/SignalR WebSocket gezilir; tek ihlal testi düşürür). **Caddy'nin kendisi ve TLS bu makinede çalıştırılmadı.** Çerez/CSRF/token davranışı değişmedi.
- **Bağlantı üretme idempotency**: `Idempotency-Key` (isteğe bağlı, 8–64 karakter) `POST /management/users/{id}/setup-link` ve `POST /platform/organizations/{id}/owner/setup-link` için. Aynı kullanıcı+anahtar tekrar gelirse ikinci bağlantı üretilip ilki iptal EDİLMEZ: **409 `SETUP_LINK_ALREADY_ISSUED`** (düz token bir daha gösterilemez, bu yüzden "aynı yanıtı döndürme" yerine güvenli ret). Anahtar SHA-256 özetiyle (`account_setup_tokens.request_key_hash`, benzersiz filtreli indeks; eşzamanlı çift istek de engellenir) saklanır. Anahtarsız eski istemciler eskisi gibi çalışır. Arayüz her tıklamada yeni anahtar gönderir.
- **Operatör komutları denetim izi**: `audit_logs.detail` (≤300): `cli op=<OS kullanıcı>@<makine> org=<slug> ref=<ref> grant|adjust=<n> budget d/m/j=…`. **Allowlist**: token, parola, bağlantı, e-posta ve serbest `--note` metni ASLA yazılmaz (testle doğrulanır; komut çıktısı da notu yansıtmaz).

## 6. Migration

`PilotClosure` (ileri yönlü): `auth_sessions` (yeni tablo), `session_photos.staff_approved_at/_by_user_id`, `generation_jobs.admitted_live`, `audit_logs.detail`, `account_setup_tokens.request_key_hash` + benzersiz filtreli indeks. Yalnızca `sanalprova_test`/`sanalprova_e2e`/`sanalprova_lab_*` üzerinde uygulandı. **Dağıtımda eski çerezler geçersiz olur** (`sp_sid` yok): herkes bir kez yeniden giriş yapar.

## 7. Testler (yerel)

Backend `PilotClosureTests` (25: kilit 423/muaf/ikinci sekme/PIN/sunucu boşta/çıkış/anonim/envanter/platform, hub kesme/süpürücü/rol değişimi/PIN kilitlenmesi, foto onayı atlatma/eksik beyan/yeniden çekim/geri alma/başka firma/kapalı oturum/gerçek modda rezervasyon öncesi, işçi/mod yalıtımı, bağlantı idempotency, operatör denetim izi) + güncellenen mevcut testler; React `lock.test.tsx` (sunucu durumu/423 olayı/diğer sekme), `photoGuide.test.tsx` (sunucu onayı); Playwright `lock.spec.ts` (sunucu 423 + ikinci sekme), `csp.spec.ts`, `flow.spec.ts`.

## 8. Kalan riskler (canlıya çıkış öncesi)

1. **Docker/Caddy/TLS/ACME/SQL TLS fiilen çalıştırılmadı** (docs/19 §0, §12 prova dağıtımı); CI `deploy-package` sonucu bilinmiyor.
2. **Gerçek tarayıcı CSP doğrulaması** Caddy olmadan simülasyondur; gerçek dağıtımda konsol/`securitypolicyviolation` izlenmeli; farklı TV tarayıcılarında CSP davranışı (özellikle eski Chromium/WebKit) denenmedi.
3. **Fotoğraf onayı** sorumluluk beyanıdır; otomatik kişi sayısı/tam boy yok. İmzalı görsel URL'leri kilitle iptal edilmez (5 dk).
4. **Çok sunuculu SignalR** (backplane, ortak kayıt) yok; pilot tek sunucu.
5. Hukuk (E3), canlı AI testi/kalite (E4), gerçek mağaza TV/tablet denemesi (E5), ticari kararlar (kredi fiyatı), dış uyarı iletimi, MFA/e-posta, kalan Faz C kalemleri (docs/20 §8).
6. `probe.bak` lab artığı (yönetici silmeli); GitHub CI bu aşamada çalıştırılmadı (push yok).

## 9. CI #16 düzeltmesi (finans/izleme ve bozuk firma ayarı)

CI #16'da 5 `PilotFazCTests` testi 500 verdi (`platform status` doğrudan `JsonReaderException`). **Kök neden:** `PaidProviderPipelineTests` fail-closed senaryoları için firma ayarını KASITLI bozar (`{bozuk json`, `{"PaidAiEnabled":"evet"}`) ve satırı paylaşılan test veritabanında bırakır; `PilotMonitoring.FinanceAsync` tüm firmaların `settings` JSON'unu tek sorguda okuduğu için tek bozuk satır tüm finans/izleme/`platform status` çıktısını düşürüyordu. Bu **Linux/CI'ya özgü bir EF davranışı değil**: aynı veriyle yerelde (Windows) aynı istisna yeniden üretildi; bozuk veri **uygulama dışı yoldan** (elle SQL/test) gelir, uygulama böyle veri yazmaz; asıl hata, izleme aracının tek bozuk firmaya karşı dayanıksız olmasıydı (yerelde tam paket sıralama şansıyla geçmişti). Tür uyumsuzluğunda istisna `JsonException` değil `InvalidOperationException`'dır: yakalama `PaidAiPolicy` ile aynı ayrımı kullanır (DB/iptal hataları yayılır).
**Düzeltme (en küçük):** firma listesi ayar JSON'u OLMADAN okunur; ayar firma başına okunur, okunamıyorsa **fail-closed** (ücretli AI kapalı, bütçe sınırları 0 — `BudgetLimits.ForAsync` ile aynı kural), `OrganizationFinance.SettingsCorrupt = true` ve KRİTİK `settings:<slug>` uyarısı üretilir. Finansal hesaplamalar ve tenant filtreleri değişmedi. Regresyon: `Bozuk_firma_ayari_finans_ve_izlemeyi_dusurmez…` (iki bozuk değer; API + `platform status`). **Aynı sınıf risk (düzeltilmedi, kapsam dışı):** `GET /platform/organizations` listesi `Take(50)` varlık yükler; en yeni 50 firmadan biri bozuksa o uç da 500 verir.
**Decimal uyarısı:** `OrganizationSettings` ondalık alanları JSON sütunundadır (SQL decimal yok): kırpma riski yok, testle kanıtlı (`0,123456789`/`12345,678901234` birebir döner); uyarı yalnızca gürültüdür. **Benzersiz indeks günlükleri:** bilinen KASITLI üreticiler eşzamanlılık testleridir (`CostReconciliationTests` süpürücü yarışı → `ux_ai_usage_logs` olay anahtarı; `AdmissionConcurrencyTests`/`IdempotencyTests` → aktif tekrar/anahtar indeksi; `CreditSystemTests` eşzamanlı referans; platform onboarding eşzamanlı slug/e-posta); hiçbir indeks kaldırılmadı. Tek tek satır atfı yerelde yapılamadı (xunit çıktıyı yalnızca başarısız koşuda yazar): CI günlüğündeki her ihlalin bu testlerden geldiği **doğrulanmadı**, yalnızca üreticiler listelendi.
