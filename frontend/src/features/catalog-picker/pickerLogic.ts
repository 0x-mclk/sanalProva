import type { DraftDto, ProductListItem } from '../../shared/api/types';

export type DraftEntry = { productId: string; variantId: string | null };
export type PickerSort = 'recommended' | 'name' | 'code';

/** Türkçe büyük/küçük harf duyarsız karşılaştırma ("İnci" ile "inci" eşleşir). */
const fold = (text: string) => text.toLocaleLowerCase('tr-TR').normalize('NFD').replace(/\p{Diacritic}/gu, '').replace(/ı/g, 'i');

/** Ürün listesini arama metni, kategori ve sıralamaya göre süzer (istemci tarafı; liste en çok 100 ürün). */
export function filterProducts(products: readonly ProductListItem[], query: string, categoryId: string | null, sort: PickerSort): ProductListItem[] {
  const needle = fold(query.trim());
  const filtered = products.filter(
    (p) => (!categoryId || p.categoryId === categoryId) && (!needle || fold(p.name).includes(needle) || fold(p.code).includes(needle) || fold(p.categoryName).includes(needle)),
  );
  if (sort === 'name') return [...filtered].sort((a, b) => a.name.localeCompare(b.name, 'tr-TR'));
  if (sort === 'code') return [...filtered].sort((a, b) => a.code.localeCompare(b.code, 'tr-TR'));
  return [...filtered]; // "Önerilen": sunucunun kategori sırası + kod sıralaması
}

/** Ürün listesinden kategori çipleri (ilk görülme sırası = sunucudaki kategori sırası). */
export function categoryChips(products: readonly ProductListItem[]): { id: string; name: string; count: number }[] {
  const map = new Map<string, { id: string; name: string; count: number }>();
  for (const p of products) {
    const entry = map.get(p.categoryId) ?? { id: p.categoryId, name: p.categoryName, count: 0 };
    entry.count += 1;
    map.set(p.categoryId, entry);
  }
  return [...map.values()];
}

/** Sunucudaki taslaktan yerel seçimi başlatır; farklı moddaki taslak yok sayılır. */
export function initialEntries(draft: DraftDto | null | undefined, mode: 'OutfitSet' | 'FreeCombination'): DraftEntry[] {
  return draft && draft.mode === mode ? draft.items.map((i) => ({ productId: i.productId, variantId: i.variantId })) : [];
}

/**
 * Seçimi değiştirir. Kategoride tek seçim (max 1) ise o kategorideki önceki ürün yer değiştirir; aynı ürüne tekrar dokunmak seçimi kaldırır.
 * `maxInCategory > 1` ise sınıra kadar eklenir, sınır dolunca en eski seçim çıkar.
 */
export function toggleEntry(
  entries: readonly DraftEntry[],
  product: Pick<ProductListItem, 'id' | 'categoryId' | 'variants'>,
  maxInCategory: number,
  categoryOf: (productId: string) => string | undefined,
): DraftEntry[] {
  if (entries.some((e) => e.productId === product.id)) {
    return entries.filter((e) => e.productId !== product.id);
  }
  const defaultVariant = product.variants.find((v) => v.isDefault && v.isActive) ?? product.variants.find((v) => v.isActive);
  const next: DraftEntry = { productId: product.id, variantId: defaultVariant?.id ?? null };
  const sameCategory = entries.filter((e) => categoryOf(e.productId) === product.categoryId);
  const dropIds = new Set(sameCategory.slice(0, Math.max(0, sameCategory.length - maxInCategory + 1)).map((e) => e.productId));
  return [...entries.filter((e) => !dropIds.has(e.productId)), next];
}

export function setVariant(entries: readonly DraftEntry[], productId: string, variantId: string): DraftEntry[] {
  return entries.map((e) => (e.productId === productId ? { ...e, variantId } : e));
}
