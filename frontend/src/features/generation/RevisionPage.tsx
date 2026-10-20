import { Button, SegmentedControl } from '@mantine/core';
import { useMemo, useState, type CSSProperties } from 'react';
import { useNavigate, useParams } from 'react-router';
import { useProduct, useProducts, useRequestRevision, useSession } from '../../shared/api/hooks';
import { ApiError } from '../../shared/api/http';
import type { ImageUrls } from '../../shared/api/types';
import { errorMessage, tr } from '../../shared/i18n/tr';
import { CheckIcon, ErrorState, Eyebrow, FlowHeader, LoadingState, Notice, PaletteIcon, PlusIcon, ProductCard } from '../../shared/ui';
import classes from './RevisionPage.module.css';

type Part = { key: string; productId: string; categoryId?: string; categoryName: string; name: string; image: ImageUrls | null };

/** Renk önizlemesi paleti: katalogda karşılığı olmayabilir, bu yüzden her zaman "stokta olmayabilir" etiketiyle sunulur. */
const PALETTE = [
  { key: 'kirik-beyaz', name: 'Kırık beyaz', hex: '#F2EDE3' },
  { key: 'bordo', name: 'Bordo', hex: '#7A1F3A' },
  { key: 'zumrut', name: 'Zümrüt', hex: '#1F6B4F' },
  { key: 'gece-mavisi', name: 'Gece mavisi', hex: '#1D2B53' },
  { key: 'altin', name: 'Altın', hex: '#B8913A' },
  { key: 'pudra', name: 'Pudra', hex: '#D9B8B0' },
];

/**
 * Revizyon ekranı (arayüz). Gerçek revizyon motoru (SAM 2 + OpenCV renk değişimi, ürün değiştirme) Aşama 5'te uygulanacak:
 * "Uygula" şimdilik sunucudan 501 alır ve sahte sonuç üretilmez; ekran bunu açıkça belirtir.
 */
export function RevisionPage() {
  const { id, imageId } = useParams();
  const navigate = useNavigate();
  const session = useSession(id);
  const request = useRequestRevision(id!);
  const singles = useProducts({ type: 'Single', selectable: true, pageSize: 100 });
  const draftItem = session.data?.draft?.items[0];
  const setDetail = useProduct(session.data?.draft?.mode === 'OutfitSet' ? draftItem?.productId : undefined);

  const [partKey, setPartKey] = useState<string | null>(null);
  const [kind, setKind] = useState<'color' | 'model'>('color');
  const [choice, setChoice] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);

  const parts = useMemo<Part[]>(() => {
    const data = session.data;
    if (!data?.draft) return [];
    if (data.draft.mode === 'OutfitSet') {
      return (setDetail.data?.components ?? []).map((c) => ({ key: c.productId, productId: c.productId, categoryName: c.categoryName, name: c.name, image: c.image }));
    }
    return data.draft.items.flatMap((item) => {
      const product = singles.data?.items.find((p) => p.id === item.productId);
      return product ? [{ key: product.id, productId: product.id, categoryId: product.categoryId, categoryName: product.categoryName, name: product.name, image: product.cover }] : [];
    });
  }, [session.data, setDetail.data, singles.data]);

  if (session.isLoading) {
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

  const base = session.data.images.find((i) => i.id === imageId) ?? session.data.images[session.data.images.length - 1];
  const part = parts.find((p) => p.key === partKey);
  const partProduct = singles.data?.items.find((p) => p.id === part?.productId);
  const alternatives = singles.data?.items.filter((p) => p.categoryId === partProduct?.categoryId && p.id !== partProduct?.id) ?? [];

  const apply = () => {
    if (!base || !part || !choice) return;
    setMessage(null);
    request.mutate(
      kind === 'color'
        ? { baseImageId: base.id, type: 'Recolor', targetCategoryId: part.categoryId, color: choice }
        : { baseImageId: base.id, type: 'ProductSwap', targetCategoryId: part.categoryId, productId: choice },
      { onError: (e) => setMessage(errorMessage(e instanceof ApiError ? e.code : 'UNKNOWN')) },
    );
  };

  return (
    <main className="sp-page">
      <FlowHeader title="Değişiklik iste" back={() => void navigate(`/prova/${session.data!.id}/sonuc/${base?.id ?? ''}`)} />
      <h1 className="sp-title">Neyi değiştirelim?</h1>
      <p className="sp-lead">Bir parça seçin; rengini veya modelini değiştirin. Önceki sürüm her zaman korunur.</p>

      <Notice tone="warning">
        <strong>Önizleme ekranı:</strong> revizyon motoru bu sürümde henüz etkin değil. Seçimlerinizi deneyebilirsiniz; &quot;Uygula&quot; şimdilik yeni görsel üretmez.
      </Notice>

      <div className={classes.layout}>
        <section aria-labelledby="part-title">
          <Eyebrow tone="muted">1 · Parça</Eyebrow>
          <h2 id="part-title" className={classes.h2}>
            Hangi parça?
          </h2>
          {parts.length === 0 ? (
            <Notice tone="info">Bu provada değiştirilebilir parça bilgisi yok. Yeni bir kombin oluşturabilirsiniz.</Notice>
          ) : (
            <ul className={classes.parts}>
              {parts.map((p) => (
                <li key={p.key}>
                  <button type="button" className={classes.part} aria-pressed={partKey === p.key} onClick={() => { setPartKey(p.key); setChoice(null); }}>
                    {p.image ? <img src={p.image.thumbUrl} alt="" /> : <span className={classes.noImage} />}
                    <span className={classes.partText}>
                      <strong>{p.categoryName}</strong>
                      <span>{p.name}</span>
                    </span>
                    {partKey === p.key ? <CheckIcon size={18} className={classes.partCheck} /> : null}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </section>

        {part ? (
          <section aria-labelledby="what-title">
            <Eyebrow tone="muted">2 · Değişiklik</Eyebrow>
            <h2 id="what-title" className={classes.h2}>
              Ne değişsin?
            </h2>
            <SegmentedControl fullWidth size="md" value={kind} onChange={(v) => { setKind(v as 'color' | 'model'); setChoice(null); }} data={[{ value: 'color', label: 'Rengi' }, { value: 'model', label: 'Başka model' }]} />

            {kind === 'color' ? (
              <div className={classes.colors}>
                {partProduct && partProduct.variants.length > 1 ? (
                  <>
                    <p className={classes.groupLabel}>Katalogdaki renkler (stokta)</p>
                    <ul className={classes.swatches}>
                      {partProduct.variants.filter((v) => v.isActive).map((v) => (
                        <li key={v.id}>
                          <button type="button" className={classes.swatch} aria-pressed={choice === `variant:${v.id}`} onClick={() => setChoice(`variant:${v.id}`)}>
                            <span className={classes.dot} style={{ '--swatch': v.colorHex ?? '#ccc' } as CSSProperties} />
                            {v.colorName}
                          </button>
                        </li>
                      ))}
                    </ul>
                  </>
                ) : null}
                <p className={classes.groupLabel}>
                  <PaletteIcon size={14} /> Renk önizlemesi — <em>{tr.app.colorPreview}</em>
                </p>
                <ul className={classes.swatches}>
                  {PALETTE.map((c) => (
                    <li key={c.key}>
                      <button type="button" className={classes.swatch} aria-pressed={choice === c.key} onClick={() => setChoice(c.key)}>
                        <span className={classes.dot} style={{ '--swatch': c.hex } as CSSProperties} />
                        {c.name}
                      </button>
                    </li>
                  ))}
                </ul>
              </div>
            ) : alternatives.length === 0 ? (
              <Notice tone="info">Bu kategoride başka ürün yok.</Notice>
            ) : (
              <ul className={classes.alternatives}>
                {alternatives.map((p) => (
                  <li key={p.id}>
                    <ProductCard name={p.name} code={p.code} subtitle={p.subtitle} image={p.cover} selected={choice === p.id} onToggle={() => setChoice(choice === p.id ? null : p.id)} />
                  </li>
                ))}
              </ul>
            )}
          </section>
        ) : null}
      </div>

      {message ? <Notice tone="warning">{message}</Notice> : null}
      <div className={classes.actions}>
        <Button disabled={!part || !choice} loading={request.isPending} onClick={apply}>
          Uygula
        </Button>
        <Button variant="default" leftSection={<PlusIcon size={18} />} onClick={() => void navigate(`/prova/${session.data!.id}/tur`)}>
          Yeni kombin oluştur
        </Button>
      </div>
    </main>
  );
}
