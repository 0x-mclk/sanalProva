# 06 — Smart TV ve SignalR Mimarisi

## 1. Tasarım ilkeleri

1. **Sunucu-otoritatif durum.** TV'nin ne gösterdiği `display_devices.display_state` alanındadır. SignalR mesajı yalnızca "durum değişti, sürüm N" der; TV içeriği REST'ten çeker. Böylece kaçırılan mesaj, sıra karışması veya yeniden bağlanma sorunu durum kaybına yol açmaz.
2. **Görsel içeriği SignalR'dan geçmez.** Sadece kimlikler/sürüm; görseller kısa ömürlü imzalı URL ile yüklenir.
3. **TV kendi oturumu dışındaki hiçbir görseli istemez/alamaz.** Yetki kontrolü sunucuda: TV token'ı → cihaz → `current_session_id` → yalnızca o oturumun görselleri.
4. **İki aşamalı bağ:** (a) *Cihaz kaydı* — TV'nin mağazaya bir kez kaydı (Manager). (b) *Oturum bağlama* — her müşteri için personelin TV'yi oturuma bağlaması (Staff).

## 2. Eşleştirme (cihaz kaydı) akışı

```
 TV (/tv)                         API                              Yönetici (PWA)
   │ localStorage'da deviceToken?   │                                    │
   │── yoksa: POST /display/pairing ▶│ kod (6 karakter, belirsiz          │
   │◀─ { code, pairingSecret, exp } ─│ karakterler hariç: 0/O, 1/I)       │
   │  ekranda "K7M-4QX" + QR         │ hash'leri kaydet, 5 dk geçerli     │
   │                                 │                                    │
   │── her 3 sn GET /display/pairing/status (Pairing <secret>) ──▶       │
   │                                 │◀─ POST /displays/claim {code,name} ─│
   │                                 │  kod doğru + süresi geçmemiş +     │
   │                                 │  aynı firma? → display_devices     │
   │                                 │  oluştur, rastgele 256-bit token,  │
   │                                 │  hash'ini sakla                    │
   │◀─ { status: Claimed, deviceToken } (yalnızca BİR kez döner)          │
   │ token → localStorage            │                                    │
   │── SignalR /hubs/display (Bearer token) ──▶                           │
   │ bekleme ekranı                  │                                    │
```

**Güvenlik önlemleri**
- Kod tek kullanımlık; 5 dakikada sona erer; IP başına istek sınırı; kod başına 5 hatalı denemede iptal.
- Kodu bilmek tek başına yetmez: claim isteği **giriş yapmış Manager** gerektirir. Kod yalnızca "bu ekranı seçiyorum" anlamındadır.
- `pairingSecret` sayesinde kodu ekrandan okuyan başka bir cihaz token'ı alamaz (token yalnızca secret sahibine döner).
- Cihaz token'ı iptal edilebilir (`DELETE /displays/{id}`) → hub'da `Revoked()` → TV eşleştirme ekranına döner.
- QR içeriği: `https://<app>/tv-cihazlar/ekle?code=K7M4QX` — personel telefonu ile okutunca kod alanı dolar.

## 3. Oturum bağlama ve içerik gönderme

```
Personel: PUT /displays/{tvId}/session { sessionId }
  → kontroller: TV aynı mağazada mı, iptal edilmemiş mi, oturum açık mı,
                TV başka açık oturuma bağlı mı (→ 409, "devral" seçeneği)
  → display_devices.current_session_id = sessionId, display_state = { mode: "idle" }
  → state_version++  → DisplayHub: group "display:{tvId}" → StateChanged(v)

Personel: PUT /displays/{tvId}/state { mode: "single", imageIds: [img7], expectedVersion: 12 }
  → img7.session_id == current_session_id ?   değilse 403
  → expectedVersion uyuşmazsa 409 (iki personel aynı anda değiştirdiyse)
  → state_version = 13 → StateChanged(13)

TV: StateChanged(13) alır → GET /display/state → { version: 13, mode, images: [{ url (5 dk), label }] }
  → görseli önceden yükle (decode) → çapraz geçişle göster
```

**Yanlış TV'ye gitmemesi için katmanlar**
1. Gruplar cihaz bazlı (`display:{deviceId}`), oturum bazlı değil; bir mesajın gideceği tek bir hedef vardır.
2. İçerik mesajda yok; TV her zaman kendi token'ıyla, kendi oturumunun görsellerini ister.
3. `PUT /state` görselin bağlı oturuma ait olduğunu doğrular.
4. Oturum kapanınca / silinince TV durumu `idle`'a çekilir ve `Cleared()` gönderilir.
5. Personel arayüzü "TV'ye gönder" öncesi hedef TV adını ve küçük önizlemesini gösterir.
6. `audit_logs`: `Display.Assigned`, `Display.StateChanged`.

## 4. Hub tanımları

**`/hubs/display`** — kimlik: `DeviceTokenAuthenticationHandler` (Bearer). Bağlanınca sunucu `display:{deviceId}` grubuna ekler, `last_seen_at` günceller.

| Yön | Mesaj | İçerik |
|---|---|---|
| S → TV | `StateChanged` | `{ stateVersion }` |
| S → TV | `Cleared` | — |
| S → TV | `Revoked` | — |
| TV → S | `Heartbeat` | `{ stateVersion }` (30 sn) — sunucu sürüm farkı görürse tekrar `StateChanged` gönderir |

**`/hubs/staff`** — kimlik: cookie. İstemci `JoinSession(sessionId)` çağırır; sunucu kullanıcının o oturumun mağazasına yetkisini doğrular ve `session:{id}` grubuna ekler. Mağaza genel olayları için `store:{storeId}`.

| Yön | Mesaj | İçerik |
|---|---|---|
| S → P | `JobUpdated` | `{ jobId, sessionId, status, stage, resultImageId?, error? }` |
| S → P | `SessionUpdated` | `{ sessionId }` |
| S → P | `DisplayStatusChanged` | `{ displayId, online, currentSessionId }` |

Çoklu sunucu örneği gerekirse (ileride): SignalR backplane olarak Redis veya Azure SignalR — mimari değişmez.

## 5. Yeniden bağlanma

| Durum | Davranış |
|---|---|
| Kısa kopma | `withAutomaticReconnect([0, 2000, 5000, 10000, 30000])`; yeniden bağlanınca **her zaman** `GET /display/state` |
| Uzun kopma | Son deneme sonrası sonsuz döngü: 30 sn'de bir dene; ekranda küçük "bağlantı bekleniyor" göstergesi; **son görsel 2 dk sonra gizlenir** (müşteri gittiyse ekranda kalmasın) |
| Token geçersiz (401) | localStorage temizlenir, eşleştirme ekranına dönülür |
| Sayfa yenilendi / TV kapanıp açıldı | Token localStorage'da → doğrudan bağlan → durumu çek |
| WebSocket engelli | SignalR otomatik olarak SSE / long polling'e düşer |
| Personel tarafı kopma | Üretim durumu `GET /jobs/{id}` polling'e düşer (5 sn) |

Çevrimiçi durumu: Sunucu `OnConnected/OnDisconnected` + heartbeat ile `last_seen_at` tutar; personel arayüzü TV'yi 60 sn heartbeat yoksa "çevrimdışı" gösterir.

## 6. Otomatik temizleme

- Oturum kapanınca: anında `idle`.
- TV'de son durum değişikliğinden sonra **X dk** (varsayılan 15) işlem yoksa: `idle` (arka plan işi).
- Saklama süresi dolan oturum silinirken: bağlı TV `idle`.

## 7. TV arayüzü notları

- 1920×1080 ve 3840×2160 için tasarım; 10-foot UI: büyük tipografi, kenarlarda %5 güvenli alan (overscan).
- Görsel modları: `idle` (marka ekranı), `single` (görsel + ürün adları), `compare` (iki görsel yan yana, "Önce / Sonra" veya sürüm etiketleri).
- Görseller ön yüklenir, ardından 300 ms çapraz geçiş; yarım yüklenmiş görsel gösterilmez.
- TV'de girdi gerekmez (uzaktan kumanda zorunlu değil).
- Ekran koruyucu/uyku: uygun tarayıcılarda Wake Lock API; desteklenmiyorsa TV ayarı dokümante edilir.
- **Erken risk testi (Aşama 3 başı):** Mağazadaki gerçek TV modelinde WebSocket, ES2019, `localStorage` ve tam ekran davranışı.

## 8. Aşama 3 uygulama notları

Aşama 3'te yukarıdaki tasarıma göre yapılan **sapmalar ve eklemeler** (kod: `backend/src/SanalProva.Infrastructure/Displays`, `backend/src/SanalProva.Api/Features/Display`, `Hubs/Hubs.cs`, `frontend/src/tv`).

**Eşleştirme**
- Kod **6 haneli sayıdır** (`482 913`), alfasayısal değil (TV'de okunup telefonda yazılması kolay). Az sayıda olası değer olduğu için kod **sunucu anahtarıyla HMAC'lenerek** saklanır; hatalı tahmin bir kayda bağlanamadığı için deneme sınırı **kullanıcı başınadır**: art arda 5 hatalı kod → 15 dk kilit (`CLAIM_LOCKED`, `429`). Ek olarak IP başına hız sınırları (kod üretme, durum yoklama, kod girme) ve bekleyen kod üst sınırı (200) vardır.
- Eşleştirme **tek kullanımlıktır**: kullanım koşullu `UPDATE` ile atomiktir; aynı kodu aynı anda kullanan iki yöneticiden yalnızca biri başarılı olur. Geçersiz, süresi dolmuş, kullanılmış ve var olmayan kod aynı yanıtı verir (`PAIRING_CODE_INVALID`).
- **Cihaz belirteci = TV'nin eşleştirme başında aldığı gizli değer.** Claim anında bu değerin SHA-256'sı cihaza taşınır; durum yoklamasında belirteç ayrıca dönmez. Böylece düz belirteç sunucuda hiçbir yerde saklanmaz ve "bir kez döner" sorunu (claim, TV'den farklı bir istekte olur) ortadan kalkar. Kodu ekrandan okuyan biri belirteci alamaz; eşleşmeden önce belirteç hiçbir yere kabul edilmez.
- QR **uygulanmadı** (ek bağımlılık gerektirir). Personel ekranı `/tv-cihazlar?kod=123456` bağlantısını destekler.

**Kimlik ve yetki**
- TV için ayrı kimlik şeması (`DeviceToken`): `Authorization: Bearer`; SignalR (tarayıcı WebSocket/SSE başlık gönderemez) için hub yolunda `access_token` sorgu parametresi. Şema yalnızca `DisplayDevice` politikalı uç noktalarda (`GET /display/state`, `/hubs/display`) geçerlidir; personel çerezi bu uç noktalara ve TV belirteci personel API'sine/hub'ına **giremez** (testli).
- Cihaz kimliği her zaman doğrulanmış belirteçten gelir; hiçbir istekte/hub çağrısında istemci kimlik göndermez. `GET /display/state` yalnızca cihazın `current_session_id`'sindeki görselleri döner; görseller kısa ömürlü (5 dk) imzalı bağlantıdır.
- Personel tarafı: `X-Store-Id` + şube rolü her istekte; TV ekleme/kaldırma/yeniden adlandırma **Manager+**, bağlama/gönderme/temizleme/bağlantı kesme **Staff+**. `StaffHub.JoinStore` rolü sunucuda doğrular.

**Durum, sürüm ve olaylar**
- Tek yazma noktası `DisplayStateService`: her değişiklik `state_version`'ı artırır (eşzamanlılık belirteci; çarpışmada yeniden dener, `expectedVersion` verilmişse `409 STATE_CONFLICT`). Sürüm hiç geri gitmez.
- `StateChanged({ stateVersion, reason })`, `reason`: `SessionAttached, ImageSent, ImageChanged, CompareOpened, CompareClosed, Cleared, SessionEnded, SessionDetached, PrivacyCleared, IdleTimeout, DeviceRevoked`. Ekran boşaldığında ayrıca `Cleared()` gider (TV görseli REST'i beklemeden gizler). Personel grubuna (`store:{id}`) `DisplayStatusChanged` gider (çevrimiçi/çevrimdışı dahil).
- TV tarafı koruması (`tvSync.ts`): eldekinden küçük sürüm yok sayılır; `Cleared`/`Revoked` olayında uçuştaki çekmeler atılıp yeniden çekilir (yavaş eski yanıt görseli geri getiremez). Yeniden bağlanınca her zaman `GET /display/state`; hub yokken 15 sn'de bir REST yoklaması; 30 sn'de bir `Heartbeat` (sürüm farkı varsa sunucu yeniden bildirir); sunucuyla 2 dk temas yoksa görsel gizlenir.
- Gösterilebilen görseller: bağlı oturumun **DEMO/AI sonuçları** ve **orijinal fotoğrafı** (önce/sonra karşılaştırması için).

**Gizlilik temizliği (sunucuda, tek noktadan)**
- Oturum kapanışı → TV boşalır **ve** bağlantı kopar (`SessionEnded`). Oturum silme, onay geri alma, saklama süresi dolması (`SessionPurger`, dosya silmeden **önce**) → aynı.
- Fotoğraf silme veya yeniden çekim → o oturumun TV görüntüsü temizlenir, oturum bağlı kalır (`PrivacyCleared`). Sonuç görselleri orijinal fotoğraftan türetildiği için, silinen fotoğraf sonrası ekranda hiçbir şey kalmaz (temkinli yorum).
- Boşta kalma: durumu 15 dk değişmeyen TV'nin görseli temizlenir (`DisplayMaintenanceJob`, `Display:IdleClearMinutes`).
- Eşleştirme kaldırma (Manager) → belirteç anında geçersiz, `Revoked()` gider, ekran boşalır, TV eşleştirme ekranına döner. Açık hub bağlantısı bir sonraki heartbeat'te de reddedilir.
- Müşteri görselleri (`/files/...`) `Cache-Control: private, no-store` ile servis edilir; TV durum yanıtı `no-store`; TV `credentials: omit` ve `cache: no-store` ile istek atar, yerel depolamada yalnızca cihaz belirteci vardır. İmzalı bağlantılar ve belirteçler günlüğe yazılmaz (istek günlüğü yalnızca yolu içerir, sorgu dizesi yok).

**TV tarayıcı uyumluluğu**
- Derleme hedefi ES2019; CSS'te flex `gap`, `aspect-ratio`, `inset` kullanılmaz. **CSS değişkenleri gerekir (Chromium ≥ 49; pratikte ≥ 56)**. `tv.html` bağımlılıksız yedek mesaj içerir: ES modül desteği yoksa (`nomodule`), 10 sn içinde uygulama açılmazsa veya `fetch`/CSS değişkeni/`localStorage` yoksa "Bu TV tarayıcısı desteklenmiyor" gösterilir.
- WebSocket zorunlu değildir: SignalR WebSocket → SSE → long polling'e düşer (E2E testinde WebSocket'siz tarayıcıyla doğrulandı). Uzaktan kumanda: giriş gerekmez; **Tamam/Enter** yalnızca durumu yeniler.
- **Gerçek TV'de denenmedi** (3.0 açık).

**Tek sunucu sınırı / çoklu sunucu gereksinimleri**
İlk sürüm tek API örneğiyle çalışır. Birden fazla örnek için: (1) SignalR **backplane** (Redis veya Azure SignalR) — aksi halde bir örnekte yapılan değişikliğin bildirimi başka örnekteki TV'ye ulaşmaz (durum veritabanında doğru kalır, TV en geç 15 sn'lik yoklamada alır); (2) `DisplayPresence` (çevrimiçi durumu) ve `ClaimAttemptLimiter` (hatalı kod sayacı) bellek içidir → `last_seen_at` ve paylaşılan depoya taşınmalı; (3) IP hız sınırları örnek başınadır. Sunucu önünde reverse proxy WebSocket/SSE'yi (`/hubs`) ve uzun bağlantıları desteklemelidir.
