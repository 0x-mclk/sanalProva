import { useEffect, useRef, useState } from 'react';
import { createHubConnection, hubPaths, HubConnectionState } from './hubConnection';

export type JobUpdatedMessage = {
  jobId: string;
  sessionId: string;
  status: string;
  stage: string | null;
  resultImageId: string | null;
  errorCode: string | null;
};

export type HubStatus = 'connecting' | 'connected' | 'offline';

/**
 * Oturum grubuna katılıp iş durumu bildirimlerini dinler (SignalR). Bildirim yalnızca "bir şey değişti" der;
 * çağıran güncel durumu REST'ten yeniden çeker. Bağlantı kopsa da arayüz yoklamayla çalışmaya devam eder:
 * SignalR yalnızca gecikmeyi azaltır, doğruluk kaynağı değildir.
 */
export function useSessionHub(sessionId: string | undefined, onJobUpdated: (message: JobUpdatedMessage) => void): HubStatus {
  const [status, setStatus] = useState<HubStatus>('connecting');
  const handler = useRef(onJobUpdated);
  useEffect(() => {
    handler.current = onJobUpdated;
  }, [onJobUpdated]);

  useEffect(() => {
    if (!sessionId) {
      return;
    }
    let active = true;
    const connection = createHubConnection(hubPaths.staff);
    const join = () => connection.invoke<boolean>('JoinSession', sessionId).catch(() => false);

    connection.on('JobUpdated', (message: JobUpdatedMessage) => {
      if (active) handler.current(message);
    });
    connection.onreconnecting(() => active && setStatus('connecting'));
    connection.onreconnected(() => {
      if (!active) return;
      setStatus('connected');
      void join(); // grup üyeliği bağlantıyla birlikte düşer
    });
    connection.onclose(() => active && setStatus('offline'));

    const started = connection
      .start()
      .then(async () => {
        if (!active) return;
        const joined = await join();
        setStatus(joined ? 'connected' : 'offline');
      })
      .catch(() => active && setStatus('offline'));

    return () => {
      active = false;
      // Kurulum sürerken durdurmak SignalR'da "bağlantı müzakere sırasında durduruldu" hatası üretir:
      // bağlantı kurulumu bitince kapatılır (ekran hızlı değişince / StrictMode çift çalıştırmasında).
      void started.finally(() => {
        if (connection.state !== HubConnectionState.Disconnected) {
          void connection.stop();
        }
      });
    };
  }, [sessionId]);

  return sessionId ? status : 'offline';
}
