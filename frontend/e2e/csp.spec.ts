import { existsSync, readFileSync } from 'node:fs';
import http from 'node:http';
import net from 'node:net';
import { extname, resolve } from 'node:path';
import { expect, test } from '@playwright/test';
import { DEMO_MANAGER, devPassword } from './support';

/**
 * Pilot öncesi kapanış: Content-Security-Policy gerçek tarayıcıda. Üretimde Caddy'nin gönderdiği politika (frontend/caddy/common.caddy) okunur ve DERLENMİŞ arayüz (dist/) küçük bir
 * Node sunucusunda o başlıkla sunulur (/api ve /hubs gerçek API'ye vekillenir, WebSocket dahil). Personel girişi, ana ekran, prova akışı sayfaları ve TV girişi gezilir;
 * tek bir `securitypolicyviolation` olayı bile testi düşürür. Caddy'nin kendisi ve TLS bu testte YOKTUR (Docker gerektirir).
 * `dist/` yoksa atlanır: önce `npm run build`.
 */
const dist = resolve(import.meta.dirname, '../dist');
const caddyFile = resolve(import.meta.dirname, '../caddy/common.caddy');
const API = { host: '127.0.0.1', port: 5080 };
const PORT = 5190;

const MIME: Record<string, string> = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png',
  '.json': 'application/json', '.webmanifest': 'application/manifest+json', '.map': 'application/json', '.ico': 'image/x-icon',
};

function policyFromCaddy(): string {
  const match = /Content-Security-Policy\s+"([^"]+)"/.exec(readFileSync(caddyFile, 'utf8'));
  if (!match) throw new Error('CSP satırı common.caddy içinde bulunamadı.');
  return match[1]!.replaceAll('{http.request.host}', `127.0.0.1:${PORT}`).replace('wss://', 'ws://');
}

function startServer(policy: string): Promise<http.Server> {
  const server = http.createServer((req, res) => {
    const url = req.url ?? '/';
    if (/^\/(api|hubs|files|health)(\/|\?|$)/.test(url)) {
      const upstream = http.request({ ...API, method: req.method, path: url, headers: { ...req.headers, host: `${API.host}:${API.port}` } }, (r) => {
        res.writeHead(r.statusCode ?? 502, r.headers);
        r.pipe(res);
      });
      upstream.on('error', () => res.writeHead(502).end());
      req.pipe(upstream);
      return;
    }
    const path = url.split('?')[0]!;
    const file = path === '/tv' || path === '/tv/' ? '/tv.html' : path;
    let full = resolve(dist, `.${file}`);
    if (!full.startsWith(dist) || !existsSync(full) || full === dist) {
      full = resolve(dist, './index.html');
    }
    res.writeHead(200, { 'Content-Type': MIME[extname(full)] ?? 'application/octet-stream', 'Content-Security-Policy': policy, 'Cache-Control': 'no-store' });
    res.end(readFileSync(full));
  });
  // WebSocket (SignalR): yükseltme isteği API'ye ham TCP ile aktarılır
  server.on('upgrade', (req, socket, head) => {
    const upstream = net.connect(API.port, API.host, () => {
      upstream.write(`${req.method} ${req.url} HTTP/1.1\r\n${Object.entries({ ...req.headers, host: `${API.host}:${API.port}` }).map(([k, v]) => `${k}: ${String(v)}`).join('\r\n')}\r\n\r\n`);
      upstream.write(head);
      socket.pipe(upstream).pipe(socket);
    });
    upstream.on('error', () => socket.destroy());
    socket.on('error', () => upstream.destroy());
  });
  return new Promise((done) => server.listen(PORT, '127.0.0.1', () => done(server)));
}

test.skip(!existsSync(resolve(dist, 'index.html')), 'dist/ yok: önce npm run build');

test('derlenmiş arayüz üretim CSP’siyle personel girişi, ana ekran, prova sayfaları ve TV girişinde ihlal üretmez', async ({ browser }) => {
  const policy = policyFromCaddy();
  // Politika beklenen yönergeleri taşır (yanlışlıkla gevşetilmesin)
  for (const directive of ["default-src 'self'", "script-src 'self'", "object-src 'none'", "frame-ancestors 'none'", "base-uri 'self'"]) expect(policy).toContain(directive);
  expect(policy).not.toMatch(/script-src[^;]*unsafe-(inline|eval)/);

  const server = await startServer(policy);
  try {
    const context = await browser.newContext({ baseURL: `http://127.0.0.1:${PORT}`, locale: 'tr-TR', viewport: { width: 430, height: 932 } });
    const violations: string[] = [];
    await context.addInitScript(() => {
      (window as unknown as { __csp: string[] }).__csp = [];
      document.addEventListener('securitypolicyviolation', (e) => (window as unknown as { __csp: string[] }).__csp.push(`${e.violatedDirective} ${e.blockedURI}`));
    });
    const page = await context.newPage();
    page.on('console', (m) => {
      if (/Content Security Policy|violates the following/i.test(m.text())) violations.push(m.text());
    });
    const collect = async () => violations.push(...(await page.evaluate(() => (window as unknown as { __csp?: string[] }).__csp ?? [])));

    await page.goto('/giris');
    await page.getByLabel('E-posta').fill(DEMO_MANAGER);
    await page.getByLabel('Parola').fill(devPassword());
    await page.getByRole('button', { name: 'Giriş yap' }).click();
    await expect(page.getByRole('heading', { name: /, / }).first()).toBeVisible();
    await page.getByRole('button', { name: 'Hesap menüsü' }).click();
    await expect(page.getByRole('menuitem', { name: 'Ekranı kilitle' })).toBeVisible();
    await page.keyboard.press('Escape');
    await collect();

    await page.goto('/katalog');
    await expect(page.getByRole('heading', { name: 'Ürünler' })).toBeVisible();
    await page.goto('/prova/yeni');
    await expect(page.getByLabel('Müşteri takma adı (isteğe bağlı)')).toBeVisible();
    await collect();

    // TV girişi: kendi sayfası (harici yedek betikler dahil) ve SignalR öncesi eşleştirme kodu isteği
    const tv = await context.newPage();
    tv.on('console', (m) => {
      if (/Content Security Policy|violates the following/i.test(m.text())) violations.push(m.text());
    });
    await context.addInitScript(() => undefined);
    await tv.goto('/tv');
    await expect(tv.locator('#tv-fallback')).toBeHidden();
    await expect(tv.getByText(/\d{3}\s?\d{3}|\d{6}/).first()).toBeVisible({ timeout: 20_000 });
    await tv.waitForTimeout(500);
    violations.push(...(await tv.evaluate(() => (window as unknown as { __csp?: string[] }).__csp ?? [])));

    expect(violations, `CSP ihlalleri:\n${violations.join('\n')}`).toEqual([]);
    await context.close();
  } finally {
    await new Promise((done) => server.close(done));
  }
});
