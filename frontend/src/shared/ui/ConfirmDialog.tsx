import { Button, Group, Modal, Stack, Text } from '@mantine/core';
import type { ReactNode } from 'react';

/** Geri alınamaz işlemler (fotoğraf/oturum silme) için onay penceresi. Odak tuzağı ve Esc ile kapanma Mantine'den gelir. */
export function ConfirmDialog({
  opened,
  title,
  children,
  confirmLabel,
  cancelLabel = 'Vazgeç',
  danger = false,
  loading = false,
  onConfirm,
  onClose,
}: {
  opened: boolean;
  title: string;
  children: ReactNode;
  confirmLabel: string;
  cancelLabel?: string;
  danger?: boolean;
  loading?: boolean;
  onConfirm: () => void;
  onClose: () => void;
}) {
  return (
    <Modal opened={opened} onClose={onClose} title={title} centered radius="lg" padding="lg" closeOnClickOutside={!loading} closeOnEscape={!loading}>
      <Stack gap="lg">
        <Text size="sm" c="dimmed" component="div">
          {children}
        </Text>
        <Group grow gap="sm">
          <Button size="md" variant="default" onClick={onClose} disabled={loading}>
            {cancelLabel}
          </Button>
          <Button size="md" color={danger ? 'red' : 'bordo'} onClick={onConfirm} loading={loading}>
            {confirmLabel}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
