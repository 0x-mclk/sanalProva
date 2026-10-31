import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { qk } from '../../shared/api/hooks';
import { lockKeys, sendActivity, sendLock, useLockStatus } from '../../shared/api/lock';
import { readLocked, writeLocked } from '../../shared/auth/lockState';
import { LoadingState } from '../../shared/ui';
import { LockScreen } from './LockScreen';

const ACTIVITY_EVENTS = ['pointerdown', 'keydown', 'touchstart', 'wheel'] as const;
const CHECK_INTERVAL_MS = 5_000;
const HEARTBEAT_MIN_GAP_MS = 20_000;
const DEFAULT_IDLE_SECONDS = 300;

/** Herhangi bir istek 423 SCREEN_LOCKED alırsa (başka sekmede/cihazda kilitlendi) uygulama bu olayla kilit ekranına geçer. */
export const SCREEN_LOCKED_EVENT = 'sp:screen-locked';

type LockControl = { lock: () => void };
const LockContext = createContext<LockControl>({ lock: () => undefined });

/** Ana ekran menüsündeki "Ekranı kilitle" için. */
export function useScreenLock(): LockControl {
  return useContext(LockContext);
}

/**
 * Ekran kilidi: kilit durumunun SAHİBİ SUNUCUDUR (oturum satırı; kilitliyken API 423 SCREEN_LOCKED verir). Bu bileşen onu arayüze yansıtır:
 * boşta kalınca (sunucu yapılandırması, varsayılan 5 dk) veya elle kilitlenince sunucuyu kilitler, içeriği DOM'dan kaldırır ve önbelleği temizler.
 * Sayfa yenileme, doğrudan URL ve ikinci sekme sunucu durumunu okur (yerel bayrak yalnızca sunucuya ulaşılamadığında güvenli yön olarak kalır): kilit atlatılamaz.
 * Etkileşim sinyali sunucuya düzenli iletilir; istemci durursa sunucu kendisi kilitler (süre + tolerans).
 */
export function IdleLock({ children }: { children: ReactNode }) {
  const client = useQueryClient();
  const status = useLockStatus();
  const idleMs = (status.data?.idleLockSeconds ?? DEFAULT_IDLE_SECONDS) * 1000;
  const [localLocked, setLocalLocked] = useState(readLocked);
  const lastActivity = useRef(0);
  const lastBeat = useRef(0);
  const sawServerLocked = useRef(false);

  const serverLocked = status.data?.locked === true;
  const locked = localLocked || serverLocked;

  const lock = useCallback(() => {
    writeLocked(true);
    setLocalLocked(true);
    // Müşteri verisi bellekte de kalmaz; kimlik ve kilit durumu korunur
    client.removeQueries({ predicate: (query) => query.queryKey[0] !== qk.me[0] && query.queryKey[0] !== 'lock' });
    void sendLock().then(() => client.invalidateQueries({ queryKey: lockKeys.status }));
  }, [client]);

  const unlocked = useCallback(() => {
    writeLocked(false);
    sawServerLocked.current = false;
    lastActivity.current = Date.now();
    setLocalLocked(false);
    void client.invalidateQueries({ queryKey: lockKeys.status });
  }, [client]);

  useEffect(() => {
    if (serverLocked) sawServerLocked.current = true;
  }, [serverLocked]);

  // Başka sekmede kilit açıldıysa (sunucu artık kilitli değil) bu sekmenin yerel bayrağı da kalkar; sunucuya ulaşılamamış bir kilit (hiç kilitli görülmedi) KALIR
  useEffect(() => {
    if (status.data && !serverLocked && sawServerLocked.current && localLocked) {
      writeLocked(false);
      sawServerLocked.current = false;
      lastActivity.current = Date.now();
      setLocalLocked(false);
    }
  }, [status.data, serverLocked, localLocked]);

  // Yerel kilit var ama sunucu hâlâ kilitli değil (kilit isteği ulaşmadı): her yoklamada yeniden denenir (kilit yakınsar)
  useEffect(() => {
    if (localLocked && status.data && !serverLocked) {
      void sendLock().then(() => client.invalidateQueries({ queryKey: lockKeys.status }));
    }
  }, [localLocked, status.data, serverLocked, status.dataUpdatedAt, client]);

  // Başka bir istek 423 aldı: sunucu oturumu kilitli
  useEffect(() => {
    const onLocked = () => {
      writeLocked(true);
      setLocalLocked(true);
      client.removeQueries({ predicate: (query) => query.queryKey[0] !== qk.me[0] && query.queryKey[0] !== 'lock' });
      void client.invalidateQueries({ queryKey: lockKeys.status });
    };
    window.addEventListener(SCREEN_LOCKED_EVENT, onLocked);
    return () => window.removeEventListener(SCREEN_LOCKED_EVENT, onLocked);
  }, [client]);

  useEffect(() => {
    if (locked) {
      return undefined;
    }
    lastActivity.current = Date.now();
    const touch = () => {
      const now = Date.now();
      lastActivity.current = now;
      if (now - lastBeat.current >= HEARTBEAT_MIN_GAP_MS) {
        lastBeat.current = now;
        void sendActivity().then((result) => {
          if (result === 'locked') window.dispatchEvent(new Event(SCREEN_LOCKED_EVENT));
        });
      }
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
  // Durum bilinmeden içerik çizilmez: sayfa yenilemede sunucu kilitliyse müşteri verisi bir an bile görünmesin
  if (status.isLoading) {
    return (
      <main className="sp-page">
        <LoadingState label="Oturum doğrulanıyor" />
      </main>
    );
  }
  return <LockContext.Provider value={control}>{children}</LockContext.Provider>;
}
