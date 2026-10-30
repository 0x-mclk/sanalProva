import { Badge, Button, Modal, Select, Stack, Switch, TextInput } from '@mantine/core';
import { useMemo, useRef, useState, type FormEvent } from 'react';
import { Link } from 'react-router';
import { ApiError } from '../../shared/api/http';
import { inviteStaff, issueStaffLink, managementKeys, useManagedStores, useRevokeStaffLinks, useSetStaffAssignments, useSetStaffStatus, useStaff } from '../../shared/api/management';
import type { StaffMemberDto } from '../../shared/api/types';
import { useAuth } from '../../shared/auth/AuthContext';
import { errorMessage } from '../../shared/i18n/tr';
import { ConfirmDialog, EmptyState, ErrorState, LoadingState, Notice, PlusIcon } from '../../shared/ui';
import { useQueryClient } from '@tanstack/react-query';
import { SetupLinkReveal } from '../platform/SetupLinkReveal';
import classes from './management.module.css';
import { buildAssignments, initialChoices, roleLabel, validateInvite, type CandidateStore, type RoleChoice } from './staffForm';

const errorOf = (caught: unknown) => errorMessage(caught instanceof ApiError ? caught.code : 'UNKNOWN');

type Reveal = { link: string; expiresAt: string; recipient: string };

/**
 * `/yonetim/personel`: personel listesi, davet, şube/rol ataması, pasife alma ve bağlantı yönetimi (Owner ve Manager).
 * Kapsam sunucuda hesaplanır (`canManage`): Manager yalnızca kendi şubelerindeki Personel'i yönetir; arayüz yalnızca sunucunun izin verdiği işlemleri gösterir
 * ve her işlemde sunucu yetkiyi yeniden doğrular. Bağlantılar yalnızca bu bileşenin yerel durumunda tutulur (önbelleğe/depolamaya yazılmaz).
 */
export function StaffPage() {
  const { me, isOwner } = useAuth();
  const query = useStaff();
  const ownerStores = useManagedStores(isOwner);
  const [inviting, setInviting] = useState(false);
  const [editing, setEditing] = useState<StaffMemberDto | null>(null);
  const [toggling, setToggling] = useState<StaffMemberDto | null>(null);
  const [linkFor, setLinkFor] = useState<StaffMemberDto | null>(null);
  const [revoking, setRevoking] = useState<StaffMemberDto | null>(null);
  const [reveal, setReveal] = useState<Reveal | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const inFlight = useRef(false);
  const client = useQueryClient();
  const setStatus = useSetStaffStatus();
  const revoke = useRevokeStaffLinks();

  // Atanabilir şubeler: Owner tüm şubeler; Manager yalnızca yönettiği (rolü Manager+) şubeler
  const candidates = useMemo<CandidateStore[]>(() => {
    if (isOwner) return (ownerStores.data ?? []).map((s) => ({ id: s.id, name: s.name, code: s.code, isActive: s.isActive }));
    return (me?.stores ?? []).filter((s) => s.role === 'Manager' || s.role === 'Owner').map((s) => ({ id: s.id, name: s.name, code: s.code, isActive: true }));
  }, [isOwner, ownerStores.data, me?.stores]);

  const run = async (action: () => Promise<void>) => {
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    setError(null);
    try {
      await action();
    } catch (caught) {
      setError(errorOf(caught));
    } finally {
      inFlight.current = false;
      setBusy(false);
    }
  };

  const confirmToggle = () =>
    run(async () => {
      if (!toggling) return;
      await setStatus.mutateAsync({ id: toggling.id, isActive: !toggling.isActive });
      setToggling(null);
    }).finally(() => setToggling(null));

  const confirmLink = () =>
    run(async () => {
      if (!linkFor) return;
      const issued = await issueStaffLink(linkFor.id);
      setReveal({ link: issued.setupLink, expiresAt: issued.expiresAt, recipient: `${linkFor.displayName} adlı kişiye` });
      void client.invalidateQueries({ queryKey: managementKeys.users });
    }).finally(() => setLinkFor(null));

  const confirmRevoke = () =>
    run(async () => {
      if (!revoking) return;
      await revoke.mutateAsync(revoking.id);
    }).finally(() => setRevoking(null));

  return (
    <main className="sp-page">
      <div className="sp-content">
        <Link to="/">← Ana sayfa</Link>
        <div className={classes.header}>
          <h1 className="sp-title" style={{ margin: 0 }}>
            Personel
          </h1>
          <Button leftSection={<PlusIcon size={18} />} onClick={() => setInviting(true)} disabled={candidates.length === 0}>
            Personel davet et
          </Button>
        </div>

        {isOwner ? null : <Notice tone="info">Yönetici olarak yalnızca kendi şubelerinizdeki Personel hesaplarını yönetebilirsiniz.</Notice>}
        {error ? <Notice tone="danger">{error}</Notice> : null}
        {reveal ? <SetupLinkReveal link={reveal.link} expiresAt={reveal.expiresAt} recipient={reveal.recipient} onClose={() => setReveal(null)} /> : null}
        {query.isLoading ? <LoadingState label="Personel yükleniyor" /> : null}
        {query.isError ? <ErrorState error={query.error} onRetry={() => void query.refetch()} /> : null}
        {query.data && query.data.length === 0 ? <EmptyState title="Henüz personel yok" description="Başlamak için “Personel davet et”i kullanın." /> : null}

        <ul className={classes.list}>
          {query.data?.map((member) => (
            <li key={member.id} className={classes.card} data-testid={`staff-${member.email}`}>
              <div className={classes.badges}>
                {member.isSelf ? <Badge variant="light">Siz</Badge> : null}
                <Badge color={member.isActive ? 'green' : 'gray'} variant="light">
                  {member.isActive ? 'Etkin' : 'Pasif'}
                </Badge>
                {member.setupPending ? (
                  <Badge color="yellow" variant="light">
                    Davet bekliyor
                  </Badge>
                ) : null}
              </div>
              <p className={classes.cardTitle}>{member.displayName}</p>
              <p className={classes.cardMeta}>{member.email}</p>
              <p className={classes.cardMeta}>
                {member.assignments.length === 0
                  ? 'Şube ataması yok'
                  : member.assignments.some((a) => a.role === 'Owner')
                    ? 'Firma sahibi (tüm şubeler)'
                    : member.assignments.map((a) => `${a.storeName}: ${roleLabel[a.role]}${a.storeActive ? '' : ' (pasif şube)'}`).join(' · ')}
              </p>
              {member.openLinkExpiresAt ? (
                <p className={classes.cardMeta}>Açık bağlantı: {new Intl.DateTimeFormat('tr-TR', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(member.openLinkExpiresAt))} tarihine kadar geçerli</p>
              ) : null}
              {member.canManage ? (
                <div className={classes.actions}>
                  <Button size="compact-md" variant="default" onClick={() => setEditing(member)} disabled={busy}>
                    Yetkileri düzenle
                  </Button>
                  {member.isActive ? (
                    <Button size="compact-md" variant="default" onClick={() => setLinkFor(member)} disabled={busy}>
                      {member.setupPending ? 'Yeni davet bağlantısı' : 'Şifre sıfırlama bağlantısı'}
                    </Button>
                  ) : null}
                  {member.openLinkExpiresAt ? (
                    <Button size="compact-md" variant="default" onClick={() => setRevoking(member)} disabled={busy}>
                      Bağlantıyı iptal et
                    </Button>
                  ) : null}
                  <Button size="compact-md" variant="default" color={member.isActive ? 'red' : undefined} onClick={() => setToggling(member)} disabled={busy}>
                    {member.isActive ? 'Pasife al' : 'Yeniden etkinleştir'}
                  </Button>
                </div>
              ) : null}
            </li>
          ))}
        </ul>
      </div>

      {inviting ? (
        <InviteModal
          candidates={candidates}
          isOwner={isOwner}
          onClose={() => setInviting(false)}
          onInvited={(r) => {
            setReveal(r);
            void client.invalidateQueries({ queryKey: managementKeys.users });
            void client.invalidateQueries({ queryKey: managementKeys.stores });
          }}
        />
      ) : null}
      {editing ? <AssignmentsModal member={editing} candidates={candidates} isOwner={isOwner} onClose={() => setEditing(null)} /> : null}

      <ConfirmDialog
        opened={toggling !== null}
        title={toggling?.isActive ? 'Hesabı pasife al' : 'Hesabı yeniden etkinleştir'}
        confirmLabel={toggling?.isActive ? 'Pasife al' : 'Etkinleştir'}
        danger={Boolean(toggling?.isActive)}
        loading={busy}
        onConfirm={() => void confirmToggle()}
        onClose={() => setToggling(null)}
      >
        {toggling?.isActive
          ? `${toggling.displayName} artık giriş yapamaz ve açık oturumları bir sonraki istekte sonlanır. Hesap ve geçmiş kayıtlar silinmez; istediğiniz zaman yeniden etkinleştirebilirsiniz.`
          : `${toggling?.displayName ?? ''} yeniden giriş yapabilir (mevcut şifresiyle). Önceki oturumlar geri gelmez.`}
      </ConfirmDialog>

      <ConfirmDialog
        opened={linkFor !== null}
        title={linkFor?.setupPending ? 'Yeni davet bağlantısı' : 'Şifre sıfırlama bağlantısı'}
        confirmLabel="Bağlantı üret"
        loading={busy}
        onConfirm={() => void confirmLink()}
        onClose={() => setLinkFor(null)}
      >
        Önceki açık bağlantı iptal edilir ve yeni bir tek kullanımlık bağlantı üretilir. Bağlantı yalnızca bir kez gösterilir.
        {linkFor?.setupPending ? '' : ' Kişinin açık oturumları sonlanır; şifre, bağlantı kullanılınca değişir.'}
      </ConfirmDialog>

      <ConfirmDialog opened={revoking !== null} title="Bağlantıyı iptal et" confirmLabel="İptal et" danger loading={busy} onConfirm={() => void confirmRevoke()} onClose={() => setRevoking(null)}>
        {revoking?.displayName} için açık olan bağlantı kullanılamaz hale gelir.
      </ConfirmDialog>
    </main>
  );
}

function StoreChoices({ candidates, choices, onChange, allowManager, error }: { candidates: CandidateStore[]; choices: Record<string, RoleChoice>; onChange: (id: string, value: RoleChoice) => void; allowManager: boolean; error?: string }) {
  return (
    <Stack gap="xs">
      {candidates.map((store) => (
        <div key={store.id} className={classes.storeRow}>
          <span>
            {store.name} <span style={{ color: 'var(--sp-color-text-muted)' }}>({store.code}){store.isActive ? '' : ' · pasif'}</span>
          </span>
          <Select
            aria-label={`${store.name} yetkisi`}
            data={[
              { value: 'none', label: 'Erişim yok' },
              { value: 'Staff', label: roleLabel.Staff },
              ...(allowManager ? [{ value: 'Manager', label: roleLabel.Manager }] : []),
            ]}
            value={choices[store.id] ?? 'none'}
            onChange={(value) => onChange(store.id, (value as RoleChoice | null) ?? 'none')}
            allowDeselect={false}
            disabled={!store.isActive && (choices[store.id] ?? 'none') === 'none'}
          />
        </div>
      ))}
      {error ? <Notice tone="danger">{error}</Notice> : null}
    </Stack>
  );
}

function InviteModal({ candidates, isOwner, onClose, onInvited }: { candidates: CandidateStore[]; isOwner: boolean; onClose: () => void; onInvited: (reveal: Reveal) => void }) {
  const active = candidates.filter((s) => s.isActive);
  const [form, setForm] = useState({ displayName: '', email: '' });
  const [choices, setChoices] = useState<Record<string, RoleChoice>>(active.length === 1 ? { [active[0]!.id]: 'Staff' } : {});
  const [ownerRole, setOwnerRole] = useState(false);
  const [fieldErrors, setFieldErrors] = useState<Partial<Record<'displayName' | 'email' | 'assignments', string>>>({});
  const [error, setError] = useState<string | null>(null);
  const [sending, setSending] = useState(false);
  const inFlight = useRef(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (inFlight.current) return;
    const assignments = buildAssignments(isOwner && ownerRole, choices, active);
    const errors = validateInvite(form, assignments);
    setFieldErrors(errors);
    if (Object.keys(errors).length > 0) return;
    inFlight.current = true;
    setSending(true);
    setError(null);
    try {
      const result = await inviteStaff({ email: form.email.trim(), displayName: form.displayName.trim(), assignments });
      onInvited({ link: result.setupLink, expiresAt: result.setupLinkExpiresAt, recipient: `${result.member.displayName} adlı kişiye` });
      onClose();
    } catch (caught) {
      setError(errorOf(caught));
    } finally {
      inFlight.current = false;
      setSending(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title="Personel davet et" centered radius="lg" padding="lg">
      <form onSubmit={(event) => void submit(event)} noValidate>
        <Stack gap="md">
          <TextInput label="Ad soyad" value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.currentTarget.value })} error={fieldErrors.displayName} required />
          <TextInput label="E-posta" type="email" autoComplete="off" value={form.email} onChange={(e) => setForm({ ...form, email: e.currentTarget.value })} error={fieldErrors.email} required />
          {isOwner ? <Switch label="Firma sahibi (tüm şubelerde tam yetki)" checked={ownerRole} onChange={(e) => setOwnerRole(e.currentTarget.checked)} /> : null}
          {ownerRole ? null : (
            <StoreChoices
              candidates={active}
              choices={choices}
              onChange={(id, value) => setChoices({ ...choices, [id]: value })}
              allowManager={isOwner}
              error={fieldErrors.assignments}
            />
          )}
          <Notice tone="info">Kişiye e-posta gitmez: davet bağlantısı bir sonraki adımda yalnızca bir kez gösterilir; güvenli bir kanaldan iletin.</Notice>
          {error ? <Notice tone="danger">{error}</Notice> : null}
          <Button type="submit" loading={sending}>
            Davet et
          </Button>
        </Stack>
      </form>
    </Modal>
  );
}

function AssignmentsModal({ member, candidates, isOwner, onClose }: { member: StaffMemberDto; candidates: CandidateStore[]; isOwner: boolean; onClose: () => void }) {
  const save = useSetStaffAssignments();
  const memberIsOwner = member.assignments.some((a) => a.role === 'Owner');
  // Pasif şubeler yalnızca mevcut atamaları varsa gösterilir
  const shown = candidates.filter((s) => s.isActive || member.assignments.some((a) => a.storeId === s.id));
  const [choices, setChoices] = useState<Record<string, RoleChoice>>(initialChoices(member.assignments));
  const [ownerRole, setOwnerRole] = useState(memberIsOwner);
  const [error, setError] = useState<string | null>(null);
  const inFlight = useRef(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (inFlight.current) return;
    const assignments = buildAssignments(isOwner && ownerRole, choices, shown);
    inFlight.current = true;
    setError(null);
    try {
      await save.mutateAsync({ id: member.id, assignments });
      onClose();
    } catch (caught) {
      setError(errorOf(caught));
    } finally {
      inFlight.current = false;
    }
  };

  return (
    <Modal opened onClose={onClose} title={`Yetkiler: ${member.displayName}`} centered radius="lg" padding="lg">
      <form onSubmit={(event) => void submit(event)} noValidate>
        <Stack gap="md">
          {isOwner ? <Switch label="Firma sahibi (tüm şubelerde tam yetki)" checked={ownerRole} onChange={(e) => setOwnerRole(e.currentTarget.checked)} /> : null}
          {ownerRole ? (
            <Notice tone="warning">Firma sahibi tüm şubelerde tam yetkiye sahiptir ve şube/personel yönetimi yapabilir.</Notice>
          ) : (
            <StoreChoices candidates={shown} choices={choices} onChange={(id, value) => setChoices({ ...choices, [id]: value })} allowManager={isOwner} />
          )}
          {memberIsOwner && !ownerRole ? <Notice tone="warning">Firma sahibi yetkisi kaldırılacak; kişi yalnızca seçtiğiniz şubelerde çalışabilir. Firmadaki son etkin firma sahibi düşürülemez.</Notice> : null}
          {error ? <Notice tone="danger">{error}</Notice> : null}
          <Button type="submit" loading={save.isPending}>
            Kaydet
          </Button>
        </Stack>
      </form>
    </Modal>
  );
}
