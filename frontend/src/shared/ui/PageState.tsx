import { Button, Loader, Stack, Text } from '@mantine/core';
import type { ReactNode } from 'react';
import { ApiError } from '../api/http';
import { errorMessage } from '../i18n/tr';
import { InfoIcon, RefreshIcon } from './icons';
import classes from './PageState.module.css';

export function LoadingState({ label = 'Yükleniyor' }: { label?: string }) {
  return (
    <div className={classes.state} role="status" aria-live="polite">
      <Loader color="bordo" size="md" />
      <Text size="sm" c="dimmed">
        {label}…
      </Text>
    </div>
  );
}

export function ErrorState({ error, onRetry, title = 'Bir sorun oluştu' }: { error: unknown; onRetry?: () => void; title?: string }) {
  const code = error instanceof ApiError ? error.code : 'UNKNOWN';
  return (
    <div className={classes.state} role="alert">
      <span className={classes.icon} data-tone="danger">
        <InfoIcon size={22} />
      </span>
      <Stack gap={4} align="center">
        <Text fw={600}>{title}</Text>
        <Text size="sm" c="dimmed" ta="center">
          {errorMessage(code)}
        </Text>
      </Stack>
      {onRetry ? (
        <Button size="md" variant="default" leftSection={<RefreshIcon size={18} />} onClick={onRetry}>
          Tekrar dene
        </Button>
      ) : null}
    </div>
  );
}

export function EmptyState({ title, description, action }: { title: string; description?: string; action?: ReactNode }) {
  return (
    <div className={classes.state}>
      <span className={classes.icon}>
        <InfoIcon size={22} />
      </span>
      <Stack gap={4} align="center">
        <Text fw={600}>{title}</Text>
        {description ? (
          <Text size="sm" c="dimmed" ta="center">
            {description}
          </Text>
        ) : null}
      </Stack>
      {action}
    </div>
  );
}

/** Satır içi (form altı) hata/uyarı bandı. */
export function Notice({ tone = 'info', children }: { tone?: 'info' | 'warning' | 'danger' | 'success'; children: ReactNode }) {
  return (
    <div className={classes.notice} data-tone={tone} role={tone === 'danger' || tone === 'warning' ? 'alert' : 'status'}>
      <InfoIcon size={18} />
      <div>{children}</div>
    </div>
  );
}
