import { Text } from '@mantine/core';
import type { ReactNode } from 'react';
import classes from './Eyebrow.module.css';

/** Küçük, büyük harfli, harf aralıklı bölüm etiketi: "AI SANAL PROVA", "HIZLI BAŞLANGIÇ", "ÇEKİM SONRASI". */
export function Eyebrow({ icon, children, tone = 'accent' }: { icon?: ReactNode; children: ReactNode; tone?: 'accent' | 'muted' | 'inverse' }) {
  return (
    <Text component="span" className={classes.root} data-tone={tone}>
      {icon}
      {children}
    </Text>
  );
}
