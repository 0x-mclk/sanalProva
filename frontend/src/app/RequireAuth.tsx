import { Navigate, Outlet, useLocation } from 'react-router';
import { useAuth } from '../shared/auth/AuthContext';
import { IdleLock } from '../features/lock/IdleLock';
import { DisplaySync } from '../shared/realtime/DisplaySync';
import { EmptyState, LoadingState } from '../shared/ui';

/**
 * Mağaza (personel) ekranları. Sıra önemlidir ve sunucudaki kapıların arayüz yansımasıdır (karar sunucudadır):
 * giriş yok → /giris; platform yöneticisi → /platform (mağaza rolü yoktur); hizmet sözleşmesi kabul edilmedi → /sozlesme; şube yok → bilgi ekranı.
 */
export function RequireAuth() {
  const { isLoading, isAuthenticated, activeStore, isPlatformAdmin, agreementRequired } = useAuth();
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
  if (isPlatformAdmin) {
    return <Navigate to="/platform" replace />;
  }
  if (agreementRequired) {
    return <Navigate to="/sozlesme" replace />;
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
      <IdleLock>
        <Outlet />
      </IdleLock>
    </>
  );
}

/** Yalnızca giriş gerektirir (mağaza/şube koşulu yok): hizmet sözleşmesi ekranı gibi mağaza kapısının ÖNÜNDEKİ sayfalar. */
export function RequireSession() {
  const { isLoading, isAuthenticated } = useAuth();
  const location = useLocation();
  if (isLoading) {
    return (
      <main className="sp-page">
        <LoadingState label="Oturum açılıyor" />
      </main>
    );
  }
  return isAuthenticated ? <Outlet /> : <Navigate to="/giris" replace state={{ from: location.pathname + location.search }} />;
}

/** Platform paneli: yalnızca platform yöneticisi. Diğer kullanıcılar ana sayfaya döner (yetki yapısı gösterilmez); sunucu ayrıca 403 verir. */
export function RequirePlatformAdmin() {
  const { isLoading, isAuthenticated, isPlatformAdmin } = useAuth();
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
  return isPlatformAdmin ? <Outlet /> : <Navigate to="/" replace />;
}

/** Katalog yönetimi gibi yetkili ekranlar: yetkisiz kullanıcı ana sayfaya döner. */
export function RequireManager() {
  const { canManageCatalog } = useAuth();
  return canManageCatalog ? <Outlet /> : <Navigate to="/" replace />;
}

/** Şube yönetimi: yalnızca firma sahibi (sunucu da Owner rolünü doğrular). */
export function RequireOwner() {
  const { isOwner } = useAuth();
  return isOwner ? <Outlet /> : <Navigate to="/" replace />;
}

/** Personel yönetimi: Manager ve Owner (kapsam sunucuda hesaplanır). */
export function RequireStaffManager() {
  const { canManageStaff } = useAuth();
  return canManageStaff ? <Outlet /> : <Navigate to="/" replace />;
}
