import { describe, expect, it } from 'vitest';
import { errorMessage, passwordPolicyTexts, tr } from './tr';

describe('M5.1 hata kodları Türkçe', () => {
  it.each([
    'AGREEMENT_ACCEPTANCE_REQUIRED',
    'AGREEMENT_VERSION_MISMATCH',
    'PAID_AI_NOT_ENABLED',
    'SETUP_LINK_INVALID',
    'PASSWORD_POLICY',
    'PLATFORM_ADMIN_REQUIRED',
    'SLUG_TAKEN',
    'EMAIL_TAKEN',
    'STORE_CODE_TAKEN',
    'OWNER_ALREADY_ACTIVATED',
  ])('%s tanımlıdır ve genel "beklenmeyen hata" mesajına düşmez', (code) => {
    expect(errorMessage(code)).not.toBe(tr.errors.UNKNOWN);
    expect(errorMessage(code)).toMatch(/[a-zçğıöşü]/i);
  });

  it('bağlantı geçersizliğinin nedeni (kullanılmış/iptal/süre) mesajda açıklanmaz', () => {
    expect(errorMessage('SETUP_LINK_INVALID')).not.toMatch(/kullanılmış|iptal edilmiş/i);
  });

  it('Identity şifre politikası kodları Türkçe maddelere çevrilir; bilinmeyen kod genel mesaj alır', () => {
    expect(passwordPolicyTexts(['PasswordTooShort', 'PasswordRequiresDigit', 'Bilinmeyen'])).toEqual([
      'En az 10 karakter olmalı.',
      'En az bir rakam içermeli.',
      'Şifre kurallara uymuyor.',
    ]);
  });
});
