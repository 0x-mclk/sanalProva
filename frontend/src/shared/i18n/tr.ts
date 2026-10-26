/**
 * Türkçe arayüz metinleri. Tüm kullanıcıya görünen ortak metinler buradan gelir; ileride çoklu dil için hazır.
 * Kullanıcıya teknik terim (model, prompt, seed) gösterilmez.
 */
export const tr = {
  app: {
    name: 'Sanal Prova',
    aiDisclaimer:
      'Bu görsel yapay zekâ ile oluşturulmuş bir önizlemedir. Beden uyumu, kalıp ve nakış detayları gerçek üründen farklı olabilir.',
    colorPreview: 'Renk önizlemesi — stokta olmayabilir',
    offline: 'İnternet bağlantısı yok',
    tvNotConnected: 'Henüz bağlı değil',
  },
  steps: {
    tryOn: ['Fotoğraf', 'Prova türü', 'Kıyafet'],
  },
  /** Oturumun bir sonraki adımı → kısa durum metni (devam eden prova listesi). */
  sessionStep: {
    Consent: 'Onay bekliyor',
    Photo: 'Fotoğraf çekimi',
    Mode: 'Prova türü seçimi',
    Outfit: 'Kıyafet seçimi',
    Generating: 'Üretiliyor',
    Result: 'Sonuç hazır',
    Closed: 'Kapatıldı',
  } as Record<string, string>,
  /** Backend ProblemDetails `code` alanı → kullanıcı mesajı. */
  errors: {
    NETWORK_ERROR: 'Sunucuya ulaşılamadı. İnternet bağlantınızı kontrol edin.',
    CSRF_INVALID: 'Güvenlik doğrulaması başarısız. Sayfayı yenileyip tekrar deneyin.',
    INVALID_CREDENTIALS: 'E-posta veya parola hatalı.',
    ACCOUNT_LOCKED: 'Çok fazla hatalı deneme. Hesap geçici olarak kilitlendi.',
    STORE_REQUIRED: 'Önce çalışacağınız şubeyi seçin.',
    STORE_FORBIDDEN: 'Bu şubeye erişim yetkiniz yok.',
    INSUFFICIENT_ROLE: 'Bu işlem için yetkiniz yok.',
    CONSENT_REQUIRED: 'Devam etmek için müşteri onayı gerekli.',
    CONSENT_INCOMPLETE: 'Fotoğrafın işlenmesi ve yapay zekâ sağlayıcısına aktarılması için onay gerekli.',
    GUARDIAN_CONSENT_REQUIRED: '18 yaş altı müşteri için veli veya yasal temsilci onayı gerekli.',
    NOTICE_OUTDATED: 'Bilgilendirme metni güncellendi. Sayfayı yenileyin.',
    CONSENT_EXISTS: 'Bu prova için onay zaten alınmış.',
    SESSION_CLOSED: 'Bu prova kapatılmış.',
    PHOTO_LIMIT: 'Bu prova için en fazla 5 fotoğraf yüklenebilir. Eskisini silin.',
    NO_PHOTO: 'Önce müşterinin fotoğrafı yüklenmeli.',
    UNSUPPORTED_IMAGE_TYPE: 'Yalnızca JPEG, PNG veya WebP görseller yüklenebilir.',
    IMAGE_TOO_LARGE: 'Dosya çok büyük (en fazla 15 MB).',
    IMAGE_TOO_SMALL: 'Görselin çözünürlüğü çok düşük. Daha yüksek kalitede çekin.',
    IMAGE_CORRUPT: 'Görsel okunamadı. Başka bir dosya deneyin.',
    IMAGE_DIMENSIONS_TOO_LARGE: 'Görselin çözünürlüğü çok yüksek.',
    EMPTY_SELECTION: 'Önce kıyafet seçin.',
    INVALID_COMBINATION: 'Seçilen kombin geçerli değil. Çakışan parçaları kaldırın.',
    PRODUCT_NOT_READY: 'Seçilen ürünlerden birinin yapay zekâ referans görseli yok.',
    WRONG_PRODUCT_TYPE: 'Bu modda seçilen ürün türü uygun değil.',
    TOO_MANY_IN_CATEGORY: 'Bu kategoriden daha fazla ürün seçilemez.',
    UNKNOWN_PRODUCT: 'Seçilen ürün bulunamadı veya pasif.',
    UNKNOWN_VARIANT: 'Seçilen renk bu ürüne ait değil.',
    QUOTA_EXCEEDED: 'Bu şubenin günlük üretim kotası doldu.',
    SESSION_QUOTA_EXCEEDED: 'Bu prova için üretim sınırına ulaşıldı.',
    ADMISSION_BUSY: 'Şu an çok fazla istek var. Birkaç saniye sonra tekrar deneyin.',
    JOB_NOT_CANCELLABLE: 'İş zaten işleniyor veya tamamlandı; iptal edilemez.',
    JOB_NOT_FAILED: 'Yalnızca başarısız işler yeniden denenebilir.',
    NOT_IMPLEMENTED: 'Revizyon motoru henüz etkin değil. Yeni bir kombin oluşturabilirsiniz.',
    DUPLICATE_CODE: 'Bu kod zaten kullanılıyor.',
    DUPLICATE_SLUG: 'Bu kısa ad başka bir kategoride kullanılıyor.',
    UNKNOWN_CATEGORY: 'Kategori bulunamadı.',
    PRODUCT_TYPE_LOCKED: 'Takım bileşeni olan ürünün tipi değiştirilemez.',
    NOT_AN_OUTFIT_SET: 'Yalnızca hazır takımların bileşenleri olur.',
    INVALID_COMPONENTS: 'Bileşen listesi geçersiz.',
    PAIRING_CODE_INVALID: 'Kod geçersiz veya süresi dolmuş. TV ekranındaki güncel kodu girin.',
    CLAIM_LOCKED: 'Çok fazla hatalı kod denemesi yapıldı. Bir süre sonra tekrar deneyin.',
    PAIRING_BUSY: 'Şu anda çok fazla eşleştirme bekliyor. Biraz sonra tekrar deneyin.',
    DISPLAY_IN_USE: 'Bu TV başka bir provaya bağlı.',
    SESSION_ON_OTHER_DISPLAY: 'Bu prova başka bir TV ekranına bağlı.',
    NO_SESSION: 'Önce prova oturumunu bu TV ile eşleyin.',
    STATE_CONFLICT: 'TV durumu başka biri tarafından değiştirildi. Tekrar deneyin.',
    INVALID_MODE: 'Geçersiz gösterim modu.',
    INVALID_IMAGES: 'Gösterim için uygun sayıda görsel seçilmedi.',
    IMAGE_NOT_IN_SESSION: 'Seçilen görsel bu TV ile eşlenen provaya ait değil.',
    INVALID_QUALITY: 'Çözünürlük 1K veya 2K, mod hızlı/dengeli/yüksek kalite olmalı.',
    MODEL_NOT_ALLOWED: 'Seçilen AI modeli kullanıma açık değil. Yöneticinize danışın.',
    QUALITY_NOT_ALLOWED: 'Seçilen kalite bu model için kullanıma açık değil.',
    QUALITY_OVER_BUDGET: 'Seçilen kalitenin en kötü durum maliyeti iş başı bütçe sınırını aşıyor. Daha düşük bir kalite seçin.',
    FREE_COMBINATION_DISABLED: 'Çok parçalı kombin henüz etkin değil. Tek kaftan veya hazır takım deneyin.',
    INVALID_AI_MODELS: 'Model ayarları geçersiz. Varsayılan model etkin olmalı ve her etkin modelde en az bir kalite seçilmeli.',
    NOT_FOUND: 'Kayıt bulunamadı.',
    BAD_REQUEST: 'İstek geçersiz.',
    PAYLOAD_TOO_LARGE: 'Gönderilen dosya çok büyük.',
    HTTP_400: 'Girilen bilgiler geçersiz. Alanları kontrol edin.',
    HTTP_401: 'Oturumunuzun süresi doldu. Lütfen tekrar giriş yapın.',
    HTTP_403: 'Bu işlem için yetkiniz yok.',
    HTTP_404: 'Kayıt bulunamadı.',
    HTTP_429: 'Çok fazla istek gönderildi. Biraz bekleyip tekrar deneyin.',
    UNKNOWN: 'Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.',
  },
} as const;

export function errorMessage(code: string | undefined): string {
  return (code && (tr.errors as Record<string, string>)[code]) || tr.errors.UNKNOWN;
}

const relative = new Intl.RelativeTimeFormat('tr-TR', { numeric: 'auto' });

/** "2 dk önce", "dün" gibi kısa göreli zaman. */
export function timeAgo(iso: string, now = Date.now()): string {
  const seconds = Math.round((new Date(iso).getTime() - now) / 1000);
  const abs = Math.abs(seconds);
  if (abs < 45) return 'az önce';
  if (abs < 3600) return relative.format(Math.round(seconds / 60), 'minute');
  if (abs < 86400) return relative.format(Math.round(seconds / 3600), 'hour');
  return relative.format(Math.round(seconds / 86400), 'day');
}

/** Saklama süresi bitimi için okunabilir metin: "yarın 14:30" gibi. */
export function retentionText(iso: string): string {
  const date = new Date(iso);
  return new Intl.DateTimeFormat('tr-TR', { day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit' }).format(date);
}
