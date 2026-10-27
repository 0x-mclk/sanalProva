import { chromium, expect, test, type Browser, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { DEMO_MANAGER, devPassword, StaffApi } from './support';

const SHOTS = process.env.E2E_SHOTS_DIR ?? resolve(import.meta.dirname, '../test-results/screens');
mkdirSync(SHOTS, { recursive: true });

// 400 ms: görsellerin yumuşak geçişi (300 ms) bitmeden ekran görüntüsü alınmaz
const shot = async (page: Page, name: string) => {
  await page.waitForTimeout(400);
  await page.screenshot({ path: resolve(SHOTS, `${name}.png`) });
};

async function login(page: Page) {
  await page.goto('/giris');
  await page.getByLabel('E-posta').fill(DEMO_MANAGER);
  await page.getByLabel('Parola').fill(devPassword());
  await page.getByRole('button', { name: 'Giriş yap' }).click();
  await expect(page.getByRole('heading', { name: /Hayalindeki|, / }).first()).toBeVisible();
}

/** TV ekranındaki 6 haneli kodu okur ("482 913" → "482913"). */
async function readCode(tv: Page): Promise<string> {
  const code = tv.locator('.tv-code');
  await expect(code).toHaveText(/^\d{3} \d{3}$/);
  return ((await code.textContent()) ?? '').replace(/\s/g, '');
}

test.describe('Smart TV · personel senaryosu', () => {
  let staffBrowser: Browser;
  let tvBrowser: Browser;
  const sessions: string[] = [];
  let api: StaffApi;
  let staff: Page;

  test.beforeAll(async () => {
    // İki ayrı tarayıcı süreci: biri personel (telefon boyutu), biri TV (1920×1080)
    staffBrowser = await chromium.launch();
    tvBrowser = await chromium.launch();
  });

  test.afterAll(async () => {
    await api?.cleanup(sessions);
    await staffBrowser?.close();
    await tvBrowser?.close();
  });

  test('eşleştir, gönder, değiştir, karşılaştır, bağlantıyı kes/bağla, fotoğraf silince temizle', async () => {
    const staffContext = await staffBrowser.newContext({ viewport: { width: 430, height: 932 }, locale: 'tr-TR' });
    const tvContext = await tvBrowser.newContext({ viewport: { width: 1920, height: 1080 }, locale: 'tr-TR' });
    staff = await staffContext.newPage();
    let tv = await tvContext.newPage();

    await test.step('1) Personel girişi; TV /tv açılır ve 6 haneli kodu gösterir', async () => {
      await login(staff);
      api = new StaffApi(staffContext.request);
      await api.init();
      await api.cleanup([]); // önceki yarım kalmış E2E TV'leri

      await tv.goto('/tv');
      await expect(tv.getByRole('heading', { name: /TV.yi eşleştirin/  })).toBeVisible();
    });

    await test.step('2) Kod personel uygulamasında girilir; TV eşleşir ve bekleme ekranına geçer', async () => {
      const code = await readCode(tv);
      await shot(tv, '01-tv-eslestirme-kodu');
      await staff.goto('/tv-cihazlar');
      await expect(staff.getByRole('heading', { name: /Büyük ekranda/ })).toBeVisible();
      // Yanlış kod önce reddedilir (tek kullanımlık, tahmin koruması)
      await staff.getByLabel('Eşleştirme kodu').fill(code === '000000' ? '111111' : '000000');
      await staff.getByLabel('TV adı').fill('E2E Prova Odası TV');
      await staff.getByRole('button', { name: 'Eşleştir' }).click();
      await expect(staff.getByRole('alert')).toContainText('Kod geçersiz');

      await staff.getByLabel('Eşleştirme kodu').fill(code);
      await staff.getByRole('button', { name: 'Eşleştir' }).click();
      await expect(staff.getByText('E2E Prova Odası TV eşleştirildi.')).toBeVisible();

      await expect(tv.getByRole('heading', { name: 'Büyük ekranda birlikte karar verin.' })).toBeVisible();
      // TV hub'a bağlanınca personel listesinde "Çevrimiçi" görünür (SignalR olayı veya yoklama)
      await expect(staff.getByText('Çevrimiçi')).toBeVisible();
      await shot(staff, '02-personel-tv-listesi');
      await shot(tv, '03-tv-bekleme');
    });

    let imageA = '';
    let imageB = '';
    let sessionId = '';
    let photoId = '';

    await test.step('3) Prova oturumu açılır, DEMO sonuç hazırlanır', async () => {
      const created = await api.createSessionWithResult('E2E Müşteri', 3);
      sessionId = created.sessionId;
      photoId = created.photoId;
      imageA = created.imageId;
      sessions.push(sessionId);
      await staff.goto(`/prova/${sessionId}/sonuc/${imageA}`);
      await expect(staff.getByRole('button', { name: "TV'ye gönder" })).toBeVisible();
      await shot(staff, '04-personel-sonuc');
    });

    await test.step('4) DEMO sonuç TV’ye gönderilir', async () => {
      await staff.getByRole('button', { name: "TV'ye gönder" }).click();
      await expect(staff.getByText('TV\'de gösteriliyor')).toBeVisible();
      const image = tv.locator('.tv-image[data-loaded="true"]');
      await expect(image).toBeVisible();
      await expect(tv.getByText('DEMO · Örnek sonuç')).toBeVisible();
      expect(await image.evaluate((el: HTMLImageElement) => el.naturalWidth)).toBeGreaterThan(0);
      await shot(tv, '05-tv-sonuc');
      await shot(staff, '06-personel-tv-baglandi');
    });

    await test.step('5) Başka bir sonuç gönderilir; TV güncellenir', async () => {
      const before = await tv.locator('.tv-image').getAttribute('src');
      imageB = await api.generate(sessionId);
      // TV bu provanın sonucunu tekli gösteriyordu: yeni sonuç hazır olunca kendiliğinden en yeniye geçer
      await expect(tv.locator('.tv-image[data-loaded="true"]')).not.toHaveAttribute('src', before!);
      await staff.goto(`/prova/${sessionId}/sonuc/${imageB}`);
      await staff.getByRole('button', { name: "TV'ye gönder" }).click();
      await expect(staff.getByText('TV\'de gösteriliyor')).toBeVisible();
      await expect(tv.locator('.tv-image[data-loaded="true"]')).not.toHaveAttribute('src', before!);
      await expect(tv.locator('.tv-figure')).toHaveCount(1);
      await shot(tv, '07-tv-yeni-sonuc');
    });

    await test.step('6) Karşılaştırma modu açılır', async () => {
      await staff.goto(`/prova/${sessionId}/karsilastir?a=${imageA}&b=${imageB}`);
      await staff.getByRole('button', { name: "TV'de karşılaştır" }).click();
      await expect(staff.getByText('karşılaştırma TV\'de gösteriliyor')).toBeVisible();
      await expect(tv.locator('.tv-figure')).toHaveCount(2);
      await expect(tv.locator('.tv-image[data-loaded="true"]')).toHaveCount(2);
      await shot(tv, '08-tv-karsilastirma');
      await shot(staff, '09-personel-karsilastirma');
    });

    await test.step('7) TV bağlantısı kesilir; personel çevrimdışı görür, TV dönünce güncel durumu sunucudan alır', async () => {
      await tv.close();
      await staff.goto('/tv-cihazlar');
      await expect(staff.getByText('Çevrimdışı').first()).toBeVisible({ timeout: 20_000 });
      await shot(staff, '10-personel-tv-cevrimdisi');

      // Çevrimdışı TV'ye gönderim açıkça reddedilir
      await staff.goto(`/prova/${sessionId}/sonuc/${imageA}`);
      await staff.getByRole('button', { name: "TV'ye gönder" }).click();
      await expect(staff.getByRole('alert')).toContainText('çevrimdışı');

      // TV kapalıyken durum değişir (olay kaçar): karşılaştırma kapanır, tekli gösterim
      const displays = await api.displays();
      await api.send('PUT', `/api/v1/displays/${displays[0]!.id}/state`, { mode: 'Single', imageIds: [imageA] });

      // TV yeniden açılır: cihaz belirteci tarayıcıda kayıtlı, kod gerekmez
      tv = await tvContext.newPage();
      await tv.goto('/tv');
      await expect(tv.locator('.tv-image[data-loaded="true"]')).toHaveCount(1);
      await expect(tv.locator('.tv-figure')).toHaveCount(1);
      await staff.goto('/tv-cihazlar');
      await expect(staff.getByText('Çevrimiçi')).toBeVisible();
      await shot(tv, '11-tv-yeniden-baglandi');
    });

    await test.step('8) Fotoğraf silinir; TV anında temizlenir', async () => {
      await api.send('DELETE', `/api/v1/sessions/${sessionId}/photos/${photoId}`);
      await expect(tv.locator('.tv-image')).toHaveCount(0);
      await expect(tv.getByRole('heading', { name: 'Prova hazırlanıyor' })).toBeVisible();
      await staff.goto('/tv-cihazlar');
      await expect(staff.getByText('Bekleme ekranı')).toBeVisible();
      await shot(tv, '12-tv-temizlendi');
    });

    await test.step('9) Yönetici eşleştirmeyi kaldırır; TV eşleştirme ekranına döner', async () => {
      await staff.getByRole('button', { name: 'Eşleştirmeyi kaldır' }).click();
      await staff.getByRole('dialog').getByRole('button', { name: 'Eşleştirmeyi kaldır' }).click();
      await expect(tv.getByRole('heading', { name: /TV.yi eşleştirin/  })).toBeVisible();
      await expect(tv.locator('.tv-code')).toHaveText(/^\d{3} \d{3}$/);
      await expect(staff.getByText('Henüz eşleştirilmiş TV yok')).toBeVisible();
      await shot(tv, '13-tv-eslestirme-kaldirildi');
    });
  });

  test('WebSocket olmayan TV tarayıcısında SignalR yedek taşımayla çalışır', async () => {
    const staffContext = await staffBrowser.newContext({ viewport: { width: 430, height: 932 } });
    const staffPage = await staffContext.newPage();
    await login(staffPage);
    api = new StaffApi(staffContext.request);
    await api.init();
    await api.cleanup([]);

    // TV kodu API ile alınır ve yönetici tarafından eşleştirilir; TV tarayıcısında WebSocket yok
    const started = await staffContext.request.post('/api/v1/display/pairing');
    const pairing = (await started.json()) as { code: string; pairingSecret: string };
    await api.send('POST', '/api/v1/displays/claim', { code: pairing.code, name: 'E2E Yedek Taşıma TV' });

    const tvContext = await tvBrowser.newContext({ viewport: { width: 1920, height: 1080 } });
    await tvContext.addInitScript((token) => {
      // Eski TV tarayıcısını taklit et: WebSocket yok, belirteç kayıtlı
      Reflect.deleteProperty(window, 'WebSocket');
      window.localStorage.setItem('sp.tv.token', token);
    }, pairing.pairingSecret);
    const tv = await tvContext.newPage();
    await tv.goto('/tv');
    await expect(tv.getByRole('heading', { name: 'Büyük ekranda birlikte karar verin.' })).toBeVisible();

    // Hub bağlantısı SSE/long polling ile kurulur: personel listesi TV'yi çevrimiçi görür
    await expect.poll(async () => (await api.displays()).find((d) => d.name === 'E2E Yedek Taşıma TV')?.online, { timeout: 20_000 }).toBe(true);

    await api.cleanup([]);
    await expect(tv.getByRole('heading', { name: /TV.yi eşleştirin/  })).toBeVisible();
    await staffContext.close();
    await tvContext.close();
  });

  test('uyumsuz TV tarayıcısında anlaşılır mesaj gösterilir', async () => {
    const context = await tvBrowser.newContext({ viewport: { width: 1920, height: 1080 } });
    await context.addInitScript(() => {
      Object.defineProperty(window, 'fetch', { value: undefined, configurable: true });
    });
    const tv = await context.newPage();
    await tv.goto('/tv');
    await expect(tv.getByRole('heading', { name: 'Bu TV tarayıcısı desteklenmiyor' })).toBeVisible();
    await expect(tv.getByRole('alert')).toContainText('çok eski');
    await shot(tv, '14-tv-uyumsuz-tarayici');
    await context.close();
  });
});
