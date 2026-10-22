import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from './client';

export type UsageReport = Schemas['UsageReport'];
export type AiQuality = Schemas['AiQualityDto'];
export type AiModelSettings = Schemas['AiModelSettingsDto'];
export type TryOnOptions = Schemas['TryOnOptionsDto'];
export type AiModelSettingsRequest = Schemas['AiModelSettingsRequest'];

/** AI kullanım ve maliyet raporu (yönetici). Tutarlar tahmindir; fatura sağlayıcıdadır. */
export function useUsageReport(from?: string, to?: string) {
  return useQuery({
    queryKey: ['usage', from, to],
    queryFn: () => unwrap(api.GET('/api/v1/admin/usage', { params: { query: { from, to } } })),
    staleTime: 15_000,
  });
}

/** Hazır takım (FASHN) kalite/çözünürlük ayarı (yönetici). */
export function useAiQuality() {
  return useQuery({ queryKey: ['ai-quality'], queryFn: () => unwrap(api.GET('/api/v1/admin/ai-quality')) });
}

export function useSetAiQuality() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: { resolution: string; mode: string }) => unwrap(api.PUT('/api/v1/admin/ai-quality', { body })),
    onSuccess: (data) => {
      client.setQueryData(['ai-quality'], data);
      void client.invalidateQueries({ queryKey: ['usage'] });
    },
  });
}

/** Tek ürün giydirme model ayarları (yönetici): etkin modeller, izinli kaliteler, varsayılanlar. */
export function useAiModels() {
  return useQuery({ queryKey: ['ai-models'], queryFn: () => unwrap(api.GET('/api/v1/admin/ai-models')) });
}

export function useSetAiModels() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: AiModelSettingsRequest) => unwrap(api.PUT('/api/v1/admin/ai-models', { body })),
    onSuccess: (data) => {
      client.setQueryData(['ai-models'], data);
      void client.invalidateQueries({ queryKey: ['try-on-options'] });
    },
  });
}

/** Personelin kullanabileceği modeller/kaliteler (yöneticinin izin verdikleri) ve kombinin gerçek modda açık olup olmadığı. */
export function useTryOnOptions() {
  return useQuery({ queryKey: ['try-on-options'], queryFn: () => unwrap(api.GET('/api/v1/ai/try-on-options')), staleTime: 60_000 });
}
