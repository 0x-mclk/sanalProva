import { useQuery } from '@tanstack/react-query';
import { api, unwrap } from './client';
import type { Schemas } from './client';

/**
 * Firma kredi görünümü (M5.4): yalnızca okuma. Kredi yükleme/düzeltme hiçbir uygulama ekranından yapılamaz (yalnızca sunucudaki operatör komutu);
 * ödeme alma bu aşamada yoktur (M5.5). Ledger sayfalaması `skip/take` iledir.
 */
export type CreditSummary = Schemas['CreditSummaryDto'];
export type CreditLedgerItem = Schemas['CreditLedgerItemDto'];

export const creditKeys = {
  summary: ['credits', 'summary'] as const,
  ledger: (skip: number, take: number) => ['credits', 'ledger', skip, take] as const,
};

export function useCredits() {
  return useQuery({ queryKey: creditKeys.summary, queryFn: () => unwrap(api.GET('/api/v1/credits')) });
}

export function useCreditLedger(skip: number, take: number) {
  return useQuery({
    queryKey: creditKeys.ledger(skip, take),
    queryFn: () => unwrap(api.GET('/api/v1/credits/ledger', { params: { query: { skip, take } } })),
  });
}
