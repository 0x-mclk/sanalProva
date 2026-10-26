import { Button } from '@mantine/core';
import { useMemo, useRef, useState } from 'react';
import { Navigate, useNavigate, useParams } from 'react-router';
import { useCategories, useProducts, useSession, useStartGeneration } from '../../shared/api/hooks';
import { newIdempotencyKey } from '../../shared/api/idempotency';
import { ApiError } from '../../shared/api/http';
import type { CategoryDto, DraftDto, ProductListItem } from '../../shared/api/types';
import { useAuth } from '../../shared/auth/AuthContext';
import { errorMessage, tr } from '../../shared/i18n/tr';
import { CloseIcon, EmptyState, ErrorState, Eyebrow, FlowHeader, LoadingState, Notice, ProductCard, SparklesIcon, StepProgress, StickyActionBar } from '../../shared/ui';
import { setVariant, toggleEntry } from './pickerLogic';
import { useDraftItems } from './useDraftItems';
import { VariantSwatches } from './VariantSwatches';
import classes from './PickerPage.module.css';

const WARNING_TEXT: Record<string, string> = {
  ConflictingCategories: 'Seçilen kategoriler birlikte kullanılamaz. Birini kaldırın.',
  TooManyInCategory: 'Bir kategoriden fazla ürün seçildi.',
  SingleProductRequired: 'Serbest kombinde yalnızca bağımsız ürünler seçilebilir.',
};

/** Serbest kombin: kategori bazlı seçim (şalvar, gömlek, yelek, kaftan, kemer...). Kategoriler katalogdan dinamik gelir. */
export function FreeCombinationPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const { me } = useAuth();
  const session = useSession(id);
  const products = useProducts({ type: 'Single', selectable: true, pageSize: 100 });
  const categories = useCategories();

  if (session.isLoading || products.isLoading || categories.isLoading) {
    return (
      <main className="sp-page">
        <LoadingState />
      </main>
    );
  }
  if (session.isError || !session.data) {
    return (
      <main className="sp-page">
        <ErrorState error={session.error} onRetry={() => void session.refetch()} />
      </main>
    );
  }
  if (session.data.photos.length === 0) {
    return <Navigate to={`/prova/${session.data.id}/fotograf`} replace />;
  }

  return (
    <FreeCombinationContent
      sessionId={session.data.id}
      code={session.data.code}
      label={session.data.customerLabel}
      serverDraft={session.data.draft}
      photoId={session.data.photos[0]?.id}
      products={products.data?.items ?? []}
      categories={categories.data ?? []}
      loadError={products.error ?? categories.error}
      demo={me?.demoCatalog ?? false}
      onBack={() => void navigate(`/prova/${session.data.id}/tur`)}
    />
  );
}

type ContentProps = {
  sessionId: string;
  code: string;
  label: string | null;
  serverDraft: DraftDto | null;
  photoId: string | undefined;
  products: ProductListItem[];
  categories: CategoryDto[];
  loadError: unknown;
  demo: boolean;
  onBack: () => void;
};

function FreeCombinationContent({ sessionId, code, label, serverDraft, photoId, products, categories, loadError, demo, onBack }: ContentProps) {
  const navigate = useNavigate();
  const { entries, setEntries, warnings, flush, saveError } = useDraftItems(sessionId, 'FreeCombination', serverDraft);
  const start = useStartGeneration(sessionId);
  const [error, setError] = useState<string | null>(null);
  const idempotencyKey = useRef<string>(newIdempotencyKey());

  // Yalnızca ürünü olan kategoriler, katalogdaki sıraya göre
  const tabs = useMemo(() => {
    const withProducts = new Set(products.map((p) => p.categoryId));
    return categories.filter((c) => withProducts.has(c.id)).sort((a, b) => a.sortOrder - b.sortOrder);
  }, [products, categories]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const activeCategory = tabs.find((c) => c.id === activeId) ?? tabs[0];

  const categoryOf = (productId: string) => products.find((p) => p.id === productId)?.categoryId;
  const visible = products.filter((p) => p.categoryId === activeCategory?.id);
  const selectedProducts = entries.map((e) => ({ entry: e, product: products.find((p) => p.id === e.productId) })).filter((x): x is { entry: typeof x.entry; product: ProductListItem } => Boolean(x.product));

  const toggle = (product: ProductListItem) => {
    const category = categories.find((c) => c.id === product.categoryId);
    setError(null);
    idempotencyKey.current = newIdempotencyKey();
    setEntries(toggleEntry(entries, product, category?.maxSelection ?? 1, categoryOf));
  };

  const remove = (productId: string) => {
    idempotencyKey.current = newIdempotencyKey();
    setEntries(entries.filter((e) => e.productId !== productId));
  };

  const generate = async () => {
    setError(null);
    try {
      await flush();
      const job = await start.mutateAsync({ idempotencyKey: idempotencyKey.current, photoId });
      void navigate(`/prova/${sessionId}/uretim/${job.id}`);
    } catch (e) {
      setError(errorMessage(e instanceof ApiError ? e.code : 'UNKNOWN'));
    }
  };

  const countIn = (categoryId: string) => entries.filter((e) => categoryOf(e.productId) === categoryId).length;

  return (
    <main className="sp-page sp-page--with-bar">
      <FlowHeader title="Kombin oluştur" back={onBack} />
      <StepProgress steps={tr.steps.tryOn} current={2} />

      <h1 className="sp-title">Kendi kombinini oluştur.</h1>
      <p className="sp-lead">
        {label ? `${label} · ` : ''}
        {code} için kategorilerden parçaları seç.
      </p>

      {loadError ? <ErrorState error={loadError} /> : null}
      {!loadError && tabs.length === 0 ? <EmptyState title="Seçilebilir ürün yok" description="Katalogda yapay zekâ referans görseli olan bağımsız ürün bulunmuyor. Yönetici ürün eklemeli." /> : null}

      {tabs.length > 0 ? (
        <div className={classes.tabs} role="tablist" aria-label="Kategoriler">
          {tabs.map((category) => (
            <button key={category.id} type="button" role="tab" id={`tab-${category.id}`} aria-selected={category.id === activeCategory?.id} aria-controls="panel" className={classes.tab} onClick={() => setActiveId(category.id)}>
              {category.name}
              {countIn(category.id) > 0 ? <span className={classes.tabCount}>{countIn(category.id)}</span> : null}
            </button>
          ))}
        </div>
      ) : null}

      <ul id="panel" role="tabpanel" aria-labelledby={activeCategory ? `tab-${activeCategory.id}` : undefined} className="sp-grid" style={{ listStyle: 'none', padding: 0, margin: 0 }}>
        {visible.map((product) => {
          const entry = entries.find((e) => e.productId === product.id);
          const variant = entry ? product.variants.find((v) => v.id === entry.variantId) : undefined;
          return (
            <li key={product.id} className={classes.cell}>
              <ProductCard name={product.name} code={product.code} subtitle={product.subtitle} image={variant?.image ?? product.cover} selected={Boolean(entry)} sampleBadge={demo} onToggle={() => toggle(product)} />
              {entry ? <VariantSwatches variants={product.variants} selectedId={entry.variantId} label={product.name} onSelect={(variantId) => setEntries(setVariant(entries, product.id, variantId))} /> : null}
            </li>
          );
        })}
      </ul>

      <StickyActionBar>
        <div>
          <Eyebrow tone="muted">{selectedProducts.length > 0 ? `${selectedProducts.length} parça seçildi` : 'Kombin çekmecesi'}</Eyebrow>
          {selectedProducts.length === 0 ? (
            <p className={classes.trayEmpty}>Kategorilerden parça seçtikçe burada birikir.</p>
          ) : (
            <ul className={classes.tray} style={{ listStyle: 'none', padding: 0, margin: '4px 0 0' }} aria-label="Seçilen parçalar">
              {selectedProducts.map(({ entry, product }) => {
                const variant = product.variants.find((v) => v.id === entry.variantId);
                return (
                  <li key={product.id} className={classes.trayItem}>
                    {(variant?.image ?? product.cover) ? <img className={classes.trayThumb} src={(variant?.image ?? product.cover)!.thumbUrl} alt="" /> : <span className={classes.trayThumb} />}
                    <span className={classes.trayLabel}>
                      <strong>{product.categoryName}</strong>
                      <span>{variant?.colorName ?? product.name}</span>
                    </span>
                    <button type="button" className={classes.trayRemove} onClick={() => remove(product.id)} aria-label={`${product.name} parçasını çıkar`}>
                      <CloseIcon size={16} />
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
        </div>
        {warnings.length > 0 ? <Notice tone="warning">{warnings.map((w) => WARNING_TEXT[w] ?? w).join(' ')}</Notice> : null}
        {error ? <Notice tone="danger">{error}</Notice> : null}
        {saveError ? <Notice tone="warning">Seçim kaydedilemedi; tekrar dokunun.</Notice> : null}
        <Button fullWidth leftSection={<SparklesIcon size={18} />} disabled={selectedProducts.length === 0 || warnings.length > 0} loading={start.isPending} onClick={() => void generate()}>
          Üret
        </Button>
        <p className={classes.barNote}>{demo ? 'Demo üretim: gerçek yapay zekâ çağrısı yapılmaz.' : 'AI prova hazırlanır; sonuç ekranda gösterilir.'}</p>
      </StickyActionBar>
    </main>
  );
}
