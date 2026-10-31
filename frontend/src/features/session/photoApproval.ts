/**
 * Personelin "fotoğraf prova için uygun" onayı (Faz C). Fotoğraf kimliğine bağlıdır (fotoğraf değişince onay geçersizdir) ve yalnızca bu sekmede (sessionStorage) tutulur.
 * Bu bir İŞ AKIŞI kontrolüdür (yanlış/uygunsuz fotoğrafla ücretli üretimi azaltır), güvenlik sınırı DEĞİLDİR: sunucu fotoğrafı yine yön/çözünürlük/netlik açısından denetler.
 */
const KEY_PREFIX = 'sp.photoApproved.';

export function isPhotoApproved(photoId: string | undefined): boolean {
  if (!photoId) return false;
  try {
    return window.sessionStorage.getItem(KEY_PREFIX + photoId) === '1';
  } catch {
    return false;
  }
}

export function setPhotoApproved(photoId: string, approved: boolean) {
  try {
    if (approved) window.sessionStorage.setItem(KEY_PREFIX + photoId, '1');
    else window.sessionStorage.removeItem(KEY_PREFIX + photoId);
  } catch {
    // depolama engelli: onay bu sayfa ömrüyle sınırlı kalır
  }
}

/** Onay kutuları (hepsi işaretlenmeden onay verilmez). */
export const PHOTO_CHECKS = [
  'Tek kişi var; baştan ayağa tüm vücut görünüyor.',
  'Kollar gövdeden hafifçe açık, duruş dik ve önden.',
  'Görüntü net ve aydınlık; yüz ve giysiler gölgede değil.',
] as const;
