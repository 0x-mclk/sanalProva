import { Button, Modal, Stack, Text } from '@mantine/core';
import { useState } from 'react';
import { Link } from 'react-router';
import {
  displayModeLabel,
  useAttachDisplay,
  useClearDisplay,
  useDetachDisplay,
  useDisplays,
  useSetDisplayState,
  type DisplayDto,
} from '../../shared/api/displays';
import { ApiError } from '../../shared/api/http';
import { errorMessage } from '../../shared/i18n/tr';
import { TvIcon } from '../../shared/ui';
import classes from './TvControls.module.css';

type Feedback = { tone: 'ok' | 'error' | 'info'; text: string; link?: boolean };

const messageOf = (error: unknown) => errorMessage(error instanceof ApiError ? error.code : undefined);

/**
 * "TV'ye gönder" / "TV'de karşılaştır" düğmesi. Provanın bağlı olduğu TV varsa doğrudan gönderir; yoksa şubedeki TV'leri sunar
 * (tek boş ve çevrimiçi TV varsa otomatik bağlar). Bağlı TV yoksa nedenini açıkça söyler. Yetki ve oturum/TV ilişkisi sunucuda doğrulanır.
 */
export function TvSendButton({
  sessionId,
  mode,
  imageIds,
  label,
  disabled = false,
}: {
  sessionId: string;
  mode: 'Single' | 'Compare';
  imageIds: string[];
  label: string;
  disabled?: boolean;
}) {
  const displays = useDisplays();
  const attach = useAttachDisplay();
  const setState = useSetDisplayState();
  const [picker, setPicker] = useState(false);
  const [feedback, setFeedback] = useState<Feedback | null>(null);
  const busy = attach.isPending || setState.isPending;

  const send = async (display: DisplayDto) => {
    try {
      await setState.mutateAsync({ id: display.id, mode, imageIds });
      setFeedback({ tone: 'ok', text: `${display.name}: ${mode === 'Compare' ? 'karşılaştırma' : 'sonuç'} TV'de gösteriliyor.` });
    } catch (error) {
      setFeedback({ tone: 'error', text: messageOf(error) });
    }
  };

  const attachAndSend = async (display: DisplayDto, force: boolean) => {
    try {
      await attach.mutateAsync({ id: display.id, sessionId, force });
      setPicker(false);
      await send(display);
    } catch (error) {
      setFeedback({ tone: 'error', text: messageOf(error) });
    }
  };

  const onClick = async () => {
    setFeedback(null);
    const list = (await displays.refetch()).data ?? displays.data ?? [];
    if (list.length === 0) {
      setFeedback({ tone: 'info', text: 'Bu şubede eşleştirilmiş TV yok. TV bağlantısı ekranından bir TV eşleştirin.', link: true });
      return;
    }
    const attached = list.find((d) => d.sessionId === sessionId);
    if (attached) {
      if (!attached.online) {
        setFeedback({ tone: 'error', text: `${attached.name} şu anda çevrimdışı. TV'nin açık ve internete bağlı olduğundan emin olun.` });
        return;
      }
      await send(attached);
      return;
    }
    const free = list.filter((d) => d.online && !d.sessionId);
    if (list.length === 1 && free.length === 1) {
      await attachAndSend(free[0]!, false);
      return;
    }
    if (list.every((d) => !d.online)) {
      setFeedback({ tone: 'error', text: 'Eşleştirilmiş TV\'lerin hiçbiri çevrimiçi değil. TV\'nin açık olduğundan emin olun.' });
      return;
    }
    setPicker(true);
  };

  return (
    <div className={classes.control}>
      <Button variant="default" size="md" leftSection={<TvIcon size={18} />} loading={busy} disabled={disabled || imageIds.length !== (mode === 'Compare' ? 2 : 1)} onClick={() => void onClick()}>
        {label}
      </Button>
      {feedback ? (
        <p className={classes.feedback} data-tone={feedback.tone} role={feedback.tone === 'error' ? 'alert' : 'status'}>
          {feedback.text}{' '}
          {feedback.link ? (
            <Link to="/tv-cihazlar" className={classes.link}>
              TV bağlantısı
            </Link>
          ) : null}
        </p>
      ) : null}

      <Modal opened={picker} onClose={() => setPicker(false)} title="Hangi TV'de gösterilsin?" centered radius="lg" padding="lg">
        <Stack gap="sm">
          {(displays.data ?? []).map((display) => {
            const here = display.sessionId === sessionId;
            const takeover = Boolean(display.sessionId) && !here;
            return (
              <button
                key={display.id}
                type="button"
                className={classes.option}
                disabled={!display.online || busy}
                onClick={() => void (here ? send(display).then(() => setPicker(false)) : attachAndSend(display, takeover))}
              >
                <span className={classes.optionName}>
                  <TvIcon size={18} /> {display.name}
                </span>
                <span className={classes.optionMeta}>
                  {!display.online ? 'Çevrimdışı' : here ? 'Bu provaya bağlı' : takeover ? `Başka provaya bağlı (${display.sessionCode ?? ''}) — devral ve gönder` : 'Boşta · Bağlı'}
                </span>
              </button>
            );
          })}
          <Text size="xs" c="dimmed">
            Bir TV'ye yalnızca bir prova bağlıdır. Devralınan TV'de önceki provanın görüntüsü hemen kaldırılır.
          </Text>
        </Stack>
      </Modal>
    </div>
  );
}

/**
 * Provanın bağlı olduğu TV'nin durumu ve eylemleri: ekranı temizle, TV bağlantısını kes.
 * Bağlı TV yoksa hiçbir şey çizmez (gönder düğmesi nedenini zaten söyler).
 */
export function TvSessionPanel({ sessionId }: { sessionId: string }) {
  const displays = useDisplays();
  const clear = useClearDisplay();
  const detach = useDetachDisplay();
  const [error, setError] = useState<string | null>(null);
  const attached = displays.data?.find((d) => d.sessionId === sessionId);
  if (!attached) {
    return null;
  }

  const run = async (action: () => Promise<unknown>) => {
    setError(null);
    try {
      await action();
    } catch (e) {
      setError(messageOf(e));
    }
  };

  return (
    <section className={classes.panel} aria-label="TV durumu">
      <p className={classes.panelTitle}>
        <span className={classes.dot} data-online={attached.online} aria-hidden="true" />
        {attached.name} · {attached.online ? 'Bağlı' : 'Çevrimdışı'}
      </p>
      <p className={classes.panelMeta}>{displayModeLabel[attached.mode]}</p>
      <div className={classes.panelActions}>
        <Button size="xs" variant="default" disabled={attached.mode === 'Idle'} loading={clear.isPending} onClick={() => void run(() => clear.mutateAsync(attached.id))}>
          Ekranı temizle
        </Button>
        <Button size="xs" variant="subtle" color="dark" loading={detach.isPending} onClick={() => void run(() => detach.mutateAsync(attached.id))}>
          TV bağlantısını kes
        </Button>
      </div>
      {error ? (
        <p className={classes.feedback} data-tone="error" role="alert">
          {error}
        </p>
      ) : null}
    </section>
  );
}
