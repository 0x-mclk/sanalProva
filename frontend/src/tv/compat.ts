const TOKEN_KEY = 'sp.tv.token';

/**
 * TV tarayıcısı uyumluluk denetimi: uygulamanın dayandığı özellikler yoksa anlaşılır bir mesaj döner (null = uyumlu).
 * WebSocket zorunlu değildir: SignalR yoksa/engelliyse SSE ve long polling'e düşer.
 */
export function checkCompatibility(): string | null {
  if (typeof fetch !== 'function' || typeof Promise === 'undefined' || typeof URLSearchParams === 'undefined') {
    return 'Bu TV tarayıcısı çok eski (ağ özellikleri eksik). TV yazılımını güncelleyin veya başka bir TV kullanın.';
  }
  if (typeof window.CSS === 'undefined' || typeof window.CSS.supports !== 'function' || !window.CSS.supports('color', 'var(--sp-test)')) {
    return 'Bu TV tarayıcısı çok eski (görünüm özellikleri eksik). TV yazılımını güncelleyin veya başka bir TV kullanın.';
  }
  try {
    window.localStorage.setItem('sp.tv.check', '1');
    window.localStorage.removeItem('sp.tv.check');
  } catch {
    return 'Bu tarayıcıda yerel depolama kapalı. TV tarayıcı ayarlarından çerez ve site verisine izin verin (eşleştirme bu cihazda saklanır).';
  }
  return null;
}

export const tvStorage = {
  get(): string | null {
    try {
      return window.localStorage.getItem(TOKEN_KEY);
    } catch {
      return null;
    }
  },
  set(token: string) {
    try {
      window.localStorage.setItem(TOKEN_KEY, token);
    } catch {
      // yazılamazsa TV oturum boyunca çalışır, yeniden açılınca tekrar eşleştirilir
    }
  },
  clear() {
    try {
      window.localStorage.removeItem(TOKEN_KEY);
    } catch {
      // yok sayılır
    }
  },
};
