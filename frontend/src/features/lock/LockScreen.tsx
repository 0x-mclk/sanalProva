import { Button, PasswordInput, Stack } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { qk, useLogout } from '../../shared/api/hooks';
import { ApiError } from '../../shared/api/http';
import { useUnlock } from '../../shared/api/lock';
import { writeLocked } from '../../shared/auth/lockState';
import { errorMessage } from '../../shared/i18n/tr';
import { Eyebrow, Notice, ShieldIcon } from '../../shared/ui';
import classes from './lock.module.css';

/** Yanlış PIN/parola dışındaki 401 ve PIN_LOCKED_OUT: oturum sunucuda kapanmıştır. */
function isSessionEnded(error: unknown) {
  return error instanceof ApiError && (error.code === 'PIN_LOCKED_OUT' || (error.status === 401 && error.code !== 'INVALID_PIN' && error.code !== 'INVALID_PASSWORD'));
}

/**
 * Kilit ekranı: PIN (veya PIN'i olmayan hesap için hesap parolası). Doğrulama sunucudadır ve hız sınırlıdır; 5 ardışık hatada sunucu tüm oturumları kapatır
 * (bu durumda kilit bayrağı temizlenir ve yönlendirme giriş ekranına gider).
 */
export function LockScreen({ hasPin, onUnlocked }: { hasPin: boolean; onUnlocked: () => void }) {
  const client = useQueryClient();
  const unlock = useUnlock();
  const logout = useLogout();
  const [secret, setSecret] = useState('');

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!secret) return;
    unlock.mutate(hasPin ? { pin: secret } : { password: secret }, {
      onSuccess: () => {
        setSecret('');
        unlock.reset();
        onUnlocked();
      },
      onError: (error) => {
        setSecret('');
        if (isSessionEnded(error)) {
          writeLocked(false);
          void client.resetQueries({ queryKey: qk.me });
        }
      },
    });
  };

  const error = unlock.error;
  const remaining = error instanceof ApiError && typeof error.details.remainingAttempts === 'number' ? error.details.remainingAttempts : null;

  return (
    <main className={classes.page} data-testid="lock-screen">
      <form className={classes.panel} onSubmit={submit} noValidate>
        <Eyebrow tone="muted">Sanal Prova</Eyebrow>
        <h1 className="sp-title">Ekran kilitli</h1>
        <p className="sp-lead">{hasPin ? 'Devam etmek için PIN’inizi girin.' : 'Devam etmek için hesap parolanızı girin.'}</p>
        <Stack gap="md">
          <PasswordInput
            label={hasPin ? 'PIN' : 'Parola'}
            autoFocus
            autoComplete={hasPin ? 'off' : 'current-password'}
            inputMode={hasPin ? 'numeric' : 'text'}
            maxLength={hasPin ? 8 : 128}
            value={secret}
            onChange={(e) => setSecret(e.currentTarget.value)}
          />
          {error ? (
            <Notice tone="danger">
              {error instanceof ApiError ? errorMessage(error.code) : errorMessage('UNKNOWN')}
              {remaining !== null ? ` Kalan hak: ${remaining}.` : ''}
            </Notice>
          ) : null}
          <Button type="submit" fullWidth loading={unlock.isPending} disabled={!secret}>
            Kilidi aç
          </Button>
          <Button variant="subtle" color="dark" fullWidth onClick={() => logout.mutate()} loading={logout.isPending}>
            Çıkış yap
          </Button>
        </Stack>
        <p className={classes.note}>
          <ShieldIcon size={16} /> Müşteri verileri kilit açılana kadar gösterilmez.
        </p>
      </form>
    </main>
  );
}
