import { Button, TextInput } from '@mantine/core';
import { useState, type FormEvent } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import {
  displayModeLabel,
  useClaimDisplay,
  useClearDisplay,
  useDetachDisplay,
  useDisplays,
  useRenameDisplay,
  useRevokeDisplay,
  type DisplayDto,
} from '../../shared/api/displays';
import { ApiError } from '../../shared/api/http';
import { useAuth } from '../../shared/auth/AuthContext';
import { errorMessage } from '../../shared/i18n/tr';
import { AppBar, ConfirmDialog, EmptyState, ErrorState, Eyebrow, LoadingState, Notice, TvBadge, TvIcon } from '../../shared/ui';
import classes from './DisplaysPage.module.css';

const errorText = (error: unknown) => errorMessage(error instanceof ApiError ? error.code : undefined);

/**
 * TV bağlantısı: şubenin eşleştirilmiş TV'leri, durumları ve yönetimi. Eşleştirme TV ekranındaki 6 haneli geçici kodla yapılır
 * (yalnızca yönetici). Tüm yetkiler sunucuda doğrulanır; bu ekran yalnızca gösterir.
 */
export function DisplaysPage() {
  const navigate = useNavigate();
  const { activeStore } = useAuth();
  const displays = useDisplays();
  const isManager = activeStore?.role === 'Manager' || activeStore?.role === 'Owner';

  return (
    <main className="sp-page">
      <AppBar title="TV bağlantısı" onBack={() => void navigate('/')} trailing={<TvBadge />} />
      <div className="sp-content">
        <h1 className="sp-title">Büyük ekranda birlikte karar verin.</h1>
        <p className="sp-lead">{activeStore?.name} şubesindeki TV&apos;ler. Sonuçlar yalnızca bağladığınız provaya ait TV&apos;de görünür.</p>

        {isManager ? <PairForm /> : <Notice tone="info">Yeni TV eşleştirmek için yönetici yetkisi gerekir. Eşleştirilmiş TV&apos;leri aşağıdan kullanabilirsiniz.</Notice>}

        {displays.isLoading ? <LoadingState label="TV'ler yükleniyor" /> : null}
        {displays.isError ? <ErrorState error={displays.error} onRetry={() => void displays.refetch()} /> : null}
        {displays.data && displays.data.length === 0 ? (
          <EmptyState
            title="Henüz eşleştirilmiş TV yok"
            description="TV'nin tarayıcısında bu uygulamanın /tv adresini açın. Ekranda 6 haneli bir kod görünür; yönetici o kodu buraya girer."
            action={<TvIcon size={28} className={classes.icon} />}
          />
        ) : null}

        <ul className={classes.list}>
          {(displays.data ?? []).map((display) => (
            <li key={display.id}>
              <DisplayCard display={display} isManager={isManager} />
            </li>
          ))}
        </ul>

        <Notice tone="info">
          Güvenlik: kod tek kullanımlıktır ve 5 dakika geçerlidir. TV yalnızca kendi provasının görsellerini alabilir; fotoğraf silindiğinde, prova kapatıldığında veya bağlantı kesildiğinde ekran hemen temizlenir.
        </Notice>
      </div>
    </main>
  );
}

function PairForm() {
  const [params, setParams] = useSearchParams();
  const [code, setCode] = useState(params.get('kod') ?? '');
  const [name, setName] = useState('');
  const [message, setMessage] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null);
  const claim = useClaimDisplay();

  const digits = code.replace(/\D/g, '');
  const valid = digits.length === 6 && name.trim().length > 0;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    setMessage(null);
    claim.mutate(
      { code: digits, name: name.trim() },
      {
        onSuccess: (display) => {
          setMessage({ tone: 'ok', text: `${display.name} eşleştirildi.` });
          setCode('');
          setName('');
          if (params.has('kod')) setParams({}, { replace: true });
        },
        onError: (error) => setMessage({ tone: 'error', text: errorText(error) }),
      },
    );
  };

  return (
    <form className={classes.pair} onSubmit={submit} aria-labelledby="pair-title">
      <Eyebrow tone="muted">Yeni TV</Eyebrow>
      <h2 id="pair-title" className={classes.pairTitle}>
        TV&apos;deki kodu girin
      </h2>
      <div className={classes.pairFields}>
        <TextInput
          label="Eşleştirme kodu"
          placeholder="123 456"
          inputMode="numeric"
          autoComplete="off"
          maxLength={7}
          value={code}
          onChange={(e) => setCode(e.currentTarget.value)}
          size="md"
          classNames={{ input: classes.codeInput }}
        />
        <TextInput label="TV adı" placeholder="Prova Odası TV" maxLength={80} value={name} onChange={(e) => setName(e.currentTarget.value)} size="md" />
      </div>
      <Button type="submit" size="md" loading={claim.isPending} disabled={!valid}>
        Eşleştir
      </Button>
      {message ? (
        <p className={classes.message} data-tone={message.tone} role={message.tone === 'error' ? 'alert' : 'status'}>
          {message.text}
        </p>
      ) : null}
    </form>
  );
}

function DisplayCard({ display, isManager }: { display: DisplayDto; isManager: boolean }) {
  const rename = useRenameDisplay();
  const revoke = useRevokeDisplay();
  const clear = useClearDisplay();
  const detach = useDetachDisplay();
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState(display.name);
  const [confirmRevoke, setConfirmRevoke] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const run = (action: Promise<unknown>) => {
    setError(null);
    action.catch((e: unknown) => setError(errorText(e)));
  };

  return (
    <article className={classes.card}>
      <div className={classes.cardHead}>
        <span className={classes.cardIcon}>
          <TvIcon size={22} />
        </span>
        <div className={classes.cardTitle}>
          {editing ? (
            <form
              className={classes.rename}
              onSubmit={(e) => {
                e.preventDefault();
                run(rename.mutateAsync({ id: display.id, name: name.trim() }).then(() => setEditing(false)));
              }}
            >
              <TextInput aria-label="TV adı" value={name} maxLength={80} onChange={(e) => setName(e.currentTarget.value)} size="sm" />
              <Button type="submit" size="xs" loading={rename.isPending} disabled={name.trim().length === 0}>
                Kaydet
              </Button>
              <Button size="xs" variant="subtle" color="dark" onClick={() => setEditing(false)}>
                Vazgeç
              </Button>
            </form>
          ) : (
            <strong className={classes.cardName}>{display.name}</strong>
          )}
          <span className={classes.status} data-online={display.online}>
            <span className={classes.dot} aria-hidden="true" />
            {display.online ? 'Çevrimiçi' : 'Çevrimdışı'}
          </span>
        </div>
      </div>

      <p className={classes.meta}>
        {display.sessionId ? (
          <>
            Prova: <strong>{display.sessionLabel ? `${display.sessionLabel} · ` : ''}{display.sessionCode}</strong> · {displayModeLabel[display.mode]}
          </>
        ) : (
          'Hiçbir provaya bağlı değil. Sonuç ekranından "TV\'ye gönder" ile bağlayabilirsiniz.'
        )}
      </p>

      <div className={classes.actions}>
        <Button size="xs" variant="default" disabled={display.mode === 'Idle'} loading={clear.isPending} onClick={() => run(clear.mutateAsync(display.id))}>
          Ekranı temizle
        </Button>
        {display.sessionId ? (
          <Button size="xs" variant="default" loading={detach.isPending} onClick={() => run(detach.mutateAsync(display.id))}>
            Bağlantıyı kes
          </Button>
        ) : null}
        {isManager ? (
          <>
            <Button size="xs" variant="subtle" color="dark" onClick={() => setEditing(true)}>
              Yeniden adlandır
            </Button>
            <Button size="xs" variant="subtle" color="red" onClick={() => setConfirmRevoke(true)}>
              Eşleştirmeyi kaldır
            </Button>
          </>
        ) : null}
      </div>
      {error ? (
        <p className={classes.message} data-tone="error" role="alert">
          {error}
        </p>
      ) : null}

      <ConfirmDialog
        opened={confirmRevoke}
        title={`${display.name} eşleştirmesi kaldırılsın mı?`}
        confirmLabel="Eşleştirmeyi kaldır"
        danger
        loading={revoke.isPending}
        onClose={() => setConfirmRevoke(false)}
        onConfirm={() => revoke.mutate(display.id, { onSuccess: () => setConfirmRevoke(false), onError: (e) => { setConfirmRevoke(false); setError(errorText(e)); } })}
      >
        TV&apos;nin erişimi hemen iptal edilir ve ekran boşalır. Yeniden kullanmak için TV&apos;de yeni bir kodla eşleştirmeniz gerekir.
      </ConfirmDialog>
    </article>
  );
}
