import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr';

/** docs/06-tv-signalr.md §5: kısa kopmalarda hızlı, sonra seyrek yeniden deneme. */
export const RECONNECT_DELAYS_MS = [0, 2000, 5000, 10000, 30000] as const;

export const hubPaths = {
  staff: '/hubs/staff',
  display: '/hubs/display',
  diagnostics: '/hubs/diagnostics',
} as const;

/**
 * SignalR bağlantısı oluşturur. Kimlik doğrulama çerezle (personel) yapılır; TV için Aşama 3'te
 * accessTokenFactory ile cihaz belirteci verilecek. WebSocket engelliyse SignalR otomatik olarak SSE/long polling'e düşer.
 */
export function createHubConnection(path: string, options: { accessTokenFactory?: () => string } = {}): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(path, { withCredentials: true, accessTokenFactory: options.accessTokenFactory })
    .withAutomaticReconnect([...RECONNECT_DELAYS_MS])
    .configureLogging(import.meta.env.DEV ? LogLevel.Information : LogLevel.Warning)
    .build();
}

export { HubConnectionState };
