import { useDisplays } from '../api/displays';
import { TvStatusBadge, type TvConnectionState } from './TvStatusBadge';

/**
 * Üst çubuktaki gerçek TV durumu. `sessionId` verilirse o provanın bağlı olduğu TV'yi, verilmezse şubenin durumunu gösterir:
 * çevrimiçi bir TV varsa "Bağlı", yalnızca çevrimdışı TV'ler varsa "Çevrimdışı", hiç TV yoksa "Bağlı değil".
 */
export function TvBadge({ sessionId }: { sessionId?: string }) {
  const { data } = useDisplays();
  let state: TvConnectionState = 'none';
  let name: string | undefined;

  if (data && data.length > 0) {
    if (sessionId) {
      const attached = data.find((d) => d.sessionId === sessionId);
      if (attached) {
        state = attached.online ? 'connected' : 'offline';
        name = attached.name;
      }
    } else {
      const online = data.find((d) => d.online);
      if (online) {
        state = 'connected';
        name = online.name;
      } else {
        state = 'offline';
        name = data[0]!.name;
      }
    }
  }

  return <TvStatusBadge name={name ?? 'TV'} state={state} />;
}
