import { describe, expect, it } from 'vitest';
import type { ProductListItem } from '../../shared/api/types';
import { categoryChips, filterProducts, initialEntries, setVariant, toggleEntry } from './pickerLogic';

const variant = (id: string, isDefault = false, isActive = true) => ({ id, code: id, colorName: id, colorHex: '#000000', isDefault, isActive, image: null });

const product = (id: string, name: string, code: string, categoryId: string, categoryName: string, variants = [variant(`${id}-v1`, true)]): ProductListItem => ({
  id, code, name, productType: 'Single', categoryId, categoryName, tagline: null, subtitle: null, isActive: true, isReadyForAi: true, cover: null, variants,
});

const items = [
  product('p1', 'İnci Şalvar Takımı', 'ŞT-012', 'c-set', 'Şalvar Takımı'),
  product('p2', 'Sultan Bindallı', 'BD-014', 'c-bind', 'Bindallı'),
  product('p3', 'Lale Şalvar', 'ŞL-101', 'c-sal', 'Şalvar', [variant('p3-a'), variant('p3-b', true)]),
];

describe('filterProducts', () => {
  it('Türkçe karakterleri ve büyük/küçük harfi ayırt etmeden arar', () => {
    expect(filterProducts(items, 'inci', null, 'recommended').map((p) => p.id)).toEqual(['p1']);
    expect(filterProducts(items, 'İNCİ', null, 'recommended').map((p) => p.id)).toEqual(['p1']);
    expect(filterProducts(items, 'salvar', null, 'recommended').map((p) => p.id)).toEqual(['p1', 'p3']);
    expect(filterProducts(items, 'bd-014', null, 'recommended').map((p) => p.id)).toEqual(['p2']);
  });

  it('kategoriye göre süzer ve ada/koda göre sıralar', () => {
    expect(filterProducts(items, '', 'c-bind', 'recommended').map((p) => p.id)).toEqual(['p2']);
    expect(filterProducts(items, '', null, 'name').map((p) => p.name)).toEqual(['İnci Şalvar Takımı', 'Lale Şalvar', 'Sultan Bindallı']);
    expect(filterProducts(items, '', null, 'code').map((p) => p.code)).toEqual(['BD-014', 'ŞL-101', 'ŞT-012']);
  });

  it('orijinal diziyi değiştirmez', () => {
    const copy = [...items];
    filterProducts(items, '', null, 'name');
    expect(items).toEqual(copy);
  });
});

describe('categoryChips', () => {
  it('kategorileri ilk görülme sırasıyla ve ürün sayısıyla çıkarır', () => {
    const chips = categoryChips([...items, product('p4', 'X', 'X-1', 'c-bind', 'Bindallı')]);
    expect(chips).toEqual([
      { id: 'c-set', name: 'Şalvar Takımı', count: 1 },
      { id: 'c-bind', name: 'Bindallı', count: 2 },
      { id: 'c-sal', name: 'Şalvar', count: 1 },
    ]);
  });
});

describe('toggleEntry', () => {
  const categoryOf = (id: string) => items.find((p) => p.id === id)?.categoryId;

  it('ürünü varsayılan varyantıyla ekler', () => {
    expect(toggleEntry([], items[2]!, 1, categoryOf)).toEqual([{ productId: 'p3', variantId: 'p3-b' }]);
  });

  it('seçili ürüne tekrar dokunmak seçimi kaldırır', () => {
    expect(toggleEntry([{ productId: 'p2', variantId: 'p2-v1' }], items[1]!, 1, categoryOf)).toEqual([]);
  });

  it('kategoride tek seçim varsa aynı kategorideki önceki ürünün yerini alır, diğer kategorilere dokunmaz', () => {
    const other = product('p5', 'Başka Şalvar', 'ŞL-102', 'c-sal', 'Şalvar');
    const all = [...items, other];
    const of = (id: string) => all.find((p) => p.id === id)?.categoryId;
    const start = [{ productId: 'p2', variantId: 'p2-v1' }, { productId: 'p3', variantId: 'p3-b' }];

    const next = toggleEntry(start, other, 1, of);

    expect(next.map((e) => e.productId)).toEqual(['p2', 'p5']);
  });

  it('en fazla sınırı >1 ise sınıra kadar ekler, dolunca en eskiyi çıkarır', () => {
    const a = product('a', 'A', 'A', 'c-x', 'X');
    const b = product('b', 'B', 'B', 'c-x', 'X');
    const c = product('c', 'C', 'C', 'c-x', 'X');
    const of = () => 'c-x';

    let entries = toggleEntry([], a, 2, of);
    entries = toggleEntry(entries, b, 2, of);
    expect(entries.map((e) => e.productId)).toEqual(['a', 'b']);
    entries = toggleEntry(entries, c, 2, of);
    expect(entries.map((e) => e.productId)).toEqual(['b', 'c']);
  });

  it('pasif varyantları varsayılan seçmez', () => {
    const p = product('z', 'Z', 'Z', 'c-z', 'Z', [variant('z1', true, false), variant('z2', false, true)]);
    expect(toggleEntry([], p, 1, () => 'c-z')[0]!.variantId).toBe('z2');
  });
});

describe('setVariant / initialEntries', () => {
  it('yalnızca ilgili ürünün rengini değiştirir', () => {
    const entries = [{ productId: 'a', variantId: 'a1' }, { productId: 'b', variantId: 'b1' }];
    expect(setVariant(entries, 'b', 'b2')).toEqual([{ productId: 'a', variantId: 'a1' }, { productId: 'b', variantId: 'b2' }]);
  });

  it('farklı moddaki sunucu taslağını yok sayar', () => {
    const draft = { mode: 'OutfitSet' as const, items: [{ categoryId: 'c', productId: 'p', variantId: 'v' }], warnings: [] };
    expect(initialEntries(draft, 'OutfitSet')).toEqual([{ productId: 'p', variantId: 'v' }]);
    expect(initialEntries(draft, 'FreeCombination')).toEqual([]);
    expect(initialEntries(null, 'OutfitSet')).toEqual([]);
  });
});
