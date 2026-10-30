import { Badge, Button, Modal, Select, Stack, TextInput } from '@mantine/core';
import { useRef, useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import { ApiError } from '../../shared/api/http';
import { useCreateStore, useManagedStores, useSetStoreStatus, useUpdateStore } from '../../shared/api/management';
import type { StoreAdminDto } from '../../shared/api/types';
import { errorMessage } from '../../shared/i18n/tr';
import { ConfirmDialog, EmptyState, ErrorState, LoadingState, Notice, PlusIcon } from '../../shared/ui';
import classes from './management.module.css';
import { quotaValue, validateStore, type StoreFormValues } from './staffForm';

const TIMEZONES = ['Europe/Istanbul', 'Europe/London', 'Europe/Berlin', 'Europe/Athens', 'Asia/Baku', 'Asia/Dubai', 'UTC'];
const empty: StoreFormValues = { name: '', code: '', timezone: 'Europe/Istanbul', quota: '' };

const errorOf = (caught: unknown) => errorMessage(caught instanceof ApiError ? caught.code : 'UNKNOWN');

/** `/yonetim/subeler`: şube listesi, ekleme/düzenleme ve pasife alma (yalnızca firma sahibi; sunucu da Owner rolünü doğrular). */
export function StoresPage() {
  const query = useManagedStores();
  const [editing, setEditing] = useState<StoreAdminDto | 'new' | null>(null);
  const [toggling, setToggling] = useState<StoreAdminDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const setStatus = useSetStoreStatus();

  const confirmToggle = async () => {
    if (!toggling) return;
    setError(null);
    try {
      await setStatus.mutateAsync({ id: toggling.id, isActive: !toggling.isActive });
    } catch (caught) {
      setError(errorOf(caught));
    }
    setToggling(null);
  };

  return (
    <main className="sp-page">
      <div className="sp-content">
        <Link to="/">← Ana sayfa</Link>
        <div className={classes.header}>
          <h1 className="sp-title" style={{ margin: 0 }}>
            Şubeler
          </h1>
          <Button leftSection={<PlusIcon size={18} />} onClick={() => setEditing('new')}>
            Yeni şube
          </Button>
        </div>

        {error ? <Notice tone="danger">{error}</Notice> : null}
        {query.isLoading ? <LoadingState label="Şubeler yükleniyor" /> : null}
        {query.isError ? <ErrorState error={query.error} onRetry={() => void query.refetch()} /> : null}
        {query.data && query.data.length === 0 ? <EmptyState title="Henüz şube yok" description="İlk şubeyi eklemek için “Yeni şube”yi kullanın." /> : null}

        <ul className={classes.list}>
          {query.data?.map((store) => (
            <li key={store.id} className={classes.card} data-testid={`store-${store.code}`}>
              <div className={classes.badges}>
                <Badge color={store.isActive ? 'green' : 'gray'} variant="light">
                  {store.isActive ? 'Etkin' : 'Pasif'}
                </Badge>
                <Badge variant="light" color="gray">
                  {store.memberCount} kişi
                </Badge>
              </div>
              <p className={classes.cardTitle}>
                {store.name} <span style={{ fontWeight: 400 }}>({store.code})</span>
              </p>
              <p className={classes.cardMeta}>
                {store.timezone} · Günlük üretim kotası: {store.dailyGenerationQuota ?? 'firma ayarı'}
              </p>
              <div className={classes.actions}>
                <Button size="compact-md" variant="default" onClick={() => setEditing(store)}>
                  Düzenle
                </Button>
                <Button size="compact-md" variant="default" color={store.isActive ? 'red' : undefined} onClick={() => setToggling(store)}>
                  {store.isActive ? 'Pasife al' : 'Yeniden etkinleştir'}
                </Button>
              </div>
            </li>
          ))}
        </ul>
      </div>

      {editing ? <StoreFormModal store={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}

      <ConfirmDialog
        opened={toggling !== null}
        title={toggling?.isActive ? 'Şubeyi pasife al' : 'Şubeyi yeniden etkinleştir'}
        confirmLabel={toggling?.isActive ? 'Pasife al' : 'Etkinleştir'}
        danger={Boolean(toggling?.isActive)}
        loading={setStatus.isPending}
        onConfirm={() => void confirmToggle()}
        onClose={() => setToggling(null)}
      >
        {toggling?.isActive
          ? 'Bu şubeye erişim hemen kesilir ve şubenin TV eşleştirmeleri iptal edilir (TV’ler yeniden eşleştirilmelidir). Veriler silinmez. Firmadaki son etkin şube pasife alınamaz.'
          : 'Şube yeniden kullanıma açılır. Daha önce iptal edilen TV’lerin yeniden eşleştirilmesi gerekir.'}
      </ConfirmDialog>
    </main>
  );
}

function StoreFormModal({ store, onClose }: { store: StoreAdminDto | null; onClose: () => void }) {
  const create = useCreateStore();
  const update = useUpdateStore();
  const [form, setForm] = useState<StoreFormValues>(
    store ? { name: store.name, code: store.code, timezone: store.timezone, quota: store.dailyGenerationQuota?.toString() ?? '' } : empty,
  );
  const [fieldErrors, setFieldErrors] = useState<Partial<Record<keyof StoreFormValues, string>>>({});
  const [error, setError] = useState<string | null>(null);
  const inFlight = useRef(false);
  const timezones = TIMEZONES.includes(form.timezone) ? TIMEZONES : [form.timezone, ...TIMEZONES];

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (inFlight.current) return;
    const errors = validateStore(form);
    setFieldErrors(errors);
    if (Object.keys(errors).length > 0) return;
    inFlight.current = true;
    setError(null);
    const body = { name: form.name.trim(), code: form.code.trim(), timezone: form.timezone, dailyGenerationQuota: quotaValue(form.quota) };
    try {
      if (store) await update.mutateAsync({ id: store.id, body });
      else await create.mutateAsync(body);
      onClose();
    } catch (caught) {
      setError(errorOf(caught));
    } finally {
      inFlight.current = false;
    }
  };

  return (
    <Modal opened onClose={onClose} title={store ? 'Şubeyi düzenle' : 'Yeni şube'} centered radius="lg" padding="lg">
      <form onSubmit={(event) => void submit(event)} noValidate>
        <Stack gap="md">
          <TextInput label="Şube adı" value={form.name} onChange={(e) => setForm({ ...form, name: e.currentTarget.value })} error={fieldErrors.name} required />
          <TextInput label="Şube kodu" description="Harf, rakam ve tire; büyük harfe çevrilir." value={form.code} onChange={(e) => setForm({ ...form, code: e.currentTarget.value })} error={fieldErrors.code} required />
          <Select label="Saat dilimi" data={timezones} value={form.timezone} onChange={(value) => setForm({ ...form, timezone: value ?? form.timezone })} allowDeselect={false} error={fieldErrors.timezone} />
          <TextInput label="Günlük üretim kotası (isteğe bağlı)" description="Boşsa firma ayarı geçerlidir." inputMode="numeric" value={form.quota} onChange={(e) => setForm({ ...form, quota: e.currentTarget.value })} error={fieldErrors.quota} />
          {error ? <Notice tone="danger">{error}</Notice> : null}
          <Button type="submit" loading={create.isPending || update.isPending}>
            {store ? 'Kaydet' : 'Şube ekle'}
          </Button>
        </Stack>
      </form>
    </Modal>
  );
}
