import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap } from './client';
import type { Schemas } from './client';
import { problemToApiError } from './http';

/**
 * Ekran kilidi (Faz C + sunucu tarafı zorlama). Kilit durumu SUNUCUDAKİ oturum satırındadır: `locked` sayfa yenilemeden/ikinci sekmeden bağımsız okunur.
 * PIN/parola düz metni yalnızca istek gövdesindedir; önbelleğe yazılmaz (mutation değişkenleri kilit açılınca sıfırlanır).
 */
export type LockStatus = Schemas['LockStatusDto'];

export const lockKeys = { status: ['lock', 'status'] as const };

/** Sunucudaki kilit durumunu yoklama aralığı: başka sekmede kilitlenen/açılan oturum bu sekmeye en geç bu kadar sürede yansır. */
export const LOCK_POLL_MS = 15_000;

export function useLockStatus(enabled = true) {
  return useQuery({ queryKey: lockKeys.status, queryFn: () => unwrap(api.GET('/api/v1/account/lock')), enabled, staleTime: 0, refetchInterval: LOCK_POLL_MS, retry: false });
}

/** Sunucuyu kilitler (idempotent). Hata durumunda çağıran yerel kilidi korur ve tekrar dener. */
export async function sendLock(): Promise<boolean> {
  try {
    const { response } = await api.POST('/api/v1/account/lock');
    return response.ok;
  } catch {
    return false;
  }
}

/** Etkileşim sinyali (boşta süresini sıfırlar). 423 dönerse oturum başka yerde kilitlenmiştir. */
export async function sendActivity(): Promise<'ok' | 'locked' | 'failed'> {
  try {
    const { response } = await api.POST('/api/v1/account/activity');
    if (response.status === 423) return 'locked';
    return response.ok ? 'ok' : 'failed';
  } catch {
    return 'failed';
  }
}

export function useUnlock() {
  return useMutation({
    mutationFn: async (body: { pin?: string; password?: string }) => {
      const { response, error } = await api.POST('/api/v1/account/unlock', { body: { pin: body.pin ?? null, password: body.password ?? null } });
      if (!response.ok) {
        throw problemToApiError(response.status, response.statusText, error);
      }
    },
    gcTime: 0,
  });
}

export function useSetPin() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async (body: { currentPassword: string; pin: string }) => {
      const { response, error } = await api.PUT('/api/v1/account/pin', { body });
      if (!response.ok) {
        throw problemToApiError(response.status, response.statusText, error);
      }
    },
    gcTime: 0,
    onSuccess: () => client.invalidateQueries({ queryKey: lockKeys.status }),
  });
}
