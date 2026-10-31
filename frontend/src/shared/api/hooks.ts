import { keepPreviousData, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { api, unwrap } from './client';
import { ApiError, guardedFetch, resetCsrfToken, toApiError } from './http';
import type {
  CategoryWriteRequest,
  DraftDto,
  JobDto,
  OutfitMode,
  PhotoDto,
  ProductDetail,
  ProductType,
  ProductWriteRequest,
  SessionDetail,
  VariantWriteRequest,
} from './types';
import { isJobActive } from './types';
import { writeLocked } from '../auth/lockState';

/** Sorgu anahtarları: ilgili önbelleği mutasyonlardan sonra tek yerden geçersiz kılmak için. */
export const qk = {
  me: ['me'] as const,
  consentNotice: ['consent-notice'] as const,
  sessions: (status: string) => ['sessions', status] as const,
  sessionsAll: ['sessions'] as const,
  session: (id: string) => ['session', id] as const,
  job: (id: string) => ['job', id] as const,
  products: ['products'] as const,
  product: (id: string) => ['product', id] as const,
  categories: (includeInactive: boolean) => ['categories', includeInactive] as const,
};

// ---------------------------------------------------------------- kimlik

/** `refetchInterval`: sözleşme bekleme ekranı gibi başkasının işlemini beklerken oturum bilgisi sık yenilenir. */
export function useMe(options: { refetchInterval?: number } = {}) {
  return useQuery({
    queryKey: qk.me,
    queryFn: () => unwrap(api.GET('/api/v1/auth/me')),
    retry: false,
    staleTime: 5 * 60_000,
    refetchInterval: options.refetchInterval,
  });
}

export function useLogin() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: { email: string; password: string; rememberMe: boolean }) => unwrap(api.POST('/api/v1/auth/login', { body })),
    onSuccess: (me) => {
      resetCsrfToken();
      writeLocked(false);
      // Önceki kullanıcının verisi kalmasın; ancak aktif `useMe` gözlemcisi kopmasın (clear() gözlemciyi önbellekten ayırır)
      client.removeQueries({ predicate: (query) => query.queryKey[0] !== qk.me[0] });
      client.setQueryData(qk.me, me);
    },
  });
}

export function useLogout() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => unwrap(api.POST('/api/v1/auth/logout')),
    onSettled: async () => {
      resetCsrfToken();
      writeLocked(false); // kilit bayrağı bir sonraki girişe taşınmaz
      // Müşteri verisi oturum kapanınca bellekte de kalmaz; kimlik sorgusu sıfırlanıp yeniden denenir (401 → giriş ekranı)
      client.removeQueries({ predicate: (query) => query.queryKey[0] !== qk.me[0] });
      await client.resetQueries({ queryKey: qk.me });
    },
  });
}

// ---------------------------------------------------------------- oturumlar

export function useConsentNotice() {
  return useQuery({ queryKey: qk.consentNotice, queryFn: () => unwrap(api.GET('/api/v1/consent-notice/current')), staleTime: Infinity });
}

export function useSessions(status: 'all' | 'open' | 'closed' = 'all', pageSize = 20) {
  return useQuery({
    queryKey: [...qk.sessions(status), pageSize],
    queryFn: () => unwrap(api.GET('/api/v1/sessions', { params: { query: { status, pageSize } } })),
    placeholderData: keepPreviousData,
  });
}

/** Görsel URL'leri 5 dakika geçerlidir: açık ekranda 4 dakikada bir yenilenir. */
export function useSession(id: string | undefined) {
  return useQuery({
    queryKey: qk.session(id ?? ''),
    queryFn: () => unwrap(api.GET('/api/v1/sessions/{id}', { params: { path: { id: id! } } })),
    enabled: Boolean(id),
    refetchInterval: 4 * 60_000,
  });
}

export function useCreateSession() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (customerLabel: string | null) => unwrap(api.POST('/api/v1/sessions', { body: { customerLabel } })),
    onSuccess: (session) => {
      client.setQueryData(qk.session(session.id), session);
      void client.invalidateQueries({ queryKey: qk.sessionsAll });
    },
  });
}

export type ConsentInput = {
  noticeVersion: string;
  processingConsent: boolean;
  foreignTransferConsent: boolean;
  extendedRetentionConsent: boolean;
  isMinor: boolean;
  guardianConsent: boolean | null;
  method: 'StaffConfirmedVerbal' | 'CustomerTapped' | 'SignedOnTablet';
};

/** Oturum kimliği çağrı anında verilir: yeni prova akışında oturum onaydan hemen önce oluşturulur. */
export function useRecordConsent() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ sessionId, body }: { sessionId: string; body: ConsentInput }) =>
      unwrap(api.POST('/api/v1/sessions/{id}/consent', { params: { path: { id: sessionId } }, body })),
    onSuccess: (session) => setSession(client, session),
  });
}

function setSession(client: QueryClient, session: SessionDetail) {
  client.setQueryData(qk.session(session.id), session);
  void client.invalidateQueries({ queryKey: qk.sessionsAll });
}

export function useCloseSession(sessionId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => unwrap(api.POST('/api/v1/sessions/{id}/close', { params: { path: { id: sessionId } } })),
    onSuccess: (session) => setSession(client, session),
  });
}

/** Oturumu ve tüm görselleri kalıcı siler (KVKK). */
export function useDeleteSession() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.DELETE('/api/v1/sessions/{id}', { params: { path: { id } } })),
    onSuccess: (_data, id) => {
      client.removeQueries({ queryKey: qk.session(id) });
      void client.invalidateQueries({ queryKey: qk.sessionsAll });
    },
  });
}

async function postForm<T>(path: string, form: FormData): Promise<T> {
  const response = await guardedFetch(new Request(new URL(path, window.location.origin), { method: 'POST', body: form }));
  if (!response.ok) {
    throw await toApiError(response);
  }
  return (await response.json()) as T;
}

export function useUploadPhoto(sessionId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (file: Blob) => {
      const form = new FormData();
      form.append('file', file, 'foto.jpg');
      form.append('angle', 'Front');
      return postForm<PhotoDto>(`/api/v1/sessions/${sessionId}/photos`, form);
    },
    onSuccess: () => void client.invalidateQueries({ queryKey: qk.session(sessionId) }),
  });
}

/** Personel fotoğraf uygunluk onayı (sunucuda kayıtlı; ücretli üretim bunsuz kabul edilmez). Üç beyanın hepsi gönderilir; sunucu hepsini ister. */
export function useApprovePhoto(sessionId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (photoId: string) =>
      unwrap(
        api.POST('/api/v1/sessions/{id}/photos/{photoId}/approval', {
          params: { path: { id: sessionId, photoId } },
          body: { singlePersonFullBody: true, frontalUprightPose: true, sharpAndWellLit: true },
        }),
      ),
    onSuccess: () => void client.invalidateQueries({ queryKey: qk.session(sessionId) }),
  });
}

export function useRevokePhotoApproval(sessionId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (photoId: string) => unwrap(api.DELETE('/api/v1/sessions/{id}/photos/{photoId}/approval', { params: { path: { id: sessionId, photoId } } })),
    onSuccess: () => void client.invalidateQueries({ queryKey: qk.session(sessionId) }),
  });
}

export function useDeletePhoto(sessionId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (photoId: string) => unwrap(api.DELETE('/api/v1/sessions/{id}/photos/{photoId}', { params: { path: { id: sessionId, photoId } } })),
    onSuccess: (session) => setSession(client, session),
  });
}

/** Seçimler sunucuda saklanır; kullanıcı ekran değiştirse veya sayfa yenilense korunur. Önbellek hemen güncellenir (iyimser). */
export function useSaveDraft(sessionId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (input: { mode: OutfitMode; items: { productId: string; variantId?: string | null }[] }) =>
      unwrap(
        api.PUT('/api/v1/sessions/{id}/draft', {
          params: { path: { id: sessionId } },
          body: { mode: input.mode, items: input.items.map((i) => ({ productId: i.productId, variantId: i.variantId ?? null })) },
        }),
      ),
    onSuccess: (draft: DraftDto) => {
      client.setQueryData<SessionDetail>(qk.session(sessionId), (old) => (old ? { ...old, draft } : old));
    },
    onError: () => void client.invalidateQueries({ queryKey: qk.session(sessionId) }),
  });
}

// ---------------------------------------------------------------- üretim

export function useStartGeneration(sessionId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ idempotencyKey, photoId, modelKey, quality }: { idempotencyKey: string; photoId?: string | null; modelKey?: string | null; quality?: string | null }) =>
      unwrap(
        api.POST('/api/v1/sessions/{id}/generations', {
          params: { path: { id: sessionId }, header: { 'Idempotency-Key': idempotencyKey } },
          body: { photoId: photoId ?? null, allowVariation: false, modelKey: modelKey ?? null, quality: quality ?? null },
        }),
      ),
    onSuccess: (job) => {
      client.setQueryData(qk.job(job.id), job);
      void client.invalidateQueries({ queryKey: qk.session(sessionId) });
    },
  });
}

/** Aktif işi hızlı, bitmiş işi hiç yoklamaz. SignalR bildirimi gelince ayrıca anında yenilenir. */
export function useJob(id: string | undefined) {
  return useQuery({
    queryKey: qk.job(id ?? ''),
    queryFn: () => unwrap(api.GET('/api/v1/jobs/{id}', { params: { path: { id: id! } } })),
    enabled: Boolean(id),
    refetchInterval: (query) => (isJobActive(query.state.data) ? 1500 : false),
  });
}

export function useCancelJob() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => unwrap(api.POST('/api/v1/jobs/{id}/cancel', { params: { path: { id } } })),
    onSuccess: (job) => {
      client.setQueryData(qk.job(job.id), job);
      void client.invalidateQueries({ queryKey: qk.session(job.sessionId) });
    },
  });
}

export function useRetryJob() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, idempotencyKey }: { id: string; idempotencyKey: string }) =>
      unwrap(api.POST('/api/v1/jobs/{id}/retry', { params: { path: { id }, header: { 'Idempotency-Key': idempotencyKey } } })),
    onSuccess: (job) => {
      client.setQueryData(qk.job(job.id), job);
      void client.invalidateQueries({ queryKey: qk.session(job.sessionId) });
    },
  });
}

export function useToggleFavorite(sessionId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ imageId, isFavorite }: { imageId: string; isFavorite: boolean }) =>
      unwrap(api.PATCH('/api/v1/images/{id}/favorite', { params: { path: { id: imageId } }, body: { isFavorite } })),
    onSuccess: () => void client.invalidateQueries({ queryKey: qk.session(sessionId) }),
  });
}

/** Revizyon motoru Aşama 5'te: sunucu 501 döner; arayüz bunu "henüz etkin değil" olarak gösterir. */
export function useRequestRevision(sessionId: string) {
  return useMutation({
    mutationFn: (body: { baseImageId: string; type: string; targetCategoryId?: string; productId?: string; variantId?: string; color?: string }) =>
      unwrap(
        api.POST('/api/v1/sessions/{id}/revisions', {
          params: { path: { id: sessionId } },
          body: {
            baseImageId: body.baseImageId, type: body.type, targetCategoryId: body.targetCategoryId ?? null,
            productId: body.productId ?? null, variantId: body.variantId ?? null, color: body.color ?? null,
          },
        }),
      ),
  });
}

// ---------------------------------------------------------------- katalog

export type ProductsQuery = {
  type?: ProductType;
  categoryId?: string;
  q?: string;
  status?: 'active' | 'inactive' | 'all';
  selectable?: boolean;
  /** Tek ürün giydirme: hazır takımlar + tüm vücut tekil ürünler (kaftan). */
  tryOn?: boolean;
  sort?: 'recommended' | 'name' | 'code' | 'newest';
  pageSize?: number;
};

export function useProducts(query: ProductsQuery = {}, enabled = true) {
  return useQuery({
    queryKey: [...qk.products, query],
    queryFn: () => unwrap(api.GET('/api/v1/products', { params: { query: { pageSize: 100, ...query } } })),
    placeholderData: keepPreviousData,
    enabled,
  });
}

export function useProduct(id: string | undefined) {
  return useQuery({
    queryKey: qk.product(id ?? ''),
    queryFn: () => unwrap(api.GET('/api/v1/products/{id}', { params: { path: { id: id! } } })),
    enabled: Boolean(id),
  });
}

export function useCategories(includeInactive = false) {
  return useQuery({
    queryKey: qk.categories(includeInactive),
    queryFn: () => unwrap(api.GET('/api/v1/categories', { params: { query: { includeInactive } } })),
    staleTime: 60_000,
  });
}

function afterProductChange(client: QueryClient, product: ProductDetail) {
  client.setQueryData(qk.product(product.id), product);
  void client.invalidateQueries({ queryKey: qk.products });
}

export function useCreateProduct() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: ProductWriteRequest) => unwrap(api.POST('/api/v1/products', { body })),
    onSuccess: (product) => afterProductChange(client, product),
  });
}

export function useUpdateProduct(id: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: ProductWriteRequest) => unwrap(api.PUT('/api/v1/products/{id}', { params: { path: { id } }, body })),
    onSuccess: (product) => afterProductChange(client, product),
  });
}

export function useSetProductStatus() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, isActive }: { id: string; isActive: boolean }) =>
      unwrap(api.PATCH('/api/v1/products/{id}/status', { params: { path: { id } }, body: { isActive } })),
    onSuccess: (product) => afterProductChange(client, product),
  });
}

export function useSaveVariant(productId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ variantId, body }: { variantId?: string; body: VariantWriteRequest }) =>
      variantId
        ? unwrap(api.PUT('/api/v1/products/{id}/variants/{variantId}', { params: { path: { id: productId, variantId } }, body }))
        : unwrap(api.POST('/api/v1/products/{id}/variants', { params: { path: { id: productId } }, body })),
    onSuccess: (product) => afterProductChange(client, product),
  });
}

export function useUploadProductImage(productId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (input: { file: Blob; variantId?: string | null; angle: string; kind: string; isAiReference: boolean }) => {
      const form = new FormData();
      form.append('file', input.file, 'urun.jpg');
      if (input.variantId) {
        form.append('variantId', input.variantId);
      }
      form.append('angle', input.angle);
      form.append('kind', input.kind);
      form.append('isAiReference', String(input.isAiReference));
      return postForm<ProductDetail>(`/api/v1/products/${productId}/images`, form);
    },
    onSuccess: (product) => afterProductChange(client, product),
  });
}

export function useUpdateProductImage(productId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ imageId, isAiReference }: { imageId: string; isAiReference: boolean }) =>
      unwrap(api.PATCH('/api/v1/products/{id}/images/{imageId}', { params: { path: { id: productId, imageId } }, body: { isAiReference, angle: null, kind: null, sortOrder: null } })),
    onSuccess: (product) => afterProductChange(client, product),
  });
}

export function useDeleteProductImage(productId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (imageId: string) => unwrap(api.DELETE('/api/v1/products/{id}/images/{imageId}', { params: { path: { id: productId, imageId } } })),
    onSuccess: (product) => afterProductChange(client, product),
  });
}

export function useSetComponents(productId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (components: { componentProductId: string; componentVariantId?: string | null }[]) =>
      unwrap(
        api.PUT('/api/v1/products/{id}/components', {
          params: { path: { id: productId } },
          body: components.map((c) => ({ componentProductId: c.componentProductId, componentVariantId: c.componentVariantId ?? null })),
        }),
      ),
    onSuccess: (product) => afterProductChange(client, product),
  });
}

export function useSaveCategory() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, body }: { id?: string; body: CategoryWriteRequest }) =>
      id ? unwrap(api.PUT('/api/v1/categories/{id}', { params: { path: { id } }, body })) : unwrap(api.POST('/api/v1/categories', { body })),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ['categories'] });
      void client.invalidateQueries({ queryKey: qk.products });
    },
  });
}

export type { JobDto };
export { ApiError };
