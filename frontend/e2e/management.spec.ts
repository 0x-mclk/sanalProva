import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { DEMO_MANAGER, devPassword } from './support';

/**
 * M5.2e: işletme sahibi React arayüzünden şube ve personel yönetimini uçtan uca yapar (izole sanalprova_e2e; gerçek AI çağrısı ve gerçek kişi verisi yok).
 * Owner şube ekler → personel davet eder → kişi AYRI tarayıcı bağlamında bağlantıyla hesabını kurar ve girer → Owner pasife alır → kişinin açık oturumu düşer →
 * yeniden etkinleştirince girer. Manager ve Staff yetki sınırları ve bağlantı sızıntısı da denetlenir.
 */

const BASE = 'http://127.0.0.1:5173';
const OWNER = 'sahip@lal.local';
const STAFF_PASSWORD = 'E2e-Personel-Parola-4821';

async function newPage(browser: Browser): Promise<{ context: BrowserContext; page: Page }> {
  const context = await browser.newContext({ baseURL: BASE, locale: 'tr-TR' });
  return { context, page: await context.newPage() };
}

async function login(page: Page, email: string, password: string) {
  await page.goto('/giris');
  await page.getByLabel('E-posta').fill(email);
  await page.getByLabel('Parola').fill(password);
  await page.getByRole('button', { name: 'Giriş yap' }).click();
}

test('Owner: şube ekler, personel davet eder; kişi hesabını kurar, pasife alınınca oturumu düşer, yeniden etkinleştirilince girer', async ({ browser }) => {
  test.setTimeout(240_000);
  const suffix = Math.random().toString(36).slice(2, 8);
  const storeName = `E2E Şube ${suffix}`;
  const storeCode = `E${suffix.toUpperCase()}`;
  const email = `e2e-${suffix}@ornek.test`;

  const owner = await newPage(browser);
  await login(owner.page, OWNER, devPassword());
  await expect(owner.page.getByRole('heading', { name: /, / }).first()).toBeVisible();

  // --- Şube ekle
  await owner.page.goto('/yonetim/subeler');
  await expect(owner.page.getByRole('heading', { name: 'Şubeler' })).toBeVisible();
  await owner.page.getByRole('button', { name: 'Yeni şube' }).click();
  await owner.page.getByLabel(/^Şube adı/).fill(storeName);
  await owner.page.getByLabel(/^Şube kodu/).fill(storeCode.toLowerCase());
  await owner.page.getByRole('dialog').getByRole('button', { name: 'Şube ekle' }).click();
  const card = owner.page.getByTestId(`store-${storeCode}`);
  await expect(card).toContainText(storeName);
  await expect(card).toContainText('1 kişi'); // yalnızca Owner (tüm şubelere yayıldı)

  // Aynı kod tekrar: Türkçe hata
  await owner.page.getByRole('button', { name: 'Yeni şube' }).click();
  await owner.page.getByLabel(/^Şube adı/).fill('Kopya');
  await owner.page.getByLabel(/^Şube kodu/).fill(storeCode);
  await owner.page.getByRole('dialog').getByRole('button', { name: 'Şube ekle' }).click();
  await expect(owner.page.getByText('Bu şube kodu zaten kullanılıyor.')).toBeVisible();
  await owner.page.keyboard.press('Escape');

  // --- Personel davet et (yalnızca yeni şubede Personel)
  await owner.page.goto('/yonetim/personel');
  await owner.page.getByRole('button', { name: 'Personel davet et' }).click();
  const dialog = owner.page.getByRole('dialog');
  await dialog.getByLabel(/^Ad soyad/).fill('E2E Kişi');
  await dialog.getByLabel(/^E-posta/).fill(email);
  await dialog.getByRole('combobox', { name: `${storeName} yetkisi` }).click();
  await owner.page.getByRole('option', { name: 'Personel', exact: true }).click();
  await dialog.getByRole('button', { name: 'Davet et' }).click();

  const link = (await owner.page.getByTestId('setup-link').innerText()).trim();
  expect(link).toContain('/hesap/sifre-belirle#t=');
  const token = link.split('#t=')[1]!;
  expect(owner.page.url()).not.toContain(token);
  expect(await owner.page.evaluate(() => JSON.stringify({ ...window.localStorage, ...window.sessionStorage }))).not.toContain(token);
  await owner.page.getByRole('button', { name: /Kapat/ }).click();
  await expect(owner.page.getByTestId('setup-link')).toHaveCount(0);

  const row = owner.page.getByTestId(`staff-${email}`);
  await expect(row).toContainText('Davet bekliyor');
  await expect(row).toContainText(`${storeName}: Personel`);

  // Aynı e-posta tekrar davet edilemez
  await owner.page.getByRole('button', { name: 'Personel davet et' }).click();
  await owner.page.getByRole('dialog').getByLabel(/^Ad soyad/).fill('Kopya Kişi');
  await owner.page.getByRole('dialog').getByLabel(/^E-posta/).fill(email);
  await owner.page.getByRole('dialog').getByRole('combobox', { name: `${storeName} yetkisi` }).click();
  await owner.page.getByRole('option', { name: 'Personel', exact: true }).click();
  await owner.page.getByRole('dialog').getByRole('button', { name: 'Davet et' }).click();
  await expect(owner.page.getByText('Bu e-posta adresiyle zaten bir hesap var.')).toBeVisible();
  await owner.page.keyboard.press('Escape');

  // --- Kişi AYRI bağlamda hesabını kurar
  const person = await newPage(browser);
  await person.page.goto(link);
  await expect(person.page.getByRole('heading', { name: 'Şifrenizi belirleyin' })).toBeVisible();
  expect(person.page.url()).not.toContain(token);
  await person.page.getByLabel(/^Yeni şifre/).fill(STAFF_PASSWORD);
  await person.page.getByLabel(/^Şifre \(tekrar\)/).fill(STAFF_PASSWORD);
  await person.page.getByRole('button', { name: 'Şifreyi belirle' }).click();
  await expect(person.page.getByRole('heading', { name: 'Şifreniz belirlendi' })).toBeVisible();

  // Aynı bağlantı ikinci kez kullanılamaz
  await person.page.goto(link);
  await person.page.getByLabel(/^Yeni şifre/).fill(STAFF_PASSWORD);
  await person.page.getByLabel(/^Şifre \(tekrar\)/).fill(STAFF_PASSWORD);
  await person.page.getByRole('button', { name: 'Şifreyi belirle' }).click();
  await expect(person.page.getByRole('heading', { name: 'Bağlantı kullanılamıyor' })).toBeVisible();

  await login(person.page, email, STAFF_PASSWORD);
  await expect(person.page.getByRole('heading', { name: /, / }).first()).toBeVisible();
  // Staff: yönetim ekranları yok (arayüz ana sayfaya döner; sunucu da reddeder)
  await person.page.goto('/yonetim/personel');
  await expect(person.page.getByRole('heading', { name: 'Personel' })).toHaveCount(0);
  await person.page.goto('/yonetim/subeler');
  await expect(person.page.getByRole('heading', { name: 'Şubeler' })).toHaveCount(0);
  expect((await person.context.request.get('/api/v1/management/users')).status()).toBe(403);
  expect((await person.context.request.get('/api/v1/management/stores')).status()).toBe(403);

  // --- Owner pasife alır: kişinin açık oturumu bir sonraki istekte düşer
  await owner.page.reload();
  const row2 = owner.page.getByTestId(`staff-${email}`);
  await expect(row2.getByText('Etkin', { exact: true })).toBeVisible();
  await row2.getByRole('button', { name: 'Pasife al' }).click();
  await owner.page.getByRole('dialog').getByRole('button', { name: 'Pasife al' }).click();
  await expect(row2.getByText('Pasif', { exact: true })).toBeVisible();
  expect((await person.context.request.get('/api/v1/auth/me')).status()).toBe(401);
  await login(person.page, email, STAFF_PASSWORD);
  await expect(person.page.getByText('E-posta veya parola hatalı.')).toBeVisible();

  // --- Yeniden etkinleştirince girer
  await row2.getByRole('button', { name: 'Yeniden etkinleştir' }).click();
  await owner.page.getByRole('dialog').getByRole('button', { name: 'Etkinleştir' }).click();
  await expect(row2.getByText('Etkin', { exact: true })).toBeVisible();
  await login(person.page, email, STAFF_PASSWORD);
  await expect(person.page.getByRole('heading', { name: /, / }).first()).toBeVisible();

  // --- Owner kendi satırında işlem düğmesi görmez
  await expect(owner.page.getByTestId(`staff-${OWNER}`).getByRole('button')).toHaveCount(0);

  await owner.context.close();
  await person.context.close();
});

test('Manager: şube yönetimine giremez, personel ekranında yalnızca Personel rolü verebilir', async ({ browser }) => {
  const manager = await newPage(browser);
  await login(manager.page, DEMO_MANAGER, devPassword());
  await expect(manager.page.getByRole('heading', { name: /, / }).first()).toBeVisible();

  await manager.page.goto('/yonetim/subeler');
  await expect(manager.page.getByRole('heading', { name: 'Şubeler' })).toHaveCount(0);
  expect((await manager.context.request.get('/api/v1/management/stores')).status()).toBe(403);

  await manager.page.goto('/yonetim/personel');
  await expect(manager.page.getByRole('heading', { name: 'Personel' })).toBeVisible();
  await expect(manager.page.getByText(/yalnızca kendi şubelerinizdeki/)).toBeVisible();
  await manager.page.getByRole('button', { name: 'Personel davet et' }).click();
  await expect(manager.page.getByText(/Firma sahibi \(tüm şubelerde/)).toHaveCount(0);
  // Rol seçeneklerinde Yönetici yok
  await manager.page.getByRole('dialog').getByRole('combobox', { name: /yetkisi$/ }).first().click();
  await expect(manager.page.getByRole('option', { name: 'Yönetici' })).toHaveCount(0);
  await expect(manager.page.getByRole('option', { name: 'Personel', exact: true })).toBeVisible();
  await manager.context.close();
});
