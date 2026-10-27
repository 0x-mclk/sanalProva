import { chromium, expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { DEMO_MANAGER, devPassword, MAIN_STORE, OTHER_FIRM_USER, selectStore, sqlScalar, StaffApi, syntheticPortraitPng } from './support';

/**
 * Gerçek kullanıcı akışları (izole sanalprova_e2e veritabanı, mock sağlayıcı, sentetik fotoğraf; gerçek AI çağrısı ve gerçek kişi verisi yok).
 * Her test kendi provalarını sonunda siler.
 */

async function login(page: Page, email = DEMO_MANAGER) {
  await page.goto('/giris');
  await page.getByLabel('E-posta').fill(email);
  await page.getByLabel('Parola').fill(devPassword());
  await page.getByRole('button', { name: 'Giriş yap' }).click();
  await expect(page.getByRole('heading', { name: /, / }).first()).toBeVisible();
}

const jobCount = (sessionId: string) => Number(sqlScalar(`SELECT COUNT(*) FROM generation_jobs WHERE session_id = '${sessionId}'`));
const guid = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/;

/** Onay + fotoğraf adımlarını arayüzden yapar; prova kimliğini döner (hazır takım seçim sayfasına kadar). */
async function startSessionViaUi(page: Page, seed: number): Promise<string> {
  await page.goto('/prova/yeni');
  await page.getByLabel('Müşteri takma adı (isteğe bağlı)').fill(`E2E Müşteri ${seed}`);
  const submit = page.getByRole('button', { name: 'Onayı Kaydet ve Devam Et' });
  await expect(submit).toBeDisabled();
  await page.getByLabel('Fotoğrafımın sanal prova için işlenmesini onaylıyorum.').check();
  await page.getByLabel(/yapay zekâ hizmet sağlayıcılarına/).check();
  await submit.click();
  await page.waitForURL(/\/prova\/[0-9a-f-]{36}\/fotograf/);
  const sessionId = guid.exec(page.url())![0];

  await page.locator('input[type="file"]').nth(1).setInputFiles({ name: 'sentetik.png', mimeType: 'image/png', buffer: syntheticPortraitPng(900, 1500, seed) });
  await expect(page.getByText('Fotoğraf hazır')).toBeVisible();
  await page.getByRole('button', { name: 'Devam Et' }).click();
  await page.getByRole('button', { name: /Hazır Takım veya Kaftan/ }).click();
  await page.waitForURL(/\/takim$/);
  return sessionId;
}

async function chooseFirstProduct(page: Page) {
  const card = page.locator('li button[aria-pressed]').first();
  await expect(card).toBeVisible();
  await card.click();
  await expect(card).toHaveAttribute('aria-pressed', 'true');
}

test.describe('Personel akışı (gerçek tarayıcı)', () => {
  let browser: Browser;
  let context: BrowserContext;
  let page: Page;
  let api: StaffApi;
  const sessions: string[] = [];

  test.beforeAll(async () => {
    browser = await chromium.launch();
    context = await browser.newContext({ viewport: { width: 430, height: 932 }, locale: 'tr-TR' });
    page = await context.newPage();
    await login(page);
    await selectStore(page, MAIN_STORE);
    api = new StaffApi(context.request);
    await api.init(MAIN_STORE);
  });

  test.afterAll(async () => {
    await api?.cleanup(sessions);
    await context?.close();
    await browser?.close();
  });

  test('giriş hatası, onay zorunluluğu, fotoğraf, seçim, mock üretim, sonuç ve favori', async () => {
    // Hatalı parola anlaşılır mesajla reddedilir; oturum açılmaz
    const wrong = await browser.newContext({ locale: 'tr-TR' });
    const wrongPage = await wrong.newPage();
    await wrongPage.goto('/giris');
    await wrongPage.getByLabel('E-posta').fill(DEMO_MANAGER);
    await wrongPage.getByLabel('Parola').fill('yanlis-parola-1');
    await wrongPage.getByRole('button', { name: 'Giriş yap' }).click();
    await expect(wrongPage.getByText('E-posta veya parola hatalı.')).toBeVisible();
    await expect(wrongPage).toHaveURL(/\/giris/);
    await wrong.close();

    const sessionId = await startSessionViaUi(page, 11);
    sessions.push(sessionId);
    // KVKK: onay sunucuda kayıtlı, fotoğraf yüklendi
    expect(Number(sqlScalar(`SELECT COUNT(*) FROM consent_records WHERE session_id = '${sessionId}'`))).toBe(1);

    await chooseFirstProduct(page);
    await page.getByRole('button', { name: 'Üret' }).click();
    await page.waitForURL(/\/sonuc\//, { timeout: 60_000 });

    const image = page.locator('img[alt*="sürüm 1"]');
    await expect(image).toBeVisible();
    expect(await image.evaluate((el: HTMLImageElement) => el.naturalWidth)).toBeGreaterThan(0);
    await expect(page.getByTitle('Gerçek yapay zekâ çıktısı değildir').first()).toBeVisible(); // mock sonuç gerçek AI çıktısı gibi sunulmaz
    await expect(page.getByRole('note')).toContainText('DEMO sonuç'); // DemoNotice
    expect(jobCount(sessionId)).toBe(1);

    // Favori: işaretlenir, sayfa yenilenince sunucudan da korunur, geri alınır
    await page.getByRole('button', { name: 'Favorilere ekle' }).click();
    await expect(page.getByRole('button', { name: 'Favorilerde' })).toHaveAttribute('aria-pressed', 'true');
    await page.reload();
    await expect(page.getByRole('button', { name: 'Favorilerde' })).toBeVisible();
    await page.getByRole('button', { name: 'Favorilerde' }).click();
    await expect(page.getByRole('button', { name: 'Favorilere ekle' })).toBeVisible();
  });

  test('ağ hatasında aynı Idempotency-Key ile tekrar denenir ve tek iş oluşur', async () => {
    const sessionId = await startSessionViaUi(page, 12);
    sessions.push(sessionId);
    await chooseFirstProduct(page);

    // Sunucu isteği işler ama yanıt istemciye ulaşmaz (ağ kopması)
    const keys: string[] = [];
    let dropped = false;
    await page.route('**/api/v1/sessions/*/generations', async (route) => {
      keys.push(route.request().headers()['idempotency-key'] ?? '');
      if (!dropped) {
        dropped = true;
        await route.fetch();
        await route.abort('connectionreset');
        return;
      }
      await route.continue();
    });

    await page.getByRole('button', { name: 'Üret' }).click();
    await expect(page.getByText('Sunucuya ulaşılamadı.')).toBeVisible();
    expect(jobCount(sessionId)).toBe(1); // sunucu işi açtı, istemci yanıtı alamadı

    await page.getByRole('button', { name: 'Üret' }).click();
    await page.waitForURL(/\/uretim\/|\/sonuc\//, { timeout: 60_000 });
    await page.unroute('**/api/v1/sessions/*/generations');

    expect(keys).toHaveLength(2);
    expect(keys[0]).not.toBe('');
    expect(keys[1]).toBe(keys[0]); // aynı kullanıcı eylemi aynı anahtarı korur
    expect(jobCount(sessionId)).toBe(1); // ikinci iş (ve ikinci maliyet) yok

    // Seçim değişince (yeni kullanıcı eylemi) yeni anahtar kullanılır: farklı ürün seçilince yeni iş açılabilir
    await page.goBack();
  });

  test('başarısız iş: Tekrar dene çift tıklamada ve ağ hatasında tek yeni iş açar', async () => {
    // Mock işi "Queued" iken başarısız yapılır (worker kapmazsa); kapılırsa yeni prova ile yeniden denenir
    let sessionId = '';
    let jobId = '';
    for (let attempt = 0; attempt < 10 && !jobId; attempt++) {
      const created = await api.createSessionWithoutGenerating(`E2E Retry ${attempt}`, 20 + attempt);
      sessions.push(created.sessionId);
      const job = await api.startGeneration(created.sessionId);
      const updated = sqlScalar(
        `UPDATE generation_jobs SET status = 'Failed', error_code = 'ProviderUnavailable' WHERE id = '${job}' AND status = 'Queued'; SELECT @@ROWCOUNT`,
      );
      if (updated.endsWith('1')) {
        sessionId = created.sessionId;
        jobId = job;
      }
    }
    expect(jobId).not.toBe('');

    await page.goto(`/prova/${sessionId}/uretim/${jobId}`);
    await expect(page.getByRole('heading', { name: 'Prova hazırlanamadı' })).toBeVisible();

    const keys: string[] = [];
    let dropped = false;
    await page.route('**/api/v1/jobs/*/retry', async (route) => {
      keys.push(route.request().headers()['idempotency-key'] ?? '');
      if (!dropped) {
        dropped = true;
        await route.fetch();
        await route.abort('connectionreset');
        return;
      }
      await route.continue();
    });

    // 1) yanıt kaybolur: sunucu yeni işi açtı
    await page.getByRole('button', { name: 'Tekrar dene' }).click();
    await expect(page.getByText('Sunucuya ulaşılamadı.')).toBeVisible();
    expect(jobCount(sessionId)).toBe(2);

    // 2) çift tıklama (ağ tekrarı): aynı anahtar, yeni iş açılmaz
    await page.getByRole('button', { name: 'Tekrar dene' }).dblclick();
    await page.waitForURL((url) => !url.pathname.endsWith(jobId), { timeout: 30_000 });
    await page.unroute('**/api/v1/jobs/*/retry');

    expect(keys.length).toBeGreaterThanOrEqual(2);
    expect(new Set(keys).size).toBe(1);
    expect(jobCount(sessionId)).toBe(2);
  });

  test('firma ve şube izolasyonu: diğer şube ve diğer firma provayı göremez', async () => {
    const created = await api.createSessionWithResult('E2E İzolasyon', 31);
    sessions.push(created.sessionId);

    // Aynı firmanın diğer şubesi
    const otherStore = new StaffApi(context.request);
    await otherStore.init('Bebek Şube');
    expect(otherStore.storeId).not.toBe(api.storeId);
    const resB = await context.request.get(`/api/v1/sessions/${created.sessionId}`, { headers: { 'X-Store-Id': otherStore.storeId } });
    expect(resB.status()).toBe(404);
    const listB = await otherStore.get<{ items: { id: string }[] }>('/api/v1/sessions?pageSize=50');
    expect(listB.items.some((s) => s.id === created.sessionId)).toBe(false);

    // Arayüzde şube değiştirince geçmiş boş, doğrudan adres bulunamaz
    await page.goto('/');
    await page.getByRole('button', { name: 'Hesap menüsü' }).click();
    await page.getByRole('menuitem', { name: 'Bebek Şube' }).click();
    await page.goto(`/prova/${created.sessionId}/sonuc/${created.imageId}`);
    await expect(page.locator('img[alt*="sürüm"]')).toHaveCount(0);
    await page.goto('/');
    await page.getByRole('button', { name: 'Hesap menüsü' }).click();
    await page.getByRole('menuitem', { name: MAIN_STORE }).click(); // sonraki testler için eski şube

    // Başka firmanın kullanıcısı: ne oturumu ne görseli görebilir, ne de başka firmanın şubesi adına işlem yapabilir
    const foreign = await browser.newContext({ locale: 'tr-TR' });
    const foreignPage = await foreign.newPage();
    await login(foreignPage, OTHER_FIRM_USER);
    const foreignApi = new StaffApi(foreign.request);
    await foreignApi.init();
    expect((await foreign.request.get(`/api/v1/sessions/${created.sessionId}`, { headers: { 'X-Store-Id': foreignApi.storeId } })).status()).toBe(404);
    expect((await foreign.request.get(`/api/v1/sessions/${created.sessionId}`, { headers: { 'X-Store-Id': api.storeId } })).status()).toBeGreaterThanOrEqual(403);
    expect((await foreign.request.get(`/api/v1/sessions/${created.sessionId}/images`, { headers: { 'X-Store-Id': foreignApi.storeId } })).status()).toBe(404);
    await foreignPage.goto('/gecmis');
    await expect(foreignPage.getByText(created.sessionId)).toHaveCount(0);
    await foreign.close();
  });
});
