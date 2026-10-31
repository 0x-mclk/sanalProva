# 18 — Pilot hazırlık ve uçtan uca ürün denetimi

Tarih: 2026-10-31 · Kapsam: kod incelemesi + mevcut otomatik test kanıtları. **Hiçbir kod değiştirilmedi**, ücretli AI çağrısı yapılmadı, korumalı kaynaklara dokunulmadı. M5.5 (ödeme) pilot ön koşulu DEĞİLDİR: kredi manuel yüklenir (`platform credits`).

Sınıflandırma: **ENGELLEYİCİ** = bu giderilmeden gerçek mağazada gerçek müşteriyle pilot yapılmaz · **ÖNEMLİ** = pilot başlayabilir ama risk/iş yükü yaratır, ilk haftalarda kapatılmalı · **SONRAYA** = pilot için gerekmez.

## 1. Uçtan uca akış değerlendirmesi

Akış: işletme açılışı → sözleşme → şube/personel → katalog → (tablet) müşteri onayı → fotoğraf → seçim → AI prova → TV.

| Adım | Durum | Kanıt / not |
| --- | --- | --- |
| İşletme açılışı (operatör CLI + platform paneli), kurulum bağlantısı, Owner şifre belirleme | ✅ çalışıyor | Playwright `onboarding.spec` |
| Hizmet sözleşmesi kabulü (Owner, sürümlü, varsayılan-zorlama) | ✅ çalışıyor, **metin TASLAK** | `AgreementTests`; metin hukuki onaysız |
| Şube/personel yönetimi, davet, rol, pasife alma | ✅ | `management.spec`, `StoreManagementTests`, `StaffManagementTests` |
| Katalog yönetimi (kategori, ürün, varyant, görsel, AI referansı) | ✅ ekranlar var | Demo katalog yalnız dev'de; **gerçek mağaza kataloğu boş başlar**, toplu içe aktarma yok |
| Müşteri KVKK onayı (zorunlu, sunucu zorlar; veli onayı; yurt dışı aktarım kutusu) | ✅ | `ConsentPage`; metin TASLAK |
| Fotoğraf: kamera (`getUserMedia`) + dosya yedeği, EXIF silme, yeniden kodlama | ✅ mock'ta | Gerçek kamera/Safari E2E yok |
| Seçim (hazır takım / tek kaftan; serbest kombin gerçek modda KAPALI) | ✅ | `OutfitSetPage` |
| AI prova (mock) → sonuç, favori, karşılaştırma, geçmiş, silme | ✅ | `flow.spec` |
| AI prova (gerçek sağlayıcı) | ⚠️ **canlı doğrulanmadı** | bkz. §3 |
| TV: eşleştirme, gönder, karşılaştır, temizle, kopma/yeniden bağlanma, gizlilik temizliği | ✅ tarayıcıda (Chromium) | `tv.spec`, `tv-privacy.spec`; **gerçek TV denenmedi** |
| Bütçe + kredi (kabulde ayırma, kapanış, belirsiz bloke) | ✅ | 693 backend testi |

**Mock modda bütün akışın çalıştığının kanıtı:** M5.4 sonrası son tam koşu — backend 693 test (0 başarısız, 4 beklenen atlama), React 147/147, Playwright 13/13 (giriş → onay → fotoğraf → seçim → mock üretim → sonuç → TV'ye gönder/karşılaştır/temizle, ağ hatasında tek iş, firma/şube izolasyonu, işletme açılışı, yönetim, kredi). Bu denetimde yeniden koşulmadı (çalışma ağacı temiz, kod değişmedi). **Kanıtın sınırları:** yalnızca Chromium; gerçek kamera, iPad/iOS Safari, gerçek TV tarayıcısı, gerçek ağ gecikmesi ve gerçek sağlayıcı süreleri hiç denenmedi.

## 2. ENGELLEYİCİ

**E1 — Üretim dağıtımı yok (frontend ve TLS).** `docker-compose.yml` yalnızca `sqlserver`, `db-*`, `image-worker`, `api` içerir; frontend için Dockerfile/yapı çıktısı sunumu, reverse proxy (docs/02'de Caddy/Nginx öngörülmüştü) ve TLS yazılmadı. Oysa: çerezler `SameSite=Strict` + `Secure` (HTTPS olmadan giriş çalışmaz), tarayıcı kamerası HTTPS ister, SignalR/PWA aynı origin ister, TV `/tv` yolunu tv.html'e yönlendirmeli. Ayrıca `ForwardedHeaders` yapılandırılmadığı için proxy arkasında istemci IP'si hep proxy IP'si olur: giriş/eşleştirme **hız sınırları tüm mağazalar için tek kovaya** düşer ve denetim IP özeti anlamsızlaşır. *Çözüm:* frontend yapı imajı + Caddy (otomatik TLS, statik SPA, `/api` `/hubs` `/files` proxy, `/tv`), `UseForwardedHeaders` (güvenilen proxy), üretim compose profili ve dağıtım runbook'u.

**E2 — Yedekleme/geri yükleme ve silme-kaydı defteri yok.** Pilotta gerçek müşteri fotoğrafı ve mali kayıtlar (kredi defteri, maliyet) var. DB ve görsel deposu için yedek betiği, geri yükleme provası ve "yedekten geri gelen silinmiş müşteri verisinin yeniden silinmesi" prosedürü yok (DEVAM-NOTU A-1/A-2). Depo yerel disk (S3 uygulaması yok): tek VM, disk dolması ve disk arızası riski yönetilmiyor.

**E3 — KVKK ve sözleşme hukuki olarak onaysız.** Müşteri aydınlatma metni ve hizmet sözleşmesi **TASLAK** (kodda böyle etiketli; "hukuki onay şart"). Ayrıca: yurt dışı aktarım (fal.ai, Pruna, ileride Gemini/FASHN) için sözleşme/DPA ve açık rıza metni; Pruna'da görsellerin geçici yüklenip otomatik silinmesi (elle silme yok, docs/12 §4) için aydınlatma; veri sorumlusu (mağaza) / veri işleyen (biz) rollerinin sözleşmede netleşmesi; VERBİS durumu; 18 yaş altı veli onayı sürecinin (K8) mağaza prosedürüne bağlanması. Bu bir kod işi değil, **dış bağımlılık**; pilot başlamadan çözülmeli.

**E4 — Gerçek AI hiç canlı doğrulanmadı.** `ModelCatalog`/`TryOnModels` içinde hiçbir model `LiveVerified` değil; model kimlikleri, fiyatlar ve hata/ret yanıt şekilleri dokümana dayalı varsayımdır (Gemini güvenlik-ret şekli açıkça "varsayım"). **Ürün kalitesi (kaftan/bindallı nakış, kumaş, vücut oranı, yüz korunumu) bilinmiyor**; `OutputValidator` yalnızca makullük denetler, kaliteyi ölçmez. 4b'nin canlı testi (docs/12 §6: Seedream + GPT Image medium ≈ 0,05–0,06 $) ve gerçek ürün/fotoğraf benchmark'ı yapılmadan hangi modelin varsayılan olacağı belli değil. Gerçek modda serbest kombin KAPALI (`LiveFreeCombinationEnabled=false`): pilot kapsamı **tek kaftan / hazır takım** olmalı, müşteriye de böyle anlatılmalı. *Gerekli:* kullanıcı onaylı canlı test + ürün bazlı görsel kalite kabul kriteri.

**E5 — Gerçek mağaza TV'sinde uyumluluk denemesi yapılmadı.** TV uygulaması ES2019/eski CSS'e göre yazıldı ve Chromium'da taklit edildi (WebSocket'siz yedek dahil); gerçek TV tarayıcısında WebSocket/SSE, `localStorage`, CSS değişkenleri, otomatik yeniden bağlanma ve uzun süreli (gün boyu) çalışma denenmedi. TV modeli/marka/tarayıcı sürümü pilot mağazadan öğrenilmeli; gerekirse yedek: HDMI + Chromium/Android TV box.

**E6 — Üretim veritabanı/gizli yönetimi sertleştirmesi.** `sanalprova_app` en az yetki hesabı, append-only DENY, `sa` kapalı, SQL TLS, gizli yönetimi yalnızca test ortamında elle uygulandı; üretim için dağıtım betiği yok (DEVAM-NOTU A-5). Dağıtımda zorunlu gizliler: `Security:SetupTokenKey`, `Storage:SigningKey`, sağlayıcı anahtarları, DataProtection anahtarları (volume var), DB parolaları. Kredi defteri tetikleyicisi (append-only) migration ile gelir; yetki ayrımı olmadan "değiştirilemez" iddiası yalnızca tetikleyiciye dayanır.

## 3. Gerçek AI sağlayıcı entegrasyonunun durumu ve üretime geçiş eksikleri

**Durum:** kod tamam ve mock/sözleşme testlerinden geçti: `FalSeedreamTryOnProvider`, `FalGptImageTryOnProvider`, `PrunaTryOnProvider`, `FashnTryOnProvider` (pasif), `GeminiProvider`; model/kalite politikası firma ayarında (`/admin/ai-models`); çift-ücret korumaları (niyet/kimlik önce kalıcı, zaman aşımında aynı tahmin sorgulanır, belirsiz iş otomatik yeniden gönderilmez); bütçe + firma sınırı + kredi + `PaidAiEnabled` kapıları.

**Üretim kullanımı için eksikler:**
1. Canlı doğrulama (E4) ve kalite benchmark'ı; `LiveVerified` işaretlerinin gerçek yanıtlarla güncellenmesi, fiyat/model kimliklerinin teyidi.
2. Sağlayıcı hesapları/anahtarları (fal, gerekirse Pruna), sağlayıcı tarafı harcama limiti ve fatura takibi (fal/Pruna istek başına maliyet **döndürmez**: harcama tahmindir, gerçek tutar faturayla elle mutabık yapılır).
3. Sağlayıcı kesintisinde kullanıcı deneyimi (gerçek gecikme 30–180 sn; zaman aşımı/ret mesajları gerçek yanıtlarla sınanmadı).
4. Pilot açılış kontrol listesi (operatör): `PaidAiEnabled` aç, `Generation:UseMockProviders=false`, `Budget:AllowPaidCalls=true`, anahtarlar, firma bütçe sınırı (`platform budget`, pilot için düşük, örn. günlük 2–3 $), kredi yükle (`platform credits`), kredi tarifesi. **Şu an bu sıra hiçbir belgede tek listede yok** (runbook gerekir).
5. Çıktıda DEMO filigranı yalnızca mock'ta; gerçek çıktıda müşteriye "AI üretimi, gerçek ürünle birebir olmayabilir" uyarısı (ticari/hukuki metin kararı).

## 4. ÖNEMLİ

**Maliyet riskleri**
- **M1 — Kötü fotoğraf → ücretli kötü sonuç:** fotoğraf denetimi yön/çözünürlük/netlikle sınırlı; **kişi sayısı ve tam boy denetlenmez** (ekranda böyle yazıyor). Personel tekrar üretir, her deneme ücret + kredi. *Öneri:* çekim rehberi/silüet kılavuzu, personelin "fotoğraf uygun" onayı, ilk üretimde maliyet uyarısı.
- **M2 — Fiyat belirsiz:** kredi tarifesi varsayılan 1 kredi/üretim (sağlayıcı maliyeti 0,015–0,15 $). Ticari karar (paket/fiyat/iade/son kullanma) bekliyor; pilot için "mağazaya ücretsiz/sabit pilot kotası" ile manuel kredi yeterli.
- **M3 — Belirsiz sonuçlar:** mutabakat (`/platform/cost-cases`) yalnızca API; ekranı yok. Belirsiz işlerin kredisi karar verilene kadar bloke kalır; müşteri/mağaza kullanılabilir krediyi eksik görür. Pilot süresince günlük elle bakılmalı ya da basit platform ekranı yapılmalı.
- **M4 — UTC gün sınırı:** günlük bütçe/kota TR gece yarısıyla (UTC+3) örtüşmez; pilotta kabul edilebilir, bilinmeli.
- **M5 — Staff/Manager kalan krediyi göremez:** mağaza ortasında beklenmedik "Yeterli krediniz yok" (402). *Öneri:* Manager+/Staff için salt okunur "kalan kredi" ve düşük kredi uyarısı (küçük iş).

**Kullanıcı deneyimi**
- **U1 — Ekran kilidi/boşta otomatik kilit yok.** Dokümanda öngörülen PIN/`/auth/unlock` uygulanmadı (`PinHash` alanı kullanılmıyor); çerez 12 saat kayan. Müşteriye açık kalan tablette önceki provalar (geçmiş) ve yönetim ekranları görünür. *Öneri:* boşta N dk sonra kilit + PIN.
- **U2 — Katalog başlangıcı:** gerçek mağazanın ürünleri, her ürün için AI referans görseli (önden, temiz arka plan) ve varyantlar elle girilir; toplu içe aktarma yok; referans görsel gereksinimi/kılavuzu yazılı değil. Kurulum emeği ve yanlış referans → kötü sonuç riski. *Öneri:* kurulum hizmeti + ürün çekim kılavuzu + ilk 10–20 ürün için başlangıç paketi.
- **U3 — Sonucun müşteriye teslimi tanımsız:** müşteri sonucu telefonuna alamıyor (QR/paylaşım/indirme yok); 24 saat saklama KVKK'ya uygun ama "gösterim" dışında değer yok. Ürün kararı: yalnızca TV'de gösterim mi, QR ile kısa süreli indirme mi (KVKK etkisiyle).
- **U4 — Revizyon motoru yok:** "Uygula" sunucudan 501; ekran bunu açıkça belirtiyor (sahte sonuç yok). Renk değişimi/ince ayar pilotta beklenti yönetimiyle ertelenebilir.
- **U5 — Bekleme ekranı gerçek gecikmeyle denenmedi** (mock ≤ 300 ms ayarlı). İptal ve yeniden dene var; uzun beklemede personel yönlendirmesi gerçek testte ayarlanmalı.
- **U6 — TV eşleştirme** 6 haneli kodla (QR yok): kabul edilebilir; mağaza kurulumunda bir kerelik.

**Operasyon ve güvenlik**
- **O1 — Tüm operatör işleri sunucu kabuğunda CLI** (işletme açma, kredi yükleme, ücretli AI, bütçe, erişim kurtarma): pilot için kabul, ama yazılı runbook, iki kişilik yetki ve "ham çıktı yalnızca terminalde" disiplini gerekir. Mutabakat ve kredi için basit platform ekranları yoksa operatör yükü artar.
- **O2 — İzleme/uyarı yok:** yalnızca JSON konsol günlüğü + sağlık uç noktaları; merkezi günlük, uyarı (disk, DB, sağlayıcı hata oranı, bütçe %80, bekleyen belirsiz iş), çalışma süresi izleme yok.
- **O3 — Çok faktörlü doğrulama yok;** platform yöneticisi denetim kayıtlarında operatör kullanıcı kimliği boş (CLI); `reissue` için sunucu idempotency yok (bilinen, belgeli).
- **O4 — Şifre sıfırlama/e-posta yok:** personel şifresi yöneticiden bağlantıyla; Owner için operatör `reset-access`. Pilotta kabul; destek hattı tanımlanmalı.
- **O5 — Tek sunucu varsayımı:** SignalR yedek düzlemi (backplane), açık bağlantı kesme tek süreçli; pilot için sorun değil, ölçeklenirken değil.
- **O6 — Güvenlik doğrulaması:** Aşama 6 (penetrasyon/güvenlik testi, performans, KVKK kontrol listesi) yapılmadı; mevcut koruma katmanları (CSRF, çerez doğrulaması, firma/şube izolasyonu, imzalı kısa ömürlü görsel URL, EXIF silme, hız sınırları) testlidir ama bağımsız doğrulama yok.
- **O7 — Docker/CI:** CI #14 6/6 yeşil (compose smoke dahil) ✓; ancak üretim imajı/sürümleme/yayın boru hattı ve geri alma prosedürü yok.

## 5. SONRAYA bırakılabilir

M5.5 ödeme entegrasyonu (manuel kredi yeterli) · QR ile TV eşleştirme · revizyon motoru (SAM 2/OpenCV) · çok parçalı serbest kombin (gerçek model doğrulaması) · e-posta (davet/sıfırlama) · çoklu sunucu/backplane · S3 depo · MFA · HEIC desteği · kredi için paket/fiyat arayüzü · kullanım raporu geliştirmeleri · TR saat dilimli bütçe günü · Pruna/FASHN canlı doğrulaması (pilotta yalnızca seçilen model).

## 6. Önerilen çözüm sırası

**Faz A — Kod (pilot altyapısı, ~1–1,5 hafta):** ✅ **Yerelde uygulandı** ([docs/19](19-uretim-dagitim-runbook.md); commit'ler A-1…A-4). Docker bu makinede olmadığı için Compose/Caddy/TLS gerçek çalıştırma **doğrulanmadı** (CI `deploy-package` işi + docker'lı prova dağıtımı gerekir). Kalan: Faz B (dış bağımlılıklar) ve Faz C.
1. **E1** dağıtım paketi: frontend imajı + Caddy/TLS + `ForwardedHeaders` + üretim compose profili + dağıtım runbook'u.
2. **E2** yedek/geri yükleme betikleri (DB + depo) + geri yükleme provası + silme-kaydı prosedürü; disk/yedek izleme.
3. **E6** üretim DB sertleştirme betiği ve gizli yönetimi kontrol listesi (dağıtımla birlikte).
4. **O1/§3.4** operatör runbook'u: işletme açılışı, ücretli AI açma sırası, bütçe/kredi/mutabakat günlük kontrolü, olay yönetimi.

**Faz B — Dış bağımlılıklar (paralel, kod yok; takvimi belirler):**
5. **E3** hukuk: aydınlatma metni, hizmet sözleşmesi, DPA/yurt dışı aktarım, VERBİS, veli onayı süreci.
6. **E4** kullanıcı onaylı canlı AI testi + gerçek ürün/fotoğrafla kalite benchmark'ı + model/kalite kararı (kapsam: tek kaftan/hazır takım).
7. **E5** pilot mağazanın TV modeli/tablet/ağı bilgisi + gerçek TV denemesi (gerekirse yedek donanım).
8. Ticari kararlar: pilot kredi kotası/fiyat politikası (M2), sonucun müşteriye teslimi (U3).

**Faz C — Pilot öncesi küçük kod (~3–5 gün):** ✅ **Yerelde uygulandı** ([docs/20](20-faz-c-pilot-kullanim-ve-guvenlik.md)); sunucu tarafı foto onayı ve dış uyarı iletimi yapılmadı.
9. Boşta kilit + PIN (U1); fotoğraf çekim rehberi ve personel onayı (M1); kalan kredi göstergesi + düşük kredi uyarısı (M5); basit platform ekranları: mutabakat ve kredi/bütçe özeti (M3, O1); çıktı üzerinde "AI üretimi" ibaresi kararı (§3.5).
10. Katalog başlangıç paketi ve ürün çekim kılavuzu (U2); izleme/uyarı asgari seti (O2).

**Faz D — Pilot sırasında / sonrası:** gerçek maliyet–kalite verisiyle model ve tarife ayarı; belirsiz işlerin günlük mutabakatı; güvenlik testi (O6); ardından M5.5 ve "sonraya" listesi.

**Pilot giriş kriteri (öneri):** E1–E6'nın tamamı kapalı; canlı testte seçilen modelin pilot ürünlerinde kabul edilebilir görsel kalite; gerçek TV'de ≥ 1 tam gün sorunsuz çalışma; yedekten geri yükleme provası başarılı; operatör runbook'u ve acil durdurma (ücretli AI kapatma, kredi/bütçe sıfırlama) provası yapılmış.
