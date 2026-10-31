import { Button } from '@mantine/core';
import { useRef, useState, type ChangeEvent } from 'react';
import { Navigate, useNavigate, useParams } from 'react-router';
import { useDeletePhoto, useSession, useUploadPhoto } from '../../shared/api/hooks';
import { ApiError } from '../../shared/api/http';
import { errorMessage, retentionText, tr } from '../../shared/i18n/tr';
import { prepareImage, validateImageFile } from '../../shared/image/prepareImage';
import {
  ArrowRightIcon,
  CameraIcon,
  CheckIcon,
  ConfirmDialog,
  ErrorState,
  Eyebrow,
  FlowHeader,
  ImageIcon,
  LoadingState,
  Notice,
  ShieldIcon,
  StepProgress,
  TrashIcon,
} from '../../shared/ui';
import { CameraView, type CameraHandle, type CameraStatus } from './CameraView';
import { PhotoApproval, PhotoGuide } from './PhotoGuide';
import classes from './PhotoPage.module.css';

/** Müşteri fotoğrafı çekme/yükleme (referans: "Müşteri fotoğrafı"). */
export function PhotoPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const session = useSession(id);
  const upload = useUploadPhoto(id!);
  const deletePhoto = useDeletePhoto(id!);

  const cameraRef = useRef<CameraHandle>(null);
  const cameraInput = useRef<HTMLInputElement>(null);
  const galleryInput = useRef<HTMLInputElement>(null);
  const [cameraStatus, setCameraStatus] = useState<CameraStatus>('starting');
  const [retaking, setRetaking] = useState(false);
  const [localError, setLocalError] = useState<string | null>(null);
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
  if (data.nextStep === 'Consent') {
    return <Navigate to={`/prova/${data.id}/onay`} replace />;
  }
  if (data.status !== 'Open') {
    return <Navigate to="/gecmis" replace />;
  }

  const photo = data.photos[0];
  const showCamera = !photo || retaking;
  const busy = upload.isPending;

  const handleBlob = async (blob: Blob) => {
    setLocalError(null);
    const check = validateImageFile(blob);
    if (!check.ok) {
      setLocalError(errorMessage(check.code));
      return;
    }
    try {
      const prepared = await prepareImage(blob);
      await upload.mutateAsync(prepared);
      setRetaking(false);
    } catch (error) {
      setLocalError(errorMessage(error instanceof ApiError ? error.code : 'UNKNOWN'));
    }
  };

  const capture = async () => {
    const blob = await cameraRef.current?.capture();
    if (blob) {
      await handleBlob(blob);
    } else {
      // Canlı kamera yoksa cihazın kendi kamera uygulaması açılır (mobil), masaüstünde dosya seçici
      cameraInput.current?.click();
    }
  };

  const onFileChosen = (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.currentTarget.files?.[0];
    event.currentTarget.value = ''; // aynı dosya tekrar seçilebilsin
    if (file) void handleBlob(file);
  };

  const warnings = photo?.quality.warnings ?? [];
  const approved = Boolean(photo?.staffApprovedAt); // onay sunucuda kayıtlı (yeniden çekim yeni fotoğraf = onaysız)

  return (
    <main className="sp-page">
      <FlowHeader title="Yeni prova" back="/" />
      <StepProgress steps={tr.steps.tryOn} current={0} />

      <div className={classes.layout}>
        <div className={classes.main}>
          <h1 className="sp-title">Önce, boydan bir fotoğraf.</h1>
          <p className="sp-lead">Müşterinin başı ve ayakları kadrajda olsun. Kollarını gövdesinden hafifçe ayırmasını isteyin.</p>

          {showCamera ? <PhotoGuide /> : null}

          <div className={classes.stage}>
            {showCamera ? (
              <CameraView ref={cameraRef} active onStatusChange={setCameraStatus} />
            ) : (
              <div className={classes.preview}>
                <img src={photo!.urls.url} alt="Çekilen müşteri fotoğrafı" />
              </div>
            )}
          </div>

          <input ref={cameraInput} className="sp-sr-only" type="file" accept="image/*" capture="environment" tabIndex={-1} onChange={onFileChosen} aria-hidden="true" />
          <input ref={galleryInput} className="sp-sr-only" type="file" accept="image/jpeg,image/png,image/webp" tabIndex={-1} onChange={onFileChosen} aria-hidden="true" />

          {showCamera ? (
            <div className={classes.actions}>
              <Button fullWidth leftSection={<CameraIcon size={20} />} loading={busy} disabled={cameraStatus === 'starting'} onClick={() => void capture()}>
                Fotoğraf Çek
              </Button>
              <Button variant="subtle" color="dark" size="md" fullWidth leftSection={<ImageIcon size={18} />} disabled={busy} onClick={() => galleryInput.current?.click()}>
                Galeriden Yükle
              </Button>
              {photo ? (
                <Button variant="subtle" size="md" fullWidth onClick={() => setRetaking(false)} disabled={busy}>
                  Vazgeç, mevcut fotoğrafı kullan
                </Button>
              ) : null}
            </div>
          ) : null}

          {localError ? <Notice tone="danger">{localError}</Notice> : null}
        </div>

        <aside className={classes.side} aria-label="Çekim sonrası">
          <div className={classes.sideHead}>
            <Eyebrow tone="muted">Çekim sonrası</Eyebrow>
          </div>
          <section className={classes.result} data-ready={photo ? true : undefined}>
            <span className={classes.thumb}>{photo ? <img src={photo.urls.thumbUrl} alt="" /> : <ImageIcon size={22} />}</span>
            <span className={classes.resultText}>
              <span className={classes.resultTitle}>
                {photo ? (
                  <>
                    <CheckIcon size={16} /> Fotoğraf hazır
                  </>
                ) : (
                  'Fotoğraf bekleniyor'
                )}
              </span>
              <span className={classes.resultSub}>
                {photo
                  ? warnings.length === 0
                    ? 'Dikey kadraj, çözünürlük ve netlik uygun görünüyor. Bu fotoğrafla devam edilebilir.'
                    : 'Fotoğraf yüklendi; aşağıdaki uyarılara bakın.'
                  : 'Çekilen veya yüklenen fotoğraf burada görünür.'}
              </span>
            </span>
          </section>

          {warnings.length > 0 ? (
            <Notice tone="warning">
              <ul className={classes.warnings}>
                {warnings.map((warning) => (
                  <li key={warning}>{warning}</li>
                ))}
              </ul>
            </Notice>
          ) : null}
          {photo ? <p className={classes.checkNote}>Kontrol edilenler: yön, çözünürlük ve netlik. Kişi sayısı ve tam boy otomatik denetlenmez.</p> : null}

          {photo && !retaking ? <PhotoApproval key={photo.id} sessionId={data.id} photoId={photo.id} approved={approved} /> : null}

          {photo && !retaking ? (
            <div className={classes.footerActions}>
              <Button variant="default" size="md" onClick={() => setRetaking(true)}>
                Yeniden Çek
              </Button>
              <Button size="md" rightSection={<ArrowRightIcon size={18} />} disabled={!approved} onClick={() => void navigate(`/prova/${data.id}/tur`)}>
                Devam Et
              </Button>
              <Button className={classes.deleteButton} variant="subtle" color="red" size="compact-md" leftSection={<TrashIcon size={16} />} onClick={() => setConfirmDelete(true)}>
                Fotoğrafı sil
              </Button>
            </div>
          ) : null}

          <p className={classes.privacy}>
            <ShieldIcon size={16} />
            <span>
              Müşteri onayı alındı. Fotoğraf yalnızca sanal prova için kullanılır ve <strong>{retentionText(data.retentionUntil)}</strong> tarihinde otomatik silinir.
            </span>
          </p>
        </aside>
      </div>

      <ConfirmDialog
        opened={confirmDelete}
        title="Fotoğraf silinsin mi?"
        confirmLabel="Fotoğrafı sil"
        danger
        loading={deletePhoto.isPending}
        onClose={() => setConfirmDelete(false)}
        onConfirm={() => photo && deletePhoto.mutate(photo.id, { onSuccess: () => setConfirmDelete(false) })}
      >
        Fotoğraf kalıcı olarak silinir. Yeni bir fotoğraf çekmeniz gerekir.
      </ConfirmDialog>
    </main>
  );
}
