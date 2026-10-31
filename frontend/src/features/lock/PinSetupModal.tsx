import { Button, Modal, PasswordInput, Stack } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { qk } from '../../shared/api/hooks';
import { ApiError } from '../../shared/api/http';
import { useSetPin } from '../../shared/api/lock';
import { errorMessage } from '../../shared/i18n/tr';
import { Notice } from '../../shared/ui';

/** Ekran kilidi PIN'i belirleme/değiştirme: hesap parolası gerekir (açık bırakılmış tablet PIN'i değiştiremesin). */
export function PinSetupModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const client = useQueryClient();
  const setPin = useSetPin();
  const [password, setPassword] = useState('');
  const [pin, setPinValue] = useState('');
  const [again, setAgain] = useState('');
  const [done, setDone] = useState(false);

  const close = () => {
    setPassword('');
    setPinValue('');
    setAgain('');
    setDone(false);
    setPin.reset();
    onClose();
  };
  const mismatch = again.length > 0 && pin !== again;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (pin !== again) return;
    setPin.mutate(
      { currentPassword: password, pin },
      {
        onSuccess: () => {
          setPassword('');
          setPinValue('');
          setAgain('');
          setDone(true);
        },
        onError: (error) => {
          setPassword('');
          if (error instanceof ApiError && error.code === 'PIN_LOCKED_OUT') void client.resetQueries({ queryKey: qk.me });
        },
      },
    );
  };

  return (
    <Modal opened={opened} onClose={close} title="Ekran kilidi PIN’i" centered>
      {done ? (
        <Stack gap="md">
          <Notice tone="success">PIN kaydedildi. Ekran boşta kalınca bu PIN ile açılır.</Notice>
          <Button onClick={close}>Tamam</Button>
        </Stack>
      ) : (
        <form onSubmit={submit} noValidate>
          <Stack gap="md">
            <p className="sp-lead" style={{ margin: 0 }}>
              4–8 rakam seçin; 1234 veya 1111 gibi kolay tahmin edilenler kabul edilmez.
            </p>
            <PasswordInput label="Hesap parolanız" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.currentTarget.value)} />
            <PasswordInput label="Yeni PIN" inputMode="numeric" autoComplete="off" maxLength={8} value={pin} onChange={(e) => setPinValue(e.currentTarget.value)} />
            <PasswordInput
              label="PIN (tekrar)"
              inputMode="numeric"
              autoComplete="off"
              maxLength={8}
              value={again}
              error={mismatch ? 'PIN’ler aynı değil.' : undefined}
              onChange={(e) => setAgain(e.currentTarget.value)}
            />
            {setPin.error ? <Notice tone="danger">{setPin.error instanceof ApiError ? errorMessage(setPin.error.code) : errorMessage('UNKNOWN')}</Notice> : null}
            <Button type="submit" loading={setPin.isPending} disabled={!password || pin.length < 4 || pin !== again}>
              PIN’i kaydet
            </Button>
          </Stack>
        </form>
      )}
    </Modal>
  );
}
