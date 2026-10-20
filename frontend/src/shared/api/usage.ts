import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from './client';

export type UsageReport = Schemas['UsageReport'];
export type AiQuality = Schemas['AiQualityDto'];

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
