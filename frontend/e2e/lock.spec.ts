import { expect, test, type Page } from '@playwright/test';
import { DEMO_MANAGER, devPassword } from './support';

/**
 * Faz C: ekran kilidi (izole sanalprova_e2e, mock sağlayıcı). Elle kilitleme, içeriğin gizlenmesi, PIN ile açma, yenilemenin kilidi açmaması ve yanlış PIN davranışı.
 * Boşta otomatik kilit süresi birim testlerinde (sahte zamanlayıcı) sınanır; 5 ardışık hata/oturum kapatma backend testlerindedir (aynı hesabın diğer testlerini etkilememesi için).
 */
const PIN = '4829';

async function login(page: Page) {
  await page.goto('/giris');
  await page.getByLabel('E-posta').fill(DEMO_MANAGER);
  await page.getByLabel('Parola').fill(devPassword());
  await page.getByRole('button', { name: 'Giriş yap' }).click();
  await expect(page.getByRole('heading', { name: /, / }).first()).toBeVisible();
}

test('PIN belirle → ekranı kilitle → içerik gizli → yanlış PIN reddedilir → yenileme kilidi açmaz → doğru PIN açar', async ({ page }) => {
  await login(page);

  // PIN belirleme: hesap parolası gerekir
  await page.getByRole('button', { name: 'Hesap menüsü' }).click();
  await page.getByRole('menuitem', { name: /Ekran kilidi PIN/ }).click();
  await page.getByLabel('Hesap parolanız').fill(devPassword());
  await page.getByLabel('Yeni PIN', { exact: true }).fill('1234');
  await page.getByLabel('PIN (tekrar)').fill('1234');
  await page.getByRole('button', { name: 'PIN’i kaydet' }).click();
  await expect(page.getByText(/kolay tahmin edilenler kabul edilmez/)).toBeVisible(); // 1234 reddedilir
  await page.getByLabel('Hesap parolanız').fill(devPassword());
  await page.getByLabel('Yeni PIN', { exact: true }).fill(PIN);
  await page.getByLabel('PIN (tekrar)').fill(PIN);
  await page.getByRole('button', { name: 'PIN’i kaydet' }).click();
  await expect(page.getByText('PIN kaydedildi.')).toBeVisible();
  await page.getByRole('button', { name: 'Tamam' }).click();

  // Elle kilitle: müşteri/yönetim içeriği DOM'dan kalkar
  await page.getByRole('button', { name: 'Hesap menüsü' }).click();
  await page.getByRole('menuitem', { name: 'Ekranı kilitle' }).click();
  await expect(page.getByTestId('lock-screen')).toBeVisible();
  await expect(page.getByText('Devam Eden Provalar')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Hesap menüsü' })).toHaveCount(0);

  // Yanlış PIN: kalan hak gösterilir, kilit açılmaz
  await page.getByLabel('PIN').fill('9999');
  await page.getByRole('button', { name: 'Kilidi aç' }).click();
  await expect(page.getByText(/PIN hatalı\. Kalan hak: 4/)).toBeVisible();
  await expect(page.getByTestId('lock-screen')).toBeVisible();

  // Yenileme kilidi AÇMAZ
  await page.reload();
  await expect(page.getByTestId('lock-screen')).toBeVisible();
  await expect(page.getByText('Devam Eden Provalar')).toHaveCount(0);

  // Gizli sekme/depolama dışında kilit URL ile atlatılamaz
  await page.goto('/gecmis');
  await expect(page.getByTestId('lock-screen')).toBeVisible();

  // Doğru PIN açar
  await page.getByLabel('PIN').fill(PIN);
  await page.getByRole('button', { name: 'Kilidi aç' }).click();
  await expect(page.getByTestId('lock-screen')).toHaveCount(0);
  await expect(page.getByRole('heading', { name: /Prova Geçmişi|Geçmiş/ }).first()).toBeVisible();
});
