import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap } from './client';
import { apiFetch } from './http';
import type { InviteStaffResponse, StaffAssignmentRequest, StaffMemberDto, StaffSetupLinkDto, StoreAdminDto } from './types';

/**
 * Şube ve personel yönetimi (M5.2d). GÜVENLİK: kurulum bağlantısı döndüren çağrılar (davet, bağlantı üretme) `useMutation` KULLANMAZ
 * (mutation önbelleği sonucu bellekte tutar); doğrudan `apiFetch` ile yapılır ve sonuç yalnızca çağıran bileşenin yerel durumuna alınır.
 */
export const managementKeys = {
  stores: ['management', 'stores'] as const,
  users: ['management', 'users'] as const,
};

export function useManagedStores(enabled = true) {
  return useQuery({ queryKey: managementKeys.stores, queryFn: () => unwrap(api.GET('/api/v1/management/stores')), enabled });
}

export type StoreForm = { name: string; code: string; timezone: string; dailyGenerationQuota: number | null };

export function useCreateStore() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: StoreForm) => unwrap(api.POST('/api/v1/management/stores', { body })),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: managementKeys.stores });
      void client.invalidateQueries({ queryKey: ['me'] }); // yeni şube Owner'ın şube listesine girer
    },
  });
}

export function useUpdateStore() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, body }: { id: string; body: StoreForm }) => unwrap(api.PUT('/api/v1/management/stores/{id}', { params: { path: { id } }, body })),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: managementKeys.stores });
      void client.invalidateQueries({ queryKey: ['me'] });
    },
  });
}

export function useSetStoreStatus() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, isActive }: { id: string; isActive: boolean }) => unwrap(api.PATCH('/api/v1/management/stores/{id}/status', { params: { path: { id } }, body: { isActive } })),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: managementKeys.stores });
      void client.invalidateQueries({ queryKey: ['me'] });
    },
  });
}

export function useStaff() {
  return useQuery({ queryKey: managementKeys.users, queryFn: () => unwrap(api.GET('/api/v1/management/users')) });
}

export function useSetStaffAssignments() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, assignments }: { id: string; assignments: StaffAssignmentRequest[] }) =>
      unwrap(api.PUT('/api/v1/management/users/{id}/assignments', { params: { path: { id } }, body: { assignments } })),
    onSuccess: () => void client.invalidateQueries({ queryKey: managementKeys.users }),
  });
}

export function useSetStaffStatus() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, isActive }: { id: string; isActive: boolean }) => unwrap(api.PATCH('/api/v1/management/users/{id}/status', { params: { path: { id } }, body: { isActive } })),
    onSuccess: () => void client.invalidateQueries({ queryKey: managementKeys.users }),
  });
}

export function useRevokeStaffLinks() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiFetch<void>(`/api/v1/management/users/${id}/setup-link`, { method: 'DELETE' }),
    onSuccess: () => void client.invalidateQueries({ queryKey: managementKeys.users }),
  });
}

/** Davet: bağlantı YALNIZCA bu yanıtta gelir. Sonuç önbelleğe alınmaz. */
export function inviteStaff(body: { email: string; displayName: string; assignments: StaffAssignmentRequest[] }): Promise<InviteStaffResponse> {
  return apiFetch<InviteStaffResponse>('/api/v1/management/users', { method: 'POST', json: body });
}

/** Bekleyen davet için yeni bağlantı veya şifre sıfırlama bağlantısı (önceki açık bağlantı iptal olur). */
export function issueStaffLink(id: string): Promise<StaffSetupLinkDto> {
  return apiFetch<StaffSetupLinkDto>(`/api/v1/management/users/${id}/setup-link`, { method: 'POST', json: {} });
}

export type { StaffMemberDto, StoreAdminDto };
