import { Link, Navigate, useNavigate, useParams } from 'react-router';
import { useProducts, useSaveDraft, useSession } from '../../shared/api/hooks';
import type { OutfitMode } from '../../shared/api/types';
import { useAuth } from '../../shared/auth/AuthContext';
import { tr } from '../../shared/i18n/tr';
import { ArrowRightIcon, ErrorState, Eyebrow, FlowHeader, InfoIcon, LayersIcon, LoadingState, Notice, SessionStrip, SparklesIcon, StepProgress, TvIcon } from '../../shared/ui';
import classes from './ModePage.module.css';

/** Prova türü seçimi (referans: "Prova türü seçimi"): hazır takım veya serbest kombin. Seçim sunucudaki taslağa yazılır. */
export function ModePage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const { me } = useAuth();
  const session = useSession(id);
  const saveDraft = useSaveDraft(id!);
  const sets = useProducts({ type: 'OutfitSet', selectable: true, pageSize: 4 });
  const singles = useProducts({ type: 'Single', selectable: true, pageSize: 40 });

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
  const data = session.data;
  if (data.photos.length === 0) {
    return <Navigate to={`/prova/${data.id}/fotograf`} replace />;
  }

  const setCover = sets.data?.items.find((p) => p.cover)?.cover;
  const comboItems = singles.data?.items ?? [];
  const comboCover = (comboItems.find((p) => p.categoryName.toLocaleLowerCase('tr-TR').includes('kaftan')) ?? comboItems.find((p) => p.cover))?.cover;

  const choose = (mode: OutfitMode) => {
    const keep = data.draft?.mode === mode ? data.draft.items.map((i) => ({ productId: i.productId, variantId: i.variantId })) : [];
    saveDraft.mutate({ mode, items: keep }, { onSuccess: () => void navigate(`/prova/${data.id}/${mode === 'OutfitSet' ? 'takim' : 'kombin'}`) });
  };

  return (
    <main className="sp-page">
      <FlowHeader title="Yeni prova" back={`/prova/${data.id}/fotograf`} />
      <StepProgress steps={tr.steps.tryOn} current={1} />

      <h1 className="sp-title">Nasıl prova yapalım?</h1>
      <p className="sp-lead">Hazır bir takım seçin ya da müşterinize özel bir kombin oluşturun.</p>

      <SessionStrip label={data.customerLabel} code={data.code} status="Boydan fotoğraf hazır" />

      <div className={classes.cards}>
        <button type="button" className={classes.card} onClick={() => choose('OutfitSet')} disabled={saveDraft.isPending}>
          <span className={classes.cardText}>
            <Eyebrow icon={<SparklesIcon />}>Hızlı başlangıç</Eyebrow>
            <span className={classes.cardTitle}>Hazır Takım Seç</span>
            <span className={classes.cardDesc}>Bindallı ve şalvar takımlarını tek seçimle deneyin.</span>
            <span className={classes.cardLink}>
              Takımları keşfet <ArrowRightIcon size={16} />
            </span>
          </span>
          <span className={classes.cardMedia}>{setCover ? <img src={setCover.thumbUrl} alt="" /> : null}</span>
        </button>

        <button type="button" className={classes.card} onClick={() => choose('FreeCombination')} disabled={saveDraft.isPending}>
          <span className={classes.cardText}>
            <Eyebrow icon={<LayersIcon />}>Özgürce kombinle</Eyebrow>
            <span className={classes.cardTitle}>Kendi Kombinini Oluştur</span>
            <span className={classes.cardDesc}>Kaftan, şalvar ve aksesuarları ayrı ayrı eşleştirin.</span>
            <span className={classes.cardLink}>
              Kombine başla <ArrowRightIcon size={16} />
            </span>
          </span>
          <span className={classes.cardMedia}>{comboCover ? <img src={comboCover.thumbUrl} alt="" /> : null}</span>
        </button>
      </div>

      {saveDraft.isError ? <Notice tone="danger">Seçim kaydedilemedi. Tekrar deneyin.</Notice> : null}

      <div className={classes.tvInfo}>
        <TvIcon size={20} />
        <span>
          <strong>Büyük ekranda birlikte karar verin.</strong> Kıyafeti seçip Üret&apos;e dokunduğunuzda prova sonucu hazırlanır. TV bağlantısı sonraki aşamada eklenecek.
        </span>
      </div>

      {me?.demoCatalog ? (
        <p className={classes.demoNote}>
          <InfoIcon size={16} /> Demo ortamı: görseller örnektir ve üretim gerçek yapay zekâ kullanmaz.
        </p>
      ) : null}
      <p className={classes.back}>
        <Link to={`/prova/${data.id}/fotograf`}>Fotoğrafı değiştir</Link>
      </p>
    </main>
  );
}
