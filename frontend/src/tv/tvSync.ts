/**
 * TV durum eşzamanlama kuralları (saf mantık, test edilebilir).
 *
 * Doğruluk kaynağı sunucudur; SignalR yalnızca "değişti" der. İki koruma eski verinin yeni görüntünün üzerine yazmasını engeller:
 * 1) Sürüm: sunucunun durum sürümü yalnızca artar; eldekinden küçük bir yanıt/olay yok sayılır.
 * 2) Dönem (epoch): "ekran temizlendi / iptal edildi" olayı geldiğinde uçuştaki istekler atılır; yeniden çekilir.
 *    Böylece temizleme olayından önce başlayan yavaş bir yanıt görseli geri getiremez.
 */
export class StateSync {
  private current = -1;
  private epochValue = 0;

  get version(): number {
    return this.current;
  }

  /** Yeni bir çekme başlarken çağrılır; dönen değer sonuçla birlikte `accept`'e verilir. */
  begin(): number {
    return this.epochValue;
  }

  /** Dönem değişti mi (çekme sürerken temizleme/iptal olayı geldi)? */
  isStale(startedEpoch: number): boolean {
    return startedEpoch !== this.epochValue;
  }

  /** Uçuştaki çekmeleri geçersiz kılar (Cleared / Revoked). */
  invalidate(): void {
    this.epochValue++;
  }

  /** Yanıt uygulanabilir mi? Uygulanabiliyorsa sürüm ilerler. */
  accept(startedEpoch: number, version: number): boolean {
    if (this.isStale(startedEpoch) || version < this.current) {
      return false;
    }
    this.current = version;
    return true;
  }

  /** `StateChanged` olayı eldekinden yeni bir sürümü mü bildiriyor? */
  isNewer(eventVersion: number): boolean {
    return eventVersion > this.current;
  }

  reset(): void {
    this.current = -1;
    this.epochValue++;
  }
}

/** Sunucuyla bu kadar süre hiç temas olmadıysa müşteri görseli ekrandan gizlenir (müşteri gittiyse ekranda kalmasın). */
export const HIDE_AFTER_MS = 120_000;

export const HEARTBEAT_MS = 30_000;
export const POLL_FALLBACK_MS = 15_000;
export const HUB_RETRY_MS = 30_000;

/** Eşleştirme kodunu okunur gruplar halinde gösterir: 482913 → "482 913". */
export function formatCode(code: string): string {
  return code.length === 6 ? `${code.slice(0, 3)} ${code.slice(3)}` : code;
}

export function formatRemaining(seconds: number): string {
  const safe = Math.max(0, Math.floor(seconds));
  const m = Math.floor(safe / 60);
  const s = safe % 60;
  return `${m}:${s < 10 ? '0' : ''}${s}`;
}
