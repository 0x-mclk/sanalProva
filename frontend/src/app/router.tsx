import { lazy, Suspense } from 'react';
import { createBrowserRouter, Navigate, Link } from 'react-router';
import { Button, Stack, Text, Title } from '@mantine/core';
import { SetupPasswordPage } from '../features/account/SetupPasswordPage';
import { AgreementPage } from '../features/agreement/AgreementPage';
import { LoginPage } from '../features/auth/LoginPage';
import { FreeCombinationPage } from '../features/catalog-picker/FreeCombinationPage';
import { OutfitSetPage } from '../features/catalog-picker/OutfitSetPage';
import { ComparePage } from '../features/generation/ComparePage';
import { GeneratingPage } from '../features/generation/GeneratingPage';
import { ResultPage } from '../features/generation/ResultPage';
import { RevisionPage } from '../features/generation/RevisionPage';
import { HistoryPage } from '../features/history/HistoryPage';
import { HomePage } from '../features/home/HomePage';
import { ConsentPage } from '../features/session/ConsentPage';
import { ModePage } from '../features/session/ModePage';
import { PhotoPage } from '../features/session/PhotoPage';
import { ResumePage } from '../features/session/ResumePage';
import { DisplaysPage } from '../features/displays/DisplaysPage';
import { LoadingState } from '../shared/ui';
import { RequireAuth, RequireManager, RequireOwner, RequirePlatformAdmin, RequireSession, RequireStaffManager } from './RequireAuth';

// Yönetim ekranları ve geliştirme ekranı yalnızca gerektiğinde yüklenir (personel ilk açılışı küçük kalır)
const ProductsPage = lazy(() => import('../features/catalog-admin/ProductsPage').then((m) => ({ default: m.ProductsPage })));
const ProductEditPage = lazy(() => import('../features/catalog-admin/ProductEditPage').then((m) => ({ default: m.ProductEditPage })));
const CategoriesPage = lazy(() => import('../features/catalog-admin/CategoriesPage').then((m) => ({ default: m.CategoriesPage })));
const UsagePage = lazy(() => import('../features/usage/UsagePage').then((m) => ({ default: m.UsagePage })));
const PlatformLayout = lazy(() => import('../features/platform/PlatformLayout').then((m) => ({ default: m.PlatformLayout })));
const OrganizationsPage = lazy(() => import('../features/platform/OrganizationsPage').then((m) => ({ default: m.OrganizationsPage })));
const CreateOrganizationPage = lazy(() => import('../features/platform/CreateOrganizationPage').then((m) => ({ default: m.CreateOrganizationPage })));
const OrganizationDetailPage = lazy(() => import('../features/platform/OrganizationDetailPage').then((m) => ({ default: m.OrganizationDetailPage })));
const FinancePage = lazy(() => import('../features/platform/FinancePage').then((m) => ({ default: m.FinancePage })));
const ReconciliationPage = lazy(() => import('../features/platform/ReconciliationPage').then((m) => ({ default: m.ReconciliationPage })));
const StoresPage = lazy(() => import('../features/management/StoresPage').then((m) => ({ default: m.StoresPage })));
const StaffPage = lazy(() => import('../features/management/StaffPage').then((m) => ({ default: m.StaffPage })));
const CreditsPage = lazy(() => import('../features/credits/CreditsPage').then((m) => ({ default: m.CreditsPage })));
const DevStatusPage = lazy(() => import('../features/dev/DevStatusPage').then((m) => ({ default: m.DevStatusPage })));

const lazyPage = (node: React.ReactNode) => (
  <Suspense
    fallback={
      <main className="sp-page">
        <LoadingState />
      </main>
    }
  >
    {node}
  </Suspense>
);

function NotFound() {
  return (
    <main className="sp-page">
      <Stack className="sp-content" gap="md" pt="xl">
        <Title order={1}>Sayfa bulunamadı</Title>
        <Text c="dimmed">Aradığınız sayfa taşınmış veya hiç var olmamış olabilir.</Text>
        <Button component={Link} to="/">
          Ana sayfaya dön
        </Button>
      </Stack>
    </main>
  );
}

/** Rotalar: docs/05-frontend-ekranlar.md §3. Oturum adımları `/prova/:id/...` altındadır; `/prova/:id` sunucudaki duruma göre doğru adıma yönlendirir. */
export const router = createBrowserRouter([
  { path: '/giris', element: <LoginPage /> },
  // Anonim: tek kullanımlık bağlantıyla şifre belirleme (token URL fragment'ındadır, okunur okunmaz adres çubuğundan silinir)
  { path: '/hesap/sifre-belirle', element: <SetupPasswordPage /> },
  // Mağaza kapısının ÖNÜNDEKİ sayfa: sözleşmeyi görüntüleme/kabul (yalnızca giriş gerekir)
  { element: <RequireSession />, children: [{ path: '/sozlesme', element: <AgreementPage /> }] },
  // Platform paneli: yalnızca platform yöneticisi (sunucu da PlatformAdminFilter ile doğrular)
  {
    element: <RequirePlatformAdmin />,
    children: [
      {
        element: lazyPage(<PlatformLayout />),
        children: [
          { path: '/platform', element: lazyPage(<OrganizationsPage />) },
          { path: '/platform/yeni', element: lazyPage(<CreateOrganizationPage />) },
          { path: '/platform/finans', element: lazyPage(<FinancePage />) },
          { path: '/platform/mutabakat', element: lazyPage(<ReconciliationPage />) },
          { path: '/platform/:id', element: lazyPage(<OrganizationDetailPage />) },
        ],
      },
    ],
  },
  {
    element: <RequireAuth />,
    children: [
      { path: '/', element: <HomePage /> },
      { path: '/prova/yeni', element: <ConsentPage /> },
      { path: '/prova/:id', element: <ResumePage /> },
      { path: '/prova/:id/onay', element: <ConsentPage /> },
      { path: '/prova/:id/fotograf', element: <PhotoPage /> },
      { path: '/prova/:id/tur', element: <ModePage /> },
      { path: '/prova/:id/takim', element: <OutfitSetPage /> },
      { path: '/prova/:id/kombin', element: <FreeCombinationPage /> },
      { path: '/prova/:id/uretim/:jobId', element: <GeneratingPage /> },
      { path: '/prova/:id/sonuc/:imageId?', element: <ResultPage /> },
      { path: '/prova/:id/revizyon/:imageId?', element: <RevisionPage /> },
      { path: '/prova/:id/karsilastir', element: <ComparePage /> },
      { path: '/gecmis', element: <HistoryPage /> },
      { path: '/tv-cihazlar', element: <DisplaysPage /> },
      { path: '/katalog', element: lazyPage(<ProductsPage />) },
      {
        element: <RequireManager />,
        children: [
          { path: '/katalog/urun/yeni', element: lazyPage(<ProductEditPage />) },
          { path: '/katalog/urun/:id', element: lazyPage(<ProductEditPage />) },
          { path: '/katalog/kategoriler', element: lazyPage(<CategoriesPage />) },
          { path: '/maliyet', element: lazyPage(<UsagePage />) },
        ],
      },
      // Şube yönetimi: yalnızca firma sahibi; personel yönetimi: Manager+ (sunucu kapsamı her işlemde yeniden hesaplar)
      {
        element: <RequireOwner />,
        children: [
          { path: '/yonetim/subeler', element: lazyPage(<StoresPage />) },
          { path: '/yonetim/kredi', element: lazyPage(<CreditsPage />) },
        ],
      },
      { element: <RequireStaffManager />, children: [{ path: '/yonetim/personel', element: lazyPage(<StaffPage />) }] },
      { path: '/dev', element: lazyPage(<DevStatusPage />) },
    ],
  },
  { path: '/prova', element: <Navigate to="/" replace /> },
  { path: '*', element: <NotFound /> },
]);
