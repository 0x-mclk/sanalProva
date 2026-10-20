import { execFileSync } from 'node:child_process';
import { deflateSync } from 'node:zlib';
import type { APIRequestContext, APIResponse } from '@playwright/test';

export const DEMO_MANAGER = 'yonetici@lal.local';

/** Demo hesap parolası: E2E_PASSWORD, yoksa API projesinin user-secrets'ındaki Seed:DevPassword. */
export function devPassword(): string {
  if (process.env.E2E_PASSWORD) return process.env.E2E_PASSWORD;
  const output = execFileSync('dotnet', ['user-secrets', 'list', '--project', '../backend/src/SanalProva.Api'], { encoding: 'utf8' });
  const match = /^Seed:DevPassword\s*=\s*(.+)$/m.exec(output);
  if (!match) throw new Error('Seed:DevPassword bulunamadı. E2E_PASSWORD ortam değişkenini verin veya ./scripts/db.ps1 init çalıştırın.');
  return match[1]!.trim();
}

const crcTable = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c >>> 0;
  }
  return table;
})();

function crc32(buffer: Buffer): number {
  let c = 0xffffffff;
  for (const byte of buffer) c = crcTable[(c ^ byte) & 0xff]! ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type: string, data: Buffer): Buffer {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([length, body, crc]);
}

/**
 * Bağımlılıksız, yapay (gerçek kişi değil) dikey test görseli üretir: renk geçişi + kareler. Gerçek müşteri fotoğrafı kullanılmaz.
 */
export function syntheticPortraitPng(width = 900, height = 1500, seed = 1): Buffer {
  const row = width * 3 + 1;
  const raw = Buffer.alloc(row * height);
  let state = seed * 2654435761;
  const next = () => {
    state = (state * 1664525 + 1013904223) >>> 0;
    return state;
  };
  for (let y = 0; y < height; y++) {
    raw[y * row] = 0;
    for (let x = 0; x < width; x++) {
      const o = y * row + 1 + x * 3;
      const block = ((x >> 5) + (y >> 5)) & 1;
      raw[o] = 200 - Math.floor((y / height) * 90) + (block ? 20 : 0);
      raw[o + 1] = 190 - Math.floor((x / width) * 70);
      raw[o + 2] = 170 + (next() & 15);
    }
  }
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header[8] = 8;
  header[9] = 2;
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(raw, { level: 6 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

export type Display = { id: string; name: string; online: boolean; sessionId: string | null; mode: string; stateVersion: number };

/** Giriş yapmış personel tarayıcısının çerezlerini kullanan API yardımcısı (CSRF ve şube başlığı dahil). */
export class StaffApi {
  private csrf: string | undefined;
  storeId = '';

  constructor(private readonly request: APIRequestContext) {}

  async init(): Promise<void> {
    const me = await this.json<{ stores: { id: string }[] }>(await this.request.get('/api/v1/auth/me'));
    this.storeId = me.stores[0]!.id;
  }

  private async headers(write: boolean): Promise<Record<string, string>> {
    const headers: Record<string, string> = { 'X-Store-Id': this.storeId };
    if (write) {
      if (!this.csrf) this.csrf = (await this.json<{ token: string }>(await this.request.get('/api/v1/auth/csrf'))).token;
      headers['X-CSRF-TOKEN'] = this.csrf;
    }
    return headers;
  }

  private async json<T>(response: APIResponse): Promise<T> {
    if (!response.ok()) throw new Error(`${response.url()} → ${response.status()} ${await response.text()}`);
    return (await response.json()) as T;
  }

  async get<T>(path: string): Promise<T> {
    return this.json<T>(await this.request.get(path, { headers: await this.headers(false) }));
  }

  async send<T>(method: 'POST' | 'PUT' | 'DELETE', path: string, data?: unknown, extraHeaders: Record<string, string> = {}): Promise<T | undefined> {
    const response = await this.request.fetch(path, { method, data, headers: { ...(await this.headers(true)), ...extraHeaders } });
    if (!response.ok()) throw new Error(`${method} ${path} → ${response.status()} ${await response.text()}`);
    return response.status() === 204 ? undefined : ((await response.json()) as T);
  }

  /** Onaylı, fotoğraflı, DEMO sonucu hazır bir prova oluşturur. */
  async createSessionWithResult(label: string, seed = 1): Promise<{ sessionId: string; photoId: string; imageId: string }> {
    const session = (await this.send<{ id: string }>('POST', '/api/v1/sessions', { customerLabel: label }))!;
    const notice = await this.get<{ version: string }>('/api/v1/consent-notice/current');
    await this.send('POST', `/api/v1/sessions/${session.id}/consent`, {
      noticeVersion: notice.version, processingConsent: true, foreignTransferConsent: true, extendedRetentionConsent: false, isMinor: false, method: 'CustomerTapped',
    });
    const upload = await this.request.post(`/api/v1/sessions/${session.id}/photos`, {
      headers: await this.headers(true),
      multipart: { file: { name: 'sentetik.png', mimeType: 'image/png', buffer: syntheticPortraitPng(900, 1500, seed) } },
    });
    const photo = await this.json<{ id: string }>(upload);
    const imageId = await this.generate(session.id);
    return { sessionId: session.id, photoId: photo.id, imageId };
  }

  /** Taslak seçimden (ilk hazır takım) bir DEMO sonucu üretir ve tamamlanmasını bekler. */
  async generate(sessionId: string): Promise<string> {
    const products = await this.get<{ items: { id: string; isReadyForAi: boolean; variants: { id: string; isDefault: boolean }[] }[] }>(
      '/api/v1/products?type=OutfitSet&selectable=true&pageSize=10',
    );
    const product = products.items.find((p) => p.isReadyForAi);
    if (!product) throw new Error('Demo katalogda hazır takım yok. Geliştirme API\'si Seed:Enabled ile başlatılmalı.');
    await this.send('PUT', `/api/v1/sessions/${sessionId}/draft`, { mode: 'OutfitSet', items: [{ productId: product.id, variantId: (product.variants.find((v) => v.isDefault) ?? product.variants[0])?.id }] });
    const job = (await this.send<{ id: string }>('POST', `/api/v1/sessions/${sessionId}/generations`, {}, { 'Idempotency-Key': crypto.randomUUID().replaceAll('-', '') }))!;
    for (let i = 0; i < 100; i++) {
      const current = await this.get<{ status: string; resultImageId: string | null }>(`/api/v1/jobs/${job.id}`);
      if (current.status === 'Succeeded' && current.resultImageId) return current.resultImageId;
      if (current.status === 'Failed' || current.status === 'Cancelled') throw new Error(`Üretim başarısız: ${current.status}`);
      await new Promise((resolve) => setTimeout(resolve, 300));
    }
    throw new Error('Üretim zaman aşımına uğradı.');
  }

  displays(): Promise<Display[]> {
    return this.get<Display[]>('/api/v1/displays');
  }

  /** Test artıkları: TV'leri ve adı verilen provaları siler. */
  async cleanup(sessionIds: string[]): Promise<void> {
    for (const id of sessionIds) {
      await this.send('DELETE', `/api/v1/sessions/${id}`).catch(() => undefined);
    }
    for (const display of await this.displays().catch(() => [] as Display[])) {
      if (display.name.startsWith('E2E')) await this.send('DELETE', `/api/v1/displays/${display.id}`).catch(() => undefined);
    }
  }
}
