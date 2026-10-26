# Devam notu (son güncelleme: M3.4 sonu)

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
| M3.4 | Bileşik FK ile tenant tutarlılığı, yapısal şube filtresi, `TenantConsistency` migration'ı | bu oturumun son commit'i |

## Ortamın durumu (önemli)

- `sanalprova_test`: tüm migration'lar uygulandı (son: `TenantConsistency`), **history sertleştirmesi uygulandı**, append-only sertleştirmesi **uygulanmadı**.
- `sanalprova_dev`: **hiçbir M3 migration'ı veya sertleştirme uygulanmadı, dokunulmadı.** `TenantScan` dev'e çalıştırılmadı (karar sonraki oturumda).
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

1. Karar: `TenantScan`'i dev'e salt-okunur çalıştırmak; sonra dev'e M3.2–M3.4 migration + sertleştirmeler için ayrı rapor/onay (`scripts/apply-history-hardening.ps1 -Hardening All`, kuru çalışma varsayılan).
2. `-Hardening AppendOnly` kuru çalışması temiz; `sanalprova_test`'e uygulama onayı bekliyor.
3. **M3.5** KVKK silme (oturum silme/saklama/onay geri alma, dosya deposu, TV, maliyet defteri).
4. M3.6 eşzamanlılık ölçümü, M3.7 idempotency, M3.8 frontend/backend sözleşmesi (`OutcomeUncertain` arayüzü dahil), M3.9 uçtan uca (Playwright), M3.10 rapor + R5 kredi veri tasarımı belgesi + platform yöneticisi sağlama komutu.
5. M4 yalnızca onayla.
