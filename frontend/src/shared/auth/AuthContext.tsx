import { createContext, useCallback, useContext, useLayoutEffect, useMemo, useState, type ReactNode } from 'react';
import { useMe } from '../api/hooks';
import { setActiveStoreId } from '../api/http';
import type { MeResponse, StoreAccessDto } from '../api/types';

const STORE_KEY = 'sp.activeStore';

type AuthState = {
  me: MeResponse | undefined;
  isLoading: boolean;
  isAuthenticated: boolean;
  activeStore: StoreAccessDto | undefined;
  setActiveStore: (id: string) => void;
  /** Katalog yönetimi: firmadaki herhangi bir şubede Manager veya Owner rolü. */
  canManageCatalog: boolean;
  /** Personel yönetimi: Manager+ (kapsam sunucuda hesaplanır). */
  canManageStaff: boolean;
  /** Şube yönetimi ve firma düzeyi işlemler: firmanın Owner'ı (Owner her şubede satır taşır). */
  isOwner: boolean;
  /** Platform (SaaS işletmecisi) yöneticisi: mağaza ekranlarını değil `/platform` panelini kullanır. */
  isPlatformAdmin: boolean;
  /** Firma güncel hizmet sözleşmesini kabul etmedi: mağaza ekranları sunucuda da kapalıdır; arayüz `/sozlesme`ye yönlendirir. */
  agreementRequired: boolean;
  /** Yalnızca firmanın Owner'ı sözleşmeyi kabul edebilir. */
  canAcceptAgreement: boolean;
};

const AuthContext = createContext<AuthState | null>(null);

function readStoredStore(): string | null {
  try {
    return window.localStorage.getItem(STORE_KEY);
  } catch {
    return null; // gizli pencere veya engellenmiş depolama
  }
}

/** Oturum ve şube seçimi. Şube kimliği hassas değildir; seçim yalnızca kullanıcı rahatlığı için tarayıcıda hatırlanır. */
export function AuthProvider({ children }: { children: ReactNode }) {
  const meQuery = useMe();
  const me = meQuery.data;
  const [storedStoreId, setStoredStoreId] = useState<string | null>(readStoredStore);

  const activeStore = useMemo(() => me?.stores.find((s) => s.id === storedStoreId) ?? me?.stores[0], [me, storedStoreId]);

  // API istemcisi her isteğe X-Store-Id ekler (sunucu rolü her istekte yeniden doğrular).
  // useLayoutEffect: alt bileşenlerin ilk sorguları (passive effect) şube başlığı ayarlanmadan gönderilmesin.
  useLayoutEffect(() => {
    setActiveStoreId(activeStore?.id ?? null);
  }, [activeStore?.id]);

  const setActiveStore = useCallback((id: string) => {
    setStoredStoreId(id);
    try {
      window.localStorage.setItem(STORE_KEY, id);
    } catch {
      // saklanamazsa oturum boyunca bellekte kalır
    }
  }, []);

  const value = useMemo<AuthState>(
    () => ({
      me,
      isLoading: meQuery.isLoading,
      isAuthenticated: Boolean(me),
      activeStore,
      setActiveStore,
      canManageCatalog: Boolean(me?.stores.some((s) => s.role === 'Manager' || s.role === 'Owner')),
      canManageStaff: Boolean(me?.stores.some((s) => s.role === 'Manager' || s.role === 'Owner')),
      isOwner: Boolean(me?.stores.some((s) => s.role === 'Owner')),
      isPlatformAdmin: Boolean(me?.isPlatformAdmin),
      agreementRequired: Boolean(me?.agreementAcceptanceRequired),
      canAcceptAgreement: Boolean(me?.canAcceptAgreement),
    }),
    [me, meQuery.isLoading, activeStore, setActiveStore],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const value = useContext(AuthContext);
  if (!value) {
    throw new Error('useAuth, AuthProvider içinde kullanılmalı.');
  }
  return value;
}
