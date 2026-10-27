# Devam notu (son güncelleme: M3.6 sonu)

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

## Ortamın durumu (önemli)

- `sanalprova_test`: tüm migration'lar uygulandı (son: `TenantConsistency`), **history ve append-only sertleştirmeleri uygulandı** (`sanalprova_app`: `__ef_migrations_history` INSERT/UPDATE/DELETE, `ai_cost_reconciliations` UPDATE/DELETE reddedilir; gerçek login testleriyle doğrulandı).
- `sanalprova_dev`: **boş** (0 tablo, migration geçmişi yok). Karar: M5 hazırlığına kadar boş kalır; migration/sertleştirme uygulanmaz. M5'te şema oluşturma, PostgreSQL kimliklerini koruma ve TenantScan adımları ayrıca planlanacak.
- Lab testleri için: `$env:SANALPROVA_LAB_ADMIN='Server=.\SANALPROVA;Integrated Security=True;TrustServerCertificate=True;Encrypt=False'` (yoksa atlanır). Testlerde AI anahtarları etkisiz bırakılır (`Ai__*__ApiKey=' '`, `AllowPaidCalls=false`).
- Son test durumu: unit 252/252, entegrasyon 218/218 (4 atlama).

## Kalan riskler

- RLS yok: okuma izolasyonu EF filtresi + envanter testine bağlı (ham SQL / `IgnoreQueryFilters` envanteri `TenantArchitectureTests`).
- `display_pairings` firma sütunu taşımıyor.
- Dev veritabanında çapraz firma verisi olup olmadığı bilinmiyor (migration ön kontrolü durdurur).
- Revizyon ucu sabit 501; Aşama 5'te sahiplik denetimi kabul kriteri.
- Mutabakat kararları DB düzeyinde değişmez değil (append-only DENY henüz uygulanmadı).
- Platform yöneticisi sağlama betiği/arayüzü yok (bayrak yalnızca veritabanında); mutabakat arayüzü yok.
- Bütçe hâlâ yaklaşık (rezervasyon R5); düzgün kapanış (graceful drain) yok.
- Gerçek ücretli AI canlı testi, gerçek mağaza TV denemesi yapılmadı.

## Sıradaki işler (onay sırasıyla)

1. Dev boş kalır (M5'e kadar).
2. (Yapıldı) Append-only `sanalprova_test`'te uygulandı.
3. (Yapıldı) **M3.5** KVKK silme (oturum silme/saklama/onay geri alma, dosya deposu, TV, maliyet defteri).
4. (Yapıldı) M3.6 eşzamanlılık ölçümü; sıradaki: M3.7 idempotency, M3.8 frontend/backend sözleşmesi (`OutcomeUncertain` arayüzü dahil), M3.9 uçtan uca (Playwright), M3.10 rapor + R5 kredi veri tasarımı belgesi + platform yöneticisi sağlama komutu.
5. M4 yalnızca onayla.

## Canlıya çıkış öncesi ZORUNLU işler (kullanıcı kararı)

1. Veritabanı ve görsel (depo) yedekleme / geri yükleme: prosedür, sıklık, geri yükleme provası.
2. Silinen müşteri verilerinin yedekten geri gelmesini önleyen prosedür (silme kaydı defteri; geri yükleme sonrası yeniden silme).
3. Maliyet ve denetim kayıtları için saklama politikası (şu an sonsuz; hukuki süre kararı).
4. AI sağlayıcılarının kişisel veri saklama ve silme koşulları: sözleşme/DPA, Pruna elle silme yok, canlı doğrulama; aydınlatma metni.
Ayrıca: gerçek mağaza TV denemesi, hukuki onaylı aydınlatma metni, S3 için `ListSessionFoldersAsync`.

## M3.6 bulgusu
Bütçe kapısı yarışı: eşikte paralel işler limiti 4-5 kat aşabilir (ölçüldü); production varsayılanı 4 paralel iş/ana makine ile üst sınır 3×birim tutar. R5 rezervasyonu zorunlu (kredi sisteminden önce).
