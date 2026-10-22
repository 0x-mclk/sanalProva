import { Button, Menu } from '@mantine/core';
import { useState } from 'react';
import { Navigate, useNavigate, useParams } from 'react-router';
import { useCloseSession, useDeleteSession, useSession, useToggleFavorite } from '../../shared/api/hooks';
import { retentionText, tr } from '../../shared/i18n/tr';
import {
  AppBar,
  CompareIcon,
  ConfirmDialog,
  DemoBadge,
  DemoNotice,
  ErrorState,
  Eyebrow,
  HeartIcon,
  LoadingState,
  PencilIcon,
  PlusIcon,
  SessionStrip,
  ShieldIcon,
  TrashIcon,
  TvBadge,
} from '../../shared/ui';
import { TvSendButton, TvSessionPanel } from '../displays/TvControls';
import classes from './ResultPage.module.css';

/** Prova sonucu: büyük görsel, sürümler, revizyon/karşılaştırma/yeni kombin eylemleri ve saklama bilgisi. */
export function ResultPage() {
  const { id, imageId } = useParams();
  const navigate = useNavigate();
  const session = useSession(id);
  const favorite = useToggleFavorite(id!);
  const close = useCloseSession(id!);
  const remove = useDeleteSession();
  const [confirmDelete, setConfirmDelete] = useState(false);

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
  const images = data.images;
  if (images.length === 0) {
    return <Navigate to={`/prova/${data.id}`} replace />;
  }
  const current = images.find((i) => i.id === imageId) ?? images[images.length - 1]!;
  const isOpen = data.status === 'Open';
  const previous = images.find((i) => i.id === current.parentImageId) ?? images[images.indexOf(current) - 1];
  const compareTo = previous ? `?a=${previous.id}&b=${current.id}` : `?a=photo&b=${current.id}`;

  return (
    <main className="sp-page">
      <AppBar
        title="Prova sonucu"
        onBack={() => void navigate('/')}
        trailing={
          <span className={classes.headerTools}>
            <TvBadge sessionId={data.id} />
            <Menu position="bottom-end" width={260} shadow="md" radius="md">
              <Menu.Target>
                <button type="button" className={classes.more} aria-label="Prova işlemleri">
                  ⋯
                </button>
              </Menu.Target>
              <Menu.Dropdown>
                {isOpen ? (
                  <Menu.Item onClick={() => close.mutate(undefined, { onSuccess: () => void navigate('/') })}>Provayı bitir (fotoğraflar saklama süresince kalır)</Menu.Item>
                ) : null}
                <Menu.Item color="red" leftSection={<TrashIcon size={16} />} onClick={() => setConfirmDelete(true)}>
                  Fotoğrafları sil ve provayı bitir
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
          </span>
        }
      />

      <div className={classes.layout}>
        <div className={classes.stage}>
          <div className={classes.frame}>
            <img className={classes.image} src={current.urls.url} alt={`${current.label}, sürüm ${current.versionNo}`} />
            {current.isDemo ? <DemoBadge floating /> : null}
            {current.isColorPreview ? <span className={classes.preview}>{tr.app.colorPreview}</span> : null}
          </div>

          {images.length > 1 ? (
            <ul className={classes.versions} aria-label="Sürümler">
              {images.map((image) => (
                <li key={image.id}>
                  <button type="button" className={classes.version} aria-current={image.id === current.id} onClick={() => void navigate(`/prova/${data.id}/sonuc/${image.id}`, { replace: true })}>
                    <img src={image.urls.thumbUrl} alt="" />
                    <span>v{image.versionNo}</span>
                  </button>
                </li>
              ))}
            </ul>
          ) : null}
        </div>

        <div className={classes.side}>
          <SessionStrip label={data.customerLabel} code={data.code} status={`Sürüm ${current.versionNo} · ${current.label}`} trailing={null} />
          {current.aiModel ? (
            <p className={classes.model}>{current.isDemo ? `Demo sonuç · seçilen model: ${current.aiModel}` : `AI modeli: ${current.aiModel}`}</p>
          ) : null}

          <div className={classes.actions}>
            {isOpen ? (
              <Button leftSection={<PencilIcon size={18} />} onClick={() => void navigate(`/prova/${data.id}/revizyon/${current.id}`)}>
                Değişiklik İste
              </Button>
            ) : null}
            <Button variant="default" leftSection={<CompareIcon size={18} />} onClick={() => void navigate(`/prova/${data.id}/karsilastir${compareTo}`)}>
              Karşılaştır
            </Button>
            {isOpen ? (
              <Button variant="default" leftSection={<PlusIcon size={18} />} onClick={() => void navigate(`/prova/${data.id}/tur`)}>
                Yeni kombin oluştur
              </Button>
            ) : null}
            <Button
              variant={current.isFavorite ? 'light' : 'subtle'}
              color={current.isFavorite ? 'bordo' : 'dark'}
              size="md"
              leftSection={<HeartIcon size={18} />}
              aria-pressed={current.isFavorite}
              loading={favorite.isPending}
              onClick={() => favorite.mutate({ imageId: current.id, isFavorite: !current.isFavorite })}
            >
              {current.isFavorite ? 'Favorilerde' : 'Favorilere ekle'}
            </Button>
            {isOpen ? <TvSendButton sessionId={data.id} mode="Single" imageIds={[current.id]} label="TV'ye gönder" /> : null}
          </div>
          {isOpen ? <TvSessionPanel sessionId={data.id} /> : null}

          {current.isDemo ? <DemoNotice /> : null}
          <p className={classes.disclaimer}>{tr.app.aiDisclaimer}</p>
          <p className={classes.privacy}>
            <ShieldIcon size={16} />
            <span>
              Fotoğraflar <strong>{retentionText(data.retentionUntil)}</strong> tarihinde otomatik silinir. Eksik veya hatalı bir şey görürseniz istediğiniz an silebilirsiniz.
            </span>
          </p>
          <Eyebrow tone="muted">{images.length} sürüm · eski sürümler korunur</Eyebrow>
        </div>
      </div>

      <ConfirmDialog
        opened={confirmDelete}
        title="Provadaki tüm fotoğraflar silinsin mi?"
        confirmLabel="Kalıcı olarak sil"
        danger
        loading={remove.isPending}
        onClose={() => setConfirmDelete(false)}
        onConfirm={() => remove.mutate(data.id, { onSuccess: () => void navigate('/', { replace: true }) })}
      >
        Müşteri fotoğrafı ve oluşturulan tüm sonuçlar kalıcı olarak silinir. Bu işlem geri alınamaz.
      </ConfirmDialog>
    </main>
  );
}
