import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { errorMessage } from '../i18n/tr';
import { ApiError, apiFetch, resetCsrfToken, setActiveStoreId } from './http';

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

/** fetch(Request) çağrısındaki isteği döner. */
const sent = (mock: ReturnType<typeof vi.fn>, index: number) => mock.mock.calls[index]![0] as Request;

describe('apiFetch', () => {
  const fetchMock = vi.fn<typeof fetch>();

  beforeEach(() => {
    resetCsrfToken();
    setActiveStoreId(null);
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => vi.unstubAllGlobals());

  it('GET isteğine CSRF başlığı eklemez ve çerezi aynı origin ile gönderir', async () => {
    fetchMock.mockResolvedValueOnce(json({ ok: true }));

    await expect(apiFetch('/api/v1/x')).resolves.toEqual({ ok: true });

    const request = sent(fetchMock, 0);
    expect(request.credentials).toBe('same-origin');
    expect(request.headers.has('X-CSRF-TOKEN')).toBe(false);
  });

  it('POST öncesi CSRF token alır, başlığa ekler ve sonraki isteklerde yeniden kullanır', async () => {
    fetchMock.mockResolvedValueOnce(json({ token: 'tok-1' })).mockResolvedValueOnce(json({ id: 1 })).mockResolvedValueOnce(json({ id: 2 }));

    await apiFetch('/api/v1/a', { method: 'POST', json: { a: 1 } });
    await apiFetch('/api/v1/b', { method: 'POST', json: { b: 2 } });

    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(String(fetchMock.mock.calls[0]![0])).toContain('/api/v1/auth/csrf');
    const second = sent(fetchMock, 2);
    expect(second.headers.get('X-CSRF-TOKEN')).toBe('tok-1');
    expect(second.headers.get('Content-Type')).toBe('application/json');
  });

  it('seçili şubeyi her isteğe X-Store-Id olarak ekler', async () => {
    setActiveStoreId('store-123');
    fetchMock.mockResolvedValueOnce(json({}));

    await apiFetch('/api/v1/sessions');

    expect(sent(fetchMock, 0).headers.get('X-Store-Id')).toBe('store-123');
  });

  it('Problem Details yanıtını kodlu ApiError’a çevirir', async () => {
    fetchMock.mockResolvedValueOnce(json({ title: 'Kota doldu', status: 429, code: 'QUOTA_EXCEEDED' }, 429));

    const error = await apiFetch('/api/v1/x').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(429);
    expect((error as ApiError).code).toBe('QUOTA_EXCEEDED');
    expect(errorMessage((error as ApiError).code)).toBe('Bu şubenin günlük üretim kotası doldu.');
  });

  it('400 yanıtında CSRF token’ı sıfırlar: sonraki istek yeni token alır', async () => {
    fetchMock
      .mockResolvedValueOnce(json({ token: 'eski' }))
      .mockResolvedValueOnce(json({ title: 'x', code: 'CSRF_INVALID' }, 400))
      .mockResolvedValueOnce(json({ token: 'yeni' }))
      .mockResolvedValueOnce(json({}));

    await apiFetch('/api/v1/a', { method: 'POST', json: {} }).catch(() => undefined);
    await apiFetch('/api/v1/a', { method: 'POST', json: {} });

    expect(sent(fetchMock, 3).headers.get('X-CSRF-TOKEN')).toBe('yeni');
  });

  it('ağ hatasını NETWORK_ERROR olarak raporlar', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'));

    await expect(apiFetch('/api/v1/x')).rejects.toMatchObject({ status: 0, code: 'NETWORK_ERROR' });
  });

  it('bilinmeyen hata kodu genel Türkçe mesaja düşer', () => {
    expect(errorMessage('SOMETHING_NEW')).toBe('Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.');
  });
});
