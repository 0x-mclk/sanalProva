import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../../shared/api/http';
import { readLocked, writeLocked } from '../../shared/auth/lockState';
import { ThemeProvider } from '../../shared/theme/ThemeProvider';
import { CreditQuoteNotice } from '../credits/CreditNotices';
import { IdleLock } from './IdleLock';

let status: { hasPin: boolean; idleLockSeconds: number };
const unlockFn = vi.fn<(body: { pin?: string; password?: string }) => Promise<void>>();

vi.mock('../../shared/api/lock', async () => {
  const query = await import('@tanstack/react-query');
  return {
    useLockStatus: () => ({ data: status }),
    useUnlock: () => query.useMutation({ mutationFn: (body: { pin?: string; password?: string }) => unlockFn(body) }),
  };
});

function renderLock(client = new QueryClient()) {
  return {
    client,
    ...render(
      <ThemeProvider>
        <QueryClientProvider client={client}>
          <IdleLock>
            <p>GIZLI-ICERIK müşteri fotoğrafı</p>
          </IdleLock>
        </QueryClientProvider>
      </ThemeProvider>,
    ),
  };
}

beforeEach(() => {
  status = { hasPin: true, idleLockSeconds: 300 };
  unlockFn.mockReset();
  window.sessionStorage.clear();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('ekran kilidi: boşta kilitlenme', () => {
  it('boşta süre dolunca içerik DOM’dan kalkar, kilit ekranı görünür ve bayrak yazılır', () => {
    vi.useFakeTimers();
    renderLock();
    expect(screen.getByText(/GIZLI-ICERIK/)).toBeInTheDocument();
    act(() => void vi.advanceTimersByTime(301_000));
    expect(screen.queryByText(/GIZLI-ICERIK/)).not.toBeInTheDocument();
    expect(screen.getByTestId('lock-screen')).toBeInTheDocument();
    expect(readLocked()).toBe(true);
  });

  it('etkileşim süreyi sıfırlar: kilit yalnızca sürekli boşta kalınca devreye girer', () => {
    vi.useFakeTimers();
    renderLock();
    act(() => void vi.advanceTimersByTime(200_000));
    fireEvent.pointerDown(window);
    act(() => void vi.advanceTimersByTime(200_000));
    expect(screen.getByText(/GIZLI-ICERIK/)).toBeInTheDocument();
    act(() => void vi.advanceTimersByTime(110_000));
    expect(screen.getByTestId('lock-screen')).toBeInTheDocument();
  });

  it('sayfa yenilenince (kilit bayrağı varken) kilitli başlar; içerik hiç çizilmez', () => {
    writeLocked(true);
    renderLock();
    expect(screen.getByTestId('lock-screen')).toBeInTheDocument();
    expect(screen.queryByText(/GIZLI-ICERIK/)).not.toBeInTheDocument();
  });

  it('kilitlenince müşteri verisi sorgu önbelleğinden de silinir; kimlik korunur', () => {
    vi.useFakeTimers();
    const client = new QueryClient({ defaultOptions: { queries: { gcTime: Infinity } } }); // gözlemcisiz test verisi 5 dk sonra kendiliğinden toplanmasın
    client.setQueryData(['sessions', 'open'], { items: ['müşteri'] });
    client.setQueryData(['me'], { id: 'u1' });
    renderLock(client);
    act(() => void vi.advanceTimersByTime(301_000));
    expect(client.getQueryData(['sessions', 'open'])).toBeUndefined();
    expect(client.getQueryData(['me'])).toEqual({ id: 'u1' });
  });
});

describe('ekran kilidi: açma', () => {
  beforeEach(() => writeLocked(true));

  it('doğru PIN içeriği geri getirir ve bayrağı temizler; PIN sunucuya gönderilir', async () => {
    unlockFn.mockResolvedValue();
    renderLock();
    fireEvent.change(screen.getByLabelText('PIN'), { target: { value: '4829' } });
    fireEvent.click(screen.getByRole('button', { name: 'Kilidi aç' }));
    await waitFor(() => expect(screen.getByText(/GIZLI-ICERIK/)).toBeInTheDocument());
    expect(unlockFn).toHaveBeenCalledWith({ pin: '4829' });
    expect(readLocked()).toBe(false);
  });

  it('yanlış PIN: hata ve kalan hak gösterilir, kilit açılmaz, alan temizlenir', async () => {
    unlockFn.mockRejectedValue(new ApiError(401, 'INVALID_PIN', 'PIN hatalı.', { remainingAttempts: 3 }));
    renderLock();
    fireEvent.change(screen.getByLabelText('PIN'), { target: { value: '0000' } });
    fireEvent.click(screen.getByRole('button', { name: 'Kilidi aç' }));
    await waitFor(() => expect(screen.getByText(/PIN hatalı\. Kalan hak: 3/)).toBeInTheDocument());
    expect(screen.queryByText(/GIZLI-ICERIK/)).not.toBeInTheDocument();
    expect(readLocked()).toBe(true);
    expect((screen.getByLabelText('PIN') as HTMLInputElement).value).toBe('');
  });

  it('çok hatalı deneme: sunucu oturumu kapattı; kilit bayrağı temizlenir (giriş ekranına yönlenir)', async () => {
    unlockFn.mockRejectedValue(new ApiError(401, 'PIN_LOCKED_OUT', 'kapandı'));
    renderLock();
    fireEvent.change(screen.getByLabelText('PIN'), { target: { value: '0000' } });
    fireEvent.click(screen.getByRole('button', { name: 'Kilidi aç' }));
    await waitFor(() => expect(readLocked()).toBe(false));
    expect(screen.getByText(/oturum kapatıldı/i)).toBeInTheDocument();
  });

  it('PIN’i olmayan hesap parolayla açar (PIN yerine parola alanı)', async () => {
    status = { hasPin: false, idleLockSeconds: 300 };
    unlockFn.mockResolvedValue();
    renderLock();
    fireEvent.change(screen.getByLabelText('Parola'), { target: { value: 'gizli-parola' } });
    fireEvent.click(screen.getByRole('button', { name: 'Kilidi aç' }));
    await waitFor(() => expect(screen.getByText(/GIZLI-ICERIK/)).toBeInTheDocument());
    expect(unlockFn).toHaveBeenCalledWith({ password: 'gizli-parola' });
  });

  it('boş girişle istek gönderilmez', () => {
    renderLock();
    expect(screen.getByRole('button', { name: 'Kilidi aç' })).toBeDisabled();
    expect(unlockFn).not.toHaveBeenCalled();
  });
});

describe('üretim öncesi kredi bilgisi', () => {
  const quote = { enforced: true, live: true, cost: 2, available: 10, sufficient: true, availableAfter: 8, lowAfter: false };
  const view = (q: Parameters<typeof CreditQuoteNotice>[0]['quote']) =>
    render(
      <ThemeProvider>
        <CreditQuoteNotice quote={q} />
      </ThemeProvider>,
    );

  it('gerçek modda bedeli, kalan krediyi ve iade güvencesini gösterir', () => {
    view(quote);
    expect(screen.getByText(/2 kredi/)).toBeInTheDocument();
    expect(screen.getByText(/kalan: 10 → 8/)).toBeInTheDocument();
    expect(screen.getByText(/kredi iade edilir/)).toBeInTheDocument();
  });

  it('yetersiz krediyi açıkça uyarır', () => {
    view({ ...quote, available: 1, sufficient: false, availableAfter: 0 });
    expect(screen.getByText(/kalan krediniz 1/)).toBeInTheDocument();
  });

  it('demo modda kredi harcanmadığını ve sonucun gerçek AI olmadığını söyler', () => {
    view({ ...quote, live: false, enforced: false, cost: 0 });
    expect(screen.getByText(/Demo üretim: kredi harcanmaz ve sonuç gerçek yapay zekâ çıktısı değildir/)).toBeInTheDocument();
  });

  it('kredi azalıyorsa uyarı tonuyla bildirir', () => {
    view({ ...quote, availableAfter: 2, lowAfter: true });
    expect(screen.getByText(/Kredi azalıyor/)).toBeInTheDocument();
  });
});
