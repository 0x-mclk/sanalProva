import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { qk } from '../../shared/api/hooks';
import { useLockStatus } from '../../shared/api/lock';
import { readLocked, writeLocked } from '../../shared/auth/lockState';
import { LockScreen } from './LockScreen';

const ACTIVITY_EVENTS = ['pointerdown', 'keydown', 'touchstart', 'wheel'] as const;
const CHECK_INTERVAL_MS = 5_000;
const DEFAULT_IDLE_SECONDS = 300;

type LockControl = { lock: () => void };
const LockContext = createContext<LockControl>({ lock: () => undefined });

/** Ana ekran menüsündeki "Ekranı kilitle" için. */
export function useScreenLock(): LockControl {
  return useContext(LockContext);
}

/**
 * Ekran kilidi (Faz C): boşta kalınca (sunucu yapılandırması, varsayılan 5 dk) veya elle kilitlenince içerik (geçmiş provalar, müşteri fotoğrafları, yönetim ekranları)
 * DOM'dan tamamen kaldırılır ve sorgu önbelleği temizlenir; yalnızca kilit ekranı görünür. Kilit bayrağı sessionStorage'dadır (sayfa yenileme kilidi açmaz).
 * Açma yalnızca sunucuda doğrulanan PIN/parola iledir. Bu bir ARAYÜZ kalkanıdır: oturum çerezi ve sunucu yetkisi değişmez.
 * Durum okunamazsa (ağ hatası) varsayılan süreyle yine kilitler (güvenli yön).
 */
export function IdleLock({ children }: { children: ReactNode }) {
  const client = useQueryClient();
  const status = useLockStatus();
  const idleMs = (status.data?.idleLockSeconds ?? DEFAULT_IDLE_SECONDS) * 1000;
  const [locked, setLocked] = useState(readLocked);
  const lastActivity = useRef(0);

  const lock = useCallback(() => {
    writeLocked(true);
    setLocked(true);
    // Müşteri verisi bellekte de kalmaz; kimlik ve kilit durumu korunur
    client.removeQueries({ predicate: (query) => query.queryKey[0] !== qk.me[0] && query.queryKey[0] !== 'lock' });
  }, [client]);

  const unlocked = useCallback(() => {
    writeLocked(false);
    lastActivity.current = Date.now();
    setLocked(false);
  }, []);

  useEffect(() => {
    if (locked) {
      return undefined;
    }
    lastActivity.current = Date.now();
    const touch = () => {
      lastActivity.current = Date.now();
    };
    for (const name of ACTIVITY_EVENTS) window.addEventListener(name, touch, { passive: true });
    const check = () => {
      if (Date.now() - lastActivity.current >= idleMs) lock();
    };
    const timer = window.setInterval(check, CHECK_INTERVAL_MS);
    // Tablet uykudan dönünce zamanlayıcı beklemeden denetlenir
    const onVisible = () => {
      if (document.visibilityState === 'visible') check();
    };
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      for (const name of ACTIVITY_EVENTS) window.removeEventListener(name, touch);
      window.clearInterval(timer);
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, [locked, idleMs, lock]);

  const control = useMemo<LockControl>(() => ({ lock }), [lock]);

  if (locked) {
    return <LockScreen hasPin={Boolean(status.data?.hasPin)} onUnlocked={unlocked} />;
  }
  return <LockContext.Provider value={control}>{children}</LockContext.Provider>;
}
