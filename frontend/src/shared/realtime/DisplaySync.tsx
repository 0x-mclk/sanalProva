import { useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { displayKeys } from '../api/displays';
import { useAuth } from '../auth/AuthContext';
import { createHubConnection, hubPaths, HubConnectionState } from './hubConnection';

/**
 * Personel uygulamasında şubenin TV olaylarını dinler (SignalR, `JoinStore`). Olay yalnızca "bir TV'nin durumu değişti" der;
 * güncel liste REST'ten çekilir (sunucu-otoritatif). Bağlantı koparsa `useDisplays` 30 sn'lik yoklamayla çalışmaya devam eder.
 */
export function DisplaySync() {
  const { activeStore } = useAuth();
  const client = useQueryClient();
  const storeId = activeStore?.id;

  useEffect(() => {
    if (!storeId) {
      return;
    }
    let active = true;
    const connection = createHubConnection(hubPaths.staff);
    const refresh = () => void client.invalidateQueries({ queryKey: displayKeys.all });
    const join = () => connection.invoke<boolean>('JoinStore', storeId).catch(() => false);

    connection.on('DisplayStatusChanged', () => active && refresh());
    connection.onreconnected(() => {
      if (!active) return;
      void join().then(refresh); // grup üyeliği bağlantıyla birlikte düşer; kaçırılan olaylar için tazele
    });

    const started = connection
      .start()
      .then(async () => {
        if (active) await join();
      })
      .catch(() => undefined);

    return () => {
      active = false;
      void started.finally(() => {
        if (connection.state !== HubConnectionState.Disconnected) {
          void connection.stop();
        }
      });
    };
  }, [storeId, client]);

  return null;
}
