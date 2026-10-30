import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ThemeProvider } from '../shared/theme/ThemeProvider';
import { RequireAuth, RequirePlatformAdmin, RequireSession } from './RequireAuth';

type AuthStub = {
  isLoading: boolean;
  isAuthenticated: boolean;
  activeStore: { id: string } | undefined;
  isPlatformAdmin: boolean;
  agreementRequired: boolean;
};

let auth: AuthStub;

vi.mock('../shared/auth/AuthContext', () => ({ useAuth: () => auth }));
vi.mock('../shared/realtime/DisplaySync', () => ({ DisplaySync: () => null }));

const base: AuthStub = { isLoading: false, isAuthenticated: true, activeStore: { id: 's1' }, isPlatformAdmin: false, agreementRequired: false };

function renderAt(path: string) {
  return render(
    <ThemeProvider>
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/giris" element={<p>GIRIS</p>} />
        <Route path="/platform" element={<p>PLATFORM</p>} />
        <Route path="/sozlesme" element={<p>SOZLESME</p>} />
        <Route element={<RequireAuth />}>
          <Route path="/" element={<p>MAGAZA</p>} />
        </Route>
        <Route element={<RequireSession />}>
          <Route path="/oturum-sayfasi" element={<p>OTURUM</p>} />
        </Route>
        <Route element={<RequirePlatformAdmin />}>
          <Route path="/platform-ic" element={<p>PLATFORM-IC</p>} />
        </Route>
      </Routes>
    </MemoryRouter>
    </ThemeProvider>,
  );
}

beforeEach(() => {
  auth = { ...base };
});

describe('mağaza kapısı (RequireAuth)', () => {
  it('giriş yoksa giriş ekranına gider', () => {
    auth = { ...base, isAuthenticated: false };
    renderAt('/');
    expect(screen.getByText('GIRIS')).toBeInTheDocument();
  });

  it('platform yöneticisi mağaza ekranı yerine /platform paneline yönlenir', () => {
    auth = { ...base, isPlatformAdmin: true, activeStore: undefined, agreementRequired: true };
    renderAt('/');
    expect(screen.getByText('PLATFORM')).toBeInTheDocument();
  });

  it('hizmet sözleşmesi kabul edilmemişse mağaza ekranları açılmaz, /sozlesme gösterilir', () => {
    auth = { ...base, agreementRequired: true };
    renderAt('/');
    expect(screen.getByText('SOZLESME')).toBeInTheDocument();
    expect(screen.queryByText('MAGAZA')).not.toBeInTheDocument();
  });

  it('sözleşme kabul edilmiş ve şube varsa mağaza ekranı açılır', () => {
    renderAt('/');
    expect(screen.getByText('MAGAZA')).toBeInTheDocument();
  });

  it('şubesi olmayan kullanıcı bilgi ekranı görür', () => {
    auth = { ...base, activeStore: undefined };
    renderAt('/');
    expect(screen.getByText('Şube atanmamış')).toBeInTheDocument();
  });
});

describe('platform paneli (RequirePlatformAdmin)', () => {
  it('platform yöneticisi içeriği görür', () => {
    auth = { ...base, isPlatformAdmin: true };
    renderAt('/platform-ic');
    expect(screen.getByText('PLATFORM-IC')).toBeInTheDocument();
  });

  it('yetkisiz personel platform ekranlarını göremez; ana sayfaya döner', () => {
    renderAt('/platform-ic');
    expect(screen.queryByText('PLATFORM-IC')).not.toBeInTheDocument();
    expect(screen.getByText('MAGAZA')).toBeInTheDocument();
  });

  it('girişsiz kullanıcı giriş ekranına gider', () => {
    auth = { ...base, isAuthenticated: false };
    renderAt('/platform-ic');
    expect(screen.getByText('GIRIS')).toBeInTheDocument();
  });
});

describe('RequireSession', () => {
  it('yalnızca giriş ister (sözleşme ekranı gibi mağaza kapısı öncesi sayfalar)', () => {
    auth = { ...base, agreementRequired: true, activeStore: undefined };
    renderAt('/oturum-sayfasi');
    expect(screen.getByText('OTURUM')).toBeInTheDocument();
  });

  it('girişsizse giriş ekranına gider', () => {
    auth = { ...base, isAuthenticated: false };
    renderAt('/oturum-sayfasi');
    expect(screen.getByText('GIRIS')).toBeInTheDocument();
  });
});
