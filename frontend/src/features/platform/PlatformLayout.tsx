import { Button } from '@mantine/core';
import { Link, Outlet } from 'react-router';
import { useLogout } from '../../shared/api/hooks';
import { useAuth } from '../../shared/auth/AuthContext';
import { Eyebrow, LogoutIcon } from '../../shared/ui';
import classes from './platform.module.css';

/** Platform paneli çerçevesi: yalnızca platform yöneticisi görür (RequirePlatformAdmin). Mağaza şubesi/oturumu yoktur. */
export function PlatformLayout() {
  const { me } = useAuth();
  const logout = useLogout();
  return (
    <main className="sp-page">
      <div className="sp-content">
        <header className={classes.header}>
          <Link to="/platform" className={classes.brand} style={{ textDecoration: 'none' }}>
            <span className={classes.logo}>lâl</span>
            <Eyebrow tone="muted">Platform paneli</Eyebrow>
          </Link>
          <Button variant="subtle" size="compact-md" leftSection={<LogoutIcon size={16} />} onClick={() => logout.mutate()} loading={logout.isPending} aria-label={`Çıkış yap (${me?.email ?? ''})`}>
            Çıkış yap
          </Button>
        </header>
        <Outlet />
      </div>
    </main>
  );
}
