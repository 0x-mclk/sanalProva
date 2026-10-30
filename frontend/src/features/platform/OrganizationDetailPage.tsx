import { Badge, Button, Stack } from '@mantine/core';
import { useState } from 'react';
import { Link, useParams } from 'react-router';
import { ApiError } from '../../shared/api/http';
import { reissueOwnerLink, useOrganizationDetail } from '../../shared/api/platform';
import type { SetupLinkResponse } from '../../shared/api/types';
import { errorMessage } from '../../shared/i18n/tr';
import { ConfirmDialog, ErrorState, LoadingState, Notice } from '../../shared/ui';
import { useRef } from 'react';
import classes from './platform.module.css';
import { SetupLinkReveal } from './SetupLinkReveal';

const limitText = (value: number | null | undefined) => (value == null ? 'platform tavanı' : `${value} $`);
const dateText = (iso: string) => new Intl.DateTimeFormat('tr-TR', { dateStyle: 'long', timeStyle: 'short' }).format(new Date(iso));

/**
 * `/platform/:id`: işletme özeti (yalnızca yönetim bilgisi; müşteri verisi yok) ve Owner kurulum bağlantısını yeniden üretme.
 * Yeniden üretme çift tıklamaya karşı istemci tarafında kilitlidir (ref + devre dışı düğme + onay penceresi). Bu, sunucu tarafı idempotency YERİNE GEÇMEZ:
 * bu uç noktada sunucu istek anahtarı tutmaz; ağ yeniden denemesi veya iki sekme iki bağlantı üretip birincisini iptal edebilir (bkz. docs/14 §6).
 */
export function OrganizationDetailPage() {
  const { id } = useParams();
  const query = useOrganizationDetail(id);
  const [confirming, setConfirming] = useState(false);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [link, setLink] = useState<SetupLinkResponse | null>(null);
  const inFlight = useRef(false);

  const reissue = async () => {
    if (inFlight.current || !id) return; // çift tıklama koruması (yalnızca istemci tarafı)
    inFlight.current = true;
    setSending(true);
    setError(null);
    try {
      setLink(await reissueOwnerLink(id));
      setConfirming(false);
    } catch (caught) {
      setError(errorMessage(caught instanceof ApiError ? caught.code : 'UNKNOWN'));
      setConfirming(false);
    } finally {
      inFlight.current = false;
      setSending(false);
    }
  };

  if (query.isLoading) return <LoadingState label="İşletme yükleniyor" />;
  if (query.isError) return <ErrorState error={query.error} onRetry={() => void query.refetch()} />;
  const organization = query.data;
  if (!organization) return null;

  const pendingOwner = organization.owners.find((owner) => owner.setupPending);

  return (
    <Stack gap="md" mt="md" pb="xl">
      <Link to="/platform">← İşletmeler</Link>
      <h1 className="sp-title" style={{ margin: 0 }}>
        {organization.name}
      </h1>
      <div className={classes.badges}>
        <Badge color={pendingOwner ? 'yellow' : 'green'} variant="light">
          {pendingOwner ? 'Kurulum: şifre bekleniyor' : 'Kurulum: tamamlandı'}
        </Badge>
        <Badge color={organization.agreementAccepted ? 'green' : 'yellow'} variant="light">
          {organization.agreementAccepted ? 'Sözleşme: kabul edildi' : 'Sözleşme: bekliyor'}
        </Badge>
        <Badge color={organization.paidAiEnabled ? 'bordo' : 'gray'} variant="light">
          {organization.paidAiEnabled ? 'Ücretli AI: açık' : 'Ücretli AI: kapalı'}
        </Badge>
      </div>

      <dl className={classes.definition}>
        <dt>Kısa ad</dt>
        <dd>{organization.slug}</dd>
        <dt>Oluşturulma</dt>
        <dd>{dateText(organization.createdAt)}</dd>
        <dt>Sözleşme sürümü</dt>
        <dd>
          {organization.agreementVersion}
          {organization.agreementAcceptedAt ? ` · kabul: ${dateText(organization.agreementAcceptedAt)}` : ''}
        </dd>
        <dt>AI bütçe sınırı</dt>
        <dd>
          günlük {limitText(organization.dailyBudgetUsd)} · aylık {limitText(organization.monthlyBudgetUsd)} · iş başı {limitText(organization.maxJobCostUsd)}
        </dd>
        <dt>Kredi</dt>
        <dd>
          bakiye {organization.creditBalance} · bekleyen {organization.creditReserved} (yükleme yalnızca operatör komutuyla)
        </dd>
        <dt>Şubeler</dt>
        <dd>{organization.stores.map((store) => `${store.name} (${store.code})`).join(', ')}</dd>
        <dt>Firma sahibi</dt>
        <dd>{organization.owners.map((owner) => `${owner.displayName} — ${owner.email}${owner.setupPending ? ' (şifre bekleniyor)' : ''}`).join('; ')}</dd>
      </dl>

      <Notice tone="info">
        Ücretli yapay zekâ yetkisi bu ekrandan değiştirilemez; yalnızca sunucudaki operatör komutlarıyla (paid-ai, budget, credits; denetime yazılır) değişir; AI bütçe sınırları da öyle. Müşteri fotoğraf, oturum ve katalog verisi bu panelde görünmez.
      </Notice>

      {link ? (
        <SetupLinkReveal
          link={link.setupLink}
          expiresAt={link.setupLinkExpiresAt}
          onClose={() => {
            setLink(null); // bağlantı bellekten silinir
            void query.refetch();
          }}
        />
      ) : null}

      {error ? <Notice tone="danger">{error}</Notice> : null}

      {pendingOwner && !link ? (
        <Button variant="default" onClick={() => setConfirming(true)} disabled={sending}>
          Kurulum bağlantısını yeniden üret
        </Button>
      ) : null}
      {!pendingOwner ? <Notice tone="success">Firma sahibi şifresini belirlemiş; yeni kurulum bağlantısı gerekmez.</Notice> : null}

      <ConfirmDialog
        opened={confirming}
        title="Bağlantıyı yeniden üret"
        confirmLabel="Yeniden üret"
        loading={sending}
        onConfirm={() => void reissue()}
        onClose={() => setConfirming(false)}
      >
        Önceki açık kurulum bağlantısı iptal edilir ve yeni bir tek kullanımlık bağlantı üretilir. Yeni bağlantı yalnızca bir kez gösterilir.
      </ConfirmDialog>
    </Stack>
  );
}
