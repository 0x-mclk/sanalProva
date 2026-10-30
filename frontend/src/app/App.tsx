import { MutationCache, QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from 'react-router';
import { ApiError } from '../shared/api/http';
import { qk } from '../shared/api/hooks';
import { AuthProvider } from '../shared/auth/AuthContext';
import { ThemeProvider } from '../shared/theme/ThemeProvider';
import { router } from './router';

/**
 * Sunucu hizmet sözleşmesi kapısı kapalıyken (ör. yeni sürüm yayımlandı) herhangi bir istek 403 AGREEMENT_ACCEPTANCE_REQUIRED alırsa oturum bilgisi yenilenir:
 * `agreementAcceptanceRequired` değişir ve yönlendirme /sozlesme'ye gider. Karar sunucudadır; burası yalnızca arayüzü senkronlar.
 */
function syncAgreementGate(error: unknown) {
  if (error instanceof ApiError && error.code === 'AGREEMENT_ACCEPTANCE_REQUIRED') {
    void queryClient.invalidateQueries({ queryKey: qk.me });
  }
}

const queryClient: QueryClient = new QueryClient({
  queryCache: new QueryCache({ onError: syncAgreementGate }),
  mutationCache: new MutationCache({ onError: syncAgreementGate }),
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      refetchOnWindowFocus: false,
      // Yetki/doğrulama hatalarında tekrar deneme anlamsız
      retry: (failureCount, error) => !(error instanceof ApiError && error.status >= 400 && error.status < 500) && failureCount < 2,
    },
  },
});

export function App() {
  return (
    <ThemeProvider>
      <QueryClientProvider client={queryClient}>
        <AuthProvider>
          <RouterProvider router={router} />
        </AuthProvider>
      </QueryClientProvider>
    </ThemeProvider>
  );
}
