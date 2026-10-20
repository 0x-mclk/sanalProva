import { Button, SegmentedControl, Select, Stack, Switch, Textarea, TextInput } from '@mantine/core';
import { useState } from 'react';
import { useNavigate, useParams } from 'react-router';
import { useCategories, useCreateProduct, useProduct, useUpdateProduct } from '../../shared/api/hooks';
import { ApiError } from '../../shared/api/http';
import type { ProductDetail, ProductType, ProductWriteRequest } from '../../shared/api/types';
import { errorMessage } from '../../shared/i18n/tr';
import { AppBar, CheckIcon, ErrorState, LoadingState, Notice } from '../../shared/ui';
import { ComponentsSection } from './ComponentsSection';
import { ImagesSection } from './ImagesSection';
import { VariantsSection } from './VariantsSection';
import classes from './CatalogAdmin.module.css';

type FormState = { code: string; name: string; categoryId: string | null; tagline: string; description: string; aiNotes: string; productType: ProductType; isActive: boolean };

const EMPTY: FormState = { code: '', name: '', categoryId: null, tagline: '', description: '', aiNotes: '', productType: 'Single', isActive: true };

function fromProduct(p: ProductDetail): FormState {
  return { code: p.code, name: p.name, categoryId: p.categoryId, tagline: p.tagline ?? '', description: p.description ?? '', aiNotes: p.aiNotes ?? '', productType: p.productType, isActive: p.isActive };
}

/** Ürün ekle/düzenle (Manager+). Yeni ürün önce temel bilgilerle oluşturulur; varyant, görsel ve takım bileşenleri sonra eklenir. */
export function ProductEditPage() {
  const { id } = useParams();
  const isNew = !id;
  const product = useProduct(id);

  if (!isNew && product.isLoading) {
    return (
      <main className="sp-page">
        <LoadingState />
      </main>
    );
  }
  if (!isNew && (product.isError || !product.data)) {
    return (
      <main className="sp-page">
        <ErrorState error={product.error} onRetry={() => void product.refetch()} />
      </main>
    );
  }
  // key: farklı ürüne geçince form durumu sıfırlansın
  return <ProductEditor key={id ?? 'new'} product={product.data} />;
}

function ProductEditor({ product }: { product: ProductDetail | undefined }) {
  const navigate = useNavigate();
  const categories = useCategories(true);
  const create = useCreateProduct();
  const update = useUpdateProduct(product?.id ?? '');
  const [form, setForm] = useState<FormState>(product ? fromProduct(product) : EMPTY);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const set = <K extends keyof FormState>(key: K, value: FormState[K]) => {
    setSaved(false);
    setForm((f) => ({ ...f, [key]: value }));
  };
  const valid = form.code.trim().length >= 2 && form.name.trim().length >= 2 && form.categoryId !== null;
  const busy = create.isPending || update.isPending;

  const save = () => {
    if (!form.categoryId) return;
    setError(null);
    const body: ProductWriteRequest = {
      code: form.code.trim(),
      name: form.name.trim(),
      categoryId: form.categoryId,
      description: form.description.trim() || null,
      tagline: form.tagline.trim() || null,
      productType: form.productType,
      genderTarget: product?.genderTarget ?? null,
      layerOrderOverride: product?.layerOrderOverride ?? null,
      aiNotes: form.aiNotes.trim() || null,
      isActive: form.isActive,
    };
    const onError = (e: unknown) => setError(errorMessage(e instanceof ApiError ? e.code : 'UNKNOWN'));
    if (product) {
      update.mutate(body, { onSuccess: () => setSaved(true), onError });
    } else {
      create.mutate(body, { onSuccess: (created) => void navigate(`/katalog/urun/${created.id}`, { replace: true }), onError });
    }
  };

  return (
    <main className="sp-page">
      <AppBar title={product ? product.name : 'Yeni ürün'} onBack={() => void navigate('/katalog')} />
      <h1 className="sp-title">{product ? 'Ürünü düzenle' : 'Yeni ürün ekle'}</h1>
      {product ? (
        <Notice tone={product.isReadyForAi ? 'success' : 'warning'}>
          {product.isReadyForAi
            ? "AI'ya hazır: ön cephe AI referans görseli var, bu ürün prova ekranlarında seçilebilir."
            : "AI'ya hazır değil: ön cephe için AI referans görseli yükleyin. Hazır olana kadar ürün prova ekranlarında görünmez."}
        </Notice>
      ) : (
        <p className="sp-lead">Önce temel bilgileri kaydedin; ardından renk varyantları ve görselleri ekleyebilirsiniz.</p>
      )}

      <div className={classes.form} style={{ marginTop: 'var(--sp-space-md)' }}>
        <section className={classes.section} aria-labelledby="basic-title">
          <h2 id="basic-title" className={classes.sectionTitle}>
            Temel bilgiler
          </h2>
          <div className={classes.row}>
            <TextInput label="Ürün kodu" required value={form.code} onChange={(e) => set('code', e.currentTarget.value)} maxLength={40} placeholder="BD-014" />
            <TextInput label="Ürün adı" required value={form.name} onChange={(e) => set('name', e.currentTarget.value)} maxLength={120} placeholder="Sultan Bindallı" />
          </div>
          <div className={classes.row}>
            <Select
              label="Kategori"
              required
              searchable
              value={form.categoryId}
              onChange={(v) => set('categoryId', v)}
              data={(categories.data ?? []).map((c) => ({ value: c.id, label: c.isActive ? c.name : `${c.name} (pasif)` }))}
              placeholder="Kategori seçin"
              nothingFoundMessage="Kategori yok"
            />
            <TextInput label="Kısa ifade" description="Kartta renk adının yanında görünür." value={form.tagline} onChange={(e) => set('tagline', e.currentTarget.value)} maxLength={60} placeholder="Altın işleme" />
          </div>
          <div>
            <p style={{ margin: '0 0 4px', fontSize: 'var(--sp-font-size-sm)', fontWeight: 600 }}>Ürün tipi</p>
            <SegmentedControl
              fullWidth
              size="md"
              value={form.productType}
              onChange={(v) => set('productType', v as ProductType)}
              disabled={Boolean(product && product.components.length > 0)}
              data={[
                { value: 'Single', label: 'Bağımsız ürün' },
                { value: 'OutfitSet', label: 'Hazır takım' },
              ]}
            />
            {form.productType === 'OutfitSet' ? <p className="sp-lead" style={{ margin: '6px 0 0', fontSize: 'var(--sp-font-size-sm)' }}>Hazır takımda AI referans görseli, takımın tamamını gösteren tek fotoğraf olmalıdır.</p> : null}
          </div>
          <Textarea label="Açıklama" value={form.description} onChange={(e) => set('description', e.currentTarget.value)} maxLength={2000} autosize minRows={2} />
          <Textarea
            label="Yapay zekâ notu (yalnızca yöneticiler görür)"
            description='Ürüne özel giyim tarifi. Örn. "paçalar bilekte büzgülü".'
            value={form.aiNotes}
            onChange={(e) => set('aiNotes', e.currentTarget.value)}
            maxLength={500}
            autosize
            minRows={2}
          />
          <Switch label={form.isActive ? 'Aktif: prova ekranlarında görünür' : 'Pasif: prova ekranlarında görünmez'} checked={form.isActive} onChange={(e) => set('isActive', e.currentTarget.checked)} />

          {error ? <Notice tone="danger">{error}</Notice> : null}
          <div className={classes.actionsBar}>
            <Button size="md" disabled={!valid} loading={busy} onClick={save}>
              {product ? 'Değişiklikleri kaydet' : 'Ürünü oluştur'}
            </Button>
            {saved ? (
              <span role="status" style={{ display: 'inline-flex', alignItems: 'center', gap: 4, color: 'var(--sp-color-success-text)' }}>
                <CheckIcon size={16} /> Kaydedildi
              </span>
            ) : null}
          </div>
        </section>

        {product ? (
          <Stack gap="lg">
            <VariantsSection product={product} />
            <ImagesSection product={product} />
            {product.productType === 'OutfitSet' ? <ComponentsSection product={product} /> : null}
          </Stack>
        ) : null}
      </div>
    </main>
  );
}
