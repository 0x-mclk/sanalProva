import { useCallback, useEffect, useRef, useState } from 'react';
import { createHubConnection, hubPaths, HubConnectionState } from '../../shared/realtime/hubConnection';
import type { TvConnectionState } from '../../shared/ui';

type Pong = { message: string; serverTime: string; connectionId: string };

/** Geliştirme tanılama hub'ına bağlanır; SignalR'ın proxy ve WebSocket üzerinden uçtan uca çalıştığını doğrular. */
export function useDiagnosticsHub() {
  const connectionRef = useRef<ReturnType<typeof createHubConnection> | null>(null);
  const [state, setState] = useState<TvConnectionState>('connecting');
  const [transport, setTransport] = useState<string | null>(null);
  const [lastPing, setLastPing] = useState<{ roundTripMs: number; pong: Pong } | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    // StrictMode efekti iki kez çalıştırır: kapatılan bağlantının geç gelen olayları durumu ezmemeli
    let active = true;
    const connection = createHubConnection(hubPaths.diagnostics);
    connectionRef.current = connection;
    connection.onreconnecting(() => active && setState('connecting'));
    connection.onreconnected(() => active && setState('connected'));
    connection.onclose(() => active && setState('offline'));

    connection
      .start()
      .then(() => {
        if (!active) return;
        setState('connected');
        setError(null);
        // Seçilen taşıma: SignalR iç alanı; yalnızca tanılama amaçlı okunur
        const internal = connection as unknown as { connection?: { transport?: { constructor?: { name?: string } } } };
        setTransport(internal.connection?.transport?.constructor?.name ?? null);
      })
      .catch((e: unknown) => {
        if (!active) return;
        setState('offline');
        setError(e instanceof Error ? e.message : String(e));
      });

    return () => {
      active = false;
      connectionRef.current = null;
      void connection.stop();
    };
  }, []);

  const ping = useCallback(async () => {
    const connection = connectionRef.current;
    if (!connection || connection.state !== HubConnectionState.Connected) {
      return;
    }
    const started = performance.now();
    const pong = await connection.invoke<Pong>('Ping', 'merhaba');
    setLastPing({ roundTripMs: Math.round(performance.now() - started), pong });
  }, []);

  return { state, transport, lastPing, error, ping };
}
