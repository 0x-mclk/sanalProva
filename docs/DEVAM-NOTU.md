# Devam notu (son güncelleme: M3.9 sonu)

Kısa durum özeti; ayrıntı için `CLAUDE.md` ve commit geçmişi. Kural: her aşama bitince dur, raporla, onay al.

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
| M3.9 | Playwright E2E (izole MSSQL), `useDraftItems.flush` yarışı düzeltmesi | bu commit (kimlik: `git log -1 --format=%h -- docs/DEVAM-NOTU.md`) |

## Ortamın durumu (önemli)

- `sanalprova_test`: tüm migration'lar uygulandı (son: `TenantConsistency`), **history ve append-only sertleştirmeleri uygulandı** (`sanalprova_app`: `__ef_migrations_history` INSERT/UPDATE/DELETE, `ai_cost_reconciliations` UPDATE/DELETE reddedilir; gerçek login testleriyle doğrulandı).
- `sanalprova_dev`: **boş** (0 tablo, migration geçmişi yok). Karar: M5 hazırlığına kadar boş kalır; migration/sertleştirme uygulanmaz. M5'te şema oluşturma, PostgreSQL kimliklerini koruma ve TenantScan adımları ayrıca planlanacak.
- Lab testleri için: `$env:SANALPROVA_LAB_ADMIN='Server=.\SANALPROVA;Integrated Security=True;TrustServerCertificate=True;Encrypt=False'` (yoksa atlanır). Testlerde AI anahtarları etkisiz bırakılır (`Ai__*__ApiKey=' '`, `AllowPaidCalls=false`).
- E2E ortamı: `sanalprova_e2e` yalnızca E2E koşusu sırasında vardır (`. ./scripts/e2e-env.ps1 up` / `down`); şu an **yok** (örnekte yalnızca `sanalprova_dev` ve `sanalprova_test`). `playwright.config.ts` ortam hazır değilse veya veritabanı adı `sanalprova_e2e` değilse çalışmaz; çalışan sunucuyu yeniden kullanmaz. Çalıştırma: PowerShell'de `. ./scripts/dev-env.ps1; . ./scripts/e2e-env.ps1 up; cd frontend; npm run test:e2e; cd ..; . ./scripts/e2e-env.ps1 down` (Windows kimliği SANALPROVA'da yönetici olmalı).
- Son test durumu (M3.9 sonu): backend derleme 0 uyarı/0 hata; birim 252/252; entegrasyon 3 turda 248/248 (işlenmemiş istisna 0, sağlayıcıya HTTP çağrısı 0); frontend typecheck/lint temiz, 70/70 test, build tamam; **E2E 3 turda 9/9** (mevcut 3 TV testi + giriş/KVKK/fotoğraf/üretim/favori, ağ hatasında aynı anahtar, retry çift tıklama, onay geri alma ve oturum silmede TV temizliği, firma/şube izolasyonu).
- E2E çıktıları: ekran görüntüleri `frontend/test-results/screens` (sentetik, gitignore'da); video/trace yalnızca başarısızlıkta üretilir ve giriş isteğinde koşuya özel rastgele parolayı içerebilir → paylaşılmaz.

## Kalan riskler

- RLS yok: okuma izolasyonu EF filtresi + envanter testine bağlı (ham SQL / `IgnoreQueryFilters` envanteri `TenantArchitectureTests`).
- `display_pairings` firma sütunu taşımıyor.
- Dev veritabanında çapraz firma verisi olup olmadığı bilinmiyor (migration ön kontrolü durdurur).
- Revizyon ucu sabit 501; Aşama 5'te sahiplik denetimi kabul kriteri.
- Mutabakat kararları DB düzeyinde değişmez değil (append-only DENY henüz uygulanmadı).
- Platform yöneticisi sağlama betiği/arayüzü yok (bayrak yalnızca veritabanında); mutabakat arayüzü yok.
- Bütçe hâlâ yaklaşık (rezervasyon R5); düzgün kapanış (graceful drain) yok.
- Gerçek ücretli AI canlı testi, gerçek mağaza TV denemesi yapılmadı.
- E2E kapsamı: yalnızca Chromium (TV taklidi); gerçek kamera (`getUserMedia`), WebKit/mobil Safari, katalog yönetim ekranları ve çok adımlı revizyon E2E dışında; E2E kurulumu elle (CI hattı yok).
- E2E izolasyon testleri API/arayüz davranışını doğrular; veritabanı düzeyi garanti M3.4 testlerindedir.
- Mock modda `/retry` anahtarsız çağrılırsa dedupe yok (ücretsiz); arayüz her zaman anahtar gönderir.
- İdempotency anahtarlarının süresi/temizliği yok (satırlar iş ile birlikte yaşar).

## Sıradaki işler (onay sırasıyla)

1. Dev boş kalır (M5'e kadar).
2. (Yapıldı) Append-only `sanalprova_test`'te uygulandı; M3.5–M3.9 tamamlandı.
3. **M3.10** (onay bekliyor): rapor + R5 kredi veri tasarımı belgesi + platform yöneticisi sağlama komutu.
4. Sonrasında SaaS'ın ticari özellikleri (R5: kredi/paket/abonelik/ödeme) ve canlıya alma çalışmaları; R5 öncesi açık riskler ve aşağıdaki zorunlu liste önce.
5. M4 yalnızca onayla.

## Canlıya çıkış öncesi ZORUNLU işler (kullanıcı kararı)

1. Veritabanı ve görsel (depo) yedekleme / geri yükleme: prosedür, sıklık, geri yükleme provası.
2. Silinen müşteri verilerinin yedekten geri gelmesini önleyen prosedür (silme kaydı defteri; geri yükleme sonrası yeniden silme).
3. Maliyet ve denetim kayıtları için saklama politikası (şu an sonsuz; hukuki süre kararı).
4. AI sağlayıcılarının kişisel veri saklama ve silme koşulları: sözleşme/DPA, Pruna elle silme yok, canlı doğrulama; aydınlatma metni.
Ayrıca: gerçek mağaza TV denemesi, hukuki onaylı aydınlatma metni, S3 için `ListSessionFoldersAsync`.

## M3.6 bulgusu ve düzeltmesi
Bütçe kapısı yarışı (eşikte paralel işler limiti 4-5 kat aşıyordu) atomik rezervasyonla kapatıldı (BudgetGuard.ReserveAsync, firma applock + reserved_cost_usd). Limitler yalnızca firma başına günlük/aylık + iş başına; platform/şube düzeyi limit yok (R5 kararı). Kredi/paket/abonelik R5.

## R5 öncesi açık riskler (korunacak)
- İptal edilmiş + maliyet kaydı yazılmamış işlerin rezervasyonu worker çökerse sonsuza kadar açık sayılabilir: zaman aşımlı kapanış kuralı/süpürücü gerekli.
- Platform genelinde bütçe limiti yok (yalnızca firma günlük/aylık + iş başı); şube düzeyi limit de yok.
