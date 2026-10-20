import { defineConfig } from '@playwright/test';

/**
 * Tarayıcı uçtan uca testleri (personel + TV). Yerel geliştirme yığınını başlatır (zaten çalışıyorsa yeniden kullanır):
 *  - API  : http://127.0.0.1:5080 (Development, yerel `sanalprova_dev` veritabanı, demo katalog — PostgreSQL önceden başlatılmış olmalı: ./scripts/db.ps1 start)
 *  - Web  : http://127.0.0.1:5173 (Vite; /api ve /hubs API'ye yönlendirilir, /tv TV girişidir)
 * Testler kendi oluşturdukları prova ve TV kayıtlarını sonunda siler. Demo hesap parolası `E2E_PASSWORD` ortam değişkeninden,
 * yoksa API projesinin user-secrets'ındaki `Seed:DevPassword` değerinden okunur. Gerçek AI çağrısı yoktur (mock sağlayıcı, DEMO sonuç).
 */
export default defineConfig({
  testDir: './e2e',
  timeout: 240_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: 'http://127.0.0.1:5173',
    trace: 'retain-on-failure',
  },
  webServer: [
    {
      command: 'powershell -NoProfile -Command ". ../scripts/dev-env.ps1; dotnet run --project ../backend/src/SanalProva.Api --launch-profile http"',
      url: 'http://127.0.0.1:5080/health/live',
      reuseExistingServer: true,
      timeout: 180_000,
      env: { Security__LoginRateLimitPerMinute: '1000', Security__DisplayPairingRateLimitPerMinute: '1000', Security__DisplayClaimRateLimitPerMinute: '1000', Generation__Mock__DelayMs: '300' },
    },
    {
      command: 'npm run dev',
      url: 'http://127.0.0.1:5173',
      reuseExistingServer: true,
      timeout: 60_000,
    },
  ],
});
