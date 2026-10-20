import { ActionIcon, Text } from '@mantine/core';
import type { ReactNode } from 'react';
import { ArrowLeftIcon } from './icons';
import classes from './AppBar.module.css';

/**
 * Akış ekranlarının üst çubuğu (referans: "← Yeni prova ......... ● Salon TV · Bağlı").
 * Alt navigasyon çubuğu yerine geri butonu + adım göstergesi kullanılır; ana sayfa kendi başlığını çizer.
 */
export function AppBar({ title, onBack, trailing }: { title?: ReactNode; onBack?: () => void; trailing?: ReactNode }) {
  return (
    <header className={classes.root}>
      {onBack ? (
        <ActionIcon variant="subtle" color="dark" size={44} radius="xl" onClick={onBack} aria-label="Geri">
          <ArrowLeftIcon />
        </ActionIcon>
      ) : null}
      <Text component="h1" className={classes.title}>
        {title}
      </Text>
      {trailing ? <div className={classes.trailing}>{trailing}</div> : null}
    </header>
  );
}
