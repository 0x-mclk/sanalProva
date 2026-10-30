import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../../shared/api/http';
import { ThemeProvider } from '../../shared/theme/ThemeProvider';
import { CreateOrganizationPage } from './CreateOrganizationPage';
import { OrganizationDetailPage } from './OrganizationDetailPage';
import { suggestSlug, validateOrganizationForm } from './createOrganization';
import { SetupLinkReveal } from './SetupLinkReveal';

const LINK = '/hesap/sifre-belirle#t=' + 'Z'.repeat(43);
const createMock = vi.fn();
const reissueMock = vi.fn();
let detail: unknown;

vi.mock('../../shared/api/platform', async (original) => ({
  ...(await original<typeof import('../../shared/api/platform')>()),
  createOrganization: (...args: unknown[]) => createMock(...args),
  reissueOwnerLink: (...args: unknown[]) => reissueMock(...args),
  useOrganizationDetail: () => ({ data: detail, isLoading: false, isError: false, refetch: vi.fn() }),
}));

function wrap(node: React.ReactNode, client = new QueryClient(), path = '/platform/yeni') {
  return {
    client,
    ...render(
      <ThemeProvider>
        <QueryClientProvider client={client}>
          <MemoryRouter initialEntries={[path]}>
            <Routes>
              <Route path="/platform" element={<p>LISTE</p>} />
              <Route path="/platform/yeni" element={node} />
              <Route path="/platform/:id" element={node ?? <p>DETAY</p>} />
            </Routes>
          </MemoryRouter>
        </QueryClientProvider>
      </ThemeProvider>,
    ),
  };
}

beforeEach(() => {
  createMock.mockReset();
  reissueMock.mockReset();
  window.localStorage.clear();
  window.sessionStorage.clear();
});

describe('form doğrulaması', () => {
  it('geçerli formu kabul eder, hatalı alanları Türkçe işaretler', () => {
    const ok = { name: 'Gül Gelinlik', slug: 'gul-gelinlik', storeName: 'Merkez', storeCode: 'MRK', ownerDisplayName: 'Ayşe Sahip', ownerEmail: 'ayse@ornek.test' };
    expect(validateOrganizationForm(ok)).toEqual({});
    const errors = validateOrganizationForm({ ...ok, slug: 'Gul Gelinlik', ownerEmail: 'yanlis', storeCode: 'M R' });
    expect(Object.keys(errors).sort()).toEqual(['ownerEmail', 'slug', 'storeCode']);
  });

  it('firma adından Türkçe karakterleri sadeleştirerek kısa ad önerir', () => {
    expect(suggestSlug('Şık Gelinlik & Nişan')).toBe('sik-gelinlik-nisan');
  });
});

describe('SetupLinkReveal', () => {
  it('bağlantıyı mutlak adresle gösterir, kapatılınca DOM’dan siler ve depolamaya yazmaz', () => {
    const onClose = vi.fn();
    const { container, rerender } = render(
      <ThemeProvider>
        <SetupLinkReveal link={LINK} expiresAt="2026-11-04T10:00:00Z" onClose={onClose} />
      </ThemeProvider>,
    );
    expect(screen.getByTestId('setup-link').textContent).toBe(window.location.origin + LINK);
    expect(screen.getByText('bir daha gösterilmez', { selector: 'strong' })).toBeInTheDocument();
    expect(window.localStorage.length + window.sessionStorage.length).toBe(0);

    fireEvent.click(screen.getByRole('button', { name: /Kapat/ }));
    expect(onClose).toHaveBeenCalled();

    rerender(<ThemeProvider>{null}</ThemeProvider>);
    expect(container.innerHTML).not.toContain('Z'.repeat(43));
  });
});

describe('CreateOrganizationPage', () => {
  const fillForm = () => {
    fireEvent.change(screen.getByLabelText(/^Firma adı/), { target: { value: 'Gül Gelinlik' } });
    fireEvent.change(screen.getByLabelText(/^İlk şube adı/), { target: { value: 'Merkez' } });
    fireEvent.change(screen.getByLabelText(/^Şube kodu/), { target: { value: 'MRK' } });
    fireEvent.change(screen.getByLabelText(/^Firma sahibi \(Owner\) adı soyadı/), { target: { value: 'Ayşe Sahip' } });
    fireEvent.change(screen.getByLabelText(/^Firma sahibi e-posta/), { target: { value: 'ayse@ornek.test' } });
  };

  const response = (overrides = {}) => ({
    organizationId: 'o1',
    storeId: 's1',
    ownerUserId: 'u1',
    setupLink: LINK,
    setupLinkExpiresAt: '2026-11-04T10:00:00Z',
    setupLinkShown: true,
    ...overrides,
  });

  it('kısa adı firma adından türetir; başarıda bağlantıyı yalnızca bir kez gösterir ve önbelleğe/depolamaya yazmaz', async () => {
    createMock.mockResolvedValueOnce(response());
    const { client, container } = wrap(<CreateOrganizationPage />);
    fillForm();
    expect((screen.getByLabelText(/^Kısa ad/) as HTMLInputElement).value).toBe('gul-gelinlik');

    fireEvent.click(screen.getByRole('button', { name: 'İşletmeyi oluştur' }));

    expect(await screen.findByTestId('setup-link')).toHaveTextContent(LINK);
    const [body, key] = createMock.mock.calls[0]! as [Record<string, string>, string];
    expect(body).toMatchObject({ name: 'Gül Gelinlik', slug: 'gul-gelinlik', storeCode: 'MRK', ownerEmail: 'ayse@ornek.test' });
    expect(key).toMatch(/^[0-9a-f]{32}$/); // Idempotency-Key: 8–64 karakter
    expect(window.localStorage.length + window.sessionStorage.length).toBe(0);
    expect(client.getMutationCache().getAll()).toHaveLength(0);
    expect(JSON.stringify(client.getQueryCache().getAll().map((q) => q.state.data))).not.toContain('Z'.repeat(43));

    // Kapatınca bağlantı bellekten silinir ve işletme sayfasına gidilir
    fireEvent.click(screen.getByRole('button', { name: /Kapat/ }));
    await waitFor(() => expect(screen.queryByTestId('setup-link')).not.toBeInTheDocument());
    expect(container.innerHTML).not.toContain('Z'.repeat(43));
  });

  it('çift tıklamada tek istek gönderilir', async () => {
    let finish: (value: unknown) => void = () => {};
    createMock.mockImplementationOnce(() => new Promise((resolve) => (finish = resolve)));
    wrap(<CreateOrganizationPage />);
    fillForm();

    const submit = screen.getByRole('button', { name: 'İşletmeyi oluştur' });
    fireEvent.click(submit);
    fireEvent.click(submit);
    fireEvent.submit(submit.closest('form')!);

    expect(createMock).toHaveBeenCalledTimes(1);
    await act(async () => finish(response()));
    expect(await screen.findByTestId('setup-link')).toBeInTheDocument();
  });

  it('ağ hatasında AYNI anahtarla tekrar dener; kesin 4xx reddinde yeni anahtar alır', async () => {
    createMock
      .mockRejectedValueOnce(new ApiError(0, 'NETWORK_ERROR', 'ağ'))
      .mockRejectedValueOnce(new ApiError(409, 'SLUG_TAKEN', 'dolu'))
      .mockResolvedValueOnce(response());
    wrap(<CreateOrganizationPage />);
    fillForm();
    const submit = () => fireEvent.click(screen.getByRole('button', { name: 'İşletmeyi oluştur' }));

    submit();
    expect(await screen.findByText(/Sunucuya ulaşılamadı/)).toBeInTheDocument();
    submit();
    expect(await screen.findByText(/firma adresi \(kısa ad\) zaten kullanılıyor/)).toBeInTheDocument();
    submit();
    await screen.findByTestId('setup-link');

    const keys = createMock.mock.calls.map((call) => call[1] as string);
    expect(keys[1]).toBe(keys[0]); // belirsiz sonuç: aynı anahtar (sunucu tekrarı tanır)
    expect(keys[2]).not.toBe(keys[1]); // kesin ret: hiçbir şey oluşmadı, yeni anahtar
  });

  it('tekrar yanıtında (bağlantı yok) yeniden gösterilmez; işletmeye yönlendirir', async () => {
    createMock.mockResolvedValueOnce(response({ setupLink: null, setupLinkExpiresAt: null, setupLinkShown: false }));
    wrap(<CreateOrganizationPage />);
    fillForm();
    fireEvent.click(screen.getByRole('button', { name: 'İşletmeyi oluştur' }));

    expect(await screen.findByText(/yalnızca ilk yanıtta gösterilir/)).toBeInTheDocument();
    expect(screen.queryByTestId('setup-link')).not.toBeInTheDocument();
  });

  it('geçersiz formda sunucuya gitmez', () => {
    wrap(<CreateOrganizationPage />);
    fireEvent.click(screen.getByRole('button', { name: 'İşletmeyi oluştur' }));
    expect(createMock).not.toHaveBeenCalled();
    expect(screen.getByText(/Firma adı 2–120 karakter olmalı/)).toBeInTheDocument();
  });
});

describe('OrganizationDetailPage', () => {
  const organization = (setupPending: boolean) => ({
    id: 'o1',
    name: 'Gül Gelinlik',
    slug: 'gul-gelinlik',
    isActive: true,
    paidAiEnabled: false,
    createdAt: '2026-11-01T10:00:00Z',
    agreementAccepted: false,
    agreementAcceptedAt: null,
    agreementVersion: '2026-11-v1',
    stores: [{ id: 's1', name: 'Merkez', code: 'MRK', isActive: true }],
    owners: [{ userId: 'u1', displayName: 'Ayşe Sahip', email: 'ayse@ornek.test', setupPending }],
  });

  const renderDetail = () => wrap(<OrganizationDetailPage />, new QueryClient(), '/platform/o1');

  it('özet, durumlar ve ücretli AI notunu gösterir; müşteri verisi yoktur', () => {
    detail = organization(true);
    renderDetail();
    expect(screen.getByRole('heading', { name: 'Gül Gelinlik' })).toBeInTheDocument();
    expect(screen.getByText('Kurulum: şifre bekleniyor')).toBeInTheDocument();
    expect(screen.getByText('Sözleşme: bekliyor')).toBeInTheDocument();
    expect(screen.getByText('Ücretli AI: kapalı')).toBeInTheDocument();
    expect(screen.getByText(/yalnızca sunucudaki operatör komutuyla/)).toBeInTheDocument();
  });

  it('yeniden üretme: onay penceresi, çift tıklamada tek istek, bağlantı yalnızca bir kez', async () => {
    detail = organization(true);
    let finish: (value: unknown) => void = () => {};
    reissueMock.mockImplementationOnce(() => new Promise((resolve) => (finish = resolve)));
    renderDetail();

    fireEvent.click(screen.getByRole('button', { name: 'Kurulum bağlantısını yeniden üret' }));
    const confirm = await screen.findByRole('button', { name: 'Yeniden üret' });
    fireEvent.click(confirm);
    fireEvent.click(confirm);
    expect(reissueMock).toHaveBeenCalledTimes(1);
    expect(reissueMock).toHaveBeenCalledWith('o1');

    await act(async () => finish({ organizationId: 'o1', ownerUserId: 'u1', setupLink: LINK, setupLinkExpiresAt: '2026-11-04T10:00:00Z' }));
    expect(await screen.findByTestId('setup-link')).toHaveTextContent(LINK);
    expect(window.localStorage.length + window.sessionStorage.length).toBe(0);

    fireEvent.click(screen.getByRole('button', { name: /Kapat/ }));
    await waitFor(() => expect(screen.queryByTestId('setup-link')).not.toBeInTheDocument());
  });

  it('Owner şifresini belirlemişse yeniden üretme düğmesi yoktur; sunucu reddi Türkçe gösterilir', async () => {
    detail = organization(false);
    renderDetail();
    expect(screen.queryByRole('button', { name: 'Kurulum bağlantısını yeniden üret' })).not.toBeInTheDocument();
    expect(screen.getByText(/şifresini belirlemiş/)).toBeInTheDocument();
  });

  it('yeniden üretme hatası Türkçe gösterilir', async () => {
    detail = organization(true);
    reissueMock.mockRejectedValueOnce(new ApiError(409, 'OWNER_ALREADY_ACTIVATED', 'x'));
    renderDetail();
    fireEvent.click(screen.getByRole('button', { name: 'Kurulum bağlantısını yeniden üret' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Yeniden üret' }));
    expect(await screen.findByText(/şifresini zaten belirlemiş/)).toBeInTheDocument();
  });
});
