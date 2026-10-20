/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react';
import { resolve } from 'node:path';
import { defineConfig, type Plugin } from 'vite';
import { VitePWA } from 'vite-plugin-pwa';

const backend = process.env.SANALPROVA_API_URL ?? 'http://127.0.0.1:5080';

/** Backend'e yönlendirilen yollar. Aynı origin: çerez + SignalR için CORS gerekmez. */
const proxied = ['/api', '/files', '/health', '/openapi'];

/** Geliştirmede /tv adresini tv.html giriş noktasına yönlendirir (üretimde reverse proxy yapar). */
function tvEntryRewrite(): Plugin {
  return {
    name: 'sanalprova-tv-entry',
    configureServer(server) {
      server.middlewares.use((req, _res, next) => {
        if (req.url === '/tv' || req.url?.startsWith('/tv?') || req.url?.startsWith('/tv/')) {
          req.url = '/tv.html';
        }
        next();
      });
    },
  };
}

export default defineConfig({
  plugins: [
    react(),
    tvEntryRewrite(),
    VitePWA({
      registerType: 'prompt',
      injectRegister: 'auto',
      manifest: {
        name: 'Sanal Prova',
        short_name: 'Sanal Prova',
        description: 'Mağaza içi yapay zekâ destekli sanal kıyafet prova uygulaması',
        lang: 'tr',
        start_url: '/',
        scope: '/',
        display: 'standalone',
        orientation: 'any',
        theme_color: '#7A1F3A',
        background_color: '#F7F3EC',
        icons: [
          { src: '/icons/icon-192.png', sizes: '192x192', type: 'image/png' },
          { src: '/icons/icon-512.png', sizes: '512x512', type: 'image/png' },
          { src: '/icons/icon-maskable-512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
        ],
      },
      workbox: {
        // Yalnızca uygulama kabuğu önbelleğe alınır. Müşteri görselleri (/files) ve API yanıtları ASLA önbelleğe alınmaz (KVKK).
        globPatterns: ['**/*.{js,css,html,woff2,svg,png}'],
        // Türkçe için latin + latin-ext yeterli; diğer alfabe alt kümeleri gerekirse ağdan yüklenir
        globIgnores: ['**/*-{cyrillic,cyrillic-ext,greek,greek-ext,vietnamese}-*.woff2'],
        navigateFallback: '/index.html',
        navigateFallbackDenylist: [/^\/api\//, /^\/files\//, /^\/hubs\//, /^\/health/, /^\/openapi/, /^\/tv/],
        runtimeCaching: [],
        cleanupOutdatedCaches: true,
      },
      devOptions: { enabled: false },
    }),
  ],
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
    proxy: {
      ...Object.fromEntries(proxied.map((path) => [path, { target: backend, changeOrigin: false }])),
      '/hubs': { target: backend, ws: true, changeOrigin: false },
    },
  },
  build: {
    // Smart TV tarayıcıları için muhafazakâr hedef
    target: 'es2019',
    sourcemap: true,
    rollupOptions: {
      input: {
        main: resolve(import.meta.dirname, 'index.html'),
        tv: resolve(import.meta.dirname, 'tv.html'),
      },
    },
  },
  test: {
    environment: 'jsdom',
    globals: false,
    setupFiles: ['./src/test/setup.ts'],
    css: { modules: { classNameStrategy: 'non-scoped' } },
    // Playwright uçtan uca testleri ayrı çalışır (npm run test:e2e)
    exclude: ['e2e/**', 'node_modules/**', 'dist/**'],
  },
});
