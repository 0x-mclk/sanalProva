import type { ReactNode } from 'react';
import { CheckIcon, UserIcon } from './icons';
import classes from './SessionStrip.module.css';

/** Bej müşteri şeridi (referans: "Zeynep · PR-024 / Boydan fotoğraf hazır ✓"). */
export function SessionStrip({ label, code, status, done = true, trailing }: { label?: string | null; code: string; status: string; done?: boolean; trailing?: ReactNode }) {
  return (
    <div className={classes.strip}>
      <span className={classes.icon}>
        <UserIcon size={18} />
      </span>
      <span className={classes.text}>
        <span className={classes.title}>
          {label ? `${label} · ` : ''}
          {code}
        </span>
        <span className={classes.status}>{status}</span>
      </span>
      {trailing ?? (done ? <CheckIcon className={classes.check} size={18} /> : null)}
    </div>
  );
}
