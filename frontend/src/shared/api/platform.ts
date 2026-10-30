import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap } from './client';
import { apiFetch } from './http';
import { qk } from './hooks';
import type { CreateOrganizationRequest, CreateOrganizationResponse, SetupLinkResponse } from './types';

/**
 * Platform yönetimi, hesap kurulumu ve hizmet sözleşmesi çağrıları (M5.1h).
 *
 * GÜVENLİK: kurulum bağlantısı/token taşıyan çağrılar (oluşturma, yeniden üretme, şifre belirleme) bilerek `useMutation` KULLANMAZ: TanStack mutation önbelleği
 * değişkenleri ve sonucu bir süre bellekte tutar. Bu çağrılar doğrudan `apiFetch` ile yapılır, sonuç yalnızca çağıran bileşenin yerel durumuna alınır;
 * sorgu/mutation önbelleğine, localStorage/sessionStorage'a veya günlüğe hiçbir zaman yazılmaz.
 */

export const platformKeys = {
  organizations: ['platform', 'organizations'] as const,
  organization: (id: string) => ['platform', 'organization', id] as const,
  agreement: ['agreement'] as const,
};

export function usePlatformOrganizations() {
  return useQuery({
    queryKey: platformKeys.organizations,
    queryFn: () => unwrap(api.GET('/api/v1/platform/organizations', { params: { query: { take: 200 } } })),
  });
}

export function useOrganizationDetail(id: string | undefined) {
  return useQuery({
    queryKey: platformKeys.organization(id ?? ''),
    queryFn: () => unwrap(api.GET('/api/v1/platform/organizations/{id}', { params: { path: { id: id! } } })),
    enabled: Boolean(id),
  });
}

/** Firma + ilk şube + Owner. Bağlantı YALNIZCA ilk yanıtta gelir; sonuç önbelleğe alınmaz. `idempotencyKey` form açılışında üretilir, ağ hatasında aynı anahtarla tekrar gönderilir. */
export function createOrganization(body: CreateOrganizationRequest, idempotencyKey: string): Promise<CreateOrganizationResponse> {
  return apiFetch<CreateOrganizationResponse>('/api/v1/platform/organizations', { method: 'POST', json: body, headers: { 'Idempotency-Key': idempotencyKey } });
}

/**
 * Owner kurulum bağlantısını yeniden üretir (önceki açık bağlantılar iptal edilir). SUNUCU tarafında bu çağrı için idempotency yoktur:
 * arayüzdeki çift tıklama kilidi (bkz. OrganizationDetailPage) yalnızca istemci tarafı korumadır; ağ yeniden denemesi veya iki sekme yine iki bağlantı üretip birincisini iptal edebilir.
 */
export function reissueOwnerLink(organizationId: string): Promise<SetupLinkResponse> {
  return apiFetch<SetupLinkResponse>(`/api/v1/platform/organizations/${organizationId}/owner/setup-link`, { method: 'POST', json: {} });
}

/** Tek kullanımlık bağlantıyla şifre belirleme (anonim). Token yalnızca istek gövdesindedir; URL'e yazılmaz. */
export function setupPassword(token: string, password: string): Promise<void> {
  return apiFetch<void>('/api/v1/account/setup-password', { method: 'POST', json: { token, password } });
}

// ---------------------------------------------------------------- hizmet sözleşmesi (müşteri KVKK onayından AYRI)

export function useAgreement() {
  return useQuery({
    queryKey: platformKeys.agreement,
    queryFn: () => unwrap(api.GET('/api/v1/agreement/current')),
  });
}

export function useAcceptAgreement() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: { version: string; textSha256: string }) => unwrap(api.POST('/api/v1/organization/agreement-acceptance', { body })),
    onSuccess: async () => {
      // Kapı açıldı: oturum bilgisi (agreementAcceptanceRequired) yenilenir; yönlendirme buradan gelir
      await client.invalidateQueries({ queryKey: qk.me });
      await client.invalidateQueries({ queryKey: platformKeys.agreement });
    },
  });
}
