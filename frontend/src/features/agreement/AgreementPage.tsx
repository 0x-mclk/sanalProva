import { Button, Checkbox, Stack } from '@mantine/core';
import { useState } from 'react';
import { Navigate } from 'react-router';
import { ApiError } from '../../shared/api/http';
import { useLogout, useMe } from '../../shared/api/hooks';
import { useAcceptAgreement, useAgreement } from '../../shared/api/platform';
import { useAuth } from '../../shared/auth/AuthContext';
import { errorMessage } from '../../shared/i18n/tr';
import { ErrorState, Eyebrow, LoadingState, Notice } from '../../shared/ui';
import classes from './AgreementPage.module.css';

/** "1. Taraflar ve konu" gibi numaralı satırlar başlık, kalanlar paragraf olarak gösterilir. */
export function AgreementText({ text }: { text: string }) {
  return (
    <div className={classes.text} tabIndex={0} role="region" aria-label="Hizmet sözleşmesi metni">
      {text
        .split('\n')
        .filter((line) => line.trim().length > 0)
        .map((line, index) => (/^\d+\.\s/.test(line) ? <h3 key={index}>{line}</h3> : <p key={index}>{line}</p>))}
    </div>
  );
}

/**
 * İşletme hizmet sözleşmesi (Sanal Prova ↔ işletme). Müşteri fotoğraf/KVKK onayından TAMAMEN ayrıdır: o, her provada müşteriden alınır ve bu ekranla ilgisi yoktur.
 * Yalnızca firmanın Owner'ı kabul eder; diğer personel bekler. Kabul edilmeden mağaza ekranları sunucuda kapalıdır (burası yalnızca yönlendirmedir).
 */
export function AgreementPage() {
  const { isPlatformAdmin, agreementRequired, canAcceptAgreement, me } = useAuth();
  const agreement = useAgreement();
  const accept = useAcceptAgreement();
  const logout = useLogout();
  const [confirmed, setConfirmed] = useState(false);
  // Owner kabul edince diğer personelin ekranı kendiliğinden açılsın: bekleyen kullanıcılar için oturum bilgisi sık yenilenir
  useMe({ refetchInterval: 10_000 });

  if (isPlatformAdmin) return <Navigate to="/platform" replace />;
  if (!agreementRequired) return <Navigate to="/" replace />;

  const current = agreement.data;
  const mismatch = accept.error instanceof ApiError && accept.error.code === 'AGREEMENT_VERSION_MISMATCH';

  return (
    <main className="sp-page">
      <Stack className="sp-content" gap="md" pt="xl" pb="xl">
        <Eyebrow>Hizmet sözleşmesi</Eyebrow>
        <h1 className="sp-title">Sözleşmeyi kabul edin</h1>
        <p className="sp-lead">
          {me?.organizationName} için Sanal Prova hizmetini kullanmaya başlamadan önce işletme hizmet sözleşmesinin firma sahibi (Owner) tarafından kabul edilmesi gerekir.
        </p>

        <Notice tone="info">
          Bu sözleşme <strong>işletmeniz ile Sanal Prova</strong> arasındadır. Müşterinin fotoğraf (KVKK) onayı ayrıdır ve her provada müşteriden ayrıca alınır; birbirinin yerine geçmez.
        </Notice>

        {agreement.isLoading ? <LoadingState label="Sözleşme yükleniyor" /> : null}
        {agreement.isError ? <ErrorState error={agreement.error} onRetry={() => void agreement.refetch()} /> : null}

        {current ? (
          <>
            <h2 style={{ margin: 0 }}>{current.title}</h2>
            <AgreementText text={current.text} />
            <p className={classes.meta}>
              Sürüm {current.version} · SHA-256 {current.textSha256}
            </p>

            {canAcceptAgreement ? (
              <Stack gap="sm">
                <Checkbox label="Sözleşme metnini okudum ve işletme adına kabul ediyorum." checked={confirmed} onChange={(e) => setConfirmed(e.currentTarget.checked)} />
                {accept.isError ? <Notice tone="danger">{errorMessage(accept.error instanceof ApiError ? accept.error.code : 'UNKNOWN')}</Notice> : null}
                <Button
                  fullWidth
                  loading={accept.isPending}
                  disabled={!confirmed || accept.isPending}
                  onClick={() => {
                    if (mismatch) void agreement.refetch();
                    accept.mutate({ version: current.version, textSha256: current.textSha256 });
                  }}
                >
                  Sözleşmeyi kabul et
                </Button>
              </Stack>
            ) : (
              <Notice tone="warning">Sözleşmenin firma sahibi (Owner) tarafından kabul edilmesi bekleniyor. Kabul edildiğinde bu ekran kendiliğinden ilerler; sorun varsa Owner ile görüşün.</Notice>
            )}
          </>
        ) : null}

        <Button variant="subtle" onClick={() => logout.mutate()} loading={logout.isPending}>
          Çıkış yap
        </Button>
      </Stack>
    </main>
  );
}
