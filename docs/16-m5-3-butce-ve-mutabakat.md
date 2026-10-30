# M5.3 — Firma bazlı bütçe ve AI işlem mutabakatı

Kapsam: firma başına günlük/aylık/iş başı AI harcama sınırı; iptal edilmiş işlerin rezervasyonlarının doğru kapanması; sonucu belirsiz işlerde çift çağrı/çift maliyet riskinin kapatılması; firmaların birbirinin bütçesini etkilememesi. Var olanlar (atomik `BudgetGuard.ReserveAsync`, `reserved_cost_usd`, `OutcomeUncertain`, `CostReconciliationSweep`, `PaidAiEnabled`, global kapılar) **yeniden yazılmadı**; üzerine eklendi. Kredi sistemi (M5.4) bu aşamada YOK.

## 1. Mevcut yapı (değişmeyenler)

- İki kapı: `Generation:UseMockProviders=false` + `Budget:AllowPaidCalls=true` + API anahtarı (global) ve firma kapısı `PaidAiEnabled` (kabulde ve `ReserveAsync` içinde). Bu aşama hiçbirini gevşetmez.
- `BudgetGuard.ReserveAsync`: firma başına `sp_getapplock` + tek transaction: kesinleşmiş harcama + diğer işlerin **açık rezervasyonları** + bu çağrının en kötü durumu sınırlara karşı denetlenir ve rezervasyon aynı transaction'da yazılır.
- Maliyet kaydı yazılan iş (başarı, kesin başarısızlık, `OutcomeUncertain`) rezervasyonu kendiliğinden kapatır: açık rezervasyon yalnızca Running / Queued+gönderim kanıtı / (yeni) Cancelled+gönderim kanıtı+kayıtsız işlerdir; Succeeded/Failed sayılmaz (çift sayım yok).

## 2. Firma bazlı sınırlar

`OrganizationSettings.DailyBudgetUsd / MonthlyBudgetUsd / MaxJobCostUsd` (JSON sütunu, **migration yok**; `null` = yalnızca platform tavanı).

**Etkin sınır = min(platform tavanı, firma sınırı)** (`BudgetLimits.Resolve`, birim testli). Platform tavanı `Generation:Budget` yapılandırmasıdır ve her firma için ayrı ayrı uygulanır; firma değeri tavanı yükseltemez. Negatif/bozuk firma değeri 0 sayılır (fail-closed); firma bulunamazsa sınırlar 0'dır.

- Denetim yeri: `ReserveAsync` (kilit altında, tek yer) ve kabuldeki `QUALITY_OVER_BUDGET` ön denetimi (`AiModelEndpoints.ExceedsJobLimit`, iş başı sınır firma değerini de dikkate alır).
- **Yazma yetkisi yalnızca operatör komutudur:** `dotnet SanalProva.Api.dll platform budget --org SLUG [--daily USD|none] [--monthly USD|none] [--job USD|none] [--confirm]`. `--confirm` yoksa yazmaz; seçeneksiz yalnızca gösterir; değer tavanı aşarsa reddedilir (çıkış kodu 3); `none` firma değerini siler; `Platform.BudgetChanged` denetim kaydı (kişisel veri yok). Hiçbir firma/personel API'si bu alanları yazamaz (kaynak taraması testi: `BudgetLimitsApiTests`).
- Görünürlük: `GET /admin/usage` etkin sınırları, platform tavanını, "firma sınırı mı" bayrağını ve açık rezervasyon toplamını verir (Manager+; yalnızca kendi firması). Platform paneli işletme ayrıntısı (salt okunur) firma değerlerini gösterir. React: kullanım ekranı "(firma sınırı)" etiketi ve devam eden ayrılmış tutar notu.
- **Bilinçli karar:** platform çapında toplam (tüm firmalar) sınırı YOKTUR. Firmaların birbirinin bütçesini etkilememesi ilkesi gereği her firma yalnızca kendi harcaması + kendi açık rezervasyonlarıyla sınırlanır (kilit de firma başınadır). Toplam maliyet kontrolü platform tavanı × firma sayısı kadardır; firma açarken tavan bilinçli seçilmelidir.

## 3. Rezervasyon kapanış kuralı (R5 öncesi risk #1 — kapandı)

Karar (kullanıcı, M4.1) uygulandı:

| Durum | Sonuç |
| --- | --- |
| Başarı / kesin başarısızlık | Maliyet kaydı (kesin tutar veya 0) iş durumuyla birlikte yazılır; rezervasyon açık sayılmaz. (değişmedi) |
| **İptal + sağlayıcıya HİÇ gönderilmemiş** (niyet ve kimlik yok) | Rezervasyon **hemen serbest**: `OpenReservations` bu işi saymaz; süpürücü `reserved_cost_usd` alanını da temizler. Önceden süresiz açık kalıp firma bütçesini haksız tüketebiliyordu. |
| **İptal + gönderilmiş** (veya gönderim niyeti/kimliği var) + kayıt yok | Kurtarma süresi boyunca (`Generation:CancelledReservationRecoverySeconds`, varsayılan 900 sn > kilit + sağlayıcı azami bekleme) rezervasyon **açık** kalır (canlı worker kendi kaydını yazabilir). Süre dolunca süpürücü **tek, idempotent** `OutcomeUncertain` maliyet kaydı yazar (en kötü durum, `Open` mutabakat durumu). |
| Çökmüş Running iş | Mevcut kurtarma (`RecoverExpiredLeasesAsync`): gönderilmemişse yeniden kuyruk/Timeout; gönderilmişse çözüm geçişi, sonra `FailUncertainWithCostAsync` ile maliyetli `OutcomeUncertain`. (değişmedi) |

Çift sayım/çift kayıt koruması: olay anahtarı `uncertain:{jobId}` filtreli benzersiz indeks (`ux_ai_usage_logs_event_key`); sorgu ayrıca "o deneme için maliyet kaydı var mı" ve "anahtar var mı" diye bakar; eşzamanlı süpürücülerde benzersiz indeks ihlali yutulur (`TryExecuteAsync`, 4 paralel süpürücü testi: tek kayıt). Worker iptali gördüğünde kendi kaydını yazarsa süpürücü kayıt eklemez.

Mutabakat: dönüştürülen kayıt mevcut platform mutabakat akışına (`ai_cost_reconciliations`, `/platform/cost`) açıktır.

## 4. Çift çağrı / çift maliyet (OutcomeUncertain) — durum

Mevcut korumalar korundu ve testlidir: gönderim niyeti ve sağlayıcı kimliği önce kalıcı yazılır; zaman aşımında aynı tahmin sorgulanır (yeni istek açılmaz); belirsiz iş kendiliğinden yeniden gönderilmez; yeni istek aynı belirsiz işi döndürür (`includeUncertain`); yalnızca personelin bilinçli "yeniden dene"si yeni ücretli iş açar ve bu iş de rezervasyon/bütçe kapısından geçer; `ux_generation_jobs_active_dedupe` tek etkin ücretli iş garantisi verir.

**Kabul edilen sınır:** personel gönderilmiş bir işi iptal edip aynı isteği hemen yeniden verirse ikinci (bilinçli) ücretli iş açılır; ilk işin en kötü durum tutarı rezervasyonda tutulduğundan bütçe aşılmaz. Otomatik engel getirilmedi (kullanıcı eylemi).

## 5. M5.4 (kredi) için temel

- Sınırlar tek çözümleyicide (`BudgetLimits`); kredi, "kalan kredi" denetimi olarak aynı kilit altındaki `ReserveAsync` içine eklenir. **Maliyet rezervasyonu** (`reserved_cost_usd`, bizim sağlayıcıya ödeyeceğimiz en kötü tutar) ile **müşteri kredi rezervasyonu** ayrı kavramlardır; aynı alana yazılmaz.
- Rezervasyon yaşam döngüsü artık tamdır (açılış → maliyet kaydı / serbest / belirsiz kayıt); kredi iadesi/düşümü bu kapanış noktalarına bağlanabilir (başarı: düş; kesin başarısızlık / hiç gönderilmemiş iptal: iade; belirsiz: mutabakata kadar bloke).
- Kapsam dışı (M5.4): kredi defteri, paketler, ödeme, kredi ekranları.

## 6. Bilinen sınırlar

- Günlük/aylık pencere UTC'dir (mağaza saat dilimine göre değil).
- fal ve Pruna istek başına gerçek maliyet döndürmez: harcama tahmindir (mutabakat sağlayıcı faturasıyla).
- Firma sınırı yalnızca operatör komutuyla değişir (firma sahibine ayrı arayüz yok; gerekirse M5.4 ile birlikte).
- Çalışan işlerin ayrılmış tutarı sınır değişikliğinden etkilenmez; yeni sınır sonraki ücretli çağrıdan itibaren geçerlidir.
- Süpürücü varsayılan 10 dakikada bir çalışır (`ReconciliationSweepMinutes`); iptal edilmiş gönderilmiş işin bütçede "açık" kalması en fazla kurtarma süresi + süpürücü aralığıdır.
