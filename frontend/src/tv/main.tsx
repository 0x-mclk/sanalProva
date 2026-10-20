import '@fontsource/playfair-display/latin-400.css';
import '@fontsource/playfair-display/latin-ext-400.css';
import '@fontsource-variable/inter/wght.css';
import './tv.css';

import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { tokenCssVariables } from '../shared/theme/cssVariables';
import { TvApp } from './TvApp';

// TV paketi Mantine'i yüklemez (eski TV tarayıcıları ve küçük paket boyutu için); aynı token'ları CSS değişkeni olarak kullanır.
for (const [name, value] of Object.entries(tokenCssVariables())) {
  document.documentElement.style.setProperty(name, value);
}

// tv.html'deki yükleme gözcüsüne uygulamanın açıldığını bildirir
(window as unknown as { __tvBooted?: boolean }).__tvBooted = true;

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <TvApp />
  </StrictMode>,
);
