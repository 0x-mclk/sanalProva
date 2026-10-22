import { SegmentedControl, Select } from '@mantine/core';
import { useMemo, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router';
import { useSession } from '../../shared/api/hooks';
import { CompareIcon, ErrorState, FlowHeader, ImageCompare, LoadingState, Notice, SideBySide } from '../../shared/ui';
import { TvSendButton, TvSessionPanel } from '../displays/TvControls';
import classes from './ComparePage.module.css';

/** `id`: TV'ye gönderilirken sunucunun tanıdığı kimlik (orijinal fotoğraf için fotoğraf kimliği). */
// label: seçicide (model adıyla); badge: görsel üzerinde kısa etiket (uzun etiket kaydırıcıda üst üste biner)
type Option = { value: string; id: string; label: string; badge: string; src: string };

/** Önce/sonra ve sürüm karşılaştırması: orijinal fotoğraf ile sonuç ya da iki sürüm; kaydırıcı veya yan yana. */
export function ComparePage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const session = useSession(id);
  const [mode, setMode] = useState<'slider' | 'side'>('slider');

  const options = useMemo<Option[]>(() => {
    const data = session.data;
    if (!data) return [];
    const list: Option[] = [];
    const photo = data.photos[0];
    if (photo) list.push({ value: 'photo', id: photo.id, label: 'Orijinal fotoğraf', badge: 'Orijinal fotoğraf', src: photo.urls.url });
    for (const image of data.images) {
      const demo = image.isDemo ? ' (DEMO)' : '';
      list.push({
        value: image.id,
        id: image.id,
        label: `Sürüm ${image.versionNo}${image.aiModel ? ` · ${image.aiModel}` : ''}${demo}`,
        badge: `Sürüm ${image.versionNo}${demo}`,
        src: image.urls.url,
      });
    }
    return list;
  }, [session.data]);

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

  const find = (value: string | null, fallback: Option | undefined) => options.find((o) => o.value === value) ?? fallback;
  const left = find(params.get('a'), options[0]);
  const right = find(params.get('b'), options[options.length - 1]);
  const set = (key: 'a' | 'b', value: string | null) => {
    if (!value) return;
    const next = new URLSearchParams(params);
    next.set(key, value);
    setParams(next, { replace: true });
  };

  return (
    <main className="sp-page">
      <FlowHeader title="Karşılaştır" back={() => void navigate(`/prova/${session.data!.id}/sonuc`)} />
      <h1 className="sp-title">Önce ve sonra.</h1>
      <p className="sp-lead">İki sürümü kaydırıcıyla veya yan yana karşılaştırın.</p>

      {options.length < 2 || !left || !right ? (
        <Notice tone="info">Karşılaştırmak için en az bir sonuç gerekir.</Notice>
      ) : (
        <div className={classes.layout}>
          <div className={classes.controls}>
            <Select label="Sol görsel" value={left.value} data={options.map(({ value, label }) => ({ value, label }))} allowDeselect={false} onChange={(v) => set('a', v)} />
            <Select label="Sağ görsel" value={right.value} data={options.map(({ value, label }) => ({ value, label }))} allowDeselect={false} onChange={(v) => set('b', v)} />
            <SegmentedControl
              fullWidth
              size="md"
              value={mode}
              onChange={(v) => setMode(v as 'slider' | 'side')}
              data={[
                { value: 'slider', label: 'Kaydırıcı' },
                { value: 'side', label: 'Yan yana' },
              ]}
            />
            {session.data.status === 'Open' ? (
              <>
                <TvSendButton sessionId={session.data.id} mode="Compare" imageIds={left.id === right.id ? [] : [left.id, right.id]} label="TV'de karşılaştır" />
                {left.id === right.id ? <p className={classes.hint}>TV'de karşılaştırmak için iki farklı görsel seçin.</p> : null}
                <TvSessionPanel sessionId={session.data.id} />
              </>
            ) : null}
          </div>
          <div className={classes.view}>
            {mode === 'slider' ? (
              <ImageCompare left={{ src: left.src, label: left.badge }} right={{ src: right.src, label: right.badge }} />
            ) : (
              <SideBySide left={{ src: left.src, label: left.badge }} right={{ src: right.src, label: right.badge }} />
            )}
            <p className={classes.hint}>
              <CompareIcon size={16} /> {mode === 'slider' ? 'Kaydırıcıyı sürükleyin veya ok tuşlarını kullanın.' : 'Her iki görsel aynı fotoğraftan türetilmiştir.'}
            </p>
          </div>
        </div>
      )}
    </main>
  );
}
