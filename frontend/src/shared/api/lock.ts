import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap } from './client';
import type { Schemas } from './client';
import { problemToApiError } from './http';

/** Ekran kilidi (Faz C). PIN/parola düz metni yalnızca istek gövdesindedir; önbelleğe yazılmaz (mutation değişkenleri kilit açılınca sıfırlanır). */
export type LockStatus = Schemas['LockStatusDto'];

export const lockKeys = { status: ['lock', 'status'] as const };

export function useLockStatus(enabled = true) {
  return useQuery({ queryKey: lockKeys.status, queryFn: () => unwrap(api.GET('/api/v1/account/lock')), enabled, staleTime: 5 * 60_000, retry: false });
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
