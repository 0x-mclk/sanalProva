import { Button, Chip, Menu, Select, TextInput } from '@mantine/core';
import { useDeferredValue, useMemo, useRef, useState } from 'react';
import { Navigate, useNavigate, useParams } from 'react-router';
import { useProducts, useSession, useStartGeneration } from '../../shared/api/hooks';
import type { DraftDto, ProductListItem } from '../../shared/api/types';
import { ApiError } from '../../shared/api/http';
import { useTryOnOptions, type TryOnOptions } from '../../shared/api/usage';
import { useAuth } from '../../shared/auth/AuthContext';
import { errorMessage, tr } from '../../shared/i18n/tr';
import { CheckIcon, ChevronDownIcon, EmptyState, ErrorState, Eyebrow, FlowHeader, FilterIcon, LoadingState, Notice, ProductCard, SearchIcon, SparklesIcon, StepProgress, StickyActionBar } from '../../shared/ui';
import { categoryChips, filterProducts, setVariant, toggleEntry, type PickerSort } from './pickerLogic';
import { useDraftItems } from './useDraftItems';
import { VariantSwatches } from './VariantSwatches';
import classes from './PickerPage.module.css';

const SORT_LABELS: Record<PickerSort, string> = { recommended: 'Önerilen', name: 'Ada göre', code: 'Koda göre' };

/**
 * Tek ürün giydirme kataloğu (referans: "Hazır takım kataloğu"): hazır takımlar ve kaftan gibi tüm vücut tekil ürünler. Tek ürün seçilir;
 * seçim sunucudaki taslağa yazılır. Personel yalnızca yöneticinin izin verdiği AI modeli ve kaliteyi seçebilir.
 */
export function OutfitSetPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const { me } = useAuth();
  const session = useSession(id);
  const products = useProducts({ tryOn: true, selectable: true, pageSize: 100 });

  if (session.isLoading || products.isLoading) {
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
    <OutfitSetContent
      sessionId={session.data.id}
      code={session.data.code}
      label={session.data.customerLabel}
      serverDraft={session.data.draft}
      photoId={session.data.photos[0]?.id}
      items={products.data?.items ?? []}
      loadError={products.error}
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
  items: ProductListItem[];
  loadError: unknown;
  demo: boolean;
  onBack: () => void;
};

function OutfitSetContent({ sessionId, code, label, serverDraft, photoId, items, loadError, demo, onBack }: ContentProps) {
  const navigate = useNavigate();
  const { entries, setEntries, warnings, flush, saveError } = useDraftItems(sessionId, 'OutfitSet', serverDraft);
  const start = useStartGeneration(sessionId);
  const [query, setQuery] = useState('');
  const deferredQuery = useDeferredValue(query);
  const [categoryId, setCategoryId] = useState<string | null>(null);
  const [sort, setSort] = useState<PickerSort>('recommended');
  const [error, setError] = useState<string | null>(null);
  const idempotencyKey = useRef<string>(crypto.randomUUID());
  const options = useTryOnOptions();
  const [model, setModel] = useState<{ key?: string; quality?: string }>({});
  const overBudget = options.data ? resolvePick(options.data, model)?.quality?.exceedsJobLimit === true : false;

  const chips = useMemo(() => categoryChips(items), [items]);
  const visible = useMemo(() => filterProducts(items, deferredQuery, categoryId, sort), [items, deferredQuery, categoryId, sort]);
  const selected = entries[0];
  const selectedProduct = items.find((p) => p.id === selected?.productId);
  const categoryOf = (productId: string) => items.find((p) => p.id === productId)?.categoryId;

  const choose = (productId: string) => {
    const product = items.find((p) => p.id === productId);
    if (!product) return;
    setError(null);
    idempotencyKey.current = crypto.randomUUID(); // seçim değişince yeni üretim isteği
    setEntries(toggleEntry(entries, product, 1, categoryOf));
  };

  const generate = async () => {
    setError(null);
    try {
      await flush();
      const job = await start.mutateAsync({ idempotencyKey: idempotencyKey.current, photoId, modelKey: model.key ?? null, quality: model.quality ?? null });
      void navigate(`/prova/${sessionId}/uretim/${job.id}`);
    } catch (e) {
      setError(errorMessage(e instanceof ApiError ? e.code : 'UNKNOWN'));
    }
  };

  return (
    <main className="sp-page sp-page--with-bar">
      <FlowHeader title="Hazır takım ve kaftanlar" back={onBack} />
      <StepProgress steps={tr.steps.tryOn} current={2} />

      <h1 className="sp-title">Bir takım, bambaşka bir his.</h1>
      <p className="sp-lead">
        {label ? `${label} · ` : ''}
        {code} için bir takım veya kaftan seç.
      </p>

      <div className={classes.tools}>
        <TextInput
          aria-label="Ürün adı veya kodu ara"
          placeholder="Ürün adı veya kodu ara"
          leftSection={<SearchIcon size={18} />}
          value={query}
          onChange={(e) => setQuery(e.currentTarget.value)}
          type="search"
        />
        <div className={classes.chips} role="group" aria-label="Kategori filtresi">
          <Chip.Group multiple={false} value={categoryId ?? 'all'} onChange={(v) => setCategoryId(v === 'all' ? null : v)}>
            <Chip value="all" variant="filled">
              Tümü
            </Chip>
            {chips.map((chip) => (
              <Chip key={chip.id} value={chip.id}>
                {chip.name}
              </Chip>
            ))}
          </Chip.Group>
          <span className={classes.filterIcon} aria-hidden="true">
            <FilterIcon size={18} />
          </span>
        </div>
        <div className={classes.countRow}>
          <span className={classes.count}>{visible.length} ürün · Tüm renkler</span>
          <Menu position="bottom-end" shadow="md" radius="md">
            <Menu.Target>
              <button type="button" className={classes.sortButton} aria-label={`Sıralama: ${SORT_LABELS[sort]}`}>
                {SORT_LABELS[sort]} <ChevronDownIcon size={16} />
              </button>
            </Menu.Target>
            <Menu.Dropdown>
              {(Object.keys(SORT_LABELS) as PickerSort[]).map((key) => (
                <Menu.Item key={key} onClick={() => setSort(key)} rightSection={sort === key ? <CheckIcon size={14} /> : null}>
                  {SORT_LABELS[key]}
                </Menu.Item>
              ))}
            </Menu.Dropdown>
          </Menu>
        </div>
      </div>

      {loadError ? <ErrorState error={loadError} /> : null}
      {!loadError && visible.length === 0 ? (
        <EmptyState title="Ürün bulunamadı" description={items.length === 0 ? 'Katalogda AI\'ya hazır takım veya kaftan yok. Yönetici ürün eklemeli.' : 'Arama veya filtreyi değiştirin.'} />
      ) : (
        <ul className="sp-grid" style={{ listStyle: 'none', padding: 0, margin: 0 }}>
          {visible.map((product) => {
            const isSelected = selected?.productId === product.id;
            const variant = isSelected ? product.variants.find((v) => v.id === selected.variantId) : undefined;
            return (
              <li key={product.id} className={classes.cell}>
                <ProductCard
                  name={product.name}
                  code={product.code}
                  subtitle={product.subtitle}
                  image={variant?.image ?? product.cover}
                  selected={isSelected}
                  sampleBadge={demo}
                  onToggle={() => choose(product.id)}
                />
                {isSelected ? (
                  <VariantSwatches variants={product.variants} selectedId={selected.variantId} label={product.name} onSelect={(variantId) => setEntries(setVariant(entries, product.id, variantId))} />
                ) : null}
              </li>
            );
          })}
        </ul>
      )}

      <StickyActionBar>
        <div className={classes.barHead}>
          <div className={classes.barText}>
            <Eyebrow tone="muted">{selectedProduct ? '1 ürün seçildi' : 'Takım veya kaftan seçin'}</Eyebrow>
            <span className={classes.barName}>{selectedProduct ? `${selectedProduct.name} · ${selectedProduct.code}` : 'Henüz seçim yapılmadı'}</span>
          </div>
          <span className={classes.barReady}>
            <CheckIcon size={14} /> Fotoğraf hazır
          </span>
        </div>
        {options.data ? (
          <ModelPicker
            options={options.data}
            value={model}
            onChange={(next) => {
              idempotencyKey.current = crypto.randomUUID(); // farklı model/kalite bilinçli yeni istektir
              setModel(next);
            }}
          />
        ) : null}
        {warnings.length > 0 ? <Notice tone="warning">Seçimde sorun var: {warnings.join(', ')}</Notice> : null}
        {error ? <Notice tone="danger">{error}</Notice> : null}
        {saveError ? <Notice tone="warning">Seçim kaydedilemedi; tekrar dokunun.</Notice> : null}
        <Button fullWidth leftSection={<SparklesIcon size={18} />} disabled={!selectedProduct || warnings.length > 0 || overBudget} loading={start.isPending} onClick={() => void generate()}>
          Üret
        </Button>
        <p className={classes.barNote}>{demo ? 'Demo üretim: gerçek yapay zekâ çağrısı yapılmaz.' : 'AI prova hazırlanır; sonuç ekranda gösterilir.'}</p>
      </StickyActionBar>
    </main>
  );
}

type Pick = { key?: string; quality?: string };

/** Seçili (yoksa firmanın varsayılanı olan) model ve kalite. */
function resolvePick(options: TryOnOptions, value: Pick) {
  const model = options.models.find((m) => m.key === (value.key ?? options.defaultModel)) ?? options.models[0];
  if (!model) return null;
  const qualityKey = value.quality && model.qualities.some((q) => q.key === value.quality) ? value.quality : model.defaultQuality;
  return { model, quality: model.qualities.find((q) => q.key === qualityKey) };
}

const usd = (value: number) => `$${value.toFixed(4)}`;

/**
 * AI modeli ve kalitesi. Seçenekler sunucudan (yönetici izinleri) gelir; seçim yapılmazsa firmanın varsayılanı kullanılır.
 * Tutar tahminidir (sağlayıcı istek başına maliyet bildirmez). Tek seçenek varsa seçici gösterilmez. Yüksek maliyetli kalitede
 * en kötü durum da gösterilir; iş başı bütçe sınırını aşan kalite seçilemez (sunucu da reddeder).
 */
function ModelPicker({ options, value, onChange }: { options: TryOnOptions; value: Pick; onChange: (next: Pick) => void }) {
  const pick = resolvePick(options, value);
  if (!pick) return null;
  const { model, quality } = pick;
  const qualityKey = quality?.key ?? model.defaultQuality;
  const single = options.models.length === 1 && model.qualities.length === 1;

  return (
    <div className={classes.modelPicker}>
      {single ? null : (
        <div className={classes.modelFields}>
          <Select
            label="AI modeli"
            size="sm"
            value={model.key}
            allowDeselect={false}
            data={options.models.map((m) => ({ value: m.key, label: m.displayName }))}
            onChange={(v) => v && onChange({ key: v, quality: undefined })}
          />
          <Select
            label="Kalite"
            size="sm"
            value={qualityKey}
            allowDeselect={false}
            disabled={model.qualities.length < 2}
            data={model.qualities.map((q) => ({
              value: q.key,
              label: `${q.label}${q.highCost ? ' · yüksek maliyet' : ''}${q.exceedsJobLimit ? ' · bütçe sınırı aşılıyor' : ''}`,
              disabled: q.exceedsJobLimit,
            }))}
            onChange={(v) => v && onChange({ key: model.key, quality: v })}
          />
        </div>
      )}
      <p className={classes.barNote}>
        {model.displayName}
        {quality ? ` · ${quality.label}` : ''}
        {options.live && quality ? ` · tahmini ≈ ${usd(quality.estimatedUsd)}` : ''}
        {options.live && quality?.highCost ? ` (en fazla ${usd(quality.worstCaseUsd)})` : ''}
        {options.live ? '' : ' · demo: ücret yok'}
      </p>
      {quality?.exceedsJobLimit ? (
        <Notice tone="warning">Bu kalitenin en kötü durum maliyeti iş başı bütçe sınırını aşıyor. Başka bir kalite seçin veya yöneticinize danışın.</Notice>
      ) : null}
    </div>
  );
}
