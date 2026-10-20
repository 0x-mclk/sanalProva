import createClient from 'openapi-fetch';
import type { components, paths } from './schema';
import { guardedFetch, problemToApiError } from './http';

/**
 * OpenAPI'den üretilen tipli istemci (src/shared/api/schema.d.ts — `scripts/gen-api-client.ps1` ile yenilenir).
 * Yol, parametre, gövde ve yanıt tipleri backend sözleşmesinden gelir: sözleşme değişince derleme hata verir.
 */
export const api = createClient<paths>({ baseUrl: window.location.origin, fetch: (request) => guardedFetch(request) });

export type Schemas = components['schemas'];

type Result<T> = { data?: T; error?: unknown; response: Response };

/** Başarılı yanıtın verisini döner; hata durumunda Problem Details'ten ApiError fırlatır (TanStack Query hata durumuna düşer). */
export async function unwrap<T>(promise: Promise<Result<T>>): Promise<T> {
  const { data, error, response } = await promise;
  if (!response.ok) {
    // openapi-fetch yanıt gövdesini zaten ayrıştırdı (error): kod ve başlık oradan okunur
    throw problemToApiError(response.status, response.statusText, error);
  }
  return data as T;
}
