import { Button, PasswordInput, Stack } from '@mantine/core';
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import { setupPassword } from '../../shared/api/platform';
import { ApiError } from '../../shared/api/http';
import { errorMessage, passwordPolicyTexts } from '../../shared/i18n/tr';
import { Eyebrow, Notice, ShieldIcon } from '../../shared/ui';
import classes from '../auth/LoginPage.module.css';
import { validateNewPassword } from './password';
import { discardSetupToken, takeSetupToken } from './setupToken';

type Status = 'form' | 'sending' | 'done' | 'invalid';

/**
 * Tek kullanımlık bağlantıyla şifre belirleme (anonim ekran). Token adres çubuğundan okunup hemen silinir ve yalnızca bellekte tutulur;
 * kullanılmış/iptal/süresi dolmuş/bilinmeyen bağlantılar TEK ve aynı güvenli mesajı gösterir (neden açıklanmaz).
 */
export function SetupPasswordPage() {
  // Aynı sekmede başka bir bağlantı yapıştırılırsa tarayıcı bunu sayfa yeniden yüklemeden "hash değişimi" sayar (router bunu yakalamaz): doğrudan `hashchange` dinlenir,
  // sayfa durumu sıfırdan başlar ve yeni token yakalanır. (`replaceState` ile fragment'ı silmek bu olayı tetiklemez.)
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    const onHashChange = () => setGeneration((value) => value + 1);
    window.addEventListener('hashchange', onHashChange);
    return () => window.removeEventListener('hashchange', onHashChange);
  }, []);
  return <SetupPasswordForm key={generation} />;
}

function SetupPasswordForm() {
  const [token, setToken] = useState<string | null>(takeSetupToken);
  const [password, setPassword] = useState('');
  const [again, setAgain] = useState('');
  const [status, setStatus] = useState<Status>(token ? 'form' : 'invalid');
  const [problems, setProblems] = useState<string[]>([]);
  const [serverError, setServerError] = useState<string | null>(null);
  const inFlight = useRef(false);

  useEffect(() => () => discardSetupToken(), []);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (inFlight.current || !token) return;
    const found = validateNewPassword(password, again);
    setProblems(found);
    setServerError(null);
    if (found.length > 0) return;

    inFlight.current = true;
    setStatus('sending');
    try {
      await setupPassword(token, password);
      // Başarı: token ve şifreler bellekten de silinir
      discardSetupToken();
      setToken(null);
      setPassword('');
      setAgain('');
      setStatus('done');
    } catch (error) {
      if (error instanceof ApiError && error.code === 'SETUP_LINK_INVALID') {
        discardSetupToken();
        setToken(null);
        setStatus('invalid');
      } else if (error instanceof ApiError && error.code === 'PASSWORD_POLICY') {
        const codes = Array.isArray(error.details.errors) ? (error.details.errors as string[]) : [];
        setProblems(codes.length > 0 ? passwordPolicyTexts(codes) : [errorMessage('PASSWORD_POLICY')]);
        setStatus('form');
      } else {
        setServerError(errorMessage(error instanceof ApiError ? error.code : 'UNKNOWN'));
        setStatus('form');
      }
    } finally {
      inFlight.current = false;
    }
  };

  return (
    <main className={classes.page}>
      <div className={classes.panel}>
        <div className={classes.brand}>
          <span className={classes.logo}>lâl</span>
          <Eyebrow>Sanal Prova</Eyebrow>
        </div>

        {status === 'done' ? (
          <Stack gap="md" mt="md">
            <h1 className="sp-title">Şifreniz belirlendi</h1>
            <Notice tone="success">Artık e-posta adresiniz ve yeni şifrenizle giriş yapabilirsiniz.</Notice>
            <Button component={Link} to="/giris" fullWidth>
              Giriş yap
            </Button>
          </Stack>
        ) : status === 'invalid' ? (
          <Stack gap="md" mt="md">
            <h1 className="sp-title">Bağlantı kullanılamıyor</h1>
            <Notice tone="danger">{errorMessage('SETUP_LINK_INVALID')}</Notice>
            <Button component={Link} to="/giris" variant="default" fullWidth>
              Giriş ekranına git
            </Button>
          </Stack>
        ) : (
          <>
            <h1 className="sp-title">Şifrenizi belirleyin</h1>
            <p className="sp-lead">Bu bağlantı tek kullanımlıktır. Hesabınız için bir şifre belirleyin.</p>
            <form onSubmit={(event) => void submit(event)} noValidate>
              <Stack gap="md">
                <PasswordInput label="Yeni şifre" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.currentTarget.value)} required />
                <PasswordInput label="Şifre (tekrar)" autoComplete="new-password" value={again} onChange={(e) => setAgain(e.currentTarget.value)} required />
                <p className={classes.note}>En az 10 karakter; büyük harf, küçük harf ve rakam içermeli.</p>
                {problems.length > 0 ? (
                  <Notice tone="danger">
                    <ul style={{ margin: 0, paddingInlineStart: 18 }}>
                      {problems.map((problem) => (
                        <li key={problem}>{problem}</li>
                      ))}
                    </ul>
                  </Notice>
                ) : null}
                {serverError ? <Notice tone="danger">{serverError}</Notice> : null}
                <Button type="submit" fullWidth loading={status === 'sending'} disabled={!password || !again}>
                  Şifreyi belirle
                </Button>
              </Stack>
            </form>
            <p className={classes.note}>
              <ShieldIcon size={16} /> Bağlantıyı kimseyle paylaşmayın.
            </p>
          </>
        )}
      </div>
    </main>
  );
}
