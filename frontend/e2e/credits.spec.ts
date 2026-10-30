import { execFileSync } from 'node:child_process';
import { resolve } from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { DEMO_MANAGER, devPassword } from './support';

/**
 * M5.4: işletme sahibi kredi ekranı (izole sanalprova_e2e; mock sağlayıcı; gerçek ödeme ve gerçek AI çağrısı yok).
 * Kredi yüklemesi GERÇEK operatör komutuyla yapılır (`platform credits --grant --ref --confirm`): hiçbir uygulama ekranından kredi yüklenemez.
 * Owner bakiyeyi/geçmişi görür; aynı referansla ikinci yükleme tekrar uygulanmaz; Manager kredi ekranına giremez.
 */

const OWNER = 'sahip@lal.local';
const apiDll = resolve(import.meta.dirname, '../../backend/src/SanalProva.Api/bin/Debug/net10.0/SanalProva.Api.dll');

function credits(...args: string[]): string {
  const database = process.env.SANALPROVA_E2E_DB;
  const key = process.env.SANALPROVA_E2E_SETUP_TOKEN_KEY;
  if (!database || !key) throw new Error('E2E ortamı hazır değil (SANALPROVA_E2E_DB / SANALPROVA_E2E_SETUP_TOKEN_KEY).');
  return execFileSync('dotnet', [apiDll, 'platform', 'credits', '--org', 'lal', ...args], {
    encoding: 'utf8',
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Production', ConnectionStrings__Main: database, Security__SetupTokenKey: key },
  });
}

async function login(page: Page, email: string) {
  await page.goto('/giris');
  await page.getByLabel('E-posta').fill(email);
  await page.getByLabel('Parola').fill(devPassword());
  await page.getByRole('button', { name: 'Giriş yap' }).click();
  await expect(page.getByRole('heading', { name: /, / }).first()).toBeVisible();
}

test('Owner kredi bakiyesini ve geçmişini görür; operatör yüklemesi referansla tek kez uygulanır; Manager giremez', async ({ page, browser }) => {
  test.setTimeout(180_000);
  const ref = `e2e-${Math.random().toString(36).slice(2, 10)}-${Date.now()}`;

  // Başlangıç: bu firmada hesap yok (taze E2E veritabanı)
  await login(page, OWNER);
  await page.goto('/yonetim/kredi');
  await expect(page.getByRole('heading', { name: 'Kredi', exact: true })).toBeVisible();
  await expect(page.getByTestId('credit-available')).toContainText('0');
  await expect(page.getByText(/Kullanılabilir krediniz yok/)).toBeVisible();
  await expect(page.getByText('Henüz kredi hareketi yok')).toBeVisible();

  // Operatör komutu: kuru çalışma değiştirmez, --confirm yükler
  expect(credits('--grant', '25', '--ref', ref)).toContain('KURU ÇALIŞMA');
  await page.reload();
  await expect(page.getByTestId('credit-available')).toContainText('0');
  expect(credits('--grant', '25', '--ref', ref, '--note', 'E2E paketi', '--confirm')).toContain('TAMAM');

  await page.reload();
  await expect(page.getByTestId('credit-available')).toContainText('25');
  await expect(page.getByTestId('credit-balance')).toContainText('25');
  await expect(page.getByTestId('credit-reserved')).toContainText('0');
  const entries = page.getByTestId('credit-entry');
  await expect(entries).toHaveCount(1);
  await expect(entries.first()).toContainText('Kredi yükleme');
  await expect(entries.first()).toContainText(`Ref: ${ref}`);
  await expect(entries.first()).toContainText('+25');
  await expect(page.getByText(/Kullanılabilir krediniz yok/)).toHaveCount(0);
  // Ekranda yükleme/ödeme yolu yok
  await expect(page.getByRole('button', { name: /yükle|satın|öde/i })).toHaveCount(0);

  // Aynı referans: ikinci kez uygulanmaz
  expect(credits('--grant', '25', '--ref', ref, '--confirm')).toContain('DEĞİŞİKLİK YOK');
  await page.reload();
  await expect(page.getByTestId('credit-balance')).toContainText('25');
  await expect(entries).toHaveCount(1);

  // Uygulama API'si yazma yolu sunmaz; okuma yalnızca Owner'a
  expect((await page.request.post('/api/v1/credits', { data: { amount: 1000 }, headers: { 'X-CSRF-TOKEN': 'x' } })).status()).toBeGreaterThanOrEqual(400);
  const summary = await (await page.request.get('/api/v1/credits')).json();
  expect(summary).toMatchObject({ balance: 25, reserved: 0, available: 25 });

  // Manager: ekrana giremez, API 403
  const context = await browser.newContext({ baseURL: 'http://127.0.0.1:5173', locale: 'tr-TR' });
  const managerPage = await context.newPage();
  await login(managerPage, DEMO_MANAGER);
  await managerPage.goto('/yonetim/kredi');
  await expect(managerPage.getByRole('heading', { name: 'Kredi', exact: true })).toHaveCount(0);
  expect((await context.request.get('/api/v1/credits')).status()).toBe(403);
  await context.close();
});
