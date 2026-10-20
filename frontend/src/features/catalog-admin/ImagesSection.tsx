import { Button, Checkbox, Select, Stack } from '@mantine/core';
import { useRef, useState } from 'react';
import { useDeleteProductImage, useUpdateProductImage, useUploadProductImage } from '../../shared/api/hooks';
import { ApiError } from '../../shared/api/http';
import type { ProductDetail } from '../../shared/api/types';
import { errorMessage } from '../../shared/i18n/tr';
import { prepareImage, validateImageFile } from '../../shared/image/prepareImage';
import { ConfirmDialog, Notice, TrashIcon, UploadIcon } from '../../shared/ui';
import classes from './CatalogAdmin.module.css';

const ANGLES = [
  { value: 'Front', label: 'Ön' },
  { value: 'Back', label: 'Arka' },
  { value: 'Left', label: 'Sol' },
  { value: 'Right', label: 'Sağ' },
  { value: 'Detail', label: 'Detay' },
];
const KINDS = [
  { value: 'OnModel', label: 'Manken üzerinde' },
  { value: 'Flat', label: 'Düz çekim' },
  { value: 'Detail', label: 'Nakış / kumaş detayı' },
];
const angleLabel = (v: string) => ANGLES.find((a) => a.value === v)?.label ?? v;

/** Ürün görselleri. Prova üretiminde yalnızca "AI referansı" işaretli ön cephe görseli kullanılır (varyant başına tek). */
export function ImagesSection({ product }: { product: ProductDetail }) {
  const upload = useUploadProductImage(product.id);
  const updateImage = useUpdateProductImage(product.id);
  const remove = useDeleteProductImage(product.id);
  const fileInput = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [variantId, setVariantId] = useState<string | null>(null);
  const [angle, setAngle] = useState('Front');
  const [kind, setKind] = useState(product.productType === 'OutfitSet' ? 'OnModel' : 'Flat');
  const [isAiReference, setIsAiReference] = useState(product.images.length === 0);
  const [error, setError] = useState<string | null>(null);
  const [deleteId, setDeleteId] = useState<string | null>(null);

  const submit = async () => {
    if (!file) return;
    setError(null);
    const check = validateImageFile(file);
    if (!check.ok) {
      setError(errorMessage(check.code));
      return;
    }
    try {
      const prepared = await prepareImage(file);
      await upload.mutateAsync({ file: prepared, variantId, angle, kind, isAiReference });
      setFile(null);
      if (fileInput.current) fileInput.current.value = '';
    } catch (e) {
      setError(errorMessage(e instanceof ApiError ? e.code : 'UNKNOWN'));
    }
  };

  const variantName = (id: string | null) => (id ? (product.variants.find((v) => v.id === id)?.colorName ?? 'Varyant') : 'Tüm renkler');

  return (
    <section className={classes.section} aria-labelledby="images-title">
      <h2 id="images-title" className={classes.sectionTitle}>
        Görseller
      </h2>
      <p className="sp-lead" style={{ margin: 0, fontSize: 'var(--sp-font-size-sm)' }}>
        {product.productType === 'OutfitSet'
          ? 'Hazır takım için AI referansı, takımın tamamını gösteren tek fotoğraf olmalıdır.'
          : 'AI referansı olarak ürünün net, düz zeminli ön cephe fotoğrafını kullanın.'}{' '}
        Bir renge özel görsel yüklemezseniz "Tüm renkler" görseli kullanılır.
      </p>

      {product.images.length > 0 ? (
        <ul className={classes.images}>
          {product.images.map((image) => (
            <li key={image.id} className={classes.imageCard}>
              <img src={image.urls.thumbUrl} alt={`${angleLabel(image.angle)} görseli, ${variantName(image.variantId)}`} loading="lazy" />
              <div className={classes.imageMeta}>
                <span>
                  <strong>{angleLabel(image.angle)}</strong> · {variantName(image.variantId)}
                </span>
                {image.isAiReference ? (
                  <span style={{ color: 'var(--sp-color-success-text)', fontWeight: 600 }}>AI referansı</span>
                ) : (
                  <Button variant="subtle" size="compact-sm" loading={updateImage.isPending && updateImage.variables?.imageId === image.id} onClick={() => updateImage.mutate({ imageId: image.id, isAiReference: true })}>
                    AI referansı yap
                  </Button>
                )}
                <Button variant="subtle" color="red" size="compact-sm" leftSection={<TrashIcon size={14} />} onClick={() => setDeleteId(image.id)}>
                  Sil
                </Button>
              </div>
            </li>
          ))}
        </ul>
      ) : (
        <Notice tone="info">Henüz görsel yok.</Notice>
      )}

      <Stack gap="sm" mt="sm">
        <p style={{ margin: 0, fontWeight: 600 }}>Yeni görsel yükle</p>
        <input ref={fileInput} type="file" accept="image/jpeg,image/png,image/webp" aria-label="Görsel dosyası" onChange={(e) => setFile(e.currentTarget.files?.[0] ?? null)} />
        <div className={classes.row}>
          <Select
            label="Renk"
            value={variantId ?? 'all'}
            onChange={(v) => setVariantId(v === 'all' ? null : v)}
            allowDeselect={false}
            data={[{ value: 'all', label: 'Tüm renkler' }, ...product.variants.map((v) => ({ value: v.id, label: v.colorName }))]}
          />
          <Select label="Açı" value={angle} onChange={(v) => setAngle(v ?? 'Front')} allowDeselect={false} data={ANGLES} />
        </div>
        <Select label="Görsel türü" value={kind} onChange={(v) => setKind(v ?? 'Flat')} allowDeselect={false} data={KINDS} />
        <Checkbox label="Bu görsel yapay zekâ referansı olsun (aynı renk ve açıdaki önceki referans kaldırılır)" checked={isAiReference} onChange={(e) => setIsAiReference(e.currentTarget.checked)} />
        {error ? <Notice tone="danger">{error}</Notice> : null}
        <div>
          <Button size="md" leftSection={<UploadIcon size={18} />} disabled={!file} loading={upload.isPending} onClick={() => void submit()}>
            Yükle
          </Button>
        </div>
      </Stack>

      <ConfirmDialog
        opened={deleteId !== null}
        title="Görsel silinsin mi?"
        confirmLabel="Sil"
        danger
        loading={remove.isPending}
        onClose={() => setDeleteId(null)}
        onConfirm={() => deleteId && remove.mutate(deleteId, { onSuccess: () => setDeleteId(null) })}
      >
        Görsel kalıcı olarak silinir. AI referans görseli ise ürün hazır olmaktan çıkar.
      </ConfirmDialog>
    </section>
  );
}
