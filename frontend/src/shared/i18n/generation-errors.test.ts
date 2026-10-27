import { describe, expect, it } from 'vitest';
import { errorMessage, tr } from './tr';

describe('üretim hata kodları Türkçe karşılık taşır', () => {
  it.each(['ADMISSION_BUSY', 'IDEMPOTENCY_KEY_REUSED', 'IDEMPOTENCY_KEY_REQUIRED', 'JOB_NOT_FAILED', 'QUOTA_EXCEEDED', 'SESSION_QUOTA_EXCEEDED', 'DUPLICATE_PRODUCT'])('%s', (code) => {
    expect(errorMessage(code)).not.toBe(tr.errors.UNKNOWN);
  });

  it('bilinmeyen kod genel iletiye düşer', () => {
    expect(errorMessage('YOK_BOYLE_KOD')).toBe(tr.errors.UNKNOWN);
  });
});
