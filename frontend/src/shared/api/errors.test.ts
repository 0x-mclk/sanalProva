import { describe, expect, it } from 'vitest';
import { ApiError } from './http';
import { problemToApiError } from './http';
import { unwrap } from './client';

const response = (status: number, statusText = '') => new Response(null, { status, statusText });

describe('API hata dönüşümü', () => {
  it('Problem Details gövdesinden kod ve başlığı alır, ek alanları details içinde tutar', () => {
    const error = problemToApiError(422, 'Unprocessable', { title: 'Seçilen kombin geçerli değil.', status: 422, code: 'INVALID_COMBINATION', errors: ['ConflictingCategories'], traceId: 'x' });

    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(422);
    expect(error.code).toBe('INVALID_COMBINATION');
    expect(error.details).toEqual({ errors: ['ConflictingCategories'] });
  });

  it('gövde yoksa HTTP_<durum> koduna düşer', () => {
    expect(problemToApiError(404, 'Not Found', undefined).code).toBe('HTTP_404');
    expect(problemToApiError(500, 'Server Error', 'metin').code).toBe('HTTP_500');
  });

  it('unwrap başarısız yanıtta openapi-fetch’in ayrıştırdığı hata gövdesinden ApiError fırlatır', async () => {
    const failing = Promise.resolve({ data: undefined, error: { code: 'INVALID_CREDENTIALS', title: 'E-posta veya parola hatalı.' }, response: response(401) });

    await expect(unwrap(failing)).rejects.toMatchObject({ status: 401, code: 'INVALID_CREDENTIALS' });
  });

  it('unwrap başarılı yanıtta veriyi döner', async () => {
    await expect(unwrap(Promise.resolve({ data: { ok: 1 }, error: undefined, response: response(200) }))).resolves.toEqual({ ok: 1 });
  });
});
