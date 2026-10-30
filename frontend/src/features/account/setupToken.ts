/**
 * Kurulum bağlantısındaki token'ı URL fragment'ından (`#t=…`) alır ve adres çubuğundan HEMEN siler.
 *
 * - Fragment tarayıcıdan sunucuya, proxy günlüklerine ve Referer başlığına hiç gitmez.
 * - Token yalnızca bellekte tutulur (bu modül değişkeni ve çağıran bileşenin durumu): localStorage, sessionStorage, TanStack Query/Mutation önbelleği, günlük veya çerezde YOKTUR.
 * - React StrictMode'da başlatıcılar iki kez çalışır; ilk çağrı fragment'ı silmiş olacağından sonuç önbelleğe alınır (aksi halde token kaybolurdu).
 * - Adres çubuğunda YENİ bir fragment varsa (aynı sekmede başka bağlantı yapıştırıldı: tarayıcı bunu sayfa yeniden yüklemeden "hash değişimi" sayar) her zaman o önceliklidir.
 */
let captured: { token: string | null } | undefined;

const MIN_LENGTH = 20;
const MAX_LENGTH = 200;

export function takeSetupToken(): string | null {
  if (window.location.hash) {
    const raw = new URLSearchParams(window.location.hash.replace(/^#/, '')).get('t');
    captured = { token: raw && raw.length >= MIN_LENGTH && raw.length <= MAX_LENGTH ? raw : null };
    // Router'ın history durumu korunur; yalnızca fragment kalkar
    window.history.replaceState(window.history.state, '', window.location.pathname + window.location.search);
  } else if (captured === undefined) {
    captured = { token: null };
  }
  return captured.token;
}

/** Sayfadan ayrılırken/başarıdan sonra çağrılır: bellekteki token da unutulur. */
export function discardSetupToken(): void {
  captured = { token: null };
}

/** Yalnızca testler için. */
export function resetSetupTokenForTests(): void {
  captured = undefined;
}
