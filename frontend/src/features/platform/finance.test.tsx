import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../../shared/api/http';
import type { CostCase, CostCaseDetail, MonitorCheck, OrganizationFinance } from '../../shared/api/platform';
import { ThemeProvider } from '../../shared/theme/ThemeProvider';
import { FinancePage } from './FinancePage';
import { ReconciliationPage } from './ReconciliationPage';

let finance: OrganizationFinance[];
let checks: MonitorCheck[];
let cases: CostCase[];
let detail: CostCaseDetail;
const decideSpy = vi.fn();

vi.mock('../../shared/api/platform', async (original) => ({
  ...(await original<typeof import('../../shared/api/platform')>()),
  usePlatformFinance: () => ({ data: finance, isLoading: false, isError: false, refetch: vi.fn() }),
  usePlatformMonitoring: () => ({ data: checks, isLoading: false, isError: false, refetch: vi.fn() }),
  useCostCases: () => ({ data: cases, isLoading: false, isError: false, refetch: vi.fn() }),
  useCostCase: () => ({ data: detail, isLoading: false, isError: false, refetch: vi.fn() }),
  decideCostCase: (...args: unknown[]) => decideSpy(...args),
}));

const org = (over: Partial<OrganizationFinance>): OrganizationFinance => ({
  organizationId: 'o1', name: 'Gül Gelinlik', slug: 'gul', isActive: true, paidAiEnabled: true, creditBalance: 40, creditReserved: 3, creditAvailable: 37, lowCredit: false,
  dailyLimitUsd: 2, monthlyLimitUsd: 30, maxJobCostUsd: 0.25, spentTodayUsd: 0.5, spentMonthUsd: 4, openReservedUsd: 0.15, openUncertainCases: 0, oldestUncertainAt: null, budgetLevel: 'Ok', settingsCorrupt: false, ...over,
});

const caseDto = (over: Partial<CostCase> = {}): CostCase => ({
  id: 'c1', organizationId: 'o1', organizationName: 'Gül Gelinlik', storeId: 's1', jobId: 'j1', provider: 'fal', model: 'seedream', quality: null, operation: 'TryOn', attempt: 1,
  providerRequestId: 'req-1', reservedCostUsd: 0.1, effectiveCostUsd: 0.1, state: 'Open', decisionCount: 0, occurredAt: '2026-11-01T10:00:00Z', ...over,
});

function wrap(node: React.ReactNode) {
  return render(
    <ThemeProvider>
      <QueryClientProvider client={new QueryClient()}>
        <MemoryRouter>{node}</MemoryRouter>
      </QueryClientProvider>
    </ThemeProvider>,
  );
}

beforeEach(() => {
  decideSpy.mockReset();
  finance = [org({}), org({ organizationId: 'o2', name: 'Az Kredili', slug: 'az', creditBalance: 2, creditAvailable: 2, creditReserved: 0, lowCredit: true, openUncertainCases: 2, budgetLevel: 'Warning' })];
  checks = [
    { name: 'database', level: 'Ok', message: 'Bağlantı kuruldu (4 ms).' },
    { name: 'disk', level: 'Critical', message: 'Depo diskinde %5 boş (2 GB).' },
  ];
  cases = [caseDto()];
  detail = { case: caseDto(), decisions: [] };
});

describe('platform finans ve izleme', () => {
  it('izleme kontrollerini seviye rozetiyle gösterir', () => {
    wrap(<FinancePage />);
    expect(within(screen.getByTestId('check-disk')).getByText('Kritik')).toBeInTheDocument();
    expect(within(screen.getByTestId('check-database')).getByText('Tamam')).toBeInTheDocument();
  });

  it('işletme başına bakiye, bütçe, açık rezervasyon ve belirsiz vakayı gösterir', () => {
    wrap(<FinancePage />);
    const row = screen.getByTestId('finance-az');
    expect(within(row).getByText(/Kredi: 2 kullanılabilir/)).toBeInTheDocument();
    expect(within(row).getByText(/2 belirsiz maliyet/)).toBeInTheDocument();
    expect(within(row).getByText('Bütçe: Uyarı')).toBeInTheDocument();
    expect(within(screen.getByTestId('finance-gul')).getByText(/Bugün \$0\.50 \/ \$2\.00/)).toBeInTheDocument();
    expect(within(screen.getByTestId('finance-gul')).getByText(/Açık rezervasyon \$0\.15/)).toBeInTheDocument();
  });
});

describe('bozuk firma ayarı', () => {
  it('ayarı okunamayan firma kırmızı uyarıyla gösterilir (fail-closed)', () => {
    finance = [org({ organizationId: 'o9', name: 'Bozuk Firma', slug: 'bozuk', settingsCorrupt: true, paidAiEnabled: false })];
    wrap(<FinancePage />);
    expect(within(screen.getByTestId('finance-bozuk')).getByText(/Ayarlar okunamıyor/)).toBeInTheDocument();
  });
});

describe('mutabakat ekranı', () => {
  const open = () => {
    wrap(<ReconciliationPage />);
    fireEvent.click(screen.getByTestId('case-c1'));
  };

  it('bekleyen vakaları listeler ve karar penceresinde geri alınamazlığı belirtir', () => {
    open();
    expect(screen.getByText(/Karar silinemez; düzeltme yeni bir karar olarak eklenir/)).toBeInTheDocument();
  });

  it('“ücretlendi” kararı tutar olmadan kaydedilemez; tutarla beklenen sıra numarası ve idempotency anahtarıyla gönderilir', async () => {
    decideSpy.mockResolvedValue(detail);
    open();
    fireEvent.click(screen.getByLabelText(/Sağlayıcıda ücretlendi/));
    expect(screen.getByRole('button', { name: 'Kararı kaydet' })).toBeDisabled();
    fireEvent.change(screen.getByLabelText(/Gerçek tutar/), { target: { value: '0.0825' } });
    fireEvent.click(screen.getByRole('button', { name: 'Kararı kaydet' }));
    await waitFor(() => expect(decideSpy).toHaveBeenCalledTimes(1));
    const [id, body, key] = decideSpy.mock.calls[0] as [string, { decision: string; amountUsd?: number; expectedSequence: number }, string];
    expect(id).toBe('c1');
    expect(body).toMatchObject({ decision: 'ConfirmedCharged', amountUsd: 0.0825, expectedSequence: 0 });
    expect(key.length).toBeGreaterThanOrEqual(8);
  });

  it('“ücretlenmedi” kararı tutar göndermez', async () => {
    decideSpy.mockResolvedValue(detail);
    open();
    fireEvent.click(screen.getByLabelText(/Sağlayıcıda ücretlenmedi/));
    fireEvent.click(screen.getByRole('button', { name: 'Kararı kaydet' }));
    await waitFor(() => expect(decideSpy).toHaveBeenCalled());
    expect((decideSpy.mock.calls[0] as [string, { amountUsd?: number }])[1].amountUsd).toBeUndefined();
  });

  it('çakışmada (409) açıklayıcı mesaj gösterir; yeniden denemede AYNI idempotency anahtarı kullanılır', async () => {
    decideSpy.mockRejectedValueOnce(new ApiError(409, 'DECISION_CONFLICT', 'çakışma')).mockResolvedValueOnce(detail);
    open();
    fireEvent.click(screen.getByLabelText(/Sağlayıcıda ücretlenmedi/));
    fireEvent.click(screen.getByRole('button', { name: 'Kararı kaydet' }));
    await waitFor(() => expect(screen.getByText(/başka bir karar girilmiş/)).toBeInTheDocument());
    fireEvent.click(screen.getByRole('button', { name: 'Kararı kaydet' }));
    await waitFor(() => expect(decideSpy).toHaveBeenCalledTimes(2));
    expect((decideSpy.mock.calls[0] as unknown[])[2]).toBe((decideSpy.mock.calls[1] as unknown[])[2]);
  });
});
