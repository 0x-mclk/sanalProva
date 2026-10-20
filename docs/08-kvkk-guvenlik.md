# 08 — KVKK ve Güvenlik

> Bu bölüm teknik tasarım içindir; **hukuki görüş değildir.** Aydınlatma metni, açık rıza metni, VERBİS kaydı ve yurt dışı aktarım mekanizması canlıya çıkmadan önce bir KVKK uzmanı/avukat tarafından onaylanmalıdır.

## 1. İşlenen kişisel veriler

| Veri | Nerede | Hassasiyet |
|---|---|---|
| Müşteri boydan fotoğrafı (yüz dahil) | `session_photos` + depolama | Kişisel veri. Yüz görüntüsü; **benzersiz tanıma amacıyla işlenmediği sürece** biyometrik sayılmaması öngörülür — yüz tanıma/eşleştirme yapılmayacak (bkz. 07 §6) |
| AI üretilmiş görseller | `generated_images` | Müşteriye ait kişisel veri olarak muamele görür |
| Takma ad | `tryon_sessions.customer_label` | Düşük; isteğe bağlı, zorunlu değil |
| Onay kaydı | `consent_records` | İspat amacıyla |
| Personel hesapları, IP | Identity, audit | Çalışan verisi |

## 2. Hukuki dayanak ve onay akışı (öneri)

1. **Aydınlatma** (KVKK m.10): "Yeni Prova" ekranında kısa metin + "Detaylı bilgi" bağlantısı. Sürümlü (`notice_version`).
2. **Açık rıza** — ayrı kutucuklar, önceden işaretli değil:
   - ☐ Fotoğrafımın sanal prova için işlenmesi
   - ☐ Fotoğrafımın yapay zekâ hizmet sağlayıcılarına (yurt dışı) aktarılması
   - ☐ (ops.) Görsellerimin 24 saatten uzun (en fazla 30 gün) saklanması
   - ☐ (18 yaş altı) Veli/vasi onayı
3. İlk iki kutu işaretlenmeden fotoğraf **yüklenemez** (sunucu da doğrular, `409 CONSENT_REQUIRED`).
4. Onay yöntemi kaydedilir: müşteri tablete dokundu / personel sözlü onayı teyit etti.
5. **Onayın geri alınması:** Tek dokunuşla oturum ve tüm görseller anında silinir; TV temizlenir.

## 3. Yurt dışına aktarım

FASHN ve Google Gemini Türkiye dışında işleme yapar. 2024 değişikliği sonrası KVKK m.9 kapsamında olası yollar: yeterlilik kararı, **standart sözleşme** (imzadan sonra Kuruma bildirim süresi var), bağlayıcı şirket kuralları veya arızi aktarımda açık rıza. Teknik olarak planlananlar:

- Sağlayıcı başına veri işleme sözleşmesi / DPA ve standart sözleşme durumunun belgelenmesi (Aşama 4 öncesi ön koşul).
- **Veri minimizasyonu:** Sağlayıcıya yalnızca gerekli görsel gider; EXIF ve metadata silinir; dosya adı rastgele; müşteri adı/takma adı **asla** gönderilmez.
- FASHN'da `return_base64: true` (sağlayıcı tarafı saklama 60 dk); Gemini ücretli katman.
- Sağlayıcı seçimi yapılandırmada; ileride yurt içi/özel sunucu modeline geçiş mümkün.
- Mock modda hiçbir veri dışarı çıkmaz (geliştirme ve demo varsayılanı).

## 4. Saklama ve otomatik silme

| Veri | Varsayılan süre | Uzatılmış (ek onayla) |
|---|---|---|
| Müşteri fotoğrafı + üretilen görseller | Oturum kapandıktan sonra **24 saat**; kapanmayan oturum son etkinlikten 24 saat sonra | En fazla 30 gün |
| Onay kaydı | Oturumla silinir; anonim istatistik (tarih, sürüm, yöntem) kalabilir | — |
| `ai_usage_logs` | 2 yıl (kişisel veri içermez, `job_id` NULL'lanır) | — |
| `audit_logs` | 1 yıl | — |
| Sunucu logları | 30 gün | — |

- `RetentionJob` (15 dk'da bir) süresi dolanları: depodan siler → veritabanından siler → audit kaydı yazar.
- Müşteri görselleri **yedeklere alınmaz** (depolama ayrı birim / yedek dışı).
- Silme talebi: personel "Oturumu şimdi sil" ile anında.

## 5. Erişim kontrolü

- Rol + mağaza bazlı yetki; her sorguda kiracı filtresi (EF global query filter) ve **entegrasyon testleriyle** kanıtlanmış izolasyon.
- Görseller yalnızca HMAC imzalı, 5 dk geçerli URL ile; dizin listeleme yok; tahmin edilemez anahtarlar.
- TV yalnızca kendisine bağlı oturumun görsellerine erişir.
- Personel ekranı boşta 5 dk sonra PIN kilidi (açık tablet riski).
- Görsel görüntüleme ve indirme audit'e yazılır. İndirme özelliği MVP'de **yok**.

## 6. Uygulama güvenliği

| Alan | Önlem |
|---|---|
| Kimlik | Identity parola politikası, hesap kilitleme, giriş rate limit; ileride Owner için 2FA |
| Oturum | HttpOnly, Secure, SameSite=Strict cookie; antiforgery |
| Gizli anahtarlar | Yalnızca backend: user-secrets / ortam değişkeni; repoda `.env` yok; anahtar rotasyonu dokümante |
| Dosya yükleme | Boyut sınırı, MIME + magic byte kontrolü, yeniden kodlama (polyglot dosyaları etkisizleştirir), rastgele dosya adı, web kökü dışında |
| Girdi doğrulama | FluentValidation; tüm ID'ler sahiplik kontrolüyle |
| Taşıma | HTTPS zorunlu, HSTS; SignalR WSS |
| Başlıklar | CSP, `X-Content-Type-Options`, `Referrer-Policy: no-referrer`, `Permissions-Policy` (yalnızca kamera) |
| İç servis | image-worker yalnızca iç ağda + paylaşılan anahtar |
| Bağımlılıklar | `dotnet list package --vulnerable`, `npm audit`, `pip-audit` CI'da |
| Loglama | Serilog destructuring politikası: görsel içeriği, base64, imzalı URL, e-posta, takma ad, token **loglanmaz**; yalnızca ID'ler. Sağlayıcı hata gövdeleri kırpılarak ve temizlenerek loglanır |

## 7. Ürün iddiaları

- UI hiçbir yerde "bedeninize uygun", "birebir" gibi ifade kullanmaz; sorumluluk metinleri [05 §8](05-frontend-ekranlar.md#8-sorumluluk-metinleri-ui).
- Renk önizlemeleri ayrı etiketlenir (katalogda olmayan ürün izlenimi verilmez).
- TV'de "Yapay zekâ önizlemesi" etiketi.

## 8. Canlıya çıkış öncesi uyum kontrol listesi

- [ ] Aydınlatma ve açık rıza metinleri hukuki onaydan geçti
- [ ] VERBİS yükümlülüğü değerlendirildi
- [ ] FASHN ve Google için aktarım mekanizması (standart sözleşme vb.) tamamlandı ve gerekli bildirim yapıldı
- [ ] Kişisel veri envanteri ve saklama-imha politikası yazıldı
- [ ] Otomatik silme testle doğrulandı
- [ ] Penetrasyon / güvenlik testi yapıldı
- [ ] Personel eğitimi (onay alma, TV kullanımı)
