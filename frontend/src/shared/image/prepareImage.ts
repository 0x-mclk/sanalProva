/** Müşteri fotoğrafı yükleme öncesi istemci tarafı kontroller. Sunucu tüm bunları yeniden doğrular (güvenlik sunucudadır). */

export const ACCEPTED_IMAGE_TYPES = ['image/jpeg', 'image/png', 'image/webp'] as const;
export const MAX_UPLOAD_BYTES = 15 * 1024 * 1024;
export const MAX_LONG_SIDE = 2048;

export type ImageCheck = { ok: true } | { ok: false; code: 'UNSUPPORTED_IMAGE_TYPE' | 'IMAGE_TOO_LARGE' | 'IMAGE_CORRUPT' };

/** Dosya türü ve boyutu: yanlış dosyada kullanıcı yüklemeyi beklemeden hemen uyarılır. */
export function validateImageFile(file: Pick<Blob, 'type' | 'size'>): ImageCheck {
  if (!(ACCEPTED_IMAGE_TYPES as readonly string[]).includes(file.type)) {
    return { ok: false, code: 'UNSUPPORTED_IMAGE_TYPE' };
  }
  if (file.size > MAX_UPLOAD_BYTES) {
    return { ok: false, code: 'IMAGE_TOO_LARGE' };
  }
  if (file.size === 0) {
    return { ok: false, code: 'IMAGE_CORRUPT' };
  }
  return { ok: true };
}

/** En-boy oranını koruyarak uzun kenarı sınırlar; küçük görseli büyütmez. */
export function fitWithin(width: number, height: number, maxLongSide: number): { width: number; height: number } {
  const longSide = Math.max(width, height);
  if (longSide <= maxLongSide) {
    return { width, height };
  }
  const scale = maxLongSide / longSide;
  return { width: Math.max(1, Math.round(width * scale)), height: Math.max(1, Math.round(height * scale)) };
}

/**
 * Yükleme öncesi: EXIF yönünü uygular, uzun kenarı 2048 px'e indirir ve JPEG olarak yeniden kodlar
 * (EXIF/konum bilgisi tarayıcıda zaten silinir, yükleme süresi kısalır). Tarayıcı desteklemiyorsa orijinali döner.
 */
export async function prepareImage(file: Blob): Promise<Blob> {
  if (typeof createImageBitmap !== 'function' || typeof document === 'undefined') {
    return file;
  }
  let bitmap: ImageBitmap;
  try {
    bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
  } catch {
    return file; // çözülemeyen dosyayı sunucu doğrular ve açıklayıcı hata döner
  }

  try {
    const { width, height } = fitWithin(bitmap.width, bitmap.height, MAX_LONG_SIDE);
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    const context = canvas.getContext('2d');
    if (!context) {
      return file;
    }
    context.drawImage(bitmap, 0, 0, width, height);
    const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/jpeg', 0.9));
    return blob ?? file;
  } finally {
    bitmap.close();
  }
}
