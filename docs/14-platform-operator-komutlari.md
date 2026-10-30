# 14 — Platform operatör komutları ve bilinen sınırlar (M5.1g)

> Durum: **uygulandı ve yerelde doğrulandı (M5.1g/h/i).** CI sonucu kullanıcı tarafından kontrol edilecek. Kapsam: ilk platform yöneticisinin güvenli oluşturulması, bağlantı yeniden üretme/iptal/erişim kurtarma, firma bazında ücretli AI yetkisi. Tasarım kaynağı: [13-m5-1-kontrollu-onboarding](13-m5-1-kontrollu-onboarding.md) §2.4–2.5.

## 1. Güvenlik modeli

- Komutlar **HTTP'den erişilemez**: `dotnet SanalProva.Api.dll platform <komut> ...` komut satırı argümanlarıyla çalışır, **web sunucusu (Kestrel) hiç başlatılmaz** ve işlem bitince süreç sonlanır.
  Hiçbir uç nokta/hub bu kodu çağırmaz (mimari testle sabit: `PlatformCommandsTests`). Platform yöneticisi bayrağını (`IsPlatformAdmin = true`) yazan **tek yer** bu komut sınıfıdır (kaynak taraması testi).
- **Yetki = veritabanı bağlantı bilgisine sahip olmak** (`ConnectionStrings__Main` ortam değişkeni). Bu bilgi hiçbir API ile elde edilemez; operatör sunucuda/konteynerde komutu çalıştırır.
- **`--confirm` olmadan hiçbir veritabanı yazması yapılmaz** (yalnızca okuma + `PLAN` çıktısı, çıkış kodu 0). Hata/geri alma durumunda yarım kayıt kalmaz (tek transaction).
- Kurulum bağlantısı **yalnızca standart çıktıya bir kez** yazılır; günlüğe, dosyaya, denetim kaydına veya veritabanına düz olarak girmez (veritabanında yalnızca `HMAC-SHA256(Security:SetupTokenKey, token)`). Operatör çıktıyı ekranda görür; **kabuk geçmişine/ekran kaydına dikkat edin**
  (bağlantı 72 saat geçerli ve tek kullanımlıktır; ele geçirilirse `revoke` ile iptal edin).
- `Security:SetupTokenKey` yoksa/32 karakterden kısaysa komut **token üretmez** (çıkış 1). Denetim kayıtları kişisel veri ve token içermez; operatör kimliği bilinmediği için `UserId` boştur (kim çalıştırdığı sunucu/işletim sistemi günlüklerindedir).

## 2. Komutlar

Ortak: `--confirm` (yazmak için), `--base-url https://panel.ornek.com` (bağlantıyı mutlak yapar; yoksa `/hesap/sifre-belirle#t=...` göreli yazılır).

| Komut | Ne yapar |
|---|---|
| `platform bootstrap --email E --name AD [--additional] [--base-url U] [--confirm]` | `platform` firmasını (yoksa) ve parolasız yöneticiyi oluşturur (`IsPlatformAdmin`, hiçbir firmada mağaza rolü yok), 72 saatlik tek kullanımlık şifre bağlantısı basar. **Aktif yönetici varsa reddeder** (çıkış 3); ikinci yönetici yalnızca `--additional` ile. E-posta sistem genelinde benzersizdir. |
| `platform reissue-link --email E [--confirm]` | Şifresini henüz belirlememiş yöneticinin önceki açık bağlantılarını iptal eder, yenisini üretir. Şifre belirlenmişse reddeder (→ `reset-access`). |
| `platform revoke --email E [--confirm]` | Yöneticinin açık kurulum bağlantılarını iptal eder (sızıntı şüphesi). |
| `platform reset-access --email E [--confirm]` | **Erişim kurtarma**: parolayı siler, güvenlik damgasını yeniler, yeni 72 saatlik bağlantı üretir. Yeni HTTP yüzeyi açmaz (mevcut `setup-password` ucunu kullanır). Açık oturum çerezleri sonraki damga denetiminde (varsayılan ≤ 30 dk) düşer. |
| `platform paid-ai --org SLUG (--enable \| --disable) [--confirm]` | Firmanın **ücretli AI yetkisini** değiştirir; `Platform.PaidAiEnabled` / `Platform.PaidAiDisabled` denetim kaydı yazar. Zaten o durumdaysa değişiklik/denetim yok. Global kapılar (gerçek mod, `AllowPaidCalls`, API anahtarı, bütçe) bundan bağımsız aynen geçerlidir. |
| `platform budget --org SLUG [--daily USD\|none] [--monthly USD\|none] [--job USD\|none] [--confirm]` | **Firma AI bütçe sınırları** (M5.3): günlük/aylık/iş başı. Değer platform tavanını (`Generation:Budget`) aşamaz (çıkış 3); `none` firma değerini siler; seçeneksiz yalnızca gösterir; `Platform.BudgetChanged` denetim kaydı. Etkin sınır = min(tavan, firma). Ayrıntı: [docs/16](16-m5-3-butce-ve-mutabakat.md). |
| `platform credits --org SLUG [(--grant N \| --adjust ±N --note GEREKÇE) --ref REFERANS] [--confirm]` | **Firma kredisi** (M5.4): bakiye gösterir; yükler/düzeltir. `--ref` zorunlu ve firma içinde benzersiz (idempotency: aynı referans ikinci kez uygulanmaz, farklı tutarla reddedilir); düzeltme gerekçe ister ve bakiye rezerve tutarın altına inemez; `Platform.CreditGranted/CreditAdjusted` denetim kaydı. Ayrıntı: [docs/17](17-m5-4-kredi-sistemi.md). |

Çıkış kodları: 0 başarılı/kuru çalışma, 1 başarısız (yapılandırma/DB), 2 kullanım hatası, 3 reddedildi (iş kuralı), 4 bulunamadı.

Örnek (ilk kurulum, yeni oluşturulmuş yalıtılmış veritabanında):

```powershell
$env:ConnectionStrings__Main = '...'   # operatör bağlantısı; dosyaya yazılmaz
$env:Security__SetupTokenKey  = '...'  # en az 32 karakter
dotnet SanalProva.Api.dll platform bootstrap --email yonetici@ornek.com --name "Platform Yöneticisi"            # kuru çalışma
dotnet SanalProva.Api.dll platform bootstrap --email yonetici@ornek.com --name "Platform Yöneticisi" --confirm  # yazar, bağlantıyı bir kez basar
```

> Proje kuralı: `sanalprova_dev` M5'e kadar boş kalır; komutu **yalnızca** izole lab/test/e2e veritabanlarına karşı çalıştırın (ve kullanıcı izniyle).

## 3. Erişim kurtarma değerlendirmesi

- Tek yönetici parolasını kaybederse: sunucu erişimi olan operatör `reset-access` çalıştırır (e-posta ile sıfırlama M5.2'de; şimdilik bağlantı operatörden iletilir).
- Tüm yöneticiler kilitlenirse: `bootstrap` aktif yönetici varken reddeder. Önce `reset-access` (aktif hesap) kullanılır; hesap pasifse veritabanı yöneticisi bilinçli olarak `--additional` ile yeni yönetici açar.
- Operatör kimliği doğrulaması (MFA) bu aşamada yoktur; yetki sunucu/DB erişimidir. Üretimde komutu çalıştırabilen kişiler en az sayıda tutulmalı ve işletim sistemi/konteyner erişim günlükleri tutulmalıdır.

## 4. Sözleşme sürümü değişirken açık SignalR bağlantıları

- Sözleşme kapısı ara katmandır; **bağlantı kurulurken** (negotiate/WebSocket) uygulanır. Açık bir `StaffHub` bağlantısı sürüm değişince kopmaz.
- Hub'ın istemciden çağrılabilen yöntemleri yalnızca `JoinSession/LeaveSession/JoinStore/LeaveStore`'dur; sunucu→istemci mesajları yalnızca **kimlik + durum** taşır (`JobUpdated`: iş/oturum kimliği, durum, aşama, sonuç görsel kimliği, hata kodu; `DisplayStatusChanged`). **Görsel/müşteri verisi hub'dan geçmez**; görseller yalnızca REST ve imzalı URL ile alınır ve REST, kabul edilmemiş firmada kapalıdır.
- **Önlem (M5.1g):** `JoinSession`/`JoinStore` artık güncel sürümün kabulünü denetler; yeni sürümde **yeni abonelik verilmez**, yeni bağlantı kurulamaz (`AgreementTests`). Mevcut abonelikler bağlantı kapanana kadar yalnızca kimlik/durum bildirimi almaya devam eder (kalan sınır).
- **Canlıya çıkış önerisi:** sözleşme metni değişikliği bir kod dağıtımıdır; dağıtımda uygulama yeniden başlar ve tüm bağlantılar kopar, yeniden bağlanma kapıdan geçer. Dağıtım sırasında kısa süreli çoklu-sunucu (rolling) penceresi kalır. Hub bağlantılarında periyodik yeniden yetkilendirme (devre dışı bırakılan kullanıcı, iptal edilen rol dahil) yoktur: bu mevcut bir sınırdır; gerekirse kısa ömürlü bağlantı veya sunucu tarafı `Abort` tasarlanmalıdır.

## 5. `PaidAiEnabled` denetiminin yarış koşulu sınırları

Kapılar: (1) kabul (`/generations`, `/retry`; gerçek modda yetkisiz → 403, iş kuyruğa girmez), (2) `BudgetGuard.ReserveAsync` içinde, rezervasyon/bütçe kilidinden ve sağlayıcı çağrısından **önce**, fail-closed. Değer her denetimde veritabanından okunur (önbellek yok; çok sunuculu ortamda da tutarlı, okuma commit edilmiş en güncel değeri görür).

Bilinen sınırlar:

1. **Okuma–gönderim penceresi:** ikinci kapı okuması ile sağlayıcıya gönderim arasında (bütçe kilidi, sorgular, rezervasyon yazımı, gönderim kapısı) milisaniyeler–on milisaniyeler vardır. Operatör bu aralıkta `--disable` ederse **o iş** yine gönderilebilir. Etki iş başına en kötü durum maliyetiyle (`MaxJobCostUsd`) ve günlük/aylık bütçeyle sınırlıdır; eşzamanlı işler için en çok `MaxParallelJobs × çağrı` mertebesindedir.
2. **Sürmekte olan çağrı iptal edilmez:** kapatma, zaten sağlayıcıya gönderilmiş isteği durdurmaz (ücret doğabilir; zaten rezervasyon + maliyet kaydı vardır).
3. **Sorgulama devamı:** önceden gönderilmiş, kimliği bilinen tahminin sonucu sorgulanırken (`resume`) yeni rezervasyon/ücretli gönderim yapılmaz; bu yol firma kapısını yeniden okumaz (yeni ücret doğurmaz).
4. **Kabul–işleme arası:** kuyrukta bekleyen iş, kapatma sonrası işlenirken ikinci kapıdan döner (`PaidAiNotEnabled`, kalıcı, yeniden denenmez, ücret/rezervasyon yok) — testli.
5. **Etkinleştirme** anında geçerlidir; kuyrukta kalıcı olarak reddedilmiş işler otomatik yeniden denenmez (personel "yeniden dene" kullanır).
6. Bayrak firma yetkisidir, harcama sınırı değildir: bütçe kapıları (`Enforce`, günlük/aylık/iş) ayrıca zorunludur. M5.4'te kredi bakiyesi bu bayrağın yanına/yerine geçecektir.
7. Bozuk/eksik ayar JSON'u: serbest kombin yolunda kapı kapalı sayar (`PaidAiNotEnabled`); hazır takım (Fashn) yolunda ayar daha erken okunup patlar ve iş yine hiçbir çağrı/ücret olmadan başarısız olur.

## 6. Arayüz (M5.1h): platform paneli, şifre belirleme, sözleşme ekranı

| Rota | Kim | Ne yapar |
|---|---|---|
| `/platform` | yalnızca platform yöneticisi | İşletme listesi; kurulum (şifre bekleniyor/tamamlandı), sözleşme (kabul edildi/bekliyor) ve ücretli AI (açık/kapalı) durumu. |
| `/platform/yeni` | platform yöneticisi | Firma + ilk şube + Owner formu. **Kurulum bağlantısı yalnızca ilk yanıtta** bu sayfanın yerel durumunda gösterilir; kapatılınca/sayfadan çıkılınca/yenilenince bir daha gösterilmez. |
| `/platform/:id` | platform yöneticisi | İşletme özeti (müşteri verisi yok) ve Owner kurulum bağlantısını yeniden üretme. Ücretli AI **bu ekrandan değiştirilemez** (yalnızca operatör komutu). |
| `/hesap/sifre-belirle` | anonim | 72 saatlik tek kullanımlık bağlantıyla şifre belirleme. Token URL fragment'ından (`#t=`) alınır ve **hemen adres çubuğundan silinir**. |
| `/sozlesme` | giriş yapmış herkes | Hizmet sözleşmesi: **yalnızca Owner** kabul eder; diğer personel Owner onayını bekler (ekran kendiliğinden ilerler). |

Yönlendirme (sunucudaki kapıların arayüz yansımasıdır; karar sunucudadır): girişsiz → `/giris`; platform yöneticisi → `/platform` (mağaza ekranlarına gidemez); sözleşme kabul edilmemiş → `/sozlesme`;
yetkisiz kullanıcı `/platform*` açarsa ana sayfaya döner. Sunucu herhangi bir istekte `403 AGREEMENT_ACCEPTANCE_REQUIRED` verirse (ör. yeni sürüm) oturum bilgisi yenilenir ve kullanıcı `/sozlesme`ye yönlenir.
Hizmet sözleşmesi ile müşteri fotoğraf (KVKK) onayı ekranda da ayrı tutulur: sözleşme ekranı açıkça "müşteri onayı ayrıdır ve her provada ayrıca alınır" der; müşteri onayı akışına dokunulmadı.

### Token ve bağlantı güvenliği (istemci)

- Token yalnızca **bellekte** tutulur (`setupToken.ts` modül değişkeni + bileşen durumu): `localStorage`, `sessionStorage`, TanStack Query/Mutation önbelleği, çerez veya günlük **yoktur**. Başarıdan/geçersizlikten sonra bellekten de silinir.
- Token taşıyan çağrılar (oluşturma, yeniden üretme, şifre belirleme) bilerek `useMutation` kullanmaz (mutation önbelleği değişkenleri/sonucu bir süre tutar); doğrudan `apiFetch` ile yapılır. Token yalnızca istek gövdesindedir; URL'de, sorgu dizesinde veya Referer'da bulunmaz.
- Geçersiz/kullanılmış/iptal/süresi dolmuş/bilinmeyen bağlantı **tek ve aynı mesajı** gösterir; neden açıklanmaz.
- Tarayıcı geçmişi/ekran görüntüsü ve panodaki bağlantı yönetici sorumluluğundadır: bağlantı ekranda düz metin görünür (operatör iletmek zorundadır). Sızıntıda `revoke`/yeniden üretme ile iptal edin.

### Bağlantı yeniden üretmede çift tıklama — sunucu idempotency'sinin YERİNE GEÇMEZ

`POST /platform/organizations/{id}/owner/setup-link` için sunucuda `Idempotency-Key` yoktur (docs/13 §2.1'deki gereksinim, şema değiştirmemek için bu aşamaya bırakıldı). Arayüzdeki koruma (onay penceresi + istek sürerken `ref` kilidi + devre dışı düğme)
yalnızca **istemci tarafı** çift tıklamayı engeller. Şunlara karşı koruma **yoktur**: ağ hatasından sonra isteğin kendiliğinden/elle yeniden denenmesi, iki sekme/iki yönetici, tarayıcı eklentileri. Sonuç: her başarılı çağrı yeni bağlantı üretir ve öncekini iptal eder;
yanıt kaybolursa yönetici yeniden üretir. Firma oluşturma ise sunucu tarafında idempotent'tir (`Idempotency-Key`; arayüz form açılışında üretir, belirsiz sonuçta aynı anahtarla yeniden gönderir).
