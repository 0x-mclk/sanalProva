/**
 * Ekran kilidi durumu (Faz C). Kilit bayrağı sessionStorage'dadır: sayfa yenilenince kilit AÇILMAZ (sekme/tarayıcı kapanınca zaten düşer).
 * Bu yalnızca arayüz kalkanıdır; kilidi açmanın tek yolu sunucuda doğrulanan PIN/parola'dır (POST /account/unlock).
 */
const LOCK_KEY = 'sp.locked';

export function readLocked(): boolean {
  try {
    return window.sessionStorage.getItem(LOCK_KEY) === '1';
  } catch {
    return false; // depolama engelli: yalnızca bellekteki durum geçerli
  }
}

export function writeLocked(locked: boolean) {
  try {
    if (locked) window.sessionStorage.setItem(LOCK_KEY, '1');
    else window.sessionStorage.removeItem(LOCK_KEY);
  } catch {
    // depolama engelli
  }
}
