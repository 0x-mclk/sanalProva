import { Button, Checkbox, SegmentedControl, Stack, TextInput } from '@mantine/core';
import { useState } from 'react';
import { useNavigate, useParams } from 'react-router';
import { useConsentNotice, useCreateSession, useRecordConsent, useSession } from '../../shared/api/hooks';
import { ApiError } from '../../shared/api/http';
import { errorMessage } from '../../shared/i18n/tr';
import { ArrowRightIcon, ErrorState, FlowHeader, LoadingState, Notice, ShieldIcon } from '../../shared/ui';
import classes from './ConsentPage.module.css';

type Method = 'CustomerTapped' | 'StaffConfirmedVerbal';

/**
 * Müşteri bilgilendirmesi ve açık rıza (KVKK). Onay verilmeden fotoğraf yüklenemez; bu kural sunucuda da uygulanır.
 * `/prova/yeni` yeni oturum açar; `/prova/:id/onay` onayı eksik mevcut oturumu tamamlar.
 */
export function ConsentPage() {
  const { id: existingId } = useParams();
  const navigate = useNavigate();
  const notice = useConsentNotice();
  const existing = useSession(existingId);
  const createSession = useCreateSession();
  const recordConsent = useRecordConsent();

  const [label, setLabel] = useState('');
  const [processing, setProcessing] = useState(false);
  const [transfer, setTransfer] = useState(false);
  const [extended, setExtended] = useState(false);
  const [minor, setMinor] = useState(false);
  const [guardian, setGuardian] = useState(false);
  const [method, setMethod] = useState<Method>('CustomerTapped');
  // Oturum oluştu ama onay başarısız olduysa tekrar denemede aynı oturum kullanılır (yetim oturum oluşmaz)
  const [createdId, setCreatedId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  if (notice.isLoading || (existingId && existing.isLoading)) {
    return (
      <main className="sp-page">
        <LoadingState />
      </main>
    );
  }
  if (notice.isError || !notice.data) {
    return (
      <main className="sp-page">
        <ErrorState error={notice.error} onRetry={() => void notice.refetch()} />
      </main>
    );
  }

  const canSubmit = processing && transfer && (!minor || guardian);
  const busy = createSession.isPending || recordConsent.isPending;

  const submit = async () => {
    setError(null);
    try {
      let sessionId = existingId ?? createdId;
      if (!sessionId) {
        const session = await createSession.mutateAsync(label.trim() || null);
        sessionId = session.id;
        setCreatedId(sessionId);
      }
      await recordConsent.mutateAsync({
        sessionId,
        body: {
          noticeVersion: notice.data.version,
          processingConsent: processing,
          foreignTransferConsent: transfer,
          extendedRetentionConsent: extended,
          isMinor: minor,
          guardianConsent: minor ? guardian : null,
          method,
        },
      });
      void navigate(`/prova/${sessionId}/fotograf`, { replace: true });
    } catch (e) {
      setError(errorMessage(e instanceof ApiError ? e.code : 'UNKNOWN'));
    }
  };

  return (
    <main className="sp-page sp-page--with-bar">
      <FlowHeader title="Yeni prova" back="/" />
      <div className="sp-content">
        <h1 className="sp-title">{notice.data.title}</h1>
        <p className="sp-lead">Fotoğraf çekmeden önce müşteriyi bilgilendirin ve onayını alın.</p>

        <section className={classes.notice} aria-label="Bilgilendirme metni">
          {notice.data.paragraphs.map((paragraph) => (
            <p key={paragraph}>{paragraph}</p>
          ))}
          <p className={classes.retention}>
            <ShieldIcon size={16} /> {extended ? 'Ek onayla fotoğraflar en fazla 30 gün saklanır.' : notice.data.retentionNote}
          </p>
        </section>

        <Stack gap="md" mt="lg">
          <TextInput
            label="Müşteri takma adı (isteğe bağlı)"
            description="Yalnızca bu ekranlarda görünür; yapay zekâ sağlayıcısına gönderilmez."
            placeholder="Örn. Zeynep H."
            maxLength={40}
            value={label}
            onChange={(e) => setLabel(e.currentTarget.value)}
            disabled={Boolean(existingId)}
          />

          <fieldset className={classes.fieldset}>
            <legend className="sp-sr-only">Müşteri onayları</legend>
            <Stack gap="sm">
              <Checkbox
                size="md"
                checked={processing}
                onChange={(e) => setProcessing(e.currentTarget.checked)}
                label="Fotoğrafımın sanal prova için işlenmesini onaylıyorum."
                description="Zorunlu"
              />
              <Checkbox
                size="md"
                checked={transfer}
                onChange={(e) => setTransfer(e.currentTarget.checked)}
                label="Fotoğrafımın yapay zekâ hizmet sağlayıcılarına (yurt dışında olabilir) iletilmesini onaylıyorum."
                description="Zorunlu"
              />
              <Checkbox
                size="md"
                checked={extended}
                onChange={(e) => setExtended(e.currentTarget.checked)}
                label="Fotoğrafımın 24 saat yerine en fazla 30 gün saklanmasını istiyorum."
                description="İsteğe bağlı"
              />
              <Checkbox size="md" checked={minor} onChange={(e) => setMinor(e.currentTarget.checked)} label="Müşteri 18 yaşından küçük." />
              {minor ? (
                <Checkbox
                  size="md"
                  ml="xl"
                  checked={guardian}
                  onChange={(e) => setGuardian(e.currentTarget.checked)}
                  label="Veli / yasal temsilci onayı alındı."
                  description="18 yaş altı için zorunlu"
                />
              ) : null}
            </Stack>
          </fieldset>

          <div>
            <p className={classes.methodLabel}>Onay nasıl alındı?</p>
            <SegmentedControl
              fullWidth
              size="md"
              value={method}
              onChange={(value) => setMethod(value as Method)}
              data={[
                { value: 'CustomerTapped', label: 'Müşteri onayladı' },
                { value: 'StaffConfirmedVerbal', label: 'Sözlü onay alındı' },
              ]}
            />
          </div>

          {error ? <Notice tone="danger">{error}</Notice> : null}
        </Stack>
      </div>

      <div className={classes.barWrap}>
        <Button fullWidth rightSection={<ArrowRightIcon size={18} />} disabled={!canSubmit} loading={busy} onClick={() => void submit()}>
          Onayı Kaydet ve Devam Et
        </Button>
      </div>
    </main>
  );
}
