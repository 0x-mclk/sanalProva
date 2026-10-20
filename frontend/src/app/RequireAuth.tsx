import { Navigate, Outlet, useLocation } from 'react-router';
import { useAuth } from '../shared/auth/AuthContext';
import { DisplaySync } from '../shared/realtime/DisplaySync';
import { EmptyState, LoadingState } from '../shared/ui';

/** Giriş yapılmamışsa giriş ekranına yönlendirir; dönüşte kaldığı yere geri gelinir. */
export function RequireAuth() {
  const { isLoading, isAuthenticated, activeStore } = useAuth();
  const location = useLocation();

  if (isLoading) {
    return (
      <main className="sp-page">
        <LoadingState label="Oturum açılıyor" />
      </main>
    );
  }
  if (!isAuthenticated) {
    return <Navigate to="/giris" replace state={{ from: location.pathname + location.search }} />;
  }
  if (!activeStore) {
    return (
      <main className="sp-page">
        <EmptyState title="Şube atanmamış" description="Hesabınıza henüz bir şube tanımlanmamış. Yöneticinizle iletişime geçin." />
      </main>
    );
  }
  return (
    <>
      <DisplaySync />
      <Outlet />
    </>
  );
}

/** Katalog yönetimi gibi yetkili ekranlar: yetkisiz kullanıcı ana sayfaya döner. */
export function RequireManager() {
  const { canManageCatalog } = useAuth();
  return canManageCatalog ? <Outlet /> : <Navigate to="/" replace />;
}
