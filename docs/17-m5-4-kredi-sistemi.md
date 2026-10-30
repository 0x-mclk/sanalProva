# M5.4 — Firma bazlı kredi sistemi

Kapsam: her işletmenin kendi kredi bakiyesi; ücretli (gerçek sağlayıcı) üretimlerin kredi tüketmesi; değiştirilemez defter; operatör yüklemesi; işletme sahibi için bakiye/geçmiş ekranı. **Gerçek ödeme alma yoktur** (M5.5). Mevcut sistemler (`BudgetGuard`, `reserved_cost_usd`, `OutcomeUncertain`, maliyet mutabakatı, `PaidAiEnabled`) yeniden yazılmadı; kredi bunların YANINA eklendi.

## 1. İki ayrı kavram (karıştırılmaz)

| | Sağlayıcı maliyeti (M5.3) | Müşteri kredisi (M5.4) |
| --- | --- | --- |
| Birim | USD (bizim ödediğimiz) | tam sayı kredi (müşterinin satın aldığı) |
| Rezervasyon alanı | `generation_jobs.reserved_cost_usd` | defterde `Reserve` girdisi + `credit_accounts.reserved_credits` |
| Kapı | `BudgetGuard.ReserveAsync` (çağrıdan önce, firma bütçe kilidi) | iş kabulü (`AdmitAsync`, kabul + kredi kilidi) |
| Kapanış | maliyet kaydı | `CreditLedgerService.SettleJobAsync` |

Kredi, USD'ye bağlanmaz; USD tarafında ek bir değişiklik yoktur.

## 2. Veri modeli (migration `CreditSystem`)

- **`credit_accounts`** (firma başına tek satır): `balance_credits` (kesinleşmiş), `reserved_credits` (devam eden işler için bloke). Kullanılabilir = bakiye − rezerve. CHECK: `balance ≥ 0`, `0 ≤ reserved ≤ balance` (negatif bakiye veritabanında imkânsız).
- **`credit_ledger`** (değiştirilemez): `entry_type` Grant | Adjustment | Reserve | Release | Consume, `amount`, `job_id` (FK DEĞİL: KVKK oturum silmesi mali kaydı silmez), `reference`, `source` (Operator | System | Payment), `note`, `tariff_key`, `balance_after`, `reserved_after`, `created_at`.
  - **Append-only:** `tr_credit_ledger_append_only` INSTEAD OF UPDATE/DELETE tetikleyicisi (hata 51200; yetkili hesap dahil). Düzeltme = yeni `Adjustment` girdisi.
  - Filtreli benzersiz indeksler: `(organization_id, reference)` (yükleme idempotency'si), iş başına **tek Reserve**, iş başına **tek kapanış (Release | Consume)** → çift düşüm / çift iade veritabanında imkânsız.
  - CHECK: tür/kaynak geçerli değerler, tutar kuralı (Adjustment ≠ 0, diğerleri > 0), şekil (Grant/Adjustment referanslı ve işsiz; Reserve/Release/Consume işli), `balance_after/reserved_after` geçerli.
- **`generation_jobs.credit_cost`**: işlem anındaki bedel (tarife sonradan değişse de işte kalır; `null` = kredi uygulanmadı).
- Değişmezler (testle sabit): `bakiye = Σ(Grant+Adjustment) − Σ(Consume)`; `rezerve = Σ(Reserve) − Σ(Release) − Σ(Consume)`; son defter girdisi hesapla aynı.

## 3. Yaşam döngüsü

| Olay | Kredi |
| --- | --- |
| İş kabulü, gerçek sağlayıcı + bedel > 0 | **Reserve** (kabul transaction'ında, işle birlikte). Yetersizse iş hiç oluşmaz: **402 `INSUFFICIENT_CREDITS`** (`required`, `available`). Aynı Idempotency-Key tekrarı ikinci rezervasyon yapmaz. |
| Mock sağlayıcı | Kredi aranmaz/tüketilmez (ücret yok). |
| Başarılı iş | **Consume** (rezervasyondaki tutar) |
| Kesin başarısızlık (sağlayıcı reddi, bütçe kapısı, zaman aşımı…) | **Release** (müşteri ödemez) |
| İptal, hiç gönderilmemiş | **Release** |
| İptal, gönderilmiş, henüz maliyet kaydı yok | **bloke** (çağrı sürüyor olabilir); işçi kaydı yazınca → Release; işçi yoksa süpürücü belirsiz kayda çevirir → mutabakata kadar bloke |
| İptal + işçinin kendi (vakasız) en kötü durum kaydı | **Release** (iptal eden müşteriye yansıtılmaz) |
| Sonucu belirsiz (`OutcomeUncertain`) | **bloke** — mutabakat kararına kadar. Karar `ConfirmedCharged` → **Consume**; `ConfirmedNotCharged` / `WrittenOff` → **Release** |
| İş kaydı yok (oturum silindi) | **Release** |
| Yeniden dene (`/retry`) | yeni iş → yeni Reserve (önceki iş zaten kapanmıştı) |

**Tek kapanış fonksiyonu:** `SettleJobAsync(jobId)` işin ŞU ANKİ durumuna göre karar verir (saf `Decide`, birim testli matris) ve idempotenttir. Çağıranlar: işçi (her işten çıkışta, `finally`), iptal ucu, hiç gönderilmemiş zaman aşımı kurtarması, maliyet kararı (`DecideAsync`), süpürücü (`SettleOpenAsync`, kendini onarma). Sıra önemsizdir; eşzamanlı 8 çağrı tek kapanış yazar.

**Eşzamanlılık:** firma başına `sp_getapplock` (`OrganizationCredits`) + tek transaction (hesap güncellemesi + defter girdisi). Kabulde kilit sırası: şube kabul kilidi → kredi kilidi (başka yerde ikisi birlikte alınmaz; kilitlenme çevrimi yok). 24 paralel rezervasyon / 7 kredi → tam 7 başarılı.

**İzolasyon:** hesap firma kimliğiyle anahtarlıdır; her sorgu firma kimliğiyle sınırlı; bir firma başkasının kredisini göremez/kullanamaz (testli).

## 4. Tarife (fiyat politikası — TİCARİ KARAR BEKLİYOR)

Gerçek kredi fiyatları/paketleri belirlenmediği için **fiyat uydurulmadı**. Güvenli varsayılan: **her ücretli üretim = 1 kredi**. Tarife yapılandırmayla değişir (`Credits` bölümü, kod değişikliği yok):

```json
"Credits": { "Enforce": true, "DefaultCostPerJob": 1,
  "Tariff": { "OutfitSet": 1, "OutfitSet:gpt-image-2.5": 2, "OutfitSet:gpt-image-2.5:high": 4, "FreeCombination": 3 } }
```

Anahtar `İşTipi[:modelAnahtarı[:kalite]]`, büyük/küçük harf duyarsız, **en özel eşleşme kazanır**; yoksa `DefaultCostPerJob`; `0` = ücretsiz. Değerler 0–1000 (uygulama açılışında doğrulanır). Tarife iş tipi/model/kalite boyutundadır (maliyeti belirleyen boyutlar); ürün/kategori ya da parça sayısı bazlı fiyat yoktur — bu ticari karar gelirse tarife anahtarları genişletilir. İşlem başında bedel işe (`credit_cost`) ve defterdeki Reserve girdisine (`tariff_key`) yazılır: tarife sonradan değişse de o iş için değişmez. `Credits:Enforce=false` kredi şartını kapatır (varsayılan açık: kredisiz ücretli üretim olmaz).

## 5. Operatör yönetimi

`dotnet SanalProva.Api.dll platform credits --org SLUG [(--grant N | --adjust ±N --note GEREKÇE) --ref REFERANS] [--confirm]`

- `--confirm` olmadan yazmaz (kuru çalışma); seçeneksiz yalnızca bakiyeyi gösterir.
- **`--ref` zorunlu**, 8–64 karakter, firma içinde benzersiz: aynı referans + aynı tür/tutar ikinci kez uygulanmaz (`DEĞİŞİKLİK YOK`, ikinci denetim yok); aynı referans farklı tutarla → reddedilir (çıkış 3). Eşzamanlı 8 aynı-referans yüklemesinde tam 1 uygulanır.
- Düzeltme gerekçe (`--note`) ister; bakiye rezerve tutarın (veya sıfırın) altına inemez.
- Denetim: `Platform.CreditGranted` / `Platform.CreditAdjusted` (kişisel veri yok).
- **Yetkisiz oluşturma yok:** kredi hesabını/defterini yazan TEK kod `CreditLedgerService`'dir (kaynak taraması testi); hiçbir HTTP ucu yükleme/düzeltme sunmaz (testli); `GET /credits` yalnızca Owner'a, yalnızca kendi firmasına.

## 6. API ve arayüz

- `GET /credits` (Owner): bakiye, rezerve, kullanılabilir, açık iş sayısı, kredi şartı uygulanıyor mu, tarife. `GET /credits/ledger?take&skip` (en yeni önce).
- Platform paneli işletme ayrıntısı: bakiye/bekleyen (salt okunur).
- React `/yonetim/kredi` (Owner; Ana sayfa menüsünde "Kredi"): kullanılabilir / bakiye / bekleyen kartları, tarife açıklaması, Türkçe işlem geçmişi (sayfalı). Yükleme/ödeme düğmesi yoktur. Yetersiz kredi Türkçe: "Yeterli krediniz yok, bu üretim başlatılamadı…" (`INSUFFICIENT_CREDITS`, 402).

## 7. M5.5 (ödeme) için temel — kod YOK

- `credit_ledger.source = Payment` ayrılmıştır; `reference` ödeme sağlayıcısının işlem kimliği için firma içinde benzersiz idempotency anahtarıdır (webhook tekrarı ikinci yükleme yapmaz).
- Ödeme entegrasyonu `CreditLedgerService.ApplyOperatorEntryAsync(... CreditEntryType.Grant, ..., CreditSource.Payment ...)` çağırır (aynı kilit, aynı idempotency, aynı denetim); fatura/ödeme kaydı ayrı tablo olarak M5.5'te tasarlanır. Para iadesi/iptali = ters `Adjustment`.

## 8. Bilinen sınırlar ve açık ticari kararlar

- Fiyat/paket, kredi son kullanma tarihi, para iadesi politikası, kredi devri, KDV/fatura: **belirlenmedi** (yukarıdaki varsayılan yalnızca güvenli yer tutucudur).
- Sonucu belirsiz işin kredisi, platform yöneticisi karar verene kadar bloke kalır (müşteri bu süre boyunca kullanılabilir krediyi eksik görür); mutabakat arayüzü hâlâ yalnızca API'dedir.
- Kredi rezervasyonu iş başınadır (iş içi yeniden denemeler aynı rezervasyonu kullanır).
- Personel (Staff/Manager) kalan krediyi göremez; yalnızca yetersizlikte hata alır. Kredi uyarı eşiği/e-posta yoktur.
- Defter silinemediği için test veritabanı sıfırlamaları (`ApiFactory`) tetikleyiciyi yalnızca atılabilir test veritabanında geçici kapatır.
- Kredi `Credits:Enforce` varsayılan açıktır; kredi yüklenmemiş mevcut firmalar gerçek moda geçtiğinde kredi yüklenene kadar üretim yapamaz (bilinçli, `PaidAiEnabled` ile aynı fail-closed yön).
