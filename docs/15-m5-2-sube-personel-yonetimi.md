# M5.2 — Şube ve personel yönetimi

Kapsam: Owner kendi firmasında şube yönetir, personel davet eder; personel tek kullanımlık bağlantıyla hesabını kurar; roller ve şube atamaları yönetilir; pasife alma ve yetki değişikliği anında etkili olur. Var olanlar (rol filtreleri, tenant filtreleri, kurulum token'ı, sözleşme zorlaması) yeniden yazılmaz, kullanılır.

## 1. Alt aşamalar

| Alt aşama | İçerik | Durum |
| --- | --- | --- |
| **M5.2a** | Şube CRUD + pasife alma (Owner); Owner satırlarının yeni şubeye yayılması; **her istekte çerez doğrulaması** (pasif/damga) | ✅ |
| **M5.2b** | Personel daveti (ilk-parola bağlantısı), kişi listesi, şube/rol ataması, kurallar (son Owner, Manager sınırı) | ✅ |
| **M5.2c** | Pasife alma/etkinleştirme, parola sıfırlama bağlantısı (`PasswordReset`), bağlantı yeniden üretme/iptal, açık SignalR bağlantısını kesme | ✅ |
| **M5.2d** | React yönetim ekranları (şubeler, personel) | ✅ (ayrı oturumda; commit eb7d34a) |
| M5.2e | React/Playwright testleri, belgeler, kapanış | bkz. rapor |

## 2. Rol modeli ve yetki sınırları

Rol, kullanıcı × şube bazındadır (`user_store_roles`). **Değişmez:** Owner firmadaki TÜM şubelerde Owner satırı taşır (yeni şube açılırken yayılır; `AppLocks.OrganizationMembers` kilidi altında). `OrganizationRoleFilter(Owner)` bu yüzden "firma Owner'ı" demektir.

| İşlem | Staff | Manager | Owner |
| --- | --- | --- | --- |
| Şube listesi / ekle / düzenle / pasife al | – | – | ✔ |
| Personel davet et | – | yalnızca kendi yönettiği şubelere, yalnızca **Staff** | ✔ (Staff/Manager/Owner) |
| Şube ataması / rol değişikliği | – | yalnızca kendi şubelerinde, Staff için | ✔ |
| Pasife alma / bağlantı yenileme | – | yalnızca tüm şubeleri kendi yönettiği şubeler olan Staff | ✔ |
| Son etkin Owner'ı düşürme/pasife alma | ✗ | ✗ | ✗ (409) |

Kendi rolünü yükseltme/kendini pasife alma engellenir. Manager hiçbir zaman Manager/Owner vermez, Owner'a dokunamaz.

## 3. M5.2a'da yapılanlar

- `GET/POST /management/stores`, `PUT /management/stores/{id}`, `PATCH /management/stores/{id}/status` (yalnızca Owner; başka firmanın şubesi 404).
- Şube kodu firmada benzersiz (büyük harfe çevrilir), saat dilimi doğrulanır, günlük kota 1–100 000 (maliyet sınırı `BudgetGuard` ayrıdır).
- Pasife alma: erişim hemen kesilir, şubenin TV eşleştirmeleri iptal edilir, **son etkin şube pasife alınamaz** (`LAST_ACTIVE_STORE`). Veri silinmez.
- **Çerez doğrulaması:** Identity varsayılanı güvenlik damgasını 30 dk'da bir denetler ve pasif hesabı hiç denetlemez. `ActiveUserCookieValidator` her istekte hesabın etkinliğini, firmasını ve damgasını okur (tek hafif sorgu); çerezi yeniden yazmaz. Sonraki aşamalarda pasife alma/şifre sıfırlama damgayı yeniler.
- Denetim: `Store.Created/Updated/Activated/Deactivated` (kişisel veri yok).

## 4. M5.2b/c'de yapılanlar

- `GET/POST /management/users`, `PUT /{id}/assignments`, `PATCH /{id}/status`, `POST|DELETE /{id}/setup-link` (Owner ve Manager; yetki `StaffManagementService`'te her işlemde güncel rollerden hesaplanır, tümü `OrganizationMembers` kilidi + tek transaction).
- **Davet:** hesap parolasız oluşturulur, 72 saatlik tek kullanımlık bağlantı (`InitialPassword` amacı; yeni enum/migration yok) yalnızca davet yanıtında görünür; liste yalnızca "açık bağlantı bitişi"ni gösterir. E-posta gönderimi yoktur: bağlantı yöneticiden kişiye iletilir.
- **Owner** firmadaki TÜM şubelerde Owner olur (Owner atanırsa sunucu tüm şubelere genişletir). Son etkin Owner düşürülemez/pasife alınamaz (`LAST_OWNER`); kendi rolünü/durumunu kimse değiştiremez.
- **Manager** yalnızca kendi şubelerine Staff ekler/çıkarır; Manager/Owner vermez, Manager/Owner'a dokunamaz; yalnızca TÜM şubeleri kendi şubeleri olan Staff'ı pasife alabilir. Başka şubelerdeki roller korunur.
- **Parola sıfırlama:** şifresi olan hesap için 24 saatlik bağlantı (`PasswordReset`); `/account/setup-password` ikisini de kabul eder: parola değişir, güvenlik damgası yenilenir (eski oturumlar düşer), kilit açılır, açık SignalR bağlantıları kesilir. Politika ihlalinde bağlantı yanmaz.
- **Pasife alma:** damga yenilenir, bekleyen bağlantılar iptal olur, açık SignalR bağlantıları kesilir; `StaffHub` pasif hesaba/şubeye yeni abonelik vermez.
- Denetim: `User.Invited/RolesChanged/Activated/Deactivated/SetupLinkIssued/SetupLinkRevoked` (e-posta, ad ve token yazılmaz).

## 5. Bilinen sınırlar

- SignalR kesme tek sunuculu bellek içi kayıttır (çok sunucuda backplane ile birlikte ele alınmalı).
- Rol değişikliği REST'te hemen etkilidir (roller her istekte okunur); açık SignalR grup üyelikleri yeniden bağlanana kadar sürebilir.
- Davet/yeniden üretim uç noktalarında sunucu tarafı idempotency yoktur (yeniden üretim önceki bağlantıyı iptal eder; tekrar davet 409 `EMAIL_TAKEN`).
- Firma pasifleştirme (platform) bu aşamanın kapsamı dışındadır; e-posta ile davet/sıfırlama sonraya kalmıştır.
