import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { CreditLedgerItem, CreditSummary } from '../../shared/api/credits';
import { ApiError } from '../../shared/api/http';
import { errorMessage } from '../../shared/i18n/tr';
import { ThemeProvider } from '../../shared/theme/ThemeProvider';
import { CreditsPage } from './CreditsPage';

let summary: CreditSummary;
let ledgerPages: Record<number, CreditLedgerItem[]>;
const ledgerSpy = vi.fn();

vi.mock('../../shared/api/credits', async (original) => ({
  ...(await original<typeof import('../../shared/api/credits')>()),
  useCredits: () => ({ data: summary, isLoading: false, isError: false, refetch: vi.fn() }),
  useCreditLedger: (skip: number, take: number) => {
    ledgerSpy(skip, take);
    return { data: ledgerPages[skip] ?? [], isLoading: false, isError: false, refetch: vi.fn() };
  },
}));

const entry = (over: Partial<CreditLedgerItem>): CreditLedgerItem => ({
  id: crypto.randomUUID(), type: 'Grant', amount: 100, balanceAfter: 100, reservedAfter: 0, jobId: null, reference: 'havale-2026-0001', source: 'Operator', note: 'Pilot paketi',
  tariffKey: null, createdAt: '2026-11-01T10:00:00Z', ...over,
});

function renderPage() {
  return render(
    <ThemeProvider>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter>
          <CreditsPage />
        </MemoryRouter>
      </QueryClientProvider>
    </ThemeProvider>,
  );
}

beforeEach(() => {
  ledgerSpy.mockReset();
  summary = { balance: 40, reserved: 3, available: 37, openJobs: 1, enforced: true, defaultCostPerJob: 1, tariff: [{ key: 'OutfitSet:gpt-image-2.5:high', credits: 5 }] };
  ledgerPages = { 0: [entry({ type: 'Reserve', amount: 3, balanceAfter: 40, reservedAfter: 3, reference: null, note: null, source: 'System', jobId: 'j1' }), entry({})] };
});

describe('CreditsPage', () => {
  it('bakiye, kullanılabilir ve bekleyen krediyi ayrı gösterir; tarifeyi açıklar', () => {
    renderPage();
    expect(within(screen.getByTestId('credit-available')).getByText('37')).toBeInTheDocument();
    expect(within(screen.getByTestId('credit-balance')).getByText('40')).toBeInTheDocument();
    expect(within(screen.getByTestId('credit-reserved')).getByText('3')).toBeInTheDocument();
    expect(screen.getByText('1 üretim için ayrıldı')).toBeInTheDocument();
    expect(screen.getByText('OutfitSet:gpt-image-2.5:high')).toBeInTheDocument();
    expect(screen.getByText(/sonradan tarife değişse de o üretimin bedeli değişmez/)).toBeInTheDocument();
  });

  it('işlem geçmişini Türkçe türlerle, referans ve notla listeler; yükleme/ödeme düğmesi yoktur', () => {
    renderPage();
    const rows = screen.getAllByTestId('credit-entry');
    expect(rows).toHaveLength(2);
    expect(within(rows[0]!).getByText('Ayrıldı (üretim başladı)')).toBeInTheDocument();
    expect(within(rows[1]!).getByText('Kredi yükleme')).toBeInTheDocument();
    expect(within(rows[1]!).getByText(/Ref: havale-2026-0001/)).toBeInTheDocument();
    expect(within(rows[1]!).getByText('+100')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /yükle|satın|öde/i })).not.toBeInTheDocument(); // yükleme yalnızca operatör komutuyla
  });

  it('kullanılabilir kredi yoksa anlaşılır uyarı gösterir', () => {
    summary = { ...summary, balance: 3, reserved: 3, available: 0 };
    renderPage();
    expect(screen.getByText(/Kullanılabilir krediniz yok/)).toBeInTheDocument();
  });

  it('kredi şartı uygulanmıyorsa (mock/kapalı) bilgi notu gösterir', () => {
    summary = { ...summary, enforced: false };
    renderPage();
    expect(screen.getByText(/kredi şartı uygulanmıyor/)).toBeInTheDocument();
  });

  it('sayfalama: sonraki sayfa skip ile istenir', () => {
    ledgerPages = { 0: Array.from({ length: 20 }, (_, i) => entry({ amount: i + 1 })), 20: [entry({ type: 'Consume', amount: 1 })] };
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Sonraki' }));
    expect(ledgerSpy).toHaveBeenLastCalledWith(20, 20);
    expect(screen.getByText('Kullanıldı (üretim tamamlandı)')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Sonraki' })).toBeDisabled();
  });

  it('boş geçmiş açıklayıcı boş durum gösterir', () => {
    ledgerPages = { 0: [] };
    renderPage();
    expect(screen.getByText('Henüz kredi hareketi yok')).toBeInTheDocument();
  });
});

describe('kredi hata mesajı', () => {
  it('yetersiz kredi Türkçe ve anlaşılır gösterilir (kod ve 402 durumu)', () => {
    expect(errorMessage(new ApiError(402, 'INSUFFICIENT_CREDITS', 'x').code)).toMatch(/Yeterli krediniz yok/);
    expect(errorMessage('HTTP_402')).toMatch(/kredi yükletin/);
  });
});
