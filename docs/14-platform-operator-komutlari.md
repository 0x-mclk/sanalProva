# 14 — Platform operatör komutları ve bilinen sınırlar (M5.1g)

> Kapsam: ilk platform yöneticisinin güvenli oluşturulması, bağlantı yeniden üretme/iptal/erişim kurtarma, firma bazında ücretli AI yetkisi. Tasarım kaynağı: [13-m5-1-kontrollu-onboarding](13-m5-1-kontrollu-onboarding.md) §2.4–2.5.

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
