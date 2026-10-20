import '@fontsource/playfair-display/latin-400.css';
import '@fontsource/playfair-display/latin-ext-400.css';
import '@fontsource/playfair-display/latin-500.css';
import '@fontsource/playfair-display/latin-ext-500.css';
import '@fontsource-variable/inter/wght.css';
import '@mantine/core/styles.css';
import './global.css';

import { MantineProvider } from '@mantine/core';
import type { ReactNode } from 'react';
import { cssVariablesResolver, theme } from './theme';

/**
 * Fontlar paketle birlikte sunulur (Google Fonts'a istek yok): çevrimdışı PWA ve
 * müşteri cihazından üçüncü tarafa IP aktarımı olmaması (KVKK) için. latin-ext Türkçe karakterleri içerir.
 */
export function ThemeProvider({ children }: { children: ReactNode }) {
  return (
    <MantineProvider theme={theme} cssVariablesResolver={cssVariablesResolver} forceColorScheme="light">
      {children}
    </MantineProvider>
  );
}
