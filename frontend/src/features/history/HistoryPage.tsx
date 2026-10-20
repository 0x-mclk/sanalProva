import { Chip } from '@mantine/core';
import { useState } from 'react';
import { Link, useNavigate } from 'react-router';
import { useDeleteSession, useSessions } from '../../shared/api/hooks';
import type { SessionSummary } from '../../shared/api/types';
import { retentionText, tr, timeAgo } from '../../shared/i18n/tr';
import { AppBar, ConfirmDialog, DemoBadge, EmptyState, ErrorState, Eyebrow, ImageIcon, LoadingState, ShieldIcon, TrashIcon } from '../../shared/ui';
import classes from './HistoryPage.module.css';

type Filter = 'all' | 'open' | 'closed';

/** Prova geçmişi: yalnızca saklama süresi dolmamış oturumlar listelenir; her biri silinebilir. */
export function HistoryPage() {
  const navigate = useNavigate();
  const [filter, setFilter] = useState<Filter>('all');
  const sessions = useSessions(filter, 50);
  const remove = useDeleteSession();
  const [target, setTarget] = useState<SessionSummary | null>(null);

  return (
    <main className="sp-page">
      <AppBar title="Prova geçmişi" onBack={() => void navigate('/')} />
      <h1 className="sp-title">Geçmiş provalar</h1>
      <p className="sp-lead">Fotoğraflar saklama süresi dolunca otomatik silinir; burada yalnızca süresi dolmamış provalar görünür.</p>

      <Chip.Group multiple={false} value={filter} onChange={(v) => setFilter(v as Filter)}>
        <div className={classes.filters} role="group" aria-label="Durum filtresi">
          <Chip value="all">Tümü</Chip>
          <Chip value="open">Devam eden</Chip>
          <Chip value="closed">Kapatılan</Chip>
        </div>
      </Chip.Group>

      {sessions.isLoading ? <LoadingState /> : null}
      {sessions.isError ? <ErrorState error={sessions.error} onRetry={() => void sessions.refetch()} /> : null}
      {sessions.data && sessions.data.items.length === 0 ? (
        <EmptyState title="Geçmiş prova yok" description="Tamamlanan ve devam eden provalar burada listelenir." action={<Link to="/prova/yeni">Yeni prova başlat</Link>} />
      ) : null}

      <ul className={classes.list}>
        {sessions.data?.items.map((session) => (
          <li key={session.id} className={classes.item}>
            <Link to={`/prova/${session.id}`} className={classes.card}>
              <span className={classes.thumbs}>
                <span className={classes.thumb}>{session.photo ? <img src={session.photo.thumbUrl} alt="" loading="lazy" /> : <ImageIcon size={20} />}</span>
                <span className={classes.thumb}>
                  {session.result ? <img src={session.result.thumbUrl} alt="" loading="lazy" /> : <ImageIcon size={20} />}
                  {session.resultIsDemo ? <DemoBadge floating /> : null}
                </span>
              </span>
              <span className={classes.info}>
                <span className={classes.title}>
                  {session.customerLabel ?? 'Müşteri'} · {session.code}
                </span>
                <span className={classes.sub}>
                  {tr.sessionStep[session.nextStep] ?? session.nextStep} · {session.versionCount} sürüm
                </span>
                <span className={classes.meta}>
                  {timeAgo(session.lastActivityAt)} · <ShieldIcon size={12} /> {retentionText(session.retentionUntil)} tarihinde silinir
                </span>
              </span>
              <Eyebrow tone="muted">{session.status === 'Open' ? 'Açık' : 'Kapalı'}</Eyebrow>
            </Link>
            <button type="button" className={classes.delete} aria-label={`${session.code} provasını sil`} onClick={() => setTarget(session)}>
              <TrashIcon size={18} />
            </button>
          </li>
        ))}
      </ul>

      <ConfirmDialog
        opened={target !== null}
        title="Prova silinsin mi?"
        confirmLabel="Kalıcı olarak sil"
        danger
        loading={remove.isPending}
        onClose={() => setTarget(null)}
        onConfirm={() => target && remove.mutate(target.id, { onSuccess: () => setTarget(null) })}
      >
        {target?.code} provasındaki müşteri fotoğrafı ve tüm sonuçlar kalıcı olarak silinir. Bu işlem geri alınamaz.
      </ConfirmDialog>
    </main>
  );
}
