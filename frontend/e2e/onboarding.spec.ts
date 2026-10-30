import { execFileSync } from 'node:child_process';
import { resolve } from 'node:path';
import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { DEMO_MANAGER, devPassword } from './support';

/**
 * M5.1h: uçtan uca onboarding (izole sanalprova_e2e veritabanı; mock sağlayıcı; gerçek AI çağrısı ve gerçek kişi verisi yok).
 * Platform yöneticisi GERÇEK operatör komutuyla (`platform bootstrap`) oluşturulur; sonra: panel → işletme oluşturma → kurulum bağlantısı → AYRI tarayıcı bağlamında
 * şifre belirleme → Owner girişi → hizmet sözleşmesi kabulü → mağazaya erişim. Token'ın URL'e, tarayıcı depolamasına, konsola ve istek adreslerine sızmadığı denetlenir.
 */

const BASE = 'http://127.0.0.1:5173';
const PASSWORD = 'E2e-Gizli-Parola-4821';
const apiDll = resolve(import.meta.dirname, '../../backend/src/SanalProva.Api/bin/Debug/net10.0/SanalProva.Api.dll');

/** Tüm bağlamlarda toplanan, token içermemesi gereken gözlemler. */
const observed = { urls: [] as string[], console: [] as string[] };

function watch(page: Page) {
  // Tarayıcı fragment'ı (#t=…) ağa hiç göndermez; Playwright ana çerçeve gezintisinde adresi fragment'lı raporlar: ağda giden adres için fragment atılır
  page.on('request', (request) => observed.urls.push(request.url().split('#')[0]!));
  page.on('console', (message) => observed.console.push(message.text()));
  page.on('pageerror', (error) => observed.console.push(error.message));
}

async function newPage(browser: Browser): Promise<{ context: BrowserContext; page: Page }> {
  const context = await browser.newContext({ baseURL: BASE });
  const page = await context.newPage();
  watch(page);
  return { context, page };
}

/** Operatör komutu: web sunucusu başlatmadan bitirir; bağlantı yalnızca standart çıktıdadır (yakalanır, günlüğe/dosyaya yazılmaz). */
function bootstrapPlatformAdmin(email: string): string {
  const database = process.env.SANALPROVA_E2E_DB;
  const key = process.env.SANALPROVA_E2E_SETUP_TOKEN_KEY;
  if (!database || !key) throw new Error('E2E ortamı hazır değil (SANALPROVA_E2E_DB / SANALPROVA_E2E_SETUP_TOKEN_KEY).');
  const output = execFileSync(
    'dotnet',
    [apiDll, 'platform', 'bootstrap', '--email', email, '--name', 'E2E Platform Yöneticisi', '--base-url', BASE, '--additional', '--confirm'],
    { encoding: 'utf8', env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Production', ConnectionStrings__Main: database, Security__SetupTokenKey: key } },
  );
  const link = output.split('\n').map((line) => line.trim()).find((line) => line.includes('/hesap/sifre-belirle#t='));
  if (!link) throw new Error('Operatör komutu bağlantı üretmedi.');
  return link;
}

const tokenOf = (link: string) => link.split('#t=')[1]!;

async function storagesOf(page: Page): Promise<string> {
  return page.evaluate(() => JSON.stringify({ local: { ...window.localStorage }, session: { ...window.sessionStorage } }));
}

async function setPassword(page: Page, link: string) {
  await page.goto(link);
  await expect(page.getByRole('heading', { name: 'Şifrenizi belirleyin' })).toBeVisible();
  // Token okunduktan hemen sonra adres çubuğundan silinmiş olmalı
  expect(page.url()).not.toContain('#t=');
  expect(page.url()).not.toContain(tokenOf(link));
  await page.getByLabel(/^Yeni şifre/).fill(PASSWORD);
  await page.getByLabel(/^Şifre \(tekrar\)/).fill(PASSWORD);
  await page.getByRole('button', { name: 'Şifreyi belirle' }).click();
  await expect(page.getByRole('heading', { name: 'Şifreniz belirlendi' })).toBeVisible();
  expect(await storagesOf(page)).not.toContain(tokenOf(link));
}

async function login(page: Page, email: string, password: string) {
  await page.goto('/giris');
  await page.getByLabel('E-posta').fill(email);
  await page.getByLabel('Parola').fill(password);
  await page.getByRole('button', { name: 'Giriş yap' }).click();
}

test('platform yöneticisi → işletme oluşturma → ayrı tarayıcıda şifre belirleme → Owner girişi → sözleşme kabulü → mağazaya erişim', async ({ browser }) => {
  test.setTimeout(240_000);
  const suffix = Math.random().toString(36).slice(2, 10);
  const adminEmail = `platform-${suffix}@ornek.test`;
  const ownerEmail = `sahip-${suffix}@ornek.test`;
  const slug = `e2e-gelinlik-${suffix}`;
  const secrets: string[] = [];

  // --- 1) Operatör komutuyla platform yöneticisi + şifre belirleme (bağlam A)
  const adminLink = bootstrapPlatformAdmin(adminEmail);
  secrets.push(tokenOf(adminLink));
  const a = await newPage(browser);
  await setPassword(a.page, adminLink);

  // Kullanılmış bağlantı: aynı bağlantıyla ikinci deneme güvenli mesajı gösterir (neden açıklanmaz)
  await a.page.goto(adminLink);
  await a.page.getByLabel(/^Yeni şifre/).fill(PASSWORD);
  await a.page.getByLabel(/^Şifre \(tekrar\)/).fill(PASSWORD);
  await a.page.getByRole('button', { name: 'Şifreyi belirle' }).click();
  await expect(a.page.getByRole('heading', { name: 'Bağlantı kullanılamıyor' })).toBeVisible();
  await expect(a.page.getByText(/kullanılmış|iptal edilmiş/i)).toHaveCount(0);

  // --- 2) Platform yöneticisi girişi → /platform
  await login(a.page, adminEmail, PASSWORD);
  await a.page.waitForURL('**/platform');
  await expect(a.page.getByRole('heading', { name: 'İşletmeler' })).toBeVisible();
  await a.page.goto('/'); // mağaza ekranı yerine panele döner
  await a.page.waitForURL('**/platform');

  // --- 3) İşletme oluştur → kurulum bağlantısı yalnızca bir kez
  await a.page.getByRole('link', { name: 'Yeni işletme' }).click();
  await a.page.waitForURL('**/platform/yeni');
  await a.page.getByLabel(/^Firma adı/).fill('E2E Gelinlik');
  await a.page.getByLabel(/^Kısa ad/).fill(slug);
  await a.page.getByLabel(/^İlk şube adı/).fill('Merkez Şube');
  await a.page.getByLabel(/^Şube kodu/).fill('MRK');
  await a.page.getByLabel(/^Firma sahibi \(Owner\) adı soyadı/).fill('E2E Sahip');
  await a.page.getByLabel(/^Firma sahibi e-posta/).fill(ownerEmail);
  await a.page.getByRole('button', { name: 'İşletmeyi oluştur' }).click();

  const firstLink = (await a.page.getByTestId('setup-link').innerText()).trim();
  expect(firstLink).toContain('/hesap/sifre-belirle#t=');
  secrets.push(tokenOf(firstLink));
  expect(a.page.url()).not.toContain(tokenOf(firstLink));
  expect(await storagesOf(a.page)).not.toContain(tokenOf(firstLink));
  await a.page.getByRole('button', { name: /Kapat/ }).click();
  await a.page.waitForURL(/\/platform\/[0-9a-f-]{36}$/);
  await expect(a.page.getByTestId('setup-link')).toHaveCount(0); // bir daha gösterilmez
  await a.page.reload();
  await expect(a.page.getByTestId('setup-link')).toHaveCount(0); // kalıcı saklanmadı
  await expect(a.page.getByText('Kurulum: şifre bekleniyor')).toBeVisible();
  await expect(a.page.getByText('Sözleşme: bekliyor')).toBeVisible();
  await expect(a.page.getByText('Ücretli AI: kapalı')).toBeVisible();

  // --- 4) Bağlantıyı yeniden üret: önceki iptal olur (çift tıklama koruması istemci tarafıdır)
  await a.page.getByRole('button', { name: 'Kurulum bağlantısını yeniden üret' }).click();
  await a.page.getByRole('button', { name: 'Yeniden üret', exact: true }).dblclick();
  const secondLink = (await a.page.getByTestId('setup-link').innerText()).trim();
  secrets.push(tokenOf(secondLink));
  expect(tokenOf(secondLink)).not.toBe(tokenOf(firstLink));
  await a.page.getByRole('button', { name: /Kapat/ }).click();
  await expect(a.page.getByTestId('setup-link')).toHaveCount(0);

  // --- 5) AYRI tarayıcı bağlamı (B): iptal edilen eski bağlantı çalışmaz; yeni bağlantıyla Owner şifresini belirler
  const b = await newPage(browser);
  await b.page.goto(firstLink);
  // Sayfa açılışında bağlantı sunucuya sorulmaz (doğrulama ucu yok); iptal edilmiş bağlantı şifre gönderilince tek güvenli mesajla reddedilir
  await b.page.getByLabel(/^Yeni şifre/).fill(PASSWORD);
  await b.page.getByLabel(/^Şifre \(tekrar\)/).fill(PASSWORD);
  await b.page.getByRole('button', { name: 'Şifreyi belirle' }).click();
  await expect(b.page.getByRole('heading', { name: 'Bağlantı kullanılamıyor' })).toBeVisible();
  expect(b.page.url()).not.toContain(tokenOf(firstLink));
  await setPassword(b.page, secondLink);

  // --- 6) Owner girişi → sözleşme ekranı; mağaza ekranları ve API sözleşme kabul edilene kadar kapalı
  await login(b.page, ownerEmail, PASSWORD);
  await b.page.waitForURL('**/sozlesme');
  await expect(b.page.getByRole('heading', { name: 'Sözleşmeyi kabul edin' })).toBeVisible();
  await expect(b.page.getByText(/Müşterinin fotoğraf \(KVKK\) onayı ayrıdır/)).toBeVisible();
  await b.page.goto('/');
  await b.page.waitForURL('**/sozlesme');
  await b.page.goto('/katalog');
  await b.page.waitForURL('**/sozlesme');
  const blocked = await b.page.request.get('/api/v1/products');
  expect(blocked.status()).toBe(403);
  expect(await blocked.text()).toContain('AGREEMENT_ACCEPTANCE_REQUIRED');

  // Owner olmayan personel bekleme mesajı görür (demo hesap, ayrı bağlam): demo firma zaten kabul etmiş olduğundan bu kontrol birim testlerindedir.

  // --- 7) Owner sözleşmeyi kabul eder → mağazaya erişim açılır
  const accept = b.page.getByRole('button', { name: 'Sözleşmeyi kabul et' });
  await expect(accept).toBeDisabled();
  await b.page.getByLabel(/Sözleşme metnini okudum/).check();
  await accept.click();
  await b.page.waitForURL(`${BASE}/`);
  await expect(b.page.getByRole('heading', { name: /, / }).first()).toBeVisible();
  expect((await b.page.request.get('/api/v1/products')).status()).toBe(200);
  await b.page.goto('/katalog');
  await expect(b.page.getByRole('heading', { name: 'Ürünler' }).first()).toBeVisible();

  // --- 8) Yetkisiz kullanıcılar platform ekranlarına erişemez
  await b.page.goto('/platform'); // Owner (yetkisiz) → ana sayfaya döner
  await b.page.waitForURL(`${BASE}/`);
  expect((await b.page.request.get('/api/v1/platform/organizations')).status()).toBe(403);
  const staff = await newPage(browser);
  await login(staff.page, DEMO_MANAGER, devPassword());
  await expect(staff.page.getByRole('heading', { name: /, / }).first()).toBeVisible();
  await staff.page.goto('/platform/yeni');
  await staff.page.waitForURL(`${BASE}/`);
  expect((await staff.page.request.get('/api/v1/platform/organizations')).status()).toBe(403);
  const anonymous = await newPage(browser);
  await anonymous.page.goto('/platform');
  await anonymous.page.waitForURL('**/giris');

  // --- 9) Platform yöneticisi listesinde durumlar güncel
  await a.page.goto('/platform');
  const card = a.page.locator('li', { hasText: slug });
  await expect(card.getByText('Kurulum: tamamlandı')).toBeVisible();
  await expect(card.getByText('Sözleşme: kabul edildi')).toBeVisible();

  // --- 10) Platform yöneticisi müşteri verisine erişemez (mağaza uçları kapalı)
  const adminApi = await a.page.request.get('/api/v1/sessions');
  expect([401, 403]).toContain(adminApi.status());

  // --- 11) Sızıntı kontrolü: token hiçbir istek adresinde, konsolda veya tarayıcı depolamasında yok
  for (const secret of secrets) {
    expect(observed.urls.filter((url) => url.includes(secret))).toEqual([]);
    expect(observed.console.filter((text) => text.includes(secret))).toEqual([]);
    expect(observed.console.filter((text) => text.includes(PASSWORD))).toEqual([]);
    for (const { page } of [a, b, staff, anonymous]) {
      expect(await storagesOf(page)).not.toContain(secret);
    }
  }
  expect(observed.urls.filter((url) => url.includes('sifre-belirle?t='))).toEqual([]); // token sorgu dizesinde de olmaz

  await Promise.all([a.context.close(), b.context.close(), staff.context.close(), anonymous.context.close()]);
});
