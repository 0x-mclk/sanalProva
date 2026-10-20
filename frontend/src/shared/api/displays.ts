import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useAuth } from '../auth/AuthContext';
import { api, unwrap, type Schemas } from './client';

export type DisplayDto = Schemas['DisplayDto'];
export type DisplayMode = Schemas['DisplayMode'];

/** Sorgu anahtarı şubeyi içerir: şube değişince başka şubenin TV listesi gösterilmez. */
export const displayKeys = {
  all: ['displays'] as const,
  list: (storeId: string | undefined) => ['displays', storeId] as const,
};

/**
 * Aktif şubenin eşleştirilmiş TV'leri. Doğruluk kaynağı REST'tir; SignalR (DisplaySync) yalnızca "değişti" der ve listeyi tazeletir.
 * Bağlantı kopsa da 30 sn'de bir yoklanır.
 */
export function useDisplays() {
  const { activeStore } = useAuth();
  return useQuery({
    queryKey: displayKeys.list(activeStore?.id),
    queryFn: () => unwrap(api.GET('/api/v1/displays')),
    enabled: Boolean(activeStore),
    staleTime: 5_000,
    refetchInterval: 30_000,
  });
}

function useInvalidateDisplays() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: displayKeys.all });
}

export function useClaimDisplay() {
  const invalidate = useInvalidateDisplays();
  return useMutation({
    mutationFn: (body: { code: string; name: string }) => unwrap(api.POST('/api/v1/displays/claim', { body })),
    onSuccess: invalidate,
  });
}

export function useRenameDisplay() {
  const invalidate = useInvalidateDisplays();
  return useMutation({
    mutationFn: ({ id, name }: { id: string; name: string }) => unwrap(api.PATCH('/api/v1/displays/{id}', { params: { path: { id } }, body: { name } })),
    onSuccess: invalidate,
  });
}

/** Eşleştirmeyi kaldırır (yönetici): TV belirteci anında geçersiz olur. */
export function useRevokeDisplay() {
  const invalidate = useInvalidateDisplays();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.DELETE('/api/v1/displays/{id}', { params: { path: { id } } })),
    onSuccess: invalidate,
  });
}

export function useAttachDisplay() {
  const invalidate = useInvalidateDisplays();
  return useMutation({
    mutationFn: ({ id, sessionId, force }: { id: string; sessionId: string; force?: boolean }) =>
      unwrap(api.PUT('/api/v1/displays/{id}/session', { params: { path: { id } }, body: { sessionId, force: force ?? false } })),
    onSuccess: invalidate,
  });
}

/** Personel TV bağlantısını keser: ekran boşalır, TV başka provaya hazır olur. */
export function useDetachDisplay() {
  const invalidate = useInvalidateDisplays();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.DELETE('/api/v1/displays/{id}/session', { params: { path: { id } } })),
    onSuccess: invalidate,
  });
}

export function useSetDisplayState() {
  const invalidate = useInvalidateDisplays();
  return useMutation({
    mutationFn: ({ id, mode, imageIds, expectedVersion }: { id: string; mode: 'Single' | 'Compare'; imageIds: string[]; expectedVersion?: number }) =>
      unwrap(api.PUT('/api/v1/displays/{id}/state', { params: { path: { id } }, body: { mode, imageIds, expectedVersion: expectedVersion ?? null } })),
    onSuccess: invalidate,
  });
}

export function useClearDisplay() {
  const invalidate = useInvalidateDisplays();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.POST('/api/v1/displays/{id}/clear', { params: { path: { id } } })),
    onSuccess: invalidate,
  });
}

export const displayModeLabel: Record<DisplayMode, string> = {
  Idle: 'Bekleme ekranı',
  Single: 'Sonuç gösteriliyor',
  Compare: 'Karşılaştırma gösteriliyor',
};
