import { Button, ColorInput, Modal, Stack, Switch, TextInput } from '@mantine/core';
import { useState, type CSSProperties } from 'react';
import { useSaveVariant } from '../../shared/api/hooks';
import { ApiError } from '../../shared/api/http';
import type { ProductDetail, VariantDto } from '../../shared/api/types';
import { errorMessage } from '../../shared/i18n/tr';
import { Notice, PencilIcon, PlusIcon } from '../../shared/ui';
import classes from './CatalogAdmin.module.css';

type Draft = { id?: string; code: string; colorName: string; colorHex: string; isDefault: boolean; isActive: boolean };

const blank = (isFirst: boolean): Draft => ({ code: '', colorName: '', colorHex: '#7A1F3A', isDefault: isFirst, isActive: true });
const fromVariant = (v: VariantDto): Draft => ({ id: v.id, code: v.code, colorName: v.colorName, colorHex: v.colorHex ?? '#7A1F3A', isDefault: v.isDefault, isActive: v.isActive });

/** Renk varyantları. Varyant silinmez, pasife alınır (geçmiş seçimler bozulmasın). Ürün başına tek varsayılan varyant vardır. */
export function VariantsSection({ product }: { product: ProductDetail }) {
  const save = useSaveVariant(product.id);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [error, setError] = useState<string | null>(null);

  const submit = () => {
    if (!draft) return;
    setError(null);
    save.mutate(
      {
        variantId: draft.id,
        body: { code: draft.code.trim(), colorName: draft.colorName.trim(), colorHex: /^#[0-9a-f]{6}$/i.test(draft.colorHex) ? draft.colorHex : null, isDefault: draft.isDefault, isActive: draft.isActive },
      },
      { onSuccess: () => setDraft(null), onError: (e) => setError(errorMessage(e instanceof ApiError ? e.code : 'UNKNOWN')) },
    );
  };

  return (
    <section className={classes.section} aria-labelledby="variants-title">
      <h2 id="variants-title" className={classes.sectionTitle}>
        Renk varyantları
      </h2>
      {product.variants.length === 0 ? <p className="sp-lead" style={{ margin: 0 }}>Henüz varyant yok. En az bir renk ekleyin.</p> : null}
      {product.variants.map((variant) => (
        <div key={variant.id} className={classes.variantRow}>
          <span className={classes.dot} style={{ '--swatch': variant.colorHex ?? '#ccc' } as CSSProperties} />
          <span className={classes.grow}>
            <strong>{variant.colorName}</strong>
            <span style={{ display: 'block', fontSize: 'var(--sp-font-size-xs)', color: 'var(--sp-color-text-muted)' }}>
              {variant.code}
              {variant.isDefault ? ' · varsayılan' : ''}
              {!variant.isActive ? ' · pasif' : ''}
            </span>
          </span>
          <Button variant="subtle" color="dark" size="compact-md" leftSection={<PencilIcon size={16} />} onClick={() => setDraft(fromVariant(variant))}>
            Düzenle
          </Button>
        </div>
      ))}
      <div>
        <Button variant="default" size="md" leftSection={<PlusIcon size={18} />} onClick={() => setDraft(blank(product.variants.length === 0))}>
          Varyant ekle
        </Button>
      </div>

      <Modal opened={draft !== null} onClose={() => setDraft(null)} title={draft?.id ? 'Varyantı düzenle' : 'Yeni varyant'} centered radius="lg" padding="lg">
        {draft ? (
          <Stack gap="sm">
            <TextInput label="Renk adı" required value={draft.colorName} onChange={(e) => setDraft({ ...draft, colorName: e.currentTarget.value })} maxLength={60} placeholder="Gece mavisi" data-autofocus />
            <TextInput label="Varyant kodu" required value={draft.code} onChange={(e) => setDraft({ ...draft, code: e.currentTarget.value })} maxLength={40} placeholder="GCE" />
            <ColorInput
              label="Renk"
              format="hex"
              value={draft.colorHex}
              onChange={(v) => setDraft({ ...draft, colorHex: v })}
              swatches={['#7A1F3A', '#1F6B4F', '#1D2B53', '#E7DDC9', '#F2EDE3', '#B8913A', '#2A2220', '#D9B8B0']}
            />
            <Switch label="Varsayılan varyant" checked={draft.isDefault} onChange={(e) => setDraft({ ...draft, isDefault: e.currentTarget.checked })} />
            <Switch label="Aktif" checked={draft.isActive} onChange={(e) => setDraft({ ...draft, isActive: e.currentTarget.checked })} />
            {error ? <Notice tone="danger">{error}</Notice> : null}
            <Button size="md" disabled={!draft.colorName.trim() || !draft.code.trim()} loading={save.isPending} onClick={submit}>
              Kaydet
            </Button>
          </Stack>
        ) : null}
      </Modal>
    </section>
  );
}
