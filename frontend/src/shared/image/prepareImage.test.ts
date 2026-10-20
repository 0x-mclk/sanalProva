import { describe, expect, it } from 'vitest';
import { MAX_UPLOAD_BYTES, fitWithin, validateImageFile } from './prepareImage';

describe('validateImageFile', () => {
  it('JPEG, PNG ve WebP kabul eder', () => {
    for (const type of ['image/jpeg', 'image/png', 'image/webp']) {
      expect(validateImageFile({ type, size: 1000 })).toEqual({ ok: true });
    }
  });

  it('desteklenmeyen türleri reddeder (HEIC, GIF, metin, PDF)', () => {
    for (const type of ['image/heic', 'image/gif', 'text/plain', 'application/pdf', '']) {
      expect(validateImageFile({ type, size: 1000 })).toEqual({ ok: false, code: 'UNSUPPORTED_IMAGE_TYPE' });
    }
  });

  it('boş ve çok büyük dosyaları reddeder', () => {
    expect(validateImageFile({ type: 'image/jpeg', size: 0 })).toEqual({ ok: false, code: 'IMAGE_CORRUPT' });
    expect(validateImageFile({ type: 'image/jpeg', size: MAX_UPLOAD_BYTES + 1 })).toEqual({ ok: false, code: 'IMAGE_TOO_LARGE' });
    expect(validateImageFile({ type: 'image/jpeg', size: MAX_UPLOAD_BYTES })).toEqual({ ok: true });
  });
});

describe('fitWithin', () => {
  it('uzun kenarı sınırlar ve en-boy oranını korur', () => {
    expect(fitWithin(4000, 3000, 2048)).toEqual({ width: 2048, height: 1536 });
    expect(fitWithin(3000, 4000, 2048)).toEqual({ width: 1536, height: 2048 });
  });

  it('küçük görseli büyütmez', () => {
    expect(fitWithin(900, 1500, 2048)).toEqual({ width: 900, height: 1500 });
  });

  it('en az 1 piksel döner', () => {
    expect(fitWithin(10000, 1, 100)).toEqual({ width: 100, height: 1 });
  });
});
