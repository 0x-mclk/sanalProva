import type { ReactNode } from 'react';
import classes from './StickyActionBar.module.css';

/**
 * Ekranın altına sabitlenen eylem çubuğu (referans: "1 TAKIM SEÇİLDİ · Üret"). Güvenli alanı (çentik/ana çubuk) hesaba katar.
 * Sayfa içeriğinin altta kalmaması için sayfada `sp-page--with-bar` sınıfı kullanılmalı.
 */
export function StickyActionBar({ children }: { children: ReactNode }) {
  return (
    <div className={classes.root}>
      <div className={classes.inner}>{children}</div>
    </div>
  );
}
