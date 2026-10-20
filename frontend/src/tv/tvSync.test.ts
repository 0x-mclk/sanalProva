import { describe, expect, it } from 'vitest';
import { formatCode, formatRemaining, StateSync } from './tvSync';

describe('StateSync', () => {
  it('eldekinden eski sürümü uygulamaz, eşit veya yeni sürümü uygular', () => {
    const sync = new StateSync();
    const epoch = sync.begin();
    expect(sync.accept(epoch, 5)).toBe(true);
    expect(sync.accept(epoch, 4)).toBe(false); // eski yanıt yeni görüntüyü ezemez
    expect(sync.accept(epoch, 5)).toBe(true);
    expect(sync.accept(epoch, 6)).toBe(true);
    expect(sync.version).toBe(6);
  });

  it('yalnızca daha yeni sürüm bildiren olay yeniden çekmeyi tetikler', () => {
    const sync = new StateSync();
    sync.accept(sync.begin(), 10);
    expect(sync.isNewer(10)).toBe(false);
    expect(sync.isNewer(9)).toBe(false);
    expect(sync.isNewer(11)).toBe(true);
  });

  it('temizleme/iptal olayı uçuştaki yanıtı geçersiz kılar (görsel geri gelmez)', () => {
    const sync = new StateSync();
    sync.accept(sync.begin(), 3);

    // Yavaş bir çekme başladı…
    const startedBefore = sync.begin();
    // …o sırada ekran temizlendi
    sync.invalidate();

    // Eski dönemde başlayan yanıt (sürümü ne olursa olsun) atılır
    expect(sync.isStale(startedBefore)).toBe(true);
    expect(sync.accept(startedBefore, 99)).toBe(false);
    expect(sync.version).toBe(3);

    // Yeni dönemde başlayan çekme uygulanır
    expect(sync.accept(sync.begin(), 4)).toBe(true);
  });

  it('sıfırlama sürümü başa alır ve uçuştaki yanıtları atar', () => {
    const sync = new StateSync();
    const epoch = sync.begin();
    sync.accept(epoch, 8);
    sync.reset();
    expect(sync.version).toBe(-1);
    expect(sync.accept(epoch, 9)).toBe(false);
  });
});

describe('gösterim yardımcıları', () => {
  it('kodu okunur gruplar halinde gösterir', () => {
    expect(formatCode('482913')).toBe('482 913');
    expect(formatCode('12')).toBe('12');
  });

  it('kalan süreyi dakika:saniye yazar', () => {
    expect(formatRemaining(272)).toBe('4:32');
    expect(formatRemaining(5)).toBe('0:05');
    expect(formatRemaining(-3)).toBe('0:00');
  });
});
