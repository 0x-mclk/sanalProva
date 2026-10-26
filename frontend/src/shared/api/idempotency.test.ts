import { afterEach, describe, expect, it, vi } from 'vitest';
import { newIdempotencyKey } from './idempotency';

const uuidV4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

describe('newIdempotencyKey', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('güvenli bağlamda randomUUID kullanır', () => {
    expect(newIdempotencyKey()).toMatch(uuidV4);
  });

  it('randomUUID olmayan bağlamda (yerel ağ HTTP) getRandomValues ile UUID v4 üretir', () => {
    const real = globalThis.crypto;
    vi.stubGlobal('crypto', { getRandomValues: real.getRandomValues.bind(real) });

    const keys = new Set(Array.from({ length: 50 }, () => newIdempotencyKey()));

    expect(keys.size).toBe(50);
    for (const key of keys) {
      expect(key).toMatch(uuidV4);
    }
  });
});
