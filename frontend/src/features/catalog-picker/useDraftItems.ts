import { useCallback, useEffect, useRef, useState } from 'react';
import { useSaveDraft } from '../../shared/api/hooks';
import type { DraftDto, OutfitMode } from '../../shared/api/types';
import { initialEntries, type DraftEntry } from './pickerLogic';

/**
 * Seçimleri ekranda anında günceller ve kısa bir gecikmeyle sunucudaki taslağa yazar (iyimser güncelleme).
 * Böylece sonraki ekranlar ve sayfa yenileme seçimi korur. `flush()` üretimden hemen önce bekleyen kaydı tamamlar.
 */
export function useDraftItems(sessionId: string, mode: OutfitMode, serverDraft: DraftDto | null | undefined) {
  const save = useSaveDraft(sessionId);
  const [entries, setEntriesState] = useState<DraftEntry[]>(() => initialEntries(serverDraft, mode));
  const [warnings, setWarnings] = useState<string[]>(() => (serverDraft?.mode === mode ? [...serverDraft.warnings] : []));
  const timer = useRef<number | null>(null);
  const latest = useRef(entries);
  const dirty = useRef(false);

  const persist = useCallback(async () => {
    if (timer.current) {
      window.clearTimeout(timer.current);
      timer.current = null;
    }
    if (!dirty.current) {
      return;
    }
    dirty.current = false;
    const draft = await save.mutateAsync({ mode, items: latest.current });
    setWarnings([...draft.warnings]);
  }, [mode, save]);

  const setEntries = useCallback(
    (next: DraftEntry[]) => {
      latest.current = next;
      dirty.current = true;
      setEntriesState(next);
      if (timer.current) window.clearTimeout(timer.current);
      timer.current = window.setTimeout(() => void persist().catch(() => undefined), 250);
    },
    [persist],
  );

  // Ekrandan çıkarken bekleyen değişiklik kaybolmasın
  useEffect(
    () => () => {
      if (dirty.current) void persist().catch(() => undefined);
    },
    [persist],
  );

  return { entries, setEntries, warnings, flush: persist, saving: save.isPending, saveError: save.error };
}
