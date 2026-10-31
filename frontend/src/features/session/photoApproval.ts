/**
 * Personelin fotoğraf uygunluk onayının maddeleri. Onay SUNUCUDA kayıtlıdır (`POST /sessions/{id}/photos/{photoId}/approval`): ücretli üretim kabulü onay olmadan
 * 409 PHOTO_NOT_APPROVED verir. Bu bir otomatik analiz değildir (kişi sayısı/tam boy ölçülmez); personelin sorumluluk beyanıdır. Yeniden çekim yeni fotoğraf = onaysız.
 */
export const PHOTO_CHECKS = [
  'Tek kişi var; baştan ayağa tüm vücut görünüyor.',
  'Kollar gövdeden hafifçe açık, duruş dik ve önden.',
  'Görüntü net ve aydınlık; yüz ve giysiler gölgede değil.',
] as const;
