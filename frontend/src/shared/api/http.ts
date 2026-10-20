/**
 * Düşük seviyeli HTTP yardımcıları. Kimlik doğrulama HttpOnly çerezle yapılır (token JS'te tutulmaz);
 * durum değiştiren isteklere antiforgery başlığı (X-CSRF-TOKEN), şube seçiliyse X-Store-Id eklenir.
 * Hatalar RFC 9457 Problem Details olarak gelir ve ApiError'a çevrilir.
 */

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  /** Sunucunun Problem Details gövdesindeki ek alanlar (ör. errors listesi). */
  readonly details: Record<string, unknown>;

  constructor(status: number, code: string, message: string, details: Record<string, unknown> = {}) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.details = details;
  }
}

type ProblemDetails = { title?: string; status?: number; code?: string; [key: string]: unknown };

const SAFE_METHODS = new Set(['GET', 'HEAD', 'OPTIONS']);
let csrfToken: string | null = null;
let activeStoreId: string | null = null;

/** Girişten/çıkıştan sonra çağrılır: antiforgery token kullanıcı kimliğine bağlıdır. */
export function resetCsrfToken() {
  csrfToken = null;
}

/** İstekler hangi şube adına yapılıyor (X-Store-Id). Sunucu rolü her istekte doğrular. */
export function setActiveStoreId(id: string | null) {
  activeStoreId = id;
}

export function getActiveStoreId() {
  return activeStoreId;
}

async function ensureCsrfToken(): Promise<string> {
  if (!csrfToken) {
    const response = await fetch('/api/v1/auth/csrf', { credentials: 'same-origin' });
    if (!response.ok) {
      throw await toApiError(response);
    }
    csrfToken = ((await response.json()) as { token: string }).token;
  }
  return csrfToken;
}

/** Problem Details nesnesinden (zaten ayrıştırılmış gövde) ApiError üretir. */
export function problemToApiError(status: number, statusText: string, problem: unknown): ApiError {
  const body: ProblemDetails = problem && typeof problem === 'object' ? (problem as ProblemDetails) : {};
  const details: Record<string, unknown> = { ...body };
  for (const key of ['title', 'status', 'code', 'type', 'traceId']) {
    delete details[key];
  }
  return new ApiError(status, typeof body.code === 'string' ? body.code : `HTTP_${status}`, typeof body.title === 'string' ? body.title : statusText, details);
}

/** Henüz okunmamış bir yanıt gövdesinden ApiError üretir (gövdeyi tüketmez). */
export async function toApiError(response: Response): Promise<ApiError> {
  let problem: unknown = {};
  try {
    problem = await response.clone().json();
  } catch {
    // gövde JSON değil veya zaten okunmuş
  }
  return problemToApiError(response.status, response.statusText, problem);
}

/**
 * Tüm API isteklerinin ortak geçidi (openapi-fetch ve apiFetch kullanır): CSRF ve şube başlıklarını ekler,
 * ağ hatasını ApiError'a çevirir, 400'de (olası CSRF süresi dolması) token'ı sıfırlar.
 */
export async function guardedFetch(input: Request): Promise<Response> {
  const headers = new Headers(input.headers);
  if (!SAFE_METHODS.has(input.method.toUpperCase())) {
    headers.set('X-CSRF-TOKEN', await ensureCsrfToken());
  }
  if (activeStoreId && !headers.has('X-Store-Id')) {
    headers.set('X-Store-Id', activeStoreId);
  }

  let response: Response;
  try {
    response = await fetch(new Request(input, { headers, credentials: 'same-origin' }));
  } catch {
    throw new ApiError(0, 'NETWORK_ERROR', 'Sunucuya ulaşılamadı.');
  }
  if (response.status === 400) {
    resetCsrfToken();
  }
  return response;
}

export async function apiFetch<T>(path: string, init: RequestInit & { json?: unknown } = {}): Promise<T> {
  const { json, headers, ...rest } = init;
  const finalHeaders = new Headers(headers);
  finalHeaders.set('Accept', 'application/json');
  if (json !== undefined) {
    finalHeaders.set('Content-Type', 'application/json');
  }

  const response = await guardedFetch(
    new Request(new URL(path, window.location.origin), { ...rest, headers: finalHeaders, body: json !== undefined ? JSON.stringify(json) : rest.body }),
  );
  if (!response.ok) {
    throw await toApiError(response);
  }
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}
