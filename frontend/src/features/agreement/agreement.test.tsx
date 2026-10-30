import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ThemeProvider } from '../../shared/theme/ThemeProvider';
import { AgreementPage } from './AgreementPage';

type AuthStub = { isPlatformAdmin: boolean; agreementRequired: boolean; canAcceptAgreement: boolean; me: { organizationName: string } };
let auth: AuthStub;
const mutate = vi.fn();
let acceptState: { isPending: boolean; isError: boolean; error: unknown };

const agreement = {
  version: '2026-11-v1',
  title: 'Sanal Prova Hizmet Sözleşmesi (TASLAK)',
  text: 'TASLAK satırı\n1. Taraflar ve konu\nBu sözleşme işletme ile Sanal Prova arasındadır.',
  textSha256: 'a'.repeat(64),
  accepted: false,
  acceptedAt: null,
  canAccept: true,
};

vi.mock('../../shared/auth/AuthContext', () => ({ useAuth: () => auth }));
vi.mock('../../shared/api/hooks', () => ({ useMe: () => ({}), useLogout: () => ({ mutate: vi.fn(), isPending: false }) }));
vi.mock('../../shared/api/platform', () => ({
  useAgreement: () => ({ data: agreement, isLoading: false, isError: false, refetch: vi.fn() }),
  useAcceptAgreement: () => ({ mutate, ...acceptState }),
}));

const renderPage = () =>
  render(
    <ThemeProvider>
      <MemoryRouter initialEntries={['/sozlesme']}>
        <Routes>
          <Route path="/" element={<p>MAGAZA</p>} />
          <Route path="/platform" element={<p>PLATFORM</p>} />
          <Route path="/sozlesme" element={<AgreementPage />} />
        </Routes>
      </MemoryRouter>
    </ThemeProvider>,
  );

beforeEach(() => {
  mutate.mockReset();
  acceptState = { isPending: false, isError: false, error: null };
  auth = { isPlatformAdmin: false, agreementRequired: true, canAcceptAgreement: true, me: { organizationName: 'Gül Gelinlik' } };
});

describe('AgreementPage', () => {
  it('Owner metni görür; onay kutusu işaretlenmeden kabul edemez; kabul sürüm + özetle gönderilir', () => {
    renderPage();
    expect(screen.getByRole('heading', { name: 'Sanal Prova Hizmet Sözleşmesi (TASLAK)' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '1. Taraflar ve konu' })).toBeInTheDocument();
    const accept = screen.getByRole('button', { name: 'Sözleşmeyi kabul et' });
    expect(accept).toBeDisabled();

    fireEvent.click(screen.getByLabelText(/Sözleşme metnini okudum/));
    expect(accept).toBeEnabled();
    fireEvent.click(accept);

    expect(mutate).toHaveBeenCalledWith({ version: '2026-11-v1', textSha256: 'a'.repeat(64) });
  });

  it('Owner olmayan personel kabul düğmesi yerine Owner onayı beklediğini görür', () => {
    auth = { ...auth, canAcceptAgreement: false };
    renderPage();
    expect(screen.getByText(/firma sahibi \(Owner\) tarafından kabul edilmesi bekleniyor/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sözleşmeyi kabul et' })).not.toBeInTheDocument();
  });

  it('hizmet sözleşmesi ile müşteri KVKK onayının ayrı olduğunu açıkça belirtir', () => {
    renderPage();
    expect(screen.getByText(/işletmeniz ile Sanal Prova/)).toBeInTheDocument();
    expect(screen.getByText(/Müşterinin fotoğraf \(KVKK\) onayı ayrıdır/)).toBeInTheDocument();
  });

  it('sürüm uyuşmazlığında Türkçe hata gösterilir', async () => {
    const { ApiError } = await import('../../shared/api/http');
    acceptState = { isPending: false, isError: true, error: new ApiError(409, 'AGREEMENT_VERSION_MISMATCH', 'x') };
    renderPage();
    expect(screen.getByText(/Sözleşme metni güncellendi/)).toBeInTheDocument();
  });

  it('platform yöneticisi /platform’a, zaten kabul edilmişse mağazaya yönlenir', () => {
    auth = { ...auth, isPlatformAdmin: true };
    const first = renderPage();
    expect(screen.getByText('PLATFORM')).toBeInTheDocument();
    first.unmount();

    auth = { ...auth, isPlatformAdmin: false, agreementRequired: false };
    renderPage();
    expect(screen.getByText('MAGAZA')).toBeInTheDocument();
  });
});
