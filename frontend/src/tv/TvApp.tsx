import { useCallback, useEffect, useState } from 'react';
import { checkCompatibility, tvStorage } from './compat';
import type { TvImage, TvState } from './tvApi';
import { formatCode, formatRemaining } from './tvSync';
import { usePairing } from './usePairing';
import { useTvConnection, type Link } from './useTvConnection';

/**
 * Smart TV giriş noktası (docs/06-tv-signalr.md). Üç durum: uyumsuz tarayıcı mesajı, eşleştirme (6 haneli kod) ve bağlı ekran.
 * TV'de giriş gerekmez; uzaktan kumandada "Tamam" yalnızca durumu yeniler. Müşteri görselleri yalnızca bellekte tutulur:
 * yerel depolamada yalnızca cihaz belirteci vardır ve görseller sunucudan "önbelleğe alma" yasağıyla gelir.
 */
export function TvApp() {
  const [incompatible] = useState(() => checkCompatibility());
  const [token, setToken] = useState<string | null>(() => tvStorage.get());

  const onClaimed = useCallback((value: string) => {
    tvStorage.set(value);
    setToken(value);
  }, []);
  const onRevoked = useCallback(() => {
    tvStorage.clear();
    setToken(null);
  }, []);

  if (incompatible) {
    return (
      <Shell>
        <h1 className="tv-title">Bu TV tarayıcısı desteklenmiyor</h1>
        <p className="tv-subtitle" role="alert">
          {incompatible}
        </p>
      </Shell>
    );
  }
  return token ? <Connected token={token} onRevoked={onRevoked} /> : <Pairing onClaimed={onClaimed} />;
}

function Shell({ children, footer }: { children: React.ReactNode; footer?: React.ReactNode }) {
  return (
    <main className="tv-screen">
      <div className="tv-brand">
        <span className="tv-logo">lâl</span>
        <span className="tv-eyebrow">Sanal Prova</span>
      </div>
      {children}
      {footer ? <footer className="tv-footer">{footer}</footer> : null}
    </main>
  );
}

function Pairing({ onClaimed }: { onClaimed: (token: string) => void }) {
  const { code, remaining, problem } = usePairing(onClaimed);
  return (
    <Shell
      footer={
        <>
          <span className="tv-dot" data-state={problem ? 'offline' : 'online'} aria-hidden="true" />
          {problem ?? 'Eşleştirme bekleniyor'}
        </>
      }
    >
      <h1 className="tv-title">TV&apos;yi eşleştirin</h1>
      <p className="tv-subtitle">Personel uygulamasında “TV bağlantısı” ekranını açın ve bu kodu girin.</p>
      <div className="tv-code" role="status" aria-live="polite" aria-label={code ? `Eşleştirme kodu ${code.split('').join(' ')}` : 'Kod hazırlanıyor'}>
        {code ? formatCode(code) : '··· ···'}
      </div>
      <p className="tv-hint">{code ? `Kod ${formatRemaining(remaining)} içinde yenilenir · tek kullanımlıktır` : 'Kod hazırlanıyor…'}</p>
    </Shell>
  );
}

const linkLabel: Record<Link, string> = {
  connecting: 'Bağlanıyor',
  online: 'Bağlı',
  reconnecting: 'Bağlantı yeniden kuruluyor',
  offline: 'Bağlantı bekleniyor',
};

function Connected({ token, onRevoked }: { token: string; onRevoked: () => void }) {
  const { view, link, stale, refresh } = useTvConnection(token, onRevoked);

  // Uzaktan kumanda: "Tamam" durumu yeniler. Başka tuş işlevi yoktur.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Enter') refresh();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [refresh]);

  // Ekran uyumasın (destekleyen tarayıcılarda)
  useEffect(() => {
    type Lock = { release: () => Promise<void> };
    const nav = navigator as Navigator & { wakeLock?: { request: (type: 'screen') => Promise<Lock> } };
    let lock: Lock | null = null;
    nav.wakeLock?.request('screen').then((l) => (lock = l)).catch(() => undefined);
    return () => {
      lock?.release().catch(() => undefined);
    };
  }, []);

  const showing = view !== null && !stale && view.mode !== 'Idle' && view.images.length > 0;
  const footer = (
    <>
      <span className="tv-dot" data-state={link === 'online' ? 'online' : 'offline'} aria-hidden="true" />
      {view?.deviceName ? `${view.deviceName} · ` : ''}
      {linkLabel[link]}
    </>
  );

  if (showing && view) {
    return <Stage view={view} footer={footer} />;
  }

  const waiting = stale
    ? { title: 'Bağlantı bekleniyor', text: 'Sunucuyla bağlantı kesildi. Güvenliğiniz için görüntü gizlendi; bağlantı gelince devam edecek.' }
    : view === null
      ? { title: 'Bağlanıyor', text: 'Sunucuyla bağlantı kuruluyor.' }
      : view.hasSession
        ? { title: 'Prova hazırlanıyor', text: 'Sonuç seçildiğinde burada büyük ekranda görünecek.' }
        : { title: 'Büyük ekranda birlikte karar verin.', text: 'Bir prova bu ekrana bağlandığında sonuç burada görünecek.' };
  return (
    <Shell footer={footer}>
      <h1 className="tv-title">{waiting.title}</h1>
      <p className="tv-subtitle">{waiting.text}</p>
    </Shell>
  );
}

function Stage({ view, footer }: { view: TvState; footer: React.ReactNode }) {
  const compare = view.mode === 'Compare' && view.images.length >= 2;
  const images = compare ? view.images.slice(0, 2) : view.images.slice(0, 1);
  return (
    <main className="tv-stage" data-mode={compare ? 'compare' : 'single'}>
      <div className="tv-stage-images">
        {images.map((image) => (
          <Figure key={image.id} image={image} compare={compare} />
        ))}
      </div>
      <footer className="tv-footer tv-footer-stage">{footer}</footer>
    </main>
  );
}

/** Görsel tamamen yüklenip çözülene kadar görünmez; ardından 300 ms yumuşak geçişle belirir (yarım görsel gösterilmez). */
function Figure({ image, compare }: { image: TvImage; compare: boolean }) {
  const [loaded, setLoaded] = useState(false);
  return (
    <figure className="tv-figure" data-compare={compare}>
      <div className="tv-frame">
        <img className="tv-image" data-loaded={loaded} src={image.url} alt={image.label} onLoad={() => setLoaded(true)} />
        {image.isDemo ? <span className="tv-demo">DEMO · Örnek sonuç</span> : null}
      </div>
      <figcaption className="tv-caption">{image.label}</figcaption>
    </figure>
  );
}
