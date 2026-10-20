import { Button, Chip, Switch, TextInput } from '@mantine/core';
import { useDeferredValue, useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { useProducts, useSetProductStatus } from '../../shared/api/hooks';
import type { ProductListItem } from '../../shared/api/types';
import { useAuth } from '../../shared/auth/AuthContext';
import { AppBar, EmptyState, ErrorState, ImageIcon, InfoIcon, LayersIcon, LoadingState, PlusIcon, SearchIcon } from '../../shared/ui';
import classes from './CatalogAdmin.module.css';

type TypeFilter = 'all' | 'OutfitSet' | 'Single';
type StatusFilter = 'active' | 'inactive' | 'all';

/** Ürün kataloğu. Herkes görüntüler; ekleme/düzenleme/aktif-pasif yalnızca Manager ve Owner'a açıktır (sunucu da doğrular). */
export function ProductsPage() {
  const navigate = useNavigate();
  const { canManageCatalog, me } = useAuth();
  const [query, setQuery] = useState('');
  const deferred = useDeferredValue(query);
  const [type, setType] = useState<TypeFilter>('all');
  const [status, setStatus] = useState<StatusFilter>('active');
  const products = useProducts({ q: deferred || undefined, type: type === 'all' ? undefined : type, status: canManageCatalog ? status : 'active', pageSize: 100, sort: 'recommended' });
  const toggle = useSetProductStatus();

  return (
    <main className="sp-page">
      <AppBar title="Ürün kataloğu" onBack={() => void navigate('/')} />
      <div className={classes.head}>
        <div>
          <h1 className="sp-title">Ürünler</h1>
          <p className="sp-lead">{canManageCatalog ? 'Ürünleri ekleyin, görsellerini ve renk varyantlarını yönetin.' : 'Katalogdaki ürünleri görüntüleyin.'}</p>
        </div>
        {canManageCatalog ? (
          <div className={classes.headActions}>
            <Button component={Link} to="/katalog/kategoriler" variant="default" size="md" leftSection={<LayersIcon size={18} />}>
              Kategoriler
            </Button>
            <Button component={Link} to="/katalog/urun/yeni" size="md" leftSection={<PlusIcon size={18} />}>
              Yeni ürün
            </Button>
          </div>
        ) : null}
      </div>

      <div className={classes.tools}>
        <TextInput aria-label="Ürün ara" placeholder="Ürün adı veya kodu ara" leftSection={<SearchIcon size={18} />} type="search" value={query} onChange={(e) => setQuery(e.currentTarget.value)} />
        <div className={classes.chipRow}>
          <Chip.Group multiple={false} value={type} onChange={(v) => setType(v as TypeFilter)}>
            <Chip value="all">Tümü</Chip>
            <Chip value="OutfitSet">Hazır takım</Chip>
            <Chip value="Single">Bağımsız ürün</Chip>
          </Chip.Group>
          {canManageCatalog ? (
            <Chip.Group multiple={false} value={status} onChange={(v) => setStatus(v as StatusFilter)}>
              <Chip value="active">Aktif</Chip>
              <Chip value="inactive">Pasif</Chip>
              <Chip value="all">Hepsi</Chip>
            </Chip.Group>
          ) : null}
        </div>
      </div>

      {products.isLoading ? <LoadingState /> : null}
      {products.isError ? <ErrorState error={products.error} onRetry={() => void products.refetch()} /> : null}
      {products.data && products.data.items.length === 0 ? (
        <EmptyState title="Ürün bulunamadı" description={canManageCatalog ? 'Arama veya filtreyi değiştirin ya da yeni ürün ekleyin.' : 'Arama veya filtreyi değiştirin.'} />
      ) : null}

      <ul className="sp-grid" style={{ listStyle: 'none', padding: 0, margin: 0 }}>
        {products.data?.items.map((product) => (
          <li key={product.id}>
            <CatalogCard
              product={product}
              sample={me?.demoCatalog ?? false}
              manage={canManageCatalog}
              busy={toggle.isPending && toggle.variables?.id === product.id}
              onToggle={(isActive) => toggle.mutate({ id: product.id, isActive })}
            />
          </li>
        ))}
      </ul>
    </main>
  );
}

function CatalogCard({ product, sample, manage, busy, onToggle }: { product: ProductListItem; sample: boolean; manage: boolean; busy: boolean; onToggle: (isActive: boolean) => void }) {
  const body = (
    <>
      <span className={classes.media}>
        {product.cover ? <img src={product.cover.thumbUrl} alt="" loading="lazy" /> : <ImageIcon size={28} />}
        {sample ? <span className={classes.sample}>Örnek</span> : null}
        {product.productType === 'OutfitSet' ? <span className={classes.typeTag}>Takım</span> : null}
      </span>
      <span className={classes.info}>
        <span className={classes.name}>{product.name}</span>
        <span className={classes.code}>
          {product.code} · {product.categoryName}
        </span>
        <span className={classes.badges}>
          {!product.isActive ? <span className={classes.badge} data-tone="muted">Pasif</span> : null}
          {!product.isReadyForAi ? (
            <span className={classes.badge} data-tone="warning">
              <InfoIcon size={12} /> AI&apos;ya hazır değil
            </span>
          ) : null}
          <span className={classes.badge}>{product.variants.filter((v) => v.isActive).length} renk</span>
        </span>
      </span>
    </>
  );
  return (
    <div className={classes.card} data-inactive={!product.isActive || undefined}>
      {manage ? (
        <Link to={`/katalog/urun/${product.id}`} className={classes.cardLink} aria-label={`${product.name} ürününü düzenle`}>
          {body}
        </Link>
      ) : (
        <div className={classes.cardLink}>{body}</div>
      )}
      {manage ? (
        <div className={classes.cardFoot}>
          <Switch label={product.isActive ? 'Aktif' : 'Pasif'} checked={product.isActive} disabled={busy} onChange={(e) => onToggle(e.currentTarget.checked)} />
        </div>
      ) : null}
    </div>
  );
}
