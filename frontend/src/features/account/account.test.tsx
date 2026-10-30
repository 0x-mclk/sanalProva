import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetCsrfToken } from '../../shared/api/http';
import { ThemeProvider } from '../../shared/theme/ThemeProvider';
import { validateNewPassword } from './password';
import { SetupPasswordPage } from './SetupPasswordPage';
import { discardSetupToken, resetSetupTokenForTests, takeSetupToken } from './setupToken';

const TOKEN = 'A'.repeat(43);
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

function setHash(hash: string, search = '') {
  window.history.replaceState({ idx: 7 }, '', `/hesap/sifre-belirle${search}${hash}`);
}

beforeEach(() => {
  resetSetupTokenForTests();
  window.localStorage.clear();
  window.sessionStorage.clear();
});

describe('takeSetupToken (URL fragment)', () => {
  it('token fragment’tan okunur ve adres çubuğundan HEMEN silinir (sorgu ve router durumu korunur)', () => {
    setHash(`#t=${TOKEN}`, '?x=1');

    expect(takeSetupToken()).toBe(TOKEN);

    expect(window.location.hash).toBe('');
    expect(window.location.pathname + window.location.search).toBe('/hesap/sifre-belirle?x=1');
    expect(window.history.state).toEqual({ idx: 7 });
  });

  it('ikinci çağrı (React StrictMode çift başlatıcı) aynı token’ı verir, fragment bir daha okunmaz', () => {
    setHash(`#t=${TOKEN}`);
    expect(takeSetupToken()).toBe(TOKEN);
    expect(takeSetupToken()).toBe(TOKEN);
  });

  it.each([['#'], ['#t='], ['#t=kisa'], [`#t=${'B'.repeat(201)}`], ['#baska=1'], ['']])('geçersiz/eksik fragment (%s) null döner', (hash) => {
    setHash(hash);
    expect(takeSetupToken()).toBeNull();
    expect(window.location.hash).toBe('');
  });

  it('adres çubuğunda YENİ fragment varsa önceki (önbellekteki) token’ı geçer', () => {
    setHash(`#t=${TOKEN}`);
    expect(takeSetupToken()).toBe(TOKEN);
    const newer = 'C'.repeat(43);
    setHash(`#t=${newer}`);
    expect(takeSetupToken()).toBe(newer);
    expect(window.location.hash).toBe('');
  });

  it('discard sonrası token unutulur; hiçbir depolamaya yazılmaz', () => {
    setHash(`#t=${TOKEN}`);
    takeSetupToken();
    discardSetupToken();
    expect(takeSetupToken()).toBeNull();
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });
});

describe('validateNewPassword', () => {
  it('sunucu politikasını yansıtır: 10 karakter, büyük/küçük harf, rakam ve eşleşme', () => {
    expect(validateNewPassword('Gizli-Parola-98765', 'Gizli-Parola-98765')).toEqual([]);
    expect(validateNewPassword('kisa', 'kisa')).toEqual(expect.arrayContaining(['En az 10 karakter olmalı.', 'En az bir büyük harf içermeli.', 'En az bir rakam içermeli.']));
    expect(validateNewPassword('Gizli-Parola-98765', 'Baska-Parola-98765')).toEqual(['Şifre ve şifre tekrarı aynı olmalı.']);
    expect(validateNewPassword('GIZLIPAROLA12345', 'GIZLIPAROLA12345')).toContain('En az bir küçük harf içermeli.');
  });
});

describe('SetupPasswordPage', () => {
  const fetchMock = vi.fn<typeof fetch>();

  beforeEach(() => {
    resetCsrfToken();
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });
  afterEach(() => vi.unstubAllGlobals());

  const renderPage = () =>
    render(
      <ThemeProvider>
        <MemoryRouter>
          <SetupPasswordPage />
        </MemoryRouter>
      </ThemeProvider>,
    );

  const fill = (password: string, again = password) => {
    fireEvent.change(screen.getByLabelText(/^Yeni şifre/), { target: { value: password } });
    fireEvent.change(screen.getByLabelText(/^Şifre \(tekrar\)/), { target: { value: again } });
  };

  const postRequest = () => fetchMock.mock.calls.map((c) => c[0] as Request).find((r) => r.method === 'POST');

  it('bağlantısız ziyaret güvenli hatayı gösterir ve hiçbir istek göndermez', () => {
    setHash('');
    renderPage();
    expect(screen.getByRole('heading', { name: 'Bağlantı kullanılamıyor' })).toBeInTheDocument();
    expect(screen.getByText(/geçersiz veya süresi dolmuş/)).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('geçersiz bağlantı gösterilirken aynı sekmede yeni bağlantı yapıştırılırsa (hashchange) sayfa yeni token’la yeniden başlar', async () => {
    setHash('');
    renderPage();
    expect(screen.getByRole('heading', { name: 'Bağlantı kullanılamıyor' })).toBeInTheDocument();

    setHash(`#t=${TOKEN}`);
    act(() => {
      window.dispatchEvent(new HashChangeEvent('hashchange'));
    });

    expect(await screen.findByRole('heading', { name: 'Şifrenizi belirleyin' })).toBeInTheDocument();
    expect(window.location.hash).toBe(''); // yeni fragment de hemen silindi
  });

  it('şifreler eşleşmezse sunucuya gitmez ve Türkçe hata gösterir', () => {
    setHash(`#t=${TOKEN}`);
    renderPage();
    fill('Gizli-Parola-98765', 'Baska-Parola-98765');
    fireEvent.click(screen.getByRole('button', { name: 'Şifreyi belirle' }));
    expect(screen.getByText('Şifre ve şifre tekrarı aynı olmalı.')).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('başarıda token gövdede gider (URL’de değil), depolamaya yazılmaz, ekranda ve DOM’da görünmez', async () => {
    setHash(`#t=${TOKEN}`);
    fetchMock.mockResolvedValueOnce(json({ token: 'csrf-1' })).mockResolvedValueOnce(new Response(null, { status: 204 }));
    const { container } = renderPage();
    expect(window.location.hash).toBe(''); // sayfa açılır açılmaz adres çubuğu temiz

    fill('Gizli-Parola-98765');
    fireEvent.click(screen.getByRole('button', { name: 'Şifreyi belirle' }));

    expect(await screen.findByRole('heading', { name: 'Şifreniz belirlendi' })).toBeInTheDocument();
    const request = postRequest()!;
    expect(request.url).toMatch(/\/api\/v1\/account\/setup-password$/);
    expect(request.url).not.toContain(TOKEN);
    expect(await request.clone().json()).toEqual({ token: TOKEN, password: 'Gizli-Parola-98765' });
    expect(window.localStorage.length + window.sessionStorage.length).toBe(0);
    expect(container.innerHTML).not.toContain(TOKEN);
    expect(container.innerHTML).not.toContain('Gizli-Parola-98765');
    expect(takeSetupToken()).toBeNull(); // bellekten de silindi
  });

  it('kullanılmış/iptal/süresi dolmuş/bilinmeyen bağlantı: tek güvenli mesaj, neden belirtilmez', async () => {
    setHash(`#t=${TOKEN}`);
    fetchMock
      .mockResolvedValueOnce(json({ token: 'csrf-1' }))
      .mockResolvedValueOnce(json({ title: 'x', status: 400, code: 'SETUP_LINK_INVALID' }, 400));
    renderPage();

    fill('Gizli-Parola-98765');
    fireEvent.click(screen.getByRole('button', { name: 'Şifreyi belirle' }));

    expect(await screen.findByRole('heading', { name: 'Bağlantı kullanılamıyor' })).toBeInTheDocument();
    expect(screen.getByText(/geçersiz veya süresi dolmuş/)).toBeInTheDocument();
    expect(screen.queryByText(/kullanılmış|iptal edilmiş/i)).not.toBeInTheDocument();
  });

  it('şifre politikası ihlalinde Türkçe maddeler gösterilir ve form açık kalır (bağlantı yanmadı)', async () => {
    setHash(`#t=${TOKEN}`);
    fetchMock
      .mockResolvedValueOnce(json({ token: 'csrf-1' }))
      .mockResolvedValueOnce(json({ title: 'x', status: 400, code: 'PASSWORD_POLICY', errors: ['PasswordRequiresDigit'] }, 400));
    renderPage();

    fill('Gizli-Parola-98765');
    fireEvent.click(screen.getByRole('button', { name: 'Şifreyi belirle' }));

    expect(await screen.findByText('En az bir rakam içermeli.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Şifreyi belirle' })).toBeInTheDocument();
  });

  it('çift tıklamada tek istek gönderilir', async () => {
    setHash(`#t=${TOKEN}`);
    let release: (value: Response) => void = () => {};
    fetchMock.mockResolvedValueOnce(json({ token: 'csrf-1' })).mockImplementationOnce(() => new Promise<Response>((resolve) => (release = resolve)));
    renderPage();

    fill('Gizli-Parola-98765');
    const button = screen.getByRole('button', { name: 'Şifreyi belirle' });
    fireEvent.click(button);
    fireEvent.click(button);
    fireEvent.submit(button.closest('form')!);

    await waitFor(() => expect(fetchMock.mock.calls.filter((c) => (c[0] as Request).method === 'POST')).toHaveLength(1));
    await act(async () => release(new Response(null, { status: 204 })));
    expect(await screen.findByRole('heading', { name: 'Şifreniz belirlendi' })).toBeInTheDocument();
  });
});
