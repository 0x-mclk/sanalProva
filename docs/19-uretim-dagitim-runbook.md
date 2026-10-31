# 19 — Üretim dağıtımı ve operasyon runbook'u (Pilot Faz A)

Kapsam: tek Linux VM üzerinde Docker Compose ile pilot dağıtımı; ilk kurulum, güncelleme, işletme açılışı, kredi/bütçe/AI etkinleştirme, yedekleme, geri yükleme, rollback, acil durdurma ve asgari izleme. Tüm dosyalar `deploy/` altındadır (geliştirme `docker-compose.yml`'inden ayrıdır, onu etkilemez).

## 0. Doğrulama durumu (DÜRÜST ÖZET)

| Parça | Durum |
| --- | --- |
| Uygulama tarafı: güvenilen vekil başlıkları, üretim başlangıç denetimi, silme günlüğü + `reapply-deletions` | ✅ otomatik testli (backend entegrasyon testleri; süreç düzeyinde "güvensiz yapılandırmayla başlamayı reddeder" dahil) |
| SQL: en az yetki sertleştirmesi + yetki doğrulaması, tam/günlük yedek, geri yükleme (üzerine yazmaz), karantina, tutarlılık denetimi, kullanıcı eşleme | ✅ gerçek SQL Server'da, yalnızca `sanalprova_lab_*` veritabanlarında testli (`ProductionOpsLabTests`); `provision-production.sql` yalnızca sözdizimi denetimli (sunucu login'lerine dokunulmadı) |
| Operasyon betikleri (`ops.sh`, `prod-env.sh`, `make-sql-cert.sh`, `preflight.sh`) | ✅ yerelde `deploy/tests/ops-selftest.sh` (54 senaryo; gerçek gpg/tar/gzip/openssl, sahte sqlcmd): şifreleme, bozulma/yanlış parola tespiti, saklama, geri yükleme korumaları |
| Compose/Caddy/Dockerfile **içerik** sözleşmesi | ✅ `DeployArtifactsTests` (portlar, ağlar, TLS, sır yokluğu, yönlendirmeler) |
| **Docker ile gerçek derleme/çalıştırma, Caddy'nin gerçek TLS/yönlendirme davranışı, SQL Server konteynerinde özel CA ile TLS, ACME sertifika alımı** | ❌ **bu makinede Docker yok: ÇALIŞTIRILMADI.** CI işi `deploy-package` (compose config + `caddy validate` + imaj derleme) ilk push'ta bunu kısmen kapatır. **İlk gerçek dağıtım bir "prova dağıtımı" olarak, gerçek müşteri verisi olmadan yapılmalıdır** (§12 kabul listesi). |
| Gerçek sunucuya dağıtım | ❌ yapılmadı (bu aşamanın kapsamı dışı) |

## 1. Mimari

```
internet ──80/443──> web (Caddy: TLS, statik SPA, /tv, /api /hubs /files vekili)
                      │ edge ağı (sabit alt ağ PROXY_SUBNET; API yalnızca buradan gelen X-Forwarded-* başlıklarına güvenir)
                      v
                     api (ASP.NET, Production, kullanıcı 'app', cap_drop ALL) ── backend ağı (internal: dış çıkış yok)
                                                                                    │
                                                                                    v
                                                                              sqlserver (Express, TLS zorunlu, port YAYIMLANMAZ)
ops (yalnızca isteğe bağlı: yedek/geri yükleme/ilk kurulum) ── backend ağı
```
- Ana makineye yalnızca **80/443** açılır. SQL Server ve API portu yayımlanmaz.
- Kalıcı birimler: `sqlserver-data`, `sqlserver-backup` (SQL'in yazdığı geçici yedek dosyaları; şifrelenince silinir), `api-storage` (müşteri/katalog görselleri), `api-dpkeys` (çerez/antiforgery anahtarları), `api-journal` (**silme günlüğü: depo/DB yedeğinden AYRI**), `caddy-data` (TLS sertifikaları).
- Gizli değerler: `deploy/.env.prod` (600) ve `deploy/secrets/` (git'e girmez, imajlara yazılmaz). Not: `docker inspect` ortam değişkenlerini gösterir; Docker daemon erişimi = sır erişimi (yalnızca yetkili yöneticiler docker grubunda olmalı).

## 2. Ön koşullar

- Linux VM (Türkiye'de barındırma tercih edilir), Docker Engine + Compose v2, ≥ 4 GB RAM, ≥ 40 GB disk (+ yedekler için ayrı disk/uzak kopya).
- Alan adı (ör. `prova.magaza.com`) ve DNS A kaydı → VM'nin genel IP'si; güvenlik duvarında yalnızca 22 (kısıtlı), 80, 443 açık. (Genel alan adı yoksa §4.2.)
- Depo sunucuya alınır (`git clone`; **main'e merge ve push kararı proje sahibindedir**); bu runbook `deploy/` klasöründen çalışır.

## 3. İlk kurulum (sıralı)

```bash
cd deploy
./scripts/prod-env.sh prova.magaza.com            # .env.prod + secrets/backup_passphrase (değerler yazdırılmaz)
./scripts/make-sql-cert.sh                        # özel CA + SQL sunucu sertifikası (SAN: sqlserver)
sudo chown 10001:0 secrets/sqlserver-tls/server.key && sudo chmod 600 secrets/sqlserver-tls/server.key   # kök değilseniz
./scripts/preflight.sh                            # TÜMÜ "TAMAM" olmalı (zayıf sır, mock/ücretli tutarsızlığı, sertifika, docker compose config)
alias dc='docker compose -f docker-compose.prod.yml --env-file .env.prod'
dc up -d --build                                   # sqlserver → db-provision → db-migrate → db-harden(+verify) → api → web
dc ps                                              # hepsi healthy/exited(0) olmalı
dc --profile ops run --rm ops tls-check            # "SQL TLS TAMAM": bağlantı şifreli VE sertifika özel CA ile doğrulandı
```
**Hemen yapılacaklar:** (a) `secrets/backup_passphrase` kopyasını sunucu DIŞINA (parola yöneticisi/kasa) kaydedin: **kaybı = tüm yedeklerin kaybı**. (b) `secrets/ca/ca.key` dosyasını çevrimdışı saklayıp sunucudan silin (yalnızca sertifika yenilerken gerekir). (c) `.env.prod` içindeki sa/migrator/app/backup parolalarını da kasaya alın.

Doğrulama: `curl -I https://prova.magaza.com/` (200, `Strict-Transport-Security`), `https://prova.magaza.com/tv` (TV giriş ekranı), `https://prova.magaza.com/health/live` (**404** olmalı: sağlık uç noktaları dışarıya kapalı), `dc logs api | head` (üretim denetimi geçti, "Now listening").

**Üretim başlangıç denetimi:** API, `Production` ortamında şunlardan biri varsa BAŞLAMAZ ve nedenini (değer yazmadan) söyler: şifresiz SQL / `TrustServerCertificate=True` / `sa` / geliştirme-test veritabanı adı; 32 karakterden kısa veya yer tutucu anahtar; göreli depo/anahtar/günlük yolu; günlük yolunun depo içinde olması; `AllowedHosts=*`; güvenilen vekil tanımsız; `Seed:Enabled`; tutarsız ücretli AI kapıları (gerçek mod + çağrı kapalı, anahtar eksik, bütçe sınırları tutarsız).

## 4. TLS seçenekleri

1. **Genel alan adı + ACME (önerilen):** `Caddyfile` (varsayılan). Caddy Let's Encrypt sertifikasını kendisi alır/yeniler; cihazlara bir şey kurmak gerekmez. 80 ve 443 internetten erişilebilir olmalı.
2. **Yerel/özel ağ (`--internal-ca`):** `./scripts/prod-env.sh prova.lan --internal-ca` → `Caddyfile.internal` (`tls internal`). Caddy'nin kök sertifikası (`dc cp web:/data/caddy/pki/authorities/local/root.crt .`) **her tablete ve TV'ye güvenilir kök olarak kurulmalıdır**; akıllı TV'lere kök sertifika kurmak çoğu zaman mümkün değildir → bu seçenek TV için önerilmez.
3. SQL TLS her iki durumda özel CA iledir (`make-sql-cert.sh`); sunucu sertifikası 2 yıl geçerlidir: **bitişten ≥ 30 gün önce yenileyin** (`./scripts/make-sql-cert.sh -f`, `dc up -d --force-recreate sqlserver`, `ops tls-check`; takvime kaydedin).

## 5. Güncelleme ve migration

```bash
git pull                                           # (sürüm etiketi/commit'i kaydedin: rollback için)
./scripts/preflight.sh
dc --profile ops run --rm ops backup full          # HER güncellemeden önce tam yedek
dc up -d --build                                   # db-migrate migrator ile ileri yönlü migration'ı uygular, db-harden sertleştirmeyi + doğrulamayı yeniden çalıştırır
dc ps && dc logs --tail=50 api
```
- Migration'lar **yalnızca ileri yönlüdür** (`Down` veri siler). Geri dönüş = yedekten geri yükleme (§9–10).
- Yeni bir *yalnızca ekleme* tablo eklenirse `deploy/sql/harden-production.sql` listesine eklenmelidir (bir test bunu zorlar).
- `db-harden` başarısız olursa (yetki doğrulaması tutmuyorsa) `api` başlamaz: bu bilinçlidir.

## 6. İşletme açılışı (sıralı)

1. **Platform yöneticisi** (bir kez): 
   ```bash
   dc run --rm --no-deps api platform bootstrap --email yonetici@sirket.com --name "Ad Soyad" --base-url https://prova.magaza.com --confirm
   ```
   Çıktıdaki tek kullanımlık bağlantıyı (yalnızca bir kez gösterilir) güvenli kanaldan yöneticiye iletin; yönetici şifresini belirler (72 sa).
2. Yönetici `https://prova.magaza.com/platform` → **Yeni işletme**: firma, ilk şube, firma sahibi (Owner) e-postası → kurulum bağlantısı **bir kez** gösterilir; sahibe iletin.
3. Firma sahibi bağlantıyla şifresini belirler, girer, **hizmet sözleşmesini kabul eder** (kabul edilmeden mağaza ekranları kapalıdır). ⚠️ Sözleşme metni hâlâ TASLAK: hukuki onay olmadan pilot başlatmayın (docs/18 E3).
4. Sahip `/yonetim/subeler` ve `/yonetim/personel` ile şube ve personeli ekler; katalog `/katalog` ile (ürün + AI referans görseli).
5. TV: mağaza TV'sinde `https://prova.magaza.com/tv` → 6 haneli kod → tablet "TV'ler" ekranından eşleştirilir.

## 7. Kredi, bütçe ve ücretli AI etkinleştirme (SIRA ÖNEMLİ)

Varsayılan: mock mod, ücretli çağrı kapalı. Gerçek AI için **hepsi** gerekir; sırayı bozmayın:

1. **Önkoşullar (kod dışı):** proje sahibinin canlı AI testi onayı ve kalite kabulü (docs/18 E4); sağlayıcı sözleşmesi/aktarım metinleri (E3); sağlayıcı hesabında harcama limiti.
2. **Firma bütçesi ve kredisi (hâlâ mock'ta iken):**
   ```bash
   dc run --rm --no-deps api platform budget --org SLUG --daily 2 --monthly 30 --job 0.25 --confirm     # pilot için DÜŞÜK başlayın
   dc run --rm --no-deps api platform credits --org SLUG --grant 50 --ref pilot-2026-11-001 --note "Pilot paketi" --confirm   # --ref benzersiz (fatura/havale no)
   ```
3. **Anahtar ve global kapılar:** `.env.prod` → `FAL_API_KEY=…` (veya kullanılacak sağlayıcı), `USE_MOCK_PROVIDERS=false`, `ALLOW_PAID_CALLS=true`; `BUDGET_*` değerleri platform tavanıdır (firma sınırı bunu aşamaz). `./scripts/preflight.sh` (gerçek mod + anahtar tutarlılığını denetler) → `dc up -d api`.
4. **Firma kapısı (en son):** `dc run --rm --no-deps api platform paid-ai --org SLUG --enable --confirm`.
5. **Duman testi:** bir sentetik/izinli fotoğrafla tek üretim; `/yonetim/kredi`'de rezerve → düşüm, `/maliyet`'te tutar; belirsiz sonuç yoksa devam.
6. İzleme (§11) açık olmadan çoklu mağazaya geçmeyin.

## 8. Yedekleme

Yedek **seti** (`BACKUP_DIR`, varsayılan `deploy/backups/`; her set kendi klasöründe, `manifest.json` + `manifest.sha256`):
- **Tam set** (`full/<UTC-zaman>/`): `db.bak.gz.gpg` (SQL tam yedek, CHECKSUM + VERIFYONLY), `storage.tar.gz.gpg` (görsel deposu; veritabanı yedeğinden SONRA alınır), `journal.gpg` (silme günlüğü).
- **Günlük seti** (`log/<UTC-zaman>/`): `log.trn.gz.gpg` (işlem günlüğü; FULL kurtarma modeli → zaman noktasına geri dönüş). RPO ≈ günlük yedek aralığı.
- Şifreleme: gpg simetrik AES256 (bütünlük korumalı: bozulan/değiştirilen dosya açılmaz); parola `secrets/backup_passphrase`. Açık `.bak` dosyaları şifrelendikten hemen sonra silinir. SQL Server **Express yedek sıkıştırmayı desteklemez**: sıkıştırma dosya düzeyinde gzip'tir.
- Yetkiler: SQL yedeği `sanalprova_backup` hesabıyla (yalnızca `db_backupoperator`; veri okuyamaz); doğrulama/geri yükleme `sa` ile; `backups/` ve `secrets/` yalnızca root/yönetici okuyabilmeli (`chmod 700`).
- Saklama: `BACKUP_RETENTION_FULL_DAYS` (14), `BACKUP_RETENTION_LOG_DAYS` (7); `prune` en yeni 3 tam seti **asla** silmez.

Zamanlama (sunucu cron'u; `deploy/` klasöründen):
```cron
*/15 * * * *  cd /opt/sanalprova/deploy && docker compose -f docker-compose.prod.yml --env-file .env.prod --profile ops run --rm -T ops backup log  >> /var/log/sanalprova-backup.log 2>&1
 15 2 * * *   cd /opt/sanalprova/deploy && docker compose -f docker-compose.prod.yml --env-file .env.prod --profile ops run --rm -T ops backup full >> /var/log/sanalprova-backup.log 2>&1
 45 3 * * *   cd /opt/sanalprova/deploy && docker compose -f docker-compose.prod.yml --env-file .env.prod --profile ops run --rm -T ops prune --yes     >> /var/log/sanalprova-backup.log 2>&1
 30 4 * * 0   cd /opt/sanalprova/deploy && docker compose -f docker-compose.prod.yml --env-file .env.prod --profile ops run --rm -T ops verify "$(ls -d backups/full/2* | tail -n 1 | sed 's#^#/#;s#^/backups#/backups#')" --deep >> /var/log/sanalprova-backup.log 2>&1
```
Elle: `dc --profile ops run --rm ops backup full | verify <set> [--deep] | list | prune [--yes]`. **Uzak kopya zorunlu:** `backups/` klasörünü başka makineye/depoya düzenli kopyalayın (dosyalar zaten şifreli: `rsync`/`rclone`); aynı disk = yedek değildir.

**Tatbikat:** ayda en az bir kez §9'daki geri yüklemeyi **geçici bir veritabanı adına** yapın (`--target sanalprova_restore_tatbikat`), `post-restore` denetiminin geçtiğini görün, sonra veritabanını silin. Tatbikat yapılmamış yedek, yedek sayılmaz.

## 9. Geri yükleme (hiçbir adım mevcut veriyi ezmez)

**Bilmeniz gerekenler (yedek yedekten sonrasını bilmez):**
- Yedekten sonraki **kredi/maliyet işlemleri, yeni oturumlar ve yönetim değişiklikleri kaybolur** (günlük zinciri ve `--stopat` bunu en aza indirir). Kaybolan kredi yüklemeleri/harcamalar için sağlayıcı faturasıyla ve yükleme belgeleriyle mutabakat yapın (`platform credits` referansları belgelerinizdedir).
- Yedekte `Queued/Running` olan işler yedekten sonra tamamlanmış/ücretlenmiş olabilir: **karantina** bunları yeniden çalıştırmaz (gönderim kanıtı olanlar `Failed/OutcomeUncertain`, diğerleri `Cancelled`).
- Yedekten sonra kullanılan kurulum bağlantıları yeniden geçerli görünürdü → karantina tüm açık bağlantıları iptal eder; tüm güvenlik damgaları yenilenir (herkes yeniden giriş yapar). Yedekten sonra pasife alınan kullanıcılar/iptal edilen TV'ler **geri gelmiş olabilir**: kullanıcı ve TV listelerini elle gözden geçirin.
- **Silinmiş kişisel veri geri gelmiş olabilir** → §10 zorunludur.

Adımlar (aynı sunucuda veritabanı bozulması; depo sağlam):
```bash
dc --profile ops run --rm ops list                                   # setleri gör
dc stop web api                                                      # trafik kes (sqlserver çalışır)
dc --profile ops run --rm ops restore-db /backups/full/<T> --log /backups/log/<T1> --log /backups/log/<T2> \
    --target sanalprova_restore_<yyyymmdd> --stopat 2026-11-01T14:30:00     # KURU ÇALIŞMA: planı gösterir
# …plan doğruysa aynı komuta --confirm ekleyin. Otomatik: geri yükle → yetim kullanıcıları düzelt → KARANTİNA → tutarlılık denetimi (+DBCC CHECKDB)
```
Denetim başarısızsa veritabanı **üretime alınmaz**; hata satırlarına göre başka bir yedek seti deneyin.
Yeni sunucuya geri yükleme (felaket): önce §3 (provision/sertifika/.env), sonra `restore-files <tam-set> --dest /restore/files --confirm` ile depoyu **yeni boş klasöre** açın, görselleri `api-storage` birimine kopyalayın (eski depo yoksa), silme günlüğünü §10'a göre birleştirin; `restore-db` yetim kullanıcıları (SID farkı) otomatik düzeltir.

## 10. Silinmiş kişisel veri (KVKK) ve geri yükleme

`SessionPurger` her oturumu silmeden ÖNCE kimliğini **silme günlüğüne** (`api-journal`, JSONL, yalnızca kimlikler/zaman) yazar. Günlük, veritabanı ve depo yedeklerinin DIŞINDADIR; her tam sette şifreli kopyası da vardır. Geri yükleme sonrası:

1. API durmuşken geri yüklenen veritabanına karşı (canlı günlük `api-journal` birimindedir; yeni sunucuda önce `journal.gpg` içeriğini bu dosyayla **birleştirin, üzerine yazmayın**):
   ```bash
   dc run --rm --no-deps -e "ConnectionStrings__Main=Server=sqlserver,1433;Database=sanalprova_restore_<yyyymmdd>;User ID=sanalprova_app;Password=$(grep ^SP_APP_PW .env.prod | cut -d= -f2);Encrypt=True;TrustServerCertificate=False;HostNameInCertificate=sqlserver" \
       api platform reapply-deletions --journal /data/journal/deleted-sessions.log            # kuru çalışma: sayıları gösterir
   # sonra aynı komuta --confirm
   ```
   Günlükteki oturumlar veritabanında yeniden belirmişse silinir (dosyalarıyla); satırı olmayan ama dosyaları geri yüklenmiş olanların depo öneki temizlenir. İdempotenttir.
2. Saklama süresi dolmuş oturumları API açılınca `RetentionJob` zaten siler (15 dk içinde); yetim klasörleri `OrphanSweeper` temizler.
3. Üretime geçiş öncesi `reapply-deletions` **çalıştırılmamış** geri yükleme KVKK ihlali riski taşır.

**Üretime geçiş (elle, bilinçli):** `ops` kabuğunda sa ile (parola `.env.prod`'dan; komut satırına yazmayın):
```bash
dc --profile ops run --rm -e SQLCMDPASSWORD="$(grep ^MSSQL_SA_PASSWORD .env.prod | cut -d= -f2)" --entrypoint sqlcmd ops -S sqlserver,1433 -U sa -b \
   -Q "ALTER DATABASE [sanalprova] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; ALTER DATABASE [sanalprova] MODIFY NAME = [sanalprova_eski_<yyyymmdd>]; ALTER DATABASE [sanalprova_eski_<yyyymmdd>] SET MULTI_USER; ALTER DATABASE [sanalprova_restore_<yyyymmdd>] MODIFY NAME = [sanalprova];"
dc up -d api web                                    # db-harden yeniden çalışır; /health ve giriş denenir
```
Eski veritabanı en az 7 gün silinmeden saklanır (geri dönüş: adları tersine çevirmek). Silmek (`DROP DATABASE`) ayrı, bilinçli bir karardır.

## 11. Asgari izleme (Faz C: betik + ekran + kontrol listesi)

**Otomatik kontrol (`deploy/scripts/monitor.sh`, salt okunur):** servis durumu, disk doluluğu (≥%80 uyarı, ≥%90 kritik), yedek yaşı (tam set ≥26 sa uyarı/≥50 sa kritik; günlük seti ≥30 dk uyarı/≥3 sa kritik), site erişimi ve uygulamanın kendi kontrolleri (`platform status`: veritabanı bağlantısı, depo diski, takılı iş, bekleyen belirsiz maliyet, firma bütçesi %80/%100, düşük kredi). Çıkış kodu **0 sorun yok / 10 uyarı / 11 kritik**; sır yazdırmaz. Cron örneği (uyarı iletimi — e-posta/Telegram/UptimeRobot "heartbeat" — sizin kuracağınız dış sistemdir, uygulamada YOKTUR):

```cron
*/10 * * * *  cd /opt/sanalprova/deploy && ./scripts/monitor.sh >> /var/log/sanalprova-monitor.log 2>&1 || /usr/local/bin/sizin-uyari-komutunuz "$?"
```
Ekran: platform yöneticisi **/platform/finans** sayfasında aynı uygulama kontrollerini ve işletme bazlı kredi/bütçe/açık rezervasyon özetini görür; **/platform/mutabakat** sayfasında belirsiz maliyet vakalarına karar verir (mevcut güvenli API: idempotent, `expectedSequence` çakışma denetimli, değiştirilemez defter). Elle kontrol: `dc run --rm --no-deps api platform status`.

Eşikler sabittir (pilot): belirsiz vaka 24 sa uyarı / 72 sa kritik; takılı iş: kuyrukta 15 dk, çalışırken 20 dk; kredi uyarı eşiği `Credits:LowBalanceThreshold` (varsayılan 10). **Doğrulama:** betik mantığı `deploy/tests/monitor-selftest.sh` ile (sahte docker/df), uygulama kontrolleri backend testleriyle doğrulandı; gerçek Docker ortamında çalıştırılmadı.

Günlük (5 dk) elle kontrol listesi (otomasyon kurulana kadar veya ek olarak):
- [ ] **Servis sağlığı:** `dc ps` (hepsi healthy; `web`, `api`, `sqlserver`); dışarıdan `https://SITE/` 200. (Harici bir çalışma süresi izleyicisi — örn. UptimeRobot — sitenin ana sayfasına bakacak şekilde kurun; `/health/*` dışarıya kapalıdır.)
- [ ] **Disk:** `df -h` — Docker birimleri ve `backups/` < %80. (`docker system df`)
- [ ] **Yedek yaşı:** `ops list` — son günlük seti < 30 dk, son tam set < 26 saat; cron günlüğünde (`/var/log/sanalprova-backup.log`) hata yok; uzak kopya güncel.
- [ ] **Veritabanı:** `dc logs --since 24h sqlserver | grep -i "error\|deadlock"`; `ops tls-check`; veri dosyası boyutu (Express sınırı **10 GB/veritabanı**).
- [ ] **Silme günlüğü:** `dc logs --since 24h api | grep "Silme günlüğüne yazılamadı"` — **hiç olmamalı** (olursa disk/izin sorunu: KVKK geri yükleme koruması eksik kalır).
- [ ] **Bütçe ve maliyet:** `/maliyet` (Owner/Manager) — bugün/ay harcama ve açık rezervasyon; sağlayıcı faturasıyla haftalık karşılaştırma (fal/Pruna maliyeti tahmindir). **Belirsiz maliyet vakaları** (`/platform` mutabakat API'si `GET /platform/cost-cases`) günlük gözden geçirilir: bekleyen vaka kredisi bloke kalır.
- [ ] **Kredi:** `/yonetim/kredi` — kullanılabilir kredi tükenmeden yükleme planı.
- [ ] **Sertifikalar:** Caddy otomatik yeniler (`dc logs web | grep -i "certificate"`); SQL sertifikası bitişinden 30 gün önce yenilenir.
- [ ] **Güvenlik:** `dc logs api | grep -c "INVALID_CREDENTIALS\|ACCOUNT_LOCKED"` olağan dışı artış; beklenmeyen platform yöneticisi/denetim kaydı.
- [ ] **Güncelleme:** işletim sistemi ve Docker güvenlik güncellemeleri; imaj yeniden derleme (`dc build --pull`).

## 12. Prova dağıtımı kabul listesi (gerçek müşteri verisinden ÖNCE, Docker'lı ortamda)

- [ ] `preflight.sh` tümü TAMAM; `dc up -d --build` sorunsuz; `dc ps` healthy.
- [ ] `ops tls-check` TAMAM (SQL sertifikası özel CA ile doğrulanıyor, bağlantı şifreli).
- [ ] `https://SITE/` SPA, `/tv` TV girişi, `/api/v1/auth/csrf` 200; `/health/live` 404; HTTP → HTTPS yönlendirmesi.
- [ ] Giriş (çerez `Secure; HttpOnly; SameSite=Strict`), CSRF'li bir yazma isteği, SignalR bağlantısı (tablet + TV) çalışıyor; **istemci IP'si günlüklerde gerçek (vekil değil)** (`dc logs api`, hız sınırı kovası/denetim IP özeti).
- [ ] Sahte `X-Forwarded-For` ile doğrudan API'ye ulaşmak mümkün değil (API portu yayımlı değil); Caddy üzerinden gönderilen sahte başlık yok sayılıyor.
- [ ] Mock modda tam prova akışı (fotoğraf → seçim → mock sonuç → TV).
- [ ] `backup full` + `backup log` + `verify --deep` başarılı; `restore-db` ile geçici veritabanı + `post-restore` denetimi başarılı; `reapply-deletions` tatbikatı (bir oturumu sil → eski yedeği geri yükle → günlükle yeniden silindiğini doğrula).
- [ ] `verify-production` (db-harden içinde) geçti; `sanalprova_app` ile ledger UPDATE/DELETE denemesi reddediliyor.
- [ ] Acil durdurma prosedürü (§14) denendi.

## 13. Rollback (uygulama sürümü)

Migration ileri yönlü olduğu için sürüm geri alma iki durumda farklıdır:
1. **Şema değişmedi** (yalnızca kod/arayüz): `git checkout <önceki-commit>` → `dc up -d --build api web`. Veri etkilenmez.
2. **Şema değişti** (yeni migration uygulandı): eski kod yeni şemayla çalışmayabilir. Seçenekler: (a) düzeltme sürümü ileri alın (tercih); (b) güncelleme öncesi tam yedekten (§8 "HER güncellemeden önce") geri yükleyin (§9–10; yedekten sonraki işlemler kaybolur ve mutabakat gerekir).
Her güncellemeden önce **commit SHA'sını ve yedek set adını** bir değişiklik günlüğüne yazın.

## 14. Acil durum: ücretli AI'yı hemen kapatma (en hızlıdan en kalıcıya)

| Amaç | Komut | Etki |
| --- | --- | --- |
| Tek işletmeyi durdur | `dc run --rm --no-deps api platform paid-ai --org SLUG --disable --confirm` | Yeni ücretli iş kabulü ve sağlayıcıya gitmemiş kuyruktaki işler durur (saniyeler); sürmekte olan çağrı iptal edilmez, bütçe/rezervasyonla sınırlıdır |
| Tek işletmenin bütçesini sıfırla | `… platform budget --org SLUG --daily 0 --confirm` | Sonraki her ücretli çağrı `BudgetExceeded` |
| TÜM işletmeler (global) | `.env.prod` → `ALLOW_PAID_CALLS=false` (ve/veya `USE_MOCK_PROVIDERS=true`); `dc up -d api` | Tüm ücretli çağrılar kapanır (birkaç saniye API yeniden başlar). Kredi/bütçe kayıtları korunur |
| Sağlayıcıyı kes | `.env.prod`'daki ilgili `*_API_KEY` değerini boşaltın / sağlayıcı panelinden anahtarı iptal edin | Sağlayıcı tarafında kesin durdurma |
| Her şeyi durdur | `dc stop web api` | Uygulama kapanır (veri korunur) |

Sonrasında: `/maliyet` ve sağlayıcı panelinden gerçek harcamayı çıkarın; belirsiz vakaları mutabakata alın; neden bulunmadan yeniden açmayın. Kredi düzeltmesi gerekirse `platform credits --adjust ±N --note "gerekçe" --ref …` (silinmez, yeni kayıt olur).

## 15. Sırların yenilenmesi

- **SQL parolaları:** `.env.prod` içinde değiştirin → `dc run --rm db-provision` (login parolalarını yeni değere çeker; idempotent) → `dc up -d api` (yeni bağlantı dizesi).
- **`STORAGE_SIGNING_KEY`:** değişince mevcut imzalı görsel adresleri geçersiz olur (kısa ömürlüdür; sorun değil). **`SETUP_TOKEN_KEY`:** değişince açık kurulum bağlantıları geçersiz olur (bekleyen davet/sıfırlama bağlantıları yeniden üretilmeli).
- **`backup_passphrase`:** değişirse eski yedekler ESKİ parolayla açılır; eski parolayı saklayın veya yeni parolayla yeniden tam yedek alın.
- Çerez anahtarları (`api-dpkeys`) silinirse tüm oturumlar düşer (kullanıcılar yeniden girer).

## 16. Bilinen sınırlar (Faz A sonrası kalanlar)

- Docker ile gerçek dağıtım ve Caddy/TLS davranışı doğrulanmadı (§0); CI `deploy-package` işi yalnızca yapıyı doğrular.
- CSP başlığı yok (SPA bir tarayıcıda denenmeden katı CSP riskli); diğer güvenlik başlıkları var.
- `sa` hesabı açıktır (geri yükleme/sertleştirme için gerekli); parolası yalnızca kasadadır ve SQL portu yayımlanmaz.
- Tek sunucu: SignalR çoklu sunucu yedek düzlemi, S3 depo, otomatik yük devretme yok.
- Yedek **uzak kopyası** otomatik değildir (operatör kurar); izleme/uyarı otomasyonu yalnızca kontrol listesidir.
- E-posta yok (davet/sıfırlama bağlantıları elle iletilir); MFA yok.
- `ImageWorker` üretimde dağıtılmaz (revizyon motoru yapılmadı): sağlık denetimi "Degraded" bildirebilir, uygulama etkilenmez.
