import { chromium, expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { DEMO_MANAGER, devPassword, MAIN_STORE, selectStore, sqlScalar, StaffApi } from './support';

/**
 * KVKK: müşteri verisi silindiğinde veya onay geri alındığında TV'deki görsel temizlenir (sentetik fotoğraf, izole veritabanı).
 * TV gerçek bir ikinci tarayıcıdır; eşleştirme belirteci tarayıcı deposuna yazılır (kod girişi tv.spec.ts'de arayüzden sınanır).
 */
test.describe('TV gizlilik temizliği', () => {
  let staffBrowser: Browser;
  let tvBrowser: Browser;
  let staffContext: BrowserContext;
  let tvContext: BrowserContext;
  let staff: Page;
  let tv: Page;
  let api: StaffApi;
  const sessions: string[] = [];

  test.beforeAll(async () => {
    staffBrowser = await chromium.launch();
    tvBrowser = await chromium.launch();
    staffContext = await staffBrowser.newContext({ viewport: { width: 430, height: 932 }, locale: 'tr-TR' });
    tvContext = await tvBrowser.newContext({ viewport: { width: 1920, height: 1080 }, locale: 'tr-TR' });
    staff = await staffContext.newPage();
    await staff.goto('/giris');
    await staff.getByLabel('E-posta').fill(DEMO_MANAGER);
    await staff.getByLabel('Parola').fill(devPassword());
    await staff.getByRole('button', { name: 'Giriş yap' }).click();
    await expect(staff.getByRole('heading', { name: /, / }).first()).toBeVisible();
    await selectStore(staff, MAIN_STORE);
    api = new StaffApi(staffContext.request);
    await api.init(MAIN_STORE);
    await api.cleanup([]);

    const started = await staffContext.request.post('/api/v1/display/pairing');
    const pairing = (await started.json()) as { code: string; pairingSecret: string };
    await api.send('POST', '/api/v1/displays/claim', { code: pairing.code, name: 'E2E Gizlilik TV' });
    await tvContext.addInitScript((token) => window.localStorage.setItem('sp.tv.token', token), pairing.pairingSecret);
    tv = await tvContext.newPage();
    await tv.goto('/tv');
    await expect(tv.getByRole('heading', { name: 'Büyük ekranda birlikte karar verin.' })).toBeVisible();
  });

  test.afterAll(async () => {
    await api?.cleanup(sessions);
    await staffContext?.close();
    await tvContext?.close();
    await staffBrowser?.close();
    await tvBrowser?.close();
  });

  async function showOnTv(label: string, seed: number): Promise<{ sessionId: string; imageId: string }> {
    const created = await api.createSessionWithResult(label, seed);
    sessions.push(created.sessionId);
    await staff.goto(`/prova/${created.sessionId}/sonuc/${created.imageId}`);
    await staff.getByRole('button', { name: "TV'ye gönder" }).click();
    await expect(staff.getByText("TV'de gösteriliyor")).toBeVisible();
    const image = tv.locator('.tv-image[data-loaded="true"]');
    await expect(image).toBeVisible();
    expect(await image.evaluate((el: HTMLImageElement) => el.naturalWidth)).toBeGreaterThan(0);
    return { sessionId: created.sessionId, imageId: created.imageId };
  }

  test('TV eşleşir ve sonucu gösterir; onay geri alınınca TV temizlenir ve fotoğraf silinir', async () => {
    const { sessionId } = await showOnTv('E2E Onay Geri Alma', 41);

    await api.send('DELETE', `/api/v1/sessions/${sessionId}/consent`);

    await expect(tv.locator('.tv-image')).toHaveCount(0);
    await expect(tv.getByRole('heading', { name: 'Büyük ekranda birlikte karar verin.' })).toBeVisible();
    // Veri sunucuda da gerçekten silindi: onay kaydı ve fotoğraf kalmadı
    expect(Number(sqlScalar(`SELECT COUNT(*) FROM session_photos WHERE session_id = '${sessionId}'`))).toBe(0);
    expect(Number(sqlScalar(`SELECT COUNT(*) FROM generated_images WHERE session_id = '${sessionId}'`))).toBe(0);
  });

  test('provayı arayüzden silince TV temizlenir ve görsel adresi artık açılmaz', async () => {
    const { sessionId, imageId } = await showOnTv('E2E Silinecek', 42);
    const src = await tv.locator('.tv-image').getAttribute('src');
    expect(src).toBeTruthy();

    await staff.goto(`/prova/${sessionId}/sonuc/${imageId}`);
    await staff.getByRole('button', { name: 'Prova işlemleri' }).click();
    await staff.getByRole('menuitem', { name: /Fotoğrafları sil ve provayı bitir/ }).click();
    await staff.getByRole('dialog').getByRole('button', { name: 'Kalıcı olarak sil' }).click();

    await expect(tv.locator('.tv-image')).toHaveCount(0);
    await expect(tv.getByRole('heading', { name: 'Büyük ekranda birlikte karar verin.' })).toBeVisible();
    expect(Number(sqlScalar(`SELECT COUNT(*) FROM tryon_sessions WHERE id = '${sessionId}'`))).toBe(0);
    expect(Number(sqlScalar(`SELECT COUNT(*) FROM generation_jobs WHERE session_id = '${sessionId}'`))).toBe(0);
    // İmzalı adres geçerli olsa bile dosya silinmiştir
    const response = await staffContext.request.get(src!);
    expect(response.status()).toBeGreaterThanOrEqual(400);
  });
});
