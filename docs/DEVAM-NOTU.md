# Devam notu (son güncelleme: M4.2 sonu — M4.3 onay bekliyor)

Kısa durum özeti; ayrıntı için `CLAUDE.md` ve commit geçmişi. Kural: her aşama bitince dur, raporla, onay al. **M4.3 için kullanıcı onayı beklenir.**

## Tamamlanan (commit'li)

| Aşama | Konu | Commit |
|---|---|---|
| M0–M2 | MSSQL geçişi, SQL Server 2025 Express (SANALPROVA), kuyruk T-SQL, applock | önceki commit'ler |
| R3 / R4 | Atomik kabul, kilit sahipliği; belirsiz AI gönderimi (otomatik yeniden gönderim yok) | `e930520`, `0ee7ab6` |
| M3.1 | Belirsiz maliyet: rezerve tutar, tekil olay anahtarı, süpürücü, platform yöneticisi mutabakatı | `db094b3` |
| M3.2 | Migration doğrulaması (geçici lab veritabanları), idempotent betik | `e593d2f` |
| M3.3 | Veri bütünlüğü (FK/CHECK/NULL/collation/decimal), history sertleştirmesi | `e5c4c59` |
| M3.4 analiz | IDOR/BOLA matrisi, mimari testler, append-only betikleri | `3ae5221` |
| M3.4 | Bileşik FK ile tenant tutarlılığı, yapısal şube filtresi, `TenantConsistency` migration'ı | `e3c5cc6` |
| M3.5 | KVKK silme doğrulaması (purge/worker yarışı, yetim temizliği, kilitlenme yeniden denemesi) | `df49fb4` |
| M3.6 | Eşzamanlılık ve yük ölçümü | `6227ede` |
| Bütçe | Atomik bütçe rezervasyonu (firma applock + `reserved_cost_usd`) | `f160763` |
| M3.7 | Idempotency doğrulaması (replay `resultImageId`, anahtar yeniden kullanımı 422) | `35d2979` |
| M3.8 | Frontend/backend sözleşmesi; `/retry` için isteğe bağlı `Idempotency-Key`; eksik Türkçe hata eşlemeleri | `fcd3a15` |
| M3.9 | Playwright E2E (izole MSSQL), `useDraftItems.flush` yarışı düzeltmesi | `5113ebb` |
| M3.10 | PostgreSQL kalıntı taraması, belge güncellemeleri, risk sınıflandırması, son regresyon | `d7fc9eb` |
| M4.1 | `db.ps1` MSSQL'e yeniden yazıldı (status/migrate/reset/script, izin listeli hedefler); README ve docs 02/03/09/10/README güncellendi | `fc0d8a1` |
| M4.2 | Docker (API, migration paketi, image-worker, SQL Server 2025 Express konteyneri) + Compose + GitHub Actions CI; **Docker/CI fiilen çalıştırılmadı** (aşağıda) | bu commit (`git log` ile bakın) |

## Ortamın durumu (önemli)

- `sanalprova_test`: tüm migration'lar uygulandı (son: `TenantConsistency`), **history ve append-only sertleştirmeleri uygulandı** (`sanalprova_app`: `__ef_migrations_history` INSERT/UPDATE/DELETE, `ai_cost_reconciliations` UPDATE/DELETE reddedilir; gerçek login testleriyle doğrulandı).
- `sanalprova_dev`: **boş** (0 tablo, migration geçmişi yok). Karar: M5 hazırlığına kadar boş kalır; migration/sertleştirme uygulanmaz. M5'te şema oluşturma, PostgreSQL kimliklerini koruma ve TenantScan adımları ayrıca planlanacak. Sonuç: API'yi dev'e karşı elle çalıştırmak bugün mümkün değil; doğrulama testler + izole E2E ile yapılır.
- PostgreSQL (`.local/pgdata`, `.local/pgsql`), yedekler ve gerçek `storage/` klasörü: **dokunulmadı**, dokunulmaz.
- Lab testleri: `$env:SANALPROVA_LAB_ADMIN='Server=.\SANALPROVA;Integrated Security=True;TrustServerCertificate=True;Encrypt=False'` (yoksa atlanır). Testlerde AI anahtarları etkisiz bırakılır (`Ai__*__ApiKey=' '`, `AllowPaidCalls=false`).
- E2E ortamı: `sanalprova_e2e` yalnızca E2E koşusu sırasında vardır (`. ./scripts/e2e-env.ps1 up` / `down`); şu an **yok**. `playwright.config.ts` ortam hazır değilse veya veritabanı adı `sanalprova_e2e` değilse çalışmaz; çalışan sunucuyu yeniden kullanmaz. Çalıştırma: PowerShell'de `. ./scripts/dev-env.ps1; . ./scripts/e2e-env.ps1 up; cd frontend; npm run test:e2e; cd ..; . ./scripts/e2e-env.ps1 down`.
- E2E çıktıları: ekran görüntüleri `frontend/test-results/screens` (sentetik, gitignore'da); video/trace yalnızca başarısızlıkta üretilir ve giriş isteğinde koşuya özel rastgele parolayı içerebilir → paylaşılmaz.
- **Son test durumu (M3.10 son regresyonu):** derleme 0 uyarı/0 hata; birim 252/252; entegrasyon 248/248 (işlenmemiş istisna 0, sağlayıcıya HTTP çağrısı 0, artakalan lab veritabanı 0); frontend typecheck/lint temiz, 70/70 test, build tamam; E2E 9/9 (tek tur; M3.9'da 3 turda 9/9 doğrulanmıştı, M3.10'da yalnızca belge değişti). Aynı testler gerekçesiz tekrarlanmadı.

## PostgreSQL kalıntı taraması (M3.10 bulgusu)

- **Çalışan PostgreSQL kodu yok:** `Npgsql` paketi, `FOR UPDATE SKIP LOCKED`, `pg_advisory`, `ILIKE`, `jsonb`, 5433 bağlantısı kaynak/test/frontend kodunda bulunmuyor (`Directory.Packages.props` yalnızca `Microsoft.EntityFrameworkCore.SqlServer`).
- **Kalan yalnızca açıklayıcı/kozmetik:** `SessionEndpoints.cs` ve `GenerationProcessor.cs` yorumları (PostgreSQL ile karşılaştırma, bilinçli), `DatabaseIntegrityTests`/`MigrationLab` yorumları, `PersistenceTests` test adı (`..._jsonb_ayarlar_...`; sütun artık `nvarchar` JSON), `DevStatusPage.tsx` açıklama yorumu ("PostgreSQL"), `playwright.config.ts` güvenlik yorumu.
- **Eski araç (M4.1'de çözüldü):** `scripts/db.ps1` MSSQL yöneticisi olarak yeniden yazıldı; PostgreSQL komutları (`init/start/stop/reseed-demo`) kaldırıldı. `.local/pgsql|pgdata|postgres*` ve `.local/backups` dokunulmadan duruyor.
- **Belgeler (M4.1'de güncellendi):** `README.md` (MSSQL kurulum, veritabanı rolleri, `db.ps1`, test/E2E, gizli anahtarlar), `docs/02`, `docs/03` (tip karşılık tablosu), `docs/09`, `docs/10`, `docs/README.md`, `CLAUDE.md`, `docs/12`. Kalan eski ifadeler yalnızca `docs/03` gövdesindeki PostgreSQL tip gösterimi (üstteki tablo karşılıkları verir) ve `docs/01`/`docs/07`/`docs/08` gibi bağlam belgelerinde tarihsel anlatımdır.

## M4.2: Docker ve CI durumu (ÖNEMLİ — doğrulanan / doğrulanamayan)

**Eklenen dosyalar:** `docker-compose.yml`, `backend/Dockerfile` (hedefler: `build` → `migrations` → `migrator`, `api`), `backend/.dockerignore`, `image-worker/Dockerfile`, `image-worker/.dockerignore`, `docker/db/provision.sql` + `docker/db/run-sql.sh` (konteyner provision/harden), `scripts/compose-env.ps1|sh`, `.env.example`, `.gitattributes` (sh/Dockerfile/docker LF), `.github/workflows/ci.yml`; E2E için `frontend/e2e/sql.ts` (Windows kimliği veya SQL girişi), `playwright.config.ts` platform bağımsız webServer komutu.

**Tasarım:** Compose yığını Windows örneklerinden tamamen yalıtılmıştır (ayrı konteyner, adlandırılmış birimler `sanalprova_*`, ana makineye yalnızca `127.0.0.1:14331` ve `127.0.0.1:8080`; image-worker hiç açılmaz). Sıra: `sqlserver` → `db-provision` (3 DB + en az yetkili girişler) → `db-migrate` (EF bundle, migrator hesabı) → `db-harden` (history/append-only DENY) → `image-worker` → `api`. Parolalar `.env`'den (git dışı, rastgele); AI anahtarları boş, mock açık, ücretli çağrılar kapalı, demo tohumu varsayılan kapalı. CI'da parolalar her koşuda üretilir ve maskelenir.

**Gerçekten çalıştırılıp doğrulananlar (Docker olmadan, yakın vekillerle; yalnızca izole `sanalprova_e2e`/temp):**
- Dockerfile'ın `dotnet publish -c Release` adımı; yayımlanan DLL **Production** ortamında ortam değişkeniyle yapılandırılıp başlatıldı: `/health/live` 200, `/health/ready` 200 Healthy (veritabanı + depolama + image-worker), OpenAPI 404 (Production'da kapalı), tohum çalışmadı, loglarda anahtar/parola izi yok.
- image-worker: temiz sanal ortamda Dockerfile'ın `pip install .` akışı + `uvicorn app.main:app`; `/health` ok; `/v1` iç anahtarsız 401.
- EF migration paketi (`dotnet ef migrations bundle`, tasarım zamanı sahte bağlantı dizesiyle; Windows'ta `win-x64`) üretildi ve **boş** bir veritabanına uygulandı (26 tablo, 6 migration).
- `docker/db/provision.sql`: `SET PARSEONLY ON` ile sözdizimi doğrulandı (dinamik SQL dizgeleri ayrıştırılamaz; elle gözden geçirildi). Yeni veritabanının varsayılan uyumluluk düzeyi SQL Server 2025'te **170**'tir; provision betiği EF kararı gereği 160'a sabitler.
- YAML (`docker-compose.yml`, `ci.yml`) ayrıştırıldı (yapı, bağımlılık koşulları, adım sayıları, eylem sürüm sabitlemesi); `bash -n` (shell betikleri, LF); PowerShell betikleri ayrıştırıldı; frontend typecheck/lint; E2E Windows'ta yeniden çalıştırıldı (config/sql yardımcı refaktörü regresyon yaratmadı).

**Ortam nedeniyle ÇALIŞTIRILAMAYANLAR (Docker bu makinede yok; WSL2'de dağıtım yok; kullanıcı yönetici değil; sistem yazılımı kurulmadı):**
- `docker compose up` ve imaj derlemeleri hiç çalışmadı. **Varsayımlar doğrulanmadı:** `mcr.microsoft.com/mssql/server:2025-latest` etiketi; `mcr.microsoft.com/mssql-tools` imajındaki sqlcmd 17'nin SQL Server 2025'e bağlanması (`-C`, TLS); 2025 imajında `MSSQL_COLLATION`/`MSSQL_PID=Express`; healthcheck'lerin (bash `/dev/tcp`) davranışı; `aspnet`/`runtime-deps` imajlarındaki `app` kullanıcısı, `bash`/`sh` varlığı ve ICU; `efbundle`'ın `linux-x64` self-contained derlemesi (NuGet runtime paketi indirme); `docker compose run --rm db-harden` bağımlılık zinciri; konteynerde `Application Name`/`Encrypt=True` bağlantısı; image-worker `opencv-python-headless`'in `slim` imajda çalışması.
- GitHub Actions hiç çalışmadı (depo uzak sunucuya itilmedi). CI'ın geçtiği **iddia edilmez**. Özellikle: testler Linux'ta hiç koşulmadı (yol/büyük-küçük harf/kültür farkları çıkabilir); `integration` işi `SANALPROVA_LAB_ADMIN` olmadığından migration laboratuvarı testlerini **atlar** (bu testler Windows `SANALPROVA` örneği ister); `e2e` işinde `mssql-tools18` kurulumu (Microsoft apt deposu) ve Playwright'ın Linux'ta ilk çalışması doğrulanmadı.
- **İlk Docker/CI koşusunda sorun çıkması olasıdır; başarısızlık gizlenmez** (`continue-on-error` yok). İlk işler: `docker compose up -d --build api` günlüklerini incelemek, hataları sırayla düzeltmek.

**Kalan riskler (M4.2):** Data Protection anahtarları birimde ama Production için TLS sonlandıran ters vekil ve gizli yönetimi tasarlanmadı; `.env` düz metin (yerel geliştirme için kabul, üretimde gizli yöneticisi gerekir); `sanalprova_dev` konteynerde yalnızca konteynerin kendi veritabanıdır (Windows `sanalprova_dev` ile ilgisiz); Compose'ta `sa` yalnızca tek seferlik işlerde kullanılır ama konteyner içinde etkindir; imaj etiketleri (`2025-latest`, `10.0`, `3.12-slim`) sabit özet (digest) değildir (tedarik zinciri sertleştirmesi M4.3); migration paketi yalnızca x64; yedekleme/geri yükleme hâlâ M4.3.

## Açık riskler (üç grup)

### A. Ticari kullanımdan önce MUTLAKA çözülmeli
1. Veritabanı ve görsel (depo) yedekleme / geri yükleme: prosedür, sıklık, **geri yükleme provası**.
2. Silinen müşteri verilerinin yedekten geri gelmesini önleyen prosedür (silme kaydı defteri; geri yükleme sonrası yeniden silme).
3. Maliyet ve denetim kayıtları için saklama politikası (şu an sonsuz; hukuki süre kararı).
4. AI sağlayıcılarının kişisel veri saklama/silme koşulları: sözleşme/DPA, Pruna'da elle silme yok, canlı doğrulama; **hukuki onaylı aydınlatma metni** (mevcut metin taslak).
5. Üretim veritabanında en az yetki + sertleştirmenin uygulanması (`sanalprova_app`/migrator ayrımı, history/append-only DENY, `sa` kapalı, TLS, gizli yönetimi); bugün yalnızca `sanalprova_test`'te uygulandı.
6. Gerçek ücretli AI canlı testi (Aşama 4/4b, onay kapılı) ve gerçek mağaza TV'sinde uyumluluk denemesi — yapılmadı.
7. Bütçe/rezervasyon: aşağıdaki "R5 öncesi" iki risk (iptal edilmiş rezervasyon temizliği, platform geneli limit) gerçek para harcanmadan önce kapatılmalı.

### B. M4 / M5 kapsamında çözülecek
- **M4 (altyapı ve belgeler, onay gerekir):** (M4.1 yapıldı: `db.ps1`, README, docs) kalan: Docker (api + image-worker + SQL Server) ve CI/CD (derleme, birim, entegrasyon, E2E; E2E bugün elle kuruluyor), yedekleme/geri yükleme betikleri, `sanalprova_dev` şema kurulumu ve PostgreSQL kimliklerini koruma + TenantScan kararı (M5 hazırlığı).
- **M5 (ticari özellikler, onay gerekir):** işletme kaydı (firma/şube açılışı), platform yöneticisi sağlama komutu/arayüzü + mutabakat arayüzü (bugün bayrak yalnızca veritabanında, arayüz yok), kredi paketleri/abonelik/ödeme (R5), bütçe için "R5 öncesi" iki kural, Aşama 5 revizyon motoru (sahiplik denetimi kabul kriteri), idempotency anahtarı saklama süresi.

### C. Daha sonra iyileştirilebilir
- RLS yok (okuma izolasyonu EF filtresi + `TenantArchitectureTests` envanteri); `display_pairings` firma sütunu taşımıyor.
- Düzgün kapanış (graceful drain) yok; çoklu sunucu için SignalR backplane yok.
- Mock modda `/retry` anahtarsız çağrılırsa dedupe yok (ücretsiz; arayüz hep anahtar gönderir).
- E2E kapsamı: yalnızca Chromium (TV taklidi); gerçek kamera (`getUserMedia`), WebKit/mobil Safari, katalog yönetim ekranları, revizyon E2E dışında.
- S3 için `ListSessionFoldersAsync` yalnızca yerel disk; HEIC desteklenmiyor; QR ile TV eşleştirme yok; yetim dosya temizliği başarısız yüklemeler için ayrı değil.
- Kozmetik: PostgreSQL'e atıf yapan yorumlar ve `jsonb` test adı (yukarıda).

## R5 öncesi açık bütçe ve rezervasyon riskleri (netleştirildi, korunacak)

Bugünkü model: `BudgetGuard.ReserveAsync` firma applock'u altında (kesinleşmiş harcama + diğer işlerin açık rezervasyonları + bu çağrının en kötü durumu) sınırlara karşı denetler ve rezervasyonu (`reserved_cost_usd`) aynı transaction'da yazar. Açık rezervasyon = Running; Queued + sağlayıcı kimliği/gönderim niyeti; maliyet kaydı olmayan Cancelled. Hata yönü **güvenli taraftadır** (fazla sayar, aşım olmaz; 0,50 $ / 0,15 $ / 16 paralel testte tam 3 çağrı, 0 aşım).

1. **İptal edilmiş + maliyet kaydı yazılmamış iş** (worker çökmesi): rezervasyon süresiz açık kalabilir → firma bütçesini haksız yere tüketir (yanlış `BudgetExceeded`; aşım değil). **KARAR (kullanıcı, M4.1; R5 öncesi ZORUNLU iş, henüz UYGULANMADI):**
   - Sağlayıcıya gönderilmediği **kesin** olan işin rezervasyonu serbest bırakılabilir (ör. gönderim niyeti/`provider_submit_started_at` ve sağlayıcı kimliği yok).
   - Sağlayıcıya gönderilmiş **veya gönderim sonucu belirsiz** işin rezervasyonu **doğrudan serbest bırakılmaz**.
   - Belirlenen **kurtarma süresi** (lease + sağlayıcı azami bekleme + pay) sonunda bu işler **idempotent** biçimde `OutcomeUncertain` maliyet kaydına dönüştürülür (tek sorgu/transaction; olay anahtarı `event_key` filtreli benzersiz indeksi ve `attempt` çiti ile aynı iş için ikinci maliyet kaydı veya çift ücret oluşmaz; süpürücü yeniden çalıştırılabilir).
   - Dönüştürülen kayıt sonradan **sağlayıcı paneli üzerinden mutabakata** açıktır (mevcut `ai_cost_reconciliations` / düzeltme satırları akışı; platform yöneticisi arayüzü M5.1 ile gelir).
   - **Muhasebe ayrımı:** *maliyet rezervasyonu* (bizim sağlayıcıya ödeyeceğimiz en kötü durum tutarı, `reserved_cost_usd`) ile ilerideki *müşteri kredi rezervasyonu* (müşterinin satın aldığı kredinin bloke edilmesi) **ayrı muhasebe kavramlarıdır**; birbirinin yerine kullanılmaz, aynı alana yazılmaz (aynı iş akışına bağlanabilir ama ayrı kayıt/alan).
   - Uygulama: M5.2 başında, R5/kredi işinden **önce**; kabul testleri: çökme sonrası iptal, gönderilmemiş iş serbest, belirsiz iş zaman aşımında tek `OutcomeUncertain` kaydı, süpürücü iki kez çalışınca ikinci kayıt yok, bütçe toplamı doğru.
2. **(Karar verildi — madde 1)** Önceki açık soru ("en kötü durumu kayda geç mi, bırak mı") yukarıdaki kararla kapandı: gönderilmiş/belirsiz → en kötü durumla `OutcomeUncertain` maliyet kaydına dönüşür.
3. **Belirsiz sonuç** (`OutcomeUncertain`) en kötü durumla sayılır ve platform yöneticisi mutabakatına kadar öyle kalır; mutabakat arayüzü yok.
4. **Günlük sınır UTC gününe göre**, mağaza saat dilimine göre değil (TR gece yarısı kaydırması).
5. fal ve Pruna istek başına gerçek maliyet döndürmez: harcama **tahmindir**; kredi/fatura R5'te gerçek maliyetle uzlaştırılmalı.
6. **R5 tasarım ilkesi (M4.1 kararıyla netleşti):** *maliyet rezervasyonu* ile *müşteri kredi rezervasyonu* ayrı muhasebe kavramlarıdır. Kredi akışı aynı iş/transaction sınırına bağlanabilir, ancak maliyet rezervasyonunun yerine geçmez ve aynı sütunu paylaşmaz (M3.6'daki "ikinci defter açılmamalı" notu maliyet tarafı içindi: maliyet için ikinci defter yok).

## Canlıya çıkış öncesi ZORUNLU işler (kullanıcı kararı — A grubundaki 1–4'ün kaynağı)

1. Veritabanı ve görsel (depo) yedekleme / geri yükleme: prosedür, sıklık, geri yükleme provası.
2. Silinen müşteri verilerinin yedekten geri gelmesini önleyen prosedür (silme kaydı defteri; geri yükleme sonrası yeniden silme).
3. Maliyet ve denetim kayıtları için saklama politikası (şu an sonsuz; hukuki süre kararı).
4. AI sağlayıcılarının kişisel veri saklama ve silme koşulları: sözleşme/DPA, Pruna elle silme yok, canlı doğrulama; aydınlatma metni.
Ayrıca: gerçek mağaza TV denemesi, hukuki onaylı aydınlatma metni, S3 için `ListSessionFoldersAsync`.

## Önerilen yol haritası (öneri; her adım onayla)

1. ~~**M4.1 Geliştirici ortamı + belgeler**~~ — **yapıldı** (`db.ps1` MSSQL, README, docs; `.local` PostgreSQL arşiv kararı: dokunulmaz, M4.3'te yedek stratejisiyle birlikte yeniden ele alınır).
2. ~~**M4.2 Konteyner + CI**~~ — **dosyalar yazıldı, fiilen çalıştırılmadı** (yukarıdaki bölüm). M4.3 başında: Docker kurulu bir makinede/CI'da ilk koşu ve düzeltmeler; imaj özetleri; idempotent migration betiğiyle dağıtım.
3. **M4.3 Yedek/geri yükleme + üretim sertleştirme:** A-1, A-2, A-5 (prova dahil); dev şema kurulumu ve PostgreSQL kimliklerini koruma kararı.
4. **M5.1 İşletme kaydı + platform yönetimi:** firma/şube açılışı, platform yöneticisi sağlama ve mutabakat arayüzü.
5. **M5.2 Bütçe/kredi:** rezervasyon kapanış kuralı, platform/şube limiti, kredi paketleri (aynı rezervasyon modeli), ardından ödeme altyapısı (sağlayıcı seçimi için ayrı karar).
6. **Canlıya çıkış kapısı:** A grubunun tamamı + canlı AI testi (onaylı) + gerçek TV denemesi + hukuki metin.
