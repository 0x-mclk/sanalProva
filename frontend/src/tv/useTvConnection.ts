import { useCallback, useEffect, useRef, useState } from 'react';
import { createHubConnection, hubPaths, HubConnectionState } from '../shared/realtime/hubConnection';
import { HEARTBEAT_MS, HIDE_AFTER_MS, HUB_RETRY_MS, POLL_FALLBACK_MS, StateSync } from './tvSync';
import { fetchState, TvAuthError, type TvState } from './tvApi';

export type Link = 'connecting' | 'online' | 'reconnecting' | 'offline';

type Connection = {
  /** Sunucunun bildirdiği son yetkili durum (null: henüz alınmadı). */
  view: TvState | null;
  link: Link;
  /** Sunucuyla uzun süredir temas yok: müşteri görseli gizlenir. */
  stale: boolean;
  /** Elle yenile (uzaktan kumanda "Tamam" tuşu). */
  refresh: () => void;
};

/**
 * TV'nin sunucuyla bağlantısı: SignalR (WebSocket → SSE → long polling) + REST durum çekme.
 * - Olaylar yalnızca tetikleyicidir; içerik her zaman cihaz belirteciyle REST'ten çekilir.
 * - Yeniden bağlanınca her zaman güncel durum çekilir; bağlantı yokken 15 sn'de bir REST ile denenir.
 * - Sürüm ve dönem korumaları eski verinin yeni görüntüyü ezmesini engeller (bkz. StateSync).
 */
export function useTvConnection(token: string, onRevoked: () => void): Connection {
  const [view, setView] = useState<TvState | null>(null);
  const [link, setLink] = useState<Link>('connecting');
  const [stale, setStale] = useState(false);
  const refreshRef = useRef<() => void>(() => undefined);
  const refreshNow = useCallback(() => refreshRef.current(), []);

  useEffect(() => {
    let active = true;
    let linkNow: Link = 'connecting';
    let lastContact = Date.now();
    let fetching = false;
    let dirty = false;
    let tick = 0;
    let retryTimer: number | undefined;
    const sync = new StateSync();

    const updateLink = (next: Link) => {
      linkNow = next;
      if (active) setLink(next);
    };

    const revoke = () => {
      if (!active) return;
      sync.reset();
      setView(null);
      onRevoked();
    };

    const refresh = async (): Promise<void> => {
      if (fetching) {
        dirty = true;
        return;
      }
      fetching = true;
      try {
        do {
          dirty = false;
          const epoch = sync.begin();
          let state: TvState;
          try {
            state = await fetchState(token);
          } catch (error) {
            if (error instanceof TvAuthError) {
              revoke();
            }
            return; // ağ hatası: mevcut durum korunur, yoklama yeniden dener
          }
          if (!active) return;
          lastContact = Date.now();
          setStale(false);
          if (sync.accept(epoch, state.version)) {
            // Aynı görselin bağlantısı korunur: imza her çekmede değişse de görsel yeniden yüklenip titremez
            setView((previous) => ({
              ...state,
              images: state.images.map((image) => {
                const known = previous?.images.find((p) => p.id === image.id);
                return known ? { ...image, url: known.url } : image;
              }),
            }));
          } else if (sync.isStale(epoch)) {
            dirty = true; // temizleme/iptal olayı sırasında başlayan yanıt atıldı: yeniden çek
          }
        } while (dirty);
      } finally {
        fetching = false;
      }
    };

    const connection = createHubConnection(hubPaths.display, { accessTokenFactory: () => token });

    connection.on('StateChanged', (message: { stateVersion: number }) => {
      if (active && sync.isNewer(message.stateVersion)) void refresh();
    });
    // Ekran boşaltıldı: görseli beklemeden gizle, sonra güncel durumu çek
    connection.on('Cleared', () => {
      if (!active) return;
      sync.invalidate();
      setView((current) => (current && current.mode !== 'Idle' ? { ...current, mode: 'Idle', images: [] } : current));
      void refresh();
    });
    connection.on('Revoked', () => {
      sync.invalidate();
      revoke();
    });
    connection.onreconnecting(() => updateLink('reconnecting'));
    connection.onreconnected(() => {
      updateLink('online');
      void refresh();
    });
    connection.onclose(() => {
      updateLink('offline');
      scheduleStart(HUB_RETRY_MS);
    });

    function scheduleStart(delay: number) {
      if (!active) return;
      window.clearTimeout(retryTimer);
      retryTimer = window.setTimeout(() => void start(), delay);
    }

    async function start() {
      if (!active || connection.state !== HubConnectionState.Disconnected) return;
      try {
        await connection.start();
        if (!active) return;
        updateLink('online');
        await refresh();
      } catch {
        updateLink('offline');
        await refresh(); // hub yoksa REST yedeği çalışır; belirteç iptalse burada anlaşılır
        scheduleStart(HUB_RETRY_MS);
      }
    }

    void start();
    void refresh();

    const heartbeat = window.setInterval(() => {
      if (connection.state === HubConnectionState.Connected) {
        connection.invoke('Heartbeat', Math.max(sync.version, 0)).catch(() => undefined);
      }
    }, HEARTBEAT_MS);

    const poll = window.setInterval(() => {
      tick++;
      // Hub yokken sık, varken seyrek (güvenlik ağı)
      if (linkNow !== 'online' || tick % 4 === 0) void refresh();
    }, POLL_FALLBACK_MS);

    const staleness = window.setInterval(() => {
      if (active && Date.now() - lastContact > HIDE_AFTER_MS) setStale(true);
    }, 5_000);

    refreshRef.current = () => void refresh();

    return () => {
      active = false;
      window.clearInterval(heartbeat);
      window.clearInterval(poll);
      window.clearInterval(staleness);
      window.clearTimeout(retryTimer);
      const stop = () => {
        if (connection.state !== HubConnectionState.Disconnected) void connection.stop();
      };
      // Kurulum sürerken durdurmak SignalR'da hata üretir: kurulum bitince kapatılır
      if (connection.state === HubConnectionState.Connecting) {
        window.setTimeout(stop, 500);
      } else {
        stop();
      }
    };
  }, [token, onRevoked]);

  return { view, link, stale, refresh: refreshNow };
}
