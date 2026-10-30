# M5.2 — Şube ve personel yönetimi

Kapsam: Owner kendi firmasında şube yönetir, personel davet eder; personel tek kullanımlık bağlantıyla hesabını kurar; roller ve şube atamaları yönetilir; pasife alma ve yetki değişikliği anında etkili olur. Var olanlar (rol filtreleri, tenant filtreleri, kurulum token'ı, sözleşme zorlaması) yeniden yazılmaz, kullanılır.

## 1. Alt aşamalar

| Alt aşama | İçerik | Durum |
| --- | --- | --- |
| **M5.2a** | Şube CRUD + pasife alma (Owner); Owner satırlarının yeni şubeye yayılması; **her istekte çerez doğrulaması** (pasif/damga) | ✅ |
| M5.2b | Personel daveti (kurulum bağlantısı, `Invitation` amacı), kişi listesi/ayrıntı, şube ataması, rol değişikliği, kuralları (son Owner, Manager sınırı) | bekliyor |
| M5.2c | Pasife alma/yeniden etkinleştirme, parola sıfırlama bağlantısı (`PasswordReset`), bağlantı yeniden üretme/iptal | bekliyor |
| M5.2d | React yönetim ekranları (şubeler, personel) | bekliyor |
| M5.2e | İzolasyon/E2E testleri, belgeler, kapanış | bekliyor |

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

## 4. Bilinen sınırlar

- Açık SignalR bağlantıları yetki değişince hemen kopmaz (yeni bağlantı ve her REST isteği denetlenir).
- Firma pasifleştirme (platform) M5.2 kapsamı dışındadır.
