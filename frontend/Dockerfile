# syntax=docker/dockerfile:1
# Sanal Prova üretim web imajı (Pilot Faz A): React derlemesi (personel + TV) + Caddy (TLS, statik SPA, /api /hubs /files vekili, /tv).
# Bağlam: ./frontend (bkz. deploy/docker-compose.prod.yml). Gizli değer YOK: site adresi çalışma zamanında SITE_ADDRESS ortam değişkeniyle gelir.
#  - Derleme: npm ci (kilit dosyasından, yeniden üretilebilir) + npm run build (tsc + vite). Test/lint CI'dadır.
#  - Çalışma: caddy:2 (varsayılan kullanıcı root; compose cap_drop ALL + yalnızca NET_BIND_SERVICE ile çalıştırır).
FROM node:22-alpine AS build
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npm run build

FROM caddy:2-alpine AS web
COPY --from=build /src/dist /srv
COPY caddy/ /etc/caddy/
# Derleme zamanı denetimi: yapı çıktısı iki giriş noktasını da içeriyor mu (aksi halde görüntü derlenmez)
RUN test -f /srv/index.html && test -f /srv/tv.html && ls /srv/assets | head -1 > /dev/null
EXPOSE 80 443
