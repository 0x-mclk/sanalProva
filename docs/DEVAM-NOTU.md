# Devam notu (son güncelleme: M3.10 sonu — M3 tamamlandı)

Kısa durum özeti; ayrıntı için `CLAUDE.md` ve commit geçmişi. Kural: her aşama bitince dur, raporla, onay al. **M4 için kullanıcı onayı beklenir.**

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
| M3.10 | PostgreSQL kalıntı taraması, belge güncellemeleri, risk sınıflandırması, son regresyon | bu commit (`git log` ile bakın) |

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
- **Eski araç:** `scripts/db.ps1` (taşınabilir PostgreSQL yöneticisi, `reseed-demo` psql DELETE'leri) hâlâ duruyor ama **kullanılmıyor**; silinmedi (veriye/yedeğe dokunmama kuralı). M4'te MSSQL kurulum betiğiyle değiştirilecek.
- **Eski belgeler (M4'te yeniden yazılacak, bugün yalnızca "tarihsel" uyarısı eklendi):** `README.md` (kurulum, 5433, `db.ps1`, `ConnectionStrings:Main` açıklaması), `docs/02` (mimari, Docker/Testcontainers/yedekleme), `docs/03` (başlık "PostgreSQL Veri Modeli"), `docs/09`, `docs/10`, `docs/README.md`. `CLAUDE.md` ve `docs/12` güncellendi.

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
- **M4 (altyapı ve belgeler, onay gerekir):** kapsamlı MSSQL kurulum/geliştirme ortamı betiği ve belgeleri (README, docs/02, 03, 09, 10, `db.ps1` değişimi), Docker (api + image-worker + SQL Server) ve CI/CD (derleme, birim, entegrasyon, E2E; E2E bugün elle kuruluyor), yedekleme/geri yükleme betikleri, `sanalprova_dev` şema kurulumu ve PostgreSQL kimliklerini koruma + TenantScan kararı (M5 hazırlığı).
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

1. **İptal edilmiş + maliyet kaydı yazılmamış iş** (worker çökmesi): rezervasyon süresiz açık kalabilir → firma bütçesini haksız yere tüketir (yanlış `BudgetExceeded`; aşım değil). Gerekli: zaman aşımlı kapanış kuralı (lease + sağlayıcı azami bekleme + pay sonrası). **Karar bekliyor:** kapanışta en kötü durum maliyetini kayda geçirmek (muhafazakâr) mı, rezervasyonu bırakmak (eksik sayma riski) mı.
2. **Platform geneli limit yok**; yalnızca firma günlük/aylık + iş başına. N firma × günlük limit platform bütçesini aşabilir. **Şube düzeyi limit de yok.**
3. **Belirsiz sonuç** (`OutcomeUncertain`) en kötü durumla sayılır ve platform yöneticisi mutabakatına kadar öyle kalır; mutabakat arayüzü yok.
4. **Günlük sınır UTC gününe göre**, mağaza saat dilimine göre değil (TR gece yarısı kaydırması).
5. fal ve Pruna istek başına gerçek maliyet döndürmez: harcama **tahmindir**; kredi/fatura R5'te gerçek maliyetle uzlaştırılmalı.
6. **R5 tasarım ilkesi:** kredi bakiyesi aynı rezervasyon satırını/transaction'ı kullanmalı; ikinci defter açılmamalı (M3.6 kararı).

## Canlıya çıkış öncesi ZORUNLU işler (kullanıcı kararı — A grubundaki 1–4'ün kaynağı)

1. Veritabanı ve görsel (depo) yedekleme / geri yükleme: prosedür, sıklık, geri yükleme provası.
2. Silinen müşteri verilerinin yedekten geri gelmesini önleyen prosedür (silme kaydı defteri; geri yükleme sonrası yeniden silme).
3. Maliyet ve denetim kayıtları için saklama politikası (şu an sonsuz; hukuki süre kararı).
4. AI sağlayıcılarının kişisel veri saklama ve silme koşulları: sözleşme/DPA, Pruna elle silme yok, canlı doğrulama; aydınlatma metni.
Ayrıca: gerçek mağaza TV denemesi, hukuki onaylı aydınlatma metni, S3 için `ListSessionFoldersAsync`.

## Önerilen yol haritası (öneri; her adım onayla)

1. **M4.1 Geliştirici ortamı + belgeler:** MSSQL kurulum/başlatma betiği (`db.ps1` yerine), README ve docs/02–03–09–10 yeniden yazımı, `.local` PostgreSQL arşiv kararı.
2. **M4.2 Konteyner + CI:** Docker Compose (api, image-worker, SQL Server), CI hattı (derleme, birim, entegrasyon, izole E2E), idempotent migration betiğiyle dağıtım.
3. **M4.3 Yedek/geri yükleme + üretim sertleştirme:** A-1, A-2, A-5 (prova dahil); dev şema kurulumu ve PostgreSQL kimliklerini koruma kararı.
4. **M5.1 İşletme kaydı + platform yönetimi:** firma/şube açılışı, platform yöneticisi sağlama ve mutabakat arayüzü.
5. **M5.2 Bütçe/kredi:** rezervasyon kapanış kuralı, platform/şube limiti, kredi paketleri (aynı rezervasyon modeli), ardından ödeme altyapısı (sağlayıcı seçimi için ayrı karar).
6. **Canlıya çıkış kapısı:** A grubunun tamamı + canlı AI testi (onaylı) + gerçek TV denemesi + hukuki metin.
