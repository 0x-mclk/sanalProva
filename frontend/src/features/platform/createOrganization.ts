import { slugify } from '../../shared/text/slugify';

export type OrganizationForm = {
  name: string;
  slug: string;
  storeName: string;
  storeCode: string;
  ownerDisplayName: string;
  ownerEmail: string;
};

const SLUG = /^[a-z0-9]+(-[a-z0-9]+)*$/;
const STORE_CODE = /^[A-Za-z0-9]+(-[A-Za-z0-9]+)*$/;
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** Sunucu doğrulamasının (DataAnnotations) istemci yansıması: alan adı → Türkçe hata. Karar sunucudadır. */
export function validateOrganizationForm(form: OrganizationForm): Partial<Record<keyof OrganizationForm, string>> {
  const errors: Partial<Record<keyof OrganizationForm, string>> = {};
  const name = form.name.trim();
  if (name.length < 2 || name.length > 120) errors.name = 'Firma adı 2–120 karakter olmalı.';
  const slug = form.slug.trim();
  if (slug.length < 3 || slug.length > 60 || !SLUG.test(slug)) errors.slug = 'Kısa ad 3–60 karakter; küçük harf, rakam ve tire (örn. gul-gelinlik).';
  const storeName = form.storeName.trim();
  if (storeName.length < 2 || storeName.length > 120) errors.storeName = 'Şube adı 2–120 karakter olmalı.';
  const storeCode = form.storeCode.trim();
  if (storeCode.length < 1 || storeCode.length > 30 || !STORE_CODE.test(storeCode)) errors.storeCode = 'Şube kodu harf, rakam ve tire içermeli (en fazla 30).';
  const displayName = form.ownerDisplayName.trim();
  if (displayName.length < 2 || displayName.length > 80) errors.ownerDisplayName = 'Ad soyad 2–80 karakter olmalı.';
  const email = form.ownerEmail.trim();
  if (email.length > 254 || !EMAIL.test(email)) errors.ownerEmail = 'Geçerli bir e-posta adresi girin.';
  return errors;
}

/** Firma adından kısa ad önerisi (kullanıcı elle değiştirmediği sürece). */
export const suggestSlug = (name: string) => slugify(name).slice(0, 60);

/** Idempotency-Key: form açılışında üretilir (8–64 karakter); sonucu belirsiz hatada aynı anahtarla yeniden gönderilir. */
export const newIdempotencyKey = () => crypto.randomUUID().replaceAll('-', '');
