import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { DraftDto } from '../../shared/api/types';
import { useDraftItems } from './useDraftItems';

const saves: { items: unknown; resolve: (draft: DraftDto) => void }[] = [];

vi.mock('../../shared/api/hooks', () => ({
  useSaveDraft: () => ({
    mutateAsync: (body: { items: unknown }) => new Promise<DraftDto>((resolve) => saves.push({ items: body.items, resolve })),
    isPending: false,
    error: null,
  }),
}));

const draft = { mode: 'OutfitSet', items: [], warnings: [] } as unknown as DraftDto;
const entry = { productId: 'p1', variantId: null };

describe('useDraftItems.flush', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    saves.length = 0;
  });
  afterEach(() => vi.useRealTimers());

  it('zamanlayıcıyla başlamış yazma sürerken flush o yazmanın bitmesini bekler (Üret taslaktan önce gitmez)', async () => {
    const { result } = renderHook(() => useDraftItems('s1', 'OutfitSet', null));
    act(() => result.current.setEntries([entry]));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(300); // gecikme doldu: yazma sunucuya gitti, yanıt bekleniyor
    });
    expect(saves).toHaveLength(1);

    let flushed = false;
    const flushing = result.current.flush().then(() => {
      flushed = true;
    });
    await act(async () => {
      await Promise.resolve();
    });
    expect(flushed).toBe(false); // yazma bitmeden dönmemeli

    await act(async () => {
      saves[0]!.resolve(draft);
      await flushing;
    });
    expect(flushed).toBe(true);
    expect(saves).toHaveLength(1); // aynı veri için ikinci yazma yok
  });

  it('bekleyen (henüz gönderilmemiş) değişiklik flush ile hemen yazılır', async () => {
    const { result } = renderHook(() => useDraftItems('s1', 'OutfitSet', null));
    act(() => result.current.setEntries([entry]));
    let flushed = false;
    const flushing = result.current.flush().then(() => {
      flushed = true;
    });
    await act(async () => {
      await Promise.resolve();
    });
    expect(saves).toHaveLength(1);
    expect(flushed).toBe(false);
    await act(async () => {
      saves[0]!.resolve(draft);
      await flushing;
    });
    expect(flushed).toBe(true);
  });
});
