import type { components } from '../shared/api/schema';

export type TvState = components['schemas']['TvStateDto'];
export type TvImage = components['schemas']['TvImageDto'];
export type PairingStarted = components['schemas']['PairingStartedDto'];
export type PairingStatus = components['schemas']['PairingStatus'];

/** Cihaz belirteci geçersiz/iptal: TV eşleştirme ekranına dönmeli. */
export class TvAuthError extends Error {
  constructor() {
    super('Cihaz belirteci geçersiz veya iptal edilmiş.');
    this.name = 'TvAuthError';
  }
}

/** Sunucu geçici olarak yanıt vermiyor (ağ, 429, 5xx): TV mevcut durumunu korur ve yeniden dener. */
export class TvNetworkError extends Error {
  constructor(message = 'Sunucuya ulaşılamadı.') {
    super(message);
    this.name = 'TvNetworkError';
  }
}

// TV hiçbir zaman çerez göndermez ve yanıtları önbelleğe almaz: kimlik yalnızca Authorization başlığıyla taşınır.
async function request(path: string, init: RequestInit & { headers: Record<string, string> }): Promise<Response> {
  try {
    return await fetch(path, { ...init, cache: 'no-store', credentials: 'omit' });
  } catch {
    throw new TvNetworkError();
  }
}

async function json<T>(response: Response): Promise<T> {
  if (!response.ok) {
    throw new TvNetworkError(`Sunucu hatası (${response.status}).`);
  }
  return (await response.json()) as T;
}

export async function startPairing(): Promise<PairingStarted> {
  return json<PairingStarted>(await request('/api/v1/display/pairing', { method: 'POST', headers: {} }));
}

export async function pairingStatus(secret: string): Promise<PairingStatus> {
  const response = await request('/api/v1/display/pairing/status', { method: 'GET', headers: { Authorization: `Pairing ${secret}` } });
  return (await json<{ status: PairingStatus }>(response)).status;
}

/** Sunucunun bu TV için belirlediği güncel (tek yetkili) görüntüleme durumu. */
export async function fetchState(token: string): Promise<TvState> {
  const response = await request('/api/v1/display/state', { method: 'GET', headers: { Authorization: `Bearer ${token}` } });
  if (response.status === 401) {
    throw new TvAuthError();
  }
  return json<TvState>(response);
}
