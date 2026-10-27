import { Button, Loader } from '@mantine/core';
import { useEffect, useRef, useState } from 'react';
import { useNavigate, useParams } from 'react-router';
import { useQueryClient } from '@tanstack/react-query';
import { qk, useCancelJob, useJob, useRetryJob, useSession } from '../../shared/api/hooks';
import { ApiError } from '../../shared/api/http';
import { newIdempotencyKey } from '../../shared/api/idempotency';
import { useAuth } from '../../shared/auth/AuthContext';
import { errorMessage } from '../../shared/i18n/tr';
import { useSessionHub } from '../../shared/realtime/useSessionHub';
import { CheckIcon, ErrorState, Eyebrow, InfoIcon, LoadingState, Notice, RefreshIcon, SparklesIcon } from '../../shared/ui';
import classes from './GeneratingPage.module.css';

const STAGES = [
  { key: 'Preparing', label: 'Hazırlanıyor', hint: 'Fotoğraf ve seçimler hazırlanıyor' },
  { key: 'Generating', label: 'Üretiliyor', hint: 'Prova görseli oluşturuluyor' },
  { key: 'Validating', label: 'Kontrol ediliyor', hint: 'Sonuç denetleniyor' },
] as const;

/** AI üretim durumu: SignalR bildirimi + yoklama. Sayfa yenilense de aynı işe bağlanır. */
export function GeneratingPage() {
  const { id, jobId } = useParams();
  const navigate = useNavigate();
  const client = useQueryClient();
  const { me } = useAuth();
  const session = useSession(id);
  const job = useJob(jobId);
  const cancel = useCancelJob();
  const retry = useRetryJob();
  const retryKey = useRef<{ jobId: string; key: string } | null>(null);
  const [elapsed, setElapsed] = useState(0);
  const [actionError, setActionError] = useState<string | null>(null);

  const hub = useSessionHub(id, (message) => {
    if (message.jobId === jobId) {
      void client.invalidateQueries({ queryKey: qk.job(jobId!) });
      void client.invalidateQueries({ queryKey: qk.session(id!) });
    }
  });

  const status = job.data?.status;
  const resultImageId = job.data?.resultImageId;

  useEffect(() => {
    if (status === 'Succeeded' && resultImageId) {
      void navigate(`/prova/${id}/sonuc/${resultImageId}`, { replace: true });
    }
    if (status === 'Cancelled') {
      void navigate(`/prova/${id}`, { replace: true });
    }
  }, [status, resultImageId, id, navigate]);

  useEffect(() => {
    if (status !== 'Queued' && status !== 'Running') return;
    const started = Date.now();
    const timer = window.setInterval(() => setElapsed(Math.floor((Date.now() - started) / 1000)), 1000);
    return () => window.clearInterval(timer);
  }, [status]);

  if (job.isLoading) {
    return (
      <main className="sp-page">
        <LoadingState label="Üretim durumu alınıyor" />
      </main>
    );
  }
  if (job.isError || !job.data) {
    return (
      <main className="sp-page">
        <ErrorState error={job.error} onRetry={() => void job.refetch()} />
      </main>
    );
  }

  const data = job.data;
  const photo = session.data?.photos[0];
  const stageIndex = data.status === 'Queued' ? -1 : Math.max(0, STAGES.findIndex((s) => s.key === data.stage));
  const failed = data.status === 'Failed';

  const doRetry = () => {
    setActionError(null);
    // Aynı başarısız iş için ağ hatasında yeniden gönderim aynı anahtarı taşır; başka bir (yeni) başarısız işte yeni anahtar üretilir
    if (retryKey.current?.jobId !== data.id) {
      retryKey.current = { jobId: data.id, key: newIdempotencyKey() };
    }
    retry.mutate({ id: data.id, idempotencyKey: retryKey.current.key }, {
      onSuccess: (next) => void navigate(`/prova/${id}/uretim/${next.id}`, { replace: true }),
      onError: (e) => setActionError(errorMessage(e instanceof ApiError ? e.code : 'UNKNOWN')),
    });
  };

  return (
    <main className={classes.page}>
      {photo ? <img className={classes.backdrop} src={photo.urls.url} alt="" aria-hidden="true" /> : null}
      <div className={classes.scrim} />

      <section className={classes.card} aria-live="polite" aria-busy={!failed}>
        <span className={classes.icon} data-failed={failed || undefined}>
          {failed ? <InfoIcon size={26} /> : <SparklesIcon size={26} />}
        </span>
        <Eyebrow tone="muted">{failed ? 'Üretim tamamlanamadı' : 'AI sanal prova'}</Eyebrow>
        <h1 className={classes.title}>{failed ? 'Prova hazırlanamadı' : data.status === 'Queued' ? 'Sıra bekleniyor' : 'Provanız hazırlanıyor'}</h1>

        {failed ? (
          <>
            <p className={classes.text}>{data.message}</p>
            {actionError ? <Notice tone="danger">{actionError}</Notice> : null}
            <div className={classes.actions}>
              {data.retryable ? (
                <Button leftSection={<RefreshIcon size={18} />} loading={retry.isPending} onClick={doRetry}>
                  Tekrar dene
                </Button>
              ) : null}
              <Button variant="default" onClick={() => void navigate(`/prova/${id}`, { replace: true })}>
                {data.retryable ? 'Seçimi değiştir' : 'Seçimi veya fotoğrafı değiştir'}
              </Button>
            </div>
          </>
        ) : (
          <>
            <p className={classes.text}>
              {data.status === 'Queued' ? (data.queuePosition && data.queuePosition > 1 ? `Sırada ${data.queuePosition}. sıradasınız.` : 'İş başlamak üzere.') : 'Bu işlem genellikle birkaç saniye sürer.'}
            </p>
            <ol className={classes.stages}>
              {STAGES.map((stage, index) => {
                const state = index < stageIndex ? 'done' : index === stageIndex ? 'active' : 'todo';
                return (
                  <li key={stage.key} className={classes.stage} data-state={state}>
                    <span className={classes.stageDot}>{state === 'done' ? <CheckIcon size={14} /> : state === 'active' ? <Loader size={14} color="bordo" /> : index + 1}</span>
                    <span>
                      <span className={classes.stageLabel}>{stage.label}</span>
                      {state === 'active' ? <span className={classes.stageHint}>{stage.hint}</span> : null}
                    </span>
                  </li>
                );
              })}
            </ol>
            <p className={classes.meta}>
              {elapsed} sn · {hub === 'connected' ? 'Canlı bağlantı' : 'Durum düzenli olarak denetleniyor'}
              {data.attempt > 1 ? ` · ${data.attempt}. deneme` : ''}
              {data.modelLabel ? ` · seçilen model: ${data.modelLabel}` : ''}
            </p>
            {me?.demoCatalog || data.isDemo ? <p className={classes.demo}>Demo üretim: gerçek yapay zekâ çağrısı yapılmaz.</p> : null}
            {data.status === 'Queued' || data.status === 'Running' ? (
              <>
                <Button variant="subtle" color="dark" size="md" loading={cancel.isPending} onClick={() => cancel.mutate(data.id)}>
                  Vazgeç
                </Button>
                {data.status === 'Running' && !data.isDemo ? (
                  <p className={classes.meta}>İşlem sağlayıcıya gönderildiyse vazgeçseniz de ücret doğabilir; sonuç atılır.</p>
                ) : null}
              </>
            ) : null}
          </>
        )}
      </section>
    </main>
  );
}
