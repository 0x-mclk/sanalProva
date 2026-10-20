import { Button, MultiSelect } from '@mantine/core';
import { useState } from 'react';
import { useProducts, useSetComponents } from '../../shared/api/hooks';
import { ApiError } from '../../shared/api/http';
import type { ProductDetail } from '../../shared/api/types';
import { errorMessage } from '../../shared/i18n/tr';
import { CheckIcon, Notice } from '../../shared/ui';
import classes from './CatalogAdmin.module.css';

/** Hazır takımın parçaları. Revizyonda "takımdaki kemeri değiştir" gibi parça bazlı işlemler için kullanılır; takım yine tek ürün gibi seçilir. */
export function ComponentsSection({ product }: { product: ProductDetail }) {
  const singles = useProducts({ type: 'Single', status: 'all', pageSize: 100 });
  const save = useSetComponents(product.id);
  const [selected, setSelected] = useState<string[]>(product.components.map((c) => c.productId));
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const variants = new Map(product.components.map((c) => [c.productId, c.variantId]));

  return (
    <section className={classes.section} aria-labelledby="components-title">
      <h2 id="components-title" className={classes.sectionTitle}>
        Takım parçaları
      </h2>
      <MultiSelect
        label="Bu takımı oluşturan ürünler"
        description="İsteğe bağlı. Parçalar bağımsız ürün olarak katalogda kayıtlı olmalıdır."
        searchable
        clearable
        value={selected}
        onChange={(v) => {
          setSaved(false);
          setSelected(v);
        }}
        data={(singles.data?.items ?? []).map((p) => ({ value: p.id, label: `${p.name} · ${p.code} (${p.categoryName})` }))}
        nothingFoundMessage="Ürün yok"
      />
      {error ? <Notice tone="danger">{error}</Notice> : null}
      <div className={classes.actionsBar}>
        <Button
          size="md"
          loading={save.isPending}
          onClick={() => {
            setError(null);
            save.mutate(
              selected.map((componentProductId) => ({ componentProductId, componentVariantId: variants.get(componentProductId) ?? null })),
              { onSuccess: () => setSaved(true), onError: (e) => setError(errorMessage(e instanceof ApiError ? e.code : 'UNKNOWN')) },
            );
          }}
        >
          Parçaları kaydet
        </Button>
        {saved ? (
          <span role="status" style={{ display: 'inline-flex', alignItems: 'center', gap: 4, color: 'var(--sp-color-success-text)' }}>
            <CheckIcon size={16} /> Kaydedildi
          </span>
        ) : null}
      </div>
    </section>
  );
}
