import { defineConfig } from '@playwright/test';

/**
 * Tarayıcı uçtan uca testleri (personel + TV). Yığını İZOLE bir ortamda başlatır: `. ./scripts/e2e-env.ps1 up` atılabilir `sanalprova_e2e`
 * SQL Server veritabanını, geçici dosya deposunu ve parolayı (E2E_PASSWORD) kurar; sonunda `. ./scripts/e2e-env.ps1 down` siler.
 *  - API  : http://127.0.0.1:5080 (Development, e2e veritabanı, demo katalog, mock sağlayıcı — gerçek AI çağrısı yoktur)
 *  - Web  : http://127.0.0.1:5173 (Vite; /api ve /hubs API'ye yönlendirilir, /tv TV girişidir)
 * Güvenlik: ortam değişkenleri yoksa veya veritabanı adı `sanalprova_e2e` değilse config hata verir (dev/test/PostgreSQL'e yanlışlıkla bağlanılmaz);
 * mevcut bir sunucu yeniden kullanılmaz (5080/5173 doluysa başlamaz). Testler kendi kayıtlarını sonunda siler.
 */
const database = process.env.SANALPROVA_E2E_DB ?? '';
const storage = process.env.SANALPROVA_E2E_STORAGE ?? '';
if (!/Database=sanalprova_e2e(;|$)/i.test(database) || !storage || !process.env.E2E_PASSWORD) {
  throw new Error('E2E izole ortamı hazır değil. Önce PowerShell içinde: . ./scripts/e2e-env.ps1 up');
}

export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/global-setup.ts',
  timeout: 240_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: 'http://127.0.0.1:5173',
    // CI'da trace kapali: giris istegindeki (kosuya ozel rastgele) parolayi icerebilir; yerelde yalnizca basarisizlikta
    trace: process.env.CI ? 'off' : 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  webServer: [
    {
      // Windows: kullanıcı klasöründeki .NET 10'u öne alan dev-env.ps1; Linux/CI: dotnet zaten PATH'te
      command:
        process.platform === 'win32'
          ? 'powershell -NoProfile -Command ". ../scripts/dev-env.ps1; dotnet run --project ../backend/src/SanalProva.Api --launch-profile http"'
          : 'dotnet run --project ../backend/src/SanalProva.Api --launch-profile http',
      url: 'http://127.0.0.1:5080/health/live',
      reuseExistingServer: false,
      timeout: 180_000,
      env: {
        ConnectionStrings__Main: database,
        Storage__LocalRoot: storage,
        Storage__SigningKey: process.env.SANALPROVA_E2E_SIGNING_KEY ?? '',
        Seed__DevPassword: process.env.E2E_PASSWORD ?? '',
        Generation__UseMockProviders: 'true',
        Generation__Budget__AllowPaidCalls: 'false',
        Ai__Fal__ApiKey: ' ',
        Ai__Pruna__ApiKey: ' ',
        Ai__Fashn__ApiKey: ' ',
        Ai__Gemini__ApiKey: ' ',
        Security__LoginRateLimitPerMinute: '1000', Security__DisplayPairingRateLimitPerMinute: '1000', Security__DisplayClaimRateLimitPerMinute: '1000', Security__DisplayPairingStatusRateLimitPerMinute: '100000', Security__DisplayStateRateLimitPerMinute: '100000', Generation__Mock__DelayMs: '300',
      },
    },
    {
      command: 'npm run dev',
      url: 'http://127.0.0.1:5173',
      reuseExistingServer: false,
      timeout: 60_000,
    },
  ],
});
