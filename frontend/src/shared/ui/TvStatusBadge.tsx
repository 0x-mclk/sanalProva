import classes from './TvStatusBadge.module.css';

export type TvConnectionState = 'connected' | 'connecting' | 'offline' | 'none';

const stateLabel: Record<TvConnectionState, string> = {
  connected: 'Bağlı',
  connecting: 'Bağlanıyor',
  offline: 'Çevrimdışı',
  none: 'Bağlı değil',
};

/** Üst çubuktaki TV durumu hapı: "● Salon TV · Bağlı". */
export function TvStatusBadge({ name, state }: { name?: string; state: TvConnectionState }) {
  const label = name ? `${name} · ${stateLabel[state]}` : `TV · ${stateLabel[state]}`;
  return (
    <span className={classes.root} data-state={state} role="status" aria-live="polite">
      <span className={classes.dot} aria-hidden="true" />
      {label}
    </span>
  );
}
