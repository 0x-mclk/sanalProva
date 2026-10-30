# 13 — M5.1 Kontrollü Onboarding: teknik plan

> Durum: **tasarım onaylandı, uygulama ayrı onay bekliyor.** Ürün kodu, migration ve veritabanları bu belgeyle değişmez.
> Kapsam: platform yöneticisinin güvenli ilk ataması, firma + ilk şube + Owner oluşturma, tek kullanımlık şifre belirleme bağlantısı,
> Owner'ın sürümlü hizmet sözleşmesi kabulü. Mevcut tenant, kimlik, rol, katalog ve üretim akışları **yeniden yazılmaz**, yalnızca eklenir.

## 1. Onaylanan kararlar

| # | Karar |
|---|---|
| D1 | İlk şifre belirleme bağlantısı **72 saat** geçerli ve **tek kullanımlık** (süre yapılandırılabilir, kodda sabit üst sınır). |
| D2 | Owner hizmet sözleşmesini kabul edene kadar **firma genelinde ticari işlemler engellenir** (sunucuda). |
| D3 | Hizmet sözleşmesi önce **taslak**; canlıya çıkmadan **hukuki kontrol** zorunlu. |
| D4 | E-posta adresi **tüm sistemde benzersiz** (veritabanı düzeyinde zorlanır). |
| D5 | Firma pasifleştirme **M5.2**'de. |
| D6 | Halka açık kayıt yok; işletmeleri **platform yöneticisi** oluşturur. |

## 2. Kesinleşen güvenlik noktaları

### 2.1 Idempotency tekrarında token sızmaz
- `POST /api/v1/platform/organizations` **`Idempotency-Key` zorunlu**. Anahtar ve istek parmak izi (SHA-256: firma adı, slug, şube, Owner e-postası, görünen ad) **firma satırında** saklanır:
  `organizations.created_by_user_id`, `creation_request_key`, `creation_request_hash`; filtreli benzersiz indeks `(created_by_user_id, creation_request_key)`.
- **Yanıtlar hiçbir yerde saklanmaz** (yanıt önbelleği / idempotency yanıt tablosu yok). Tekrar aynı anahtar + aynı parmak izi: `200` ile **aynı kimlikler**, `setupLink: null`, `setupLinkShown: false`
  ("Bağlantı yalnızca ilk yanıtta gösterilir; gerekirse yeniden üretin"). Aynı anahtar + farklı gövde: `422 IDEMPOTENCY_KEY_REUSED` (mevcut sözleşmeyle aynı kod).
- Düz token **yalnızca** ilk 201 yanıtının gövdesinde bir kez bulunur: `Cache-Control: no-store`, `Pragma: no-cache`. Veritabanında yalnızca `HMAC-SHA256(Security:SetupTokenKey, token)`.
- Token hiçbir günlüğe, denetim kaydına, hata yanıtına veya OpenAPI örneğine girmez; istek günlüğü zaten yalnızca yolu yazar, bağlantıda token **fragment**'tadır (`/hesap/sifre-belirle#t=…`) ve sunucuya hiç gitmez.
- Yeniden üretim (`POST /platform/organizations/{id}/owner/setup-link`) önceki açık token'ları aynı transaction'da iptal eder; ayrı `Idempotency-Key` ister, tekrarında **yeni token üretmez ve göstermez** (aynı kural).

### 2.2 Token tüketimi atomik, hata halinde rollback
`POST /api/v1/account/setup-password` tek bir veritabanı transaction'ında:
1. **Önce** şifre politikası doğrulanır (`UserManager.PasswordValidators`) — politika hatası token'ı tüketmez.
2. Koşullu tüketim: `UPDATE account_setup_tokens SET used_at=@now WHERE token_hash=@h AND used_at IS NULL AND revoked_at IS NULL AND expires_at > @now` → etkilenen satır 1 değilse `SETUP_LINK_INVALID`.
3. Aynı `DbContext`/transaction'da `AddPasswordAsync` + `UpdateSecurityStampAsync` + denetim kaydı.
4. Herhangi bir adım başarısızsa **rollback**: token kullanılmamış kalır, şifre yazılmaz. Eşzamanlı iki istekten **tam biri** başarılı olur.
- Geçersiz / süresi dolmuş / kullanılmış / iptal edilmiş için **tek yanıt**: `400 SETUP_LINK_INVALID` (hangisi olduğu açığa çıkmaz). CSRF zorunlu, IP hız sınırı.

### 2.3 Sözleşme zorlaması sunucuda; yalnızca gerekli uç noktalar muaf
- Zorlama mevcut **`StoreAccessFilter` ve `OrganizationRoleFilter`** içinde (tüm firma/şube kapsamlı personel uç noktaları bunlardan geçer):
  firmanın güncel sözleşme sürümü için kabul kaydı yoksa `403 AGREEMENT_ACCEPTANCE_REQUIRED`.
- **Muaf liste (sabit, gerekçeli, mimari testle korunur):** `GET /auth/csrf`, `POST /auth/login`, `POST /auth/logout`, `GET /auth/me`, `GET /agreement/current`,
  `POST /organization/agreement-acceptance` (yalnızca Owner), `POST /account/setup-password` (anonim), `/platform/*` (platform yöneticisi; Platform firması sözleşmeye tabi değil),
  TV cihaz uç noktaları ve imzalı dosya uç noktaları (ayrı kimlik/yetenek modeli; sözleşmesi olmayan firmada oturum zaten açılamaz).
- **Mimari test:** `EndpointDataSource` üzerinden tüm `/api/v1` uç noktaları taranır; her biri ya sözleşmeyi zorlayan filtrelerden birini taşır ya da muaf listededir. Yeni bir uç nokta bu kuralı atlayamaz.
- `MeResponse.agreementAcceptanceRequired / canAcceptAgreement` yalnızca ekran yönlendirmesi içindir; karar sunucudadır.

### 2.4 Platform yöneticisi yalnızca yetkili operatör tarafından atanır
- **API ile platform yöneticisi atanamaz/kaldırılamaz** (mevcut ilke korunur). Atama yalnızca sunucuda, veritabanı kimlik bilgisine sahip operatörün çalıştırdığı komutla:
  `dotnet SanalProva.Api.dll platform bootstrap --email … --name … [--base-url …] --confirm` (+ `reissue-link`, `revoke`).
  Web sunucusu başlamaz, HTTP ile tetiklenemez; `--confirm` yoksa kuru çalışma; aktif platform yöneticisi varken `--additional` olmadan reddeder; bağlantıyı yalnızca konsola basar (günlüğe/dosyaya değil); denetim kaydı yazar.
- **Mimari test:** `IsPlatformAdmin = true` ataması yalnızca komut sınıfında ve geliştirme tohumunda (`Development` + `Seed:Enabled`) bulunabilir; başka her yerde test kırılır.

### 2.5 Yeni işletme ücretli AI'ya otomatik yetkili değil
- `OrganizationSettings.PaidAiEnabled` (JSON ayarı, **varsayılan `false`**; mevcut firmalarda alan yoksa da `false`). Oluşturma API'si bu alanı **ayarlayamaz**.
- Zorlama iki katmanlı: (1) gerçek (mock olmayan) modda kabulde `403 PAID_AI_NOT_ENABLED` — iş kuyruğa girmez; (2) `BudgetGuard.ReserveAsync` sağlayıcı çağrısından önce aynı koşulu yeniden denetler
  (yeni `GenerationErrorCode.PaidAiNotEnabled`, yeniden denenmez, ücret/rezervasyon yok). Mock mod ve demo akışı etkilenmez.
- M5.4'e kadar açma yalnızca operatör komutuyla (`platform paid-ai --org <slug> --enable --confirm`, denetimli); M5.4'te kredi bakiyesi kontrolü bu bayrağın yerine/yanına geçer.
- Global kapılar (`UseMockProviders`, `AllowPaidCalls`, API anahtarı, bütçe) aynen kalır; firma bayrağı **ek** bir kapıdır, hiçbirini gevşetmez.

### 2.6 İşletme hizmet sözleşmesi ≠ müşteri KVKK onayı
- Ayrı tablo (`organization_agreement_acceptances` ↔ mevcut `consent_records`), ayrı metin sabiti (`ServiceAgreement` ↔ `ConsentNotice`), ayrı uç noktalar, ayrı denetim olay adları, ayrı hata kodları.
  Sözleşme **firmayı** (Owner) bağlar; müşteri onayı **oturuma** bağlıdır ve fotoğraf yüklemenin ön koşulu olmaya devam eder. Biri diğerinin yerine geçmez; kod paylaşmazlar.
- Test: sözleşme kabulü müşteri onayı yerine geçmez (onaysız oturuma fotoğraf yüklenemez); müşteri onayı sözleşme yerine geçmez.

### 2.7 Mevcut yapılar korunur
- Tenant filtreleri, `X-Store-Id`, rol filtreleri, katalog, prova/üretim hattı, idempotency, bütçe rezervasyonu, KVKK silme değişmez. Platform işlemleri tek bir serviste (`PlatformOnboardingService`) bilinçli
  `IgnoreQueryFilters()` kullanır ve `TenantArchitectureTests` envanterinde gerekçelendirilir. Platform yöneticisinin firmada rolü yoktur; firma oturum/fotoğraf/katalog uç noktalarına erişemez.
- Mevcut testler: fixture'lar (`TestData.CreateTenantAsync`, `DevDataSeeder`) güncel sözleşme kabulünü yazar; mevcut 248 entegrasyon testinin davranışı değişmez.

## 3. Bileşenler (özet)

**API:** `POST/GET /platform/organizations`, `GET /platform/organizations/{id}`, `POST /platform/organizations/{id}/owner/setup-link`, `POST /account/setup-password`,
`GET /agreement/current`, `POST /organization/agreement-acceptance`; `MeResponse` + `isPlatformAdmin`, `agreementAcceptanceRequired`, `canAcceptAgreement`.
Yeni hata kodları: `SETUP_LINK_INVALID`, `SLUG_TAKEN`, `STORE_CODE_TAKEN`, `EMAIL_TAKEN`, `AGREEMENT_ACCEPTANCE_REQUIRED`, `PAID_AI_NOT_ENABLED` (+ mevcut `IDEMPOTENCY_KEY_REUSED`, `PLATFORM_ADMIN_REQUIRED`).

**Denetim olayları** (kişisel veri ve token yok; firma olayları hedef firmaya, `AuditTrail.RecordFor`): `Platform.AdminBootstrapped`, `Platform.OrganizationCreated`, `Platform.SetupLinkIssued`,
`Platform.SetupLinkRevoked`, `Platform.PaidAiChanged`, `Account.PasswordSet`, `Organization.AgreementAccepted`.

**React:** `/platform`, `/platform/yeni` (bağlantı tek seferlik modalde; yalnızca bileşen durumunda, sorgu önbelleğine/`localStorage`'a yazılmaz), `/platform/:id`,
`/hesap/sifre-belirle` (fragment okunur ve `history.replaceState` ile hemen silinir), `/sozlesme`; `RequireAuth` yönlendirmesi (platform yöneticisi → `/platform`, sözleşme gerekiyorsa → `/sozlesme`).

## 4. Uygulama adımları (her biri: derleme + test + commit; push ayrı onayla)

| Adım | Kapsam | Çıktı / test |
|---|---|---|
| **M5.1a** | Şema: migration `PlatformOnboarding`, varlıklar, yapılandırma, sertleştirme (aşağıda ayrıntılı) | Laboratuvar migration doğrulaması, şema/bütünlük testleri |
| M5.1b | Token servisi (üret, HMAC, ver, iptal, tüket) + `Security:SetupTokenKey` | Birim testleri |
| M5.1c | Platform firma API'si + idempotency (2.1) + `AuditTrail.RecordFor` + envanter | Entegrasyon: yetki, oluşturma, çakışma, tekrar, izolasyon |
| M5.1d | `POST /account/setup-password` (2.2) + hız sınırı + günlük sızıntı testi | Entegrasyon: tek kullanım, süre, iptal, eşzamanlılık, rollback |
| M5.1e | Sözleşme sabiti + kabul + filtre zorlaması + muaf liste mimari testi + fixture'lar (2.3, 2.6) | Entegrasyon: engelleme, kabul, yeni sürüm, KVKK ayrımı |
| M5.1f | Ücretli AI firma kapısı (2.5) | Entegrasyon (gerçek-mod sahte sağlayıcı): kabulde 403, rezervasyonda ret, ücret yok |
| M5.1g | Operatör komutları (`bootstrap`, `reissue-link`, `revoke`, `paid-ai`) + `IsPlatformAdmin` mimari testi (2.4) | Entegrasyon |
| M5.1h | React ekranları + `tr.ts` + OpenAPI yeniden üretimi | Frontend birim testleri |
| M5.1i | Playwright E2E + CI + belgeler (`README`, `CLAUDE.md`, `DEVAM-NOTU`) | CI yeşil |

## 5. M5.1a — kesin kapsam (yalnızca şema; davranış değişikliği yok)

### 5.1 Dosyalar
| Dosya | Değişiklik |
|---|---|
| `backend/src/SanalProva.Infrastructure/Identity/AccountSetupToken.cs` (yeni) | Varlık: `Id`, `UserId`, `OrganizationId`, `Purpose` (enum `InitialPassword` \| `PasswordReset`), `TokenHash` (byte[32]), `CreatedAt`, `ExpiresAt`, `UsedAt?`, `RevokedAt?`, `CreatedByUserId?`. `ITenantOwned`. |
| `backend/src/SanalProva.Domain/Tenancy/OrganizationAgreementAcceptance.cs` (yeni) | Varlık: `Id`, `OrganizationId`, `UserId`, `AgreementVersion`, `TextSha256` (byte[32]), `AcceptedAt`, `IpHash?`. `ITenantOwned`, yalnızca ekleme. |
| `backend/src/SanalProva.Domain/Tenancy/Tenancy.cs` | `Organization`: `CreatedByUserId?`, `CreationRequestKey?` (≤64), `CreationRequestHash?` (byte[32]). `OrganizationSettings.PaidAiEnabled` (bool, varsayılan `false`; JSON ayarı, sütun değişikliği değil). |
| `backend/src/SanalProva.Domain/Generation/Generation.cs` | `GenerationErrorCode.PaidAiNotEnabled` (yalnızca enum değeri; davranış M5.1f'de). |
| `backend/src/SanalProva.Infrastructure/Persistence/AppDbContext.cs` | İki yeni `DbSet` + yapılandırma: snake_case tablo adları, bileşik FK'lar, indeksler (aşağıda), tenant filtreleri (`ITenantOwned`). |
| `backend/src/SanalProva.Infrastructure/Persistence/Migrations/…_PlatformOnboarding.cs` (yeni) | Aşağıdaki migration. |
| `scripts/mssql/harden-append-only.sql`, `scripts/mssql/provision.sql`, `docker/db/provision.sql` | `organization_agreement_acceptances` için `DENY UPDATE, DELETE … TO [sanalprova_app]` (mevcut `ai_cost_reconciliations` deseniyle aynı, tablo yoksa atlanır). |
| `scripts/ci/smoke-assert.sh` | `sanalprova_app` sözleşme kabul kaydını **güncelleyemez** kontrolü (mevcut DENY kontrolleriyle aynı biçim). |
| `backend/tests/SanalProva.IntegrationTests/…` | Aşağıdaki testler; `RowFactory`/`DatabaseIntegrityTests` envanteri yeni tablolara genişler. |

### 5.2 Migration `PlatformOnboarding` (ileri yönlü; `Down` yalnızca belge amaçlı, geri dönüş yedekten)
1. **Ön kontrol (veri değiştirmeden):** `users` içinde aynı `normalized_email`'e sahip birden fazla satır varsa `THROW 51201` ve dur (M3.4 `51200` deseni). Hiçbir şey değişmez.
2. `account_setup_tokens`: PK `id`; `ux_account_setup_tokens_token_hash` (benzersiz); `ix_account_setup_tokens_user_purpose_open` (`user_id, purpose`) filtreli `WHERE used_at IS NULL AND revoked_at IS NULL`;
   bileşik FK `(user_id, organization_id) → users(id, organization_id)` (`ON DELETE CASCADE`); `CHECK (expires_at > created_at)`; `purpose` enum CHECK (mevcut konvansiyon).
3. `organization_agreement_acceptances`: PK `id`; benzersiz `(organization_id, agreement_version)`; bileşik FK `(user_id, organization_id) → users(id, organization_id)` (`NO ACTION`); FK `organization_id → organizations`.
4. `organizations`: `created_by_user_id uniqueidentifier NULL`, `creation_request_key nvarchar(64) NULL`, `creation_request_hash binary(32) NULL`;
   filtreli benzersiz `ux_organizations_creation_request (created_by_user_id, creation_request_key) WHERE creation_request_key IS NOT NULL`.
5. `users`: `ux_users_normalized_email` filtreli **benzersiz** `WHERE normalized_email IS NOT NULL` (Identity'nin benzersiz olmayan `EmailIndex`'i korunur ya da yerine geçer — migration üretiminde netleşir, davranış aynı).
6. `generation_jobs.error_code` enum CHECK'i yeni değeri kapsayacak şekilde güncellenir (otomatik üretilen CHECK konvansiyonu).
7. Veri doldurma **yok** (`sanalprova_dev` boş, üretim verisi yok). Mevcut firmalarda sözleşme kabulü yok: zorlama M5.1e'de gelir, fixture'lar o adımda güncellenir.

### 5.3 M5.1a testleri
- `AppDbContextFactoryTests` (mevcut): tasarım zamanı modelinde bekleyen değişiklik yok (migration modeli tam yansıtır).
- **Laboratuvar migration doğrulaması** (`MigrationValidationTests`, geçici `sanalprova_lab_<8hex>`, `SANALPROVA_LAB_ADMIN` gerekir): önceki şemadan veriyle ileri migration;
  **yinelenen e-posta varken ön kontrol `51201` ile durur ve hiçbir şey değişmez**; idempotent betik (`db.ps1 script`) yeni migration'ı içerir.
- **Şema testleri** (`sanalprova_test`): token hash benzersizliği; aynı firma + sürüm için ikinci kabul reddedilir; çapraz firma `user_id`/`organization_id` bileşik FK ile reddedilir;
  sistem geneli e-posta benzersizliği (iki firmada aynı e-posta reddedilir); `expires_at > created_at` CHECK; yeni enum değeri kabul edilir.
- **Sertleştirme:** `sanalprova_app` `organization_agreement_acceptances` üzerinde `UPDATE`/`DELETE` yapamaz, `INSERT`/`SELECT` yapabilir; `MigrationValidationTests`'teki betik/provision eşdeğerlik denetimleri yeni DENY'yi de sayar.
- `RowFactory` + `DatabaseIntegrityTests`: tüm yeni FK'lar ve enum sütunları veri odaklı sınanır; `KvkkDeletionTests` katalog taramasında yeni tabloların `uniqueidentifier` sütunları gerekçeli ele alınır.
- CI: compose smoke yeni DENY kontrolünü içerir; mevcut 248 entegrasyon testi ve E2E değişmeden geçer (davranış değişikliği yok).

### 5.4 M5.1a kabul ölçütleri ve sınırlar
- Derleme 0 uyarı / 0 hata; birim + entegrasyon (yerel `sanalprova_test`, onayla) + laboratuvar (onayla) + CI yeşil.
- Migration yalnızca izole hedeflere uygulanır: laboratuvar (`sanalprova_lab_*`), `sanalprova_test` (test host'u), Docker/CI konteyneri. **`sanalprova_dev`, PostgreSQL, yedekler ve gerçek storage'a dokunulmaz.**
- M5.1a'da API, UI ve iş kuralı değişmez (yalnızca şema + sertleştirme + testler). Sonraki adımlar ayrı onayla.

## 6. Açık konular
- Hizmet sözleşmesi metni: taslağı geliştirme ekibi hazırlar, **hukuki onay olmadan canlıya çıkmaz** (D3).
- E-posta gönderimi M5.2+ (şifre sıfırlama da o zaman e-postaya taşınabilir); MVP'de bağlantı yalnızca platform yöneticisine gösterilir.
- Sistem geneli e-posta benzersizliği: bir kişi iki firmada aynı e-postayla hesap açamaz (D4, bilinçli kısıt).
