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

/** Personel/Manager görünümü (Faz C): yalnızca kullanılabilir kredi ve uyarı bayrakları (defter/tarife yok). */
export type CreditBalance = Schemas['CreditBalanceDto'];
export type CreditQuote = Schemas['CreditQuoteDto'];

export const staffCreditKeys = {
  balance: ['credits', 'balance'] as const,
  quote: (jobType: string, modelKey?: string, quality?: string) => ['credits', 'quote', jobType, modelKey ?? '', quality ?? ''] as const,
};

export function useCreditBalance(enabled = true) {
  return useQuery({ queryKey: staffCreditKeys.balance, queryFn: () => unwrap(api.GET('/api/v1/credits/balance')), enabled, staleTime: 30_000 });
}

/** Üretim başlamadan bedel bilgisi. Hiçbir şey ayrılmaz; kabulde sunucu tarifeyi yeniden okur. */
export function useCreditQuote(jobType: 'OutfitSet' | 'FreeCombination', modelKey?: string, quality?: string) {
  return useQuery({
    queryKey: staffCreditKeys.quote(jobType, modelKey, quality),
    queryFn: () => unwrap(api.GET('/api/v1/credits/quote', { params: { query: { jobType, modelKey, quality } } })),
    staleTime: 15_000,
  });
}
