/**
 * İstemci tarafı şifre ön doğrulaması: sunucu politikasının (Identity: en az 10 karakter, büyük harf, küçük harf, rakam) aynısı; kullanıcıya hemen geri bildirim içindir.
 * Karar sunucudadır (PASSWORD_POLICY); burada geçse bile sunucu reddedebilir.
 */
export function validateNewPassword(password: string, again: string): string[] {
  const problems: string[] = [];
  if (password.length < 10) problems.push('En az 10 karakter olmalı.');
  if (!/\p{Ll}/u.test(password)) problems.push('En az bir küçük harf içermeli.');
  if (!/\p{Lu}/u.test(password)) problems.push('En az bir büyük harf içermeli.');
  if (!/\d/.test(password)) problems.push('En az bir rakam içermeli.');
  if (password.length > 128) problems.push('En fazla 128 karakter olabilir.');
  if (again !== password) problems.push('Şifre ve şifre tekrarı aynı olmalı.');
  return problems;
}
