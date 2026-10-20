import { Button, Checkbox, PasswordInput, Stack, TextInput } from '@mantine/core';
import { useState, type FormEvent } from 'react';
import { Navigate, useLocation, useNavigate } from 'react-router';
import { useLogin } from '../../shared/api/hooks';
import { ApiError } from '../../shared/api/http';
import { useAuth } from '../../shared/auth/AuthContext';
import { errorMessage } from '../../shared/i18n/tr';
import { Eyebrow, Notice, ShieldIcon } from '../../shared/ui';
import classes from './LoginPage.module.css';

export function LoginPage() {
  const { isAuthenticated } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const login = useLogin();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [rememberMe, setRememberMe] = useState(false);

  const from = (location.state as { from?: string } | null)?.from ?? '/';
  if (isAuthenticated) {
    return <Navigate to={from} replace />;
  }

  const submit = (event: FormEvent) => {
    event.preventDefault();
    login.mutate({ email: email.trim(), password, rememberMe }, { onSuccess: () => void navigate(from, { replace: true }) });
  };

  const error = login.error instanceof ApiError ? errorMessage(login.error.code) : login.error ? errorMessage('UNKNOWN') : null;

  return (
    <main className={classes.page}>
      <div className={classes.panel}>
        <div className={classes.brand}>
          <span className={classes.logo}>lâl</span>
          <Eyebrow>Sanal Prova</Eyebrow>
        </div>
        <h1 className="sp-title">Personel girişi</h1>
        <p className="sp-lead">Müşteri provalarını başlatmak için hesabınızla giriş yapın.</p>

        <form onSubmit={submit} noValidate>
          <Stack gap="md">
            <TextInput label="E-posta" type="email" autoComplete="username" inputMode="email" value={email} onChange={(e) => setEmail(e.currentTarget.value)} required />
            <PasswordInput label="Parola" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.currentTarget.value)} required />
            <Checkbox label="Bu cihazda oturumu açık tut" checked={rememberMe} onChange={(e) => setRememberMe(e.currentTarget.checked)} />
            {error ? <Notice tone="danger">{error}</Notice> : null}
            <Button type="submit" fullWidth loading={login.isPending} disabled={!email.trim() || !password}>
              Giriş yap
            </Button>
          </Stack>
        </form>

        <p className={classes.note}>
          <ShieldIcon size={16} /> Müşteri fotoğrafları güvenle, yalnızca prova için kullanılır.
        </p>
      </div>
    </main>
  );
}
