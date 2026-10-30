import type { StaffAssignmentDto, StaffAssignmentRequest, StoreRole } from '../../shared/api/types';

export type RoleChoice = 'none' | 'Staff' | 'Manager';

export const roleLabel: Record<StoreRole, string> = { Staff: 'Personel', Manager: 'Yönetici', Owner: 'Firma sahibi' };

export type CandidateStore = { id: string; name: string; code: string; isActive: boolean };

/** Düzenlenen kişinin mevcut rollerinden form satırlarını üretir (Owner satırları "none" görünür; Owner ayrı anahtardır). */
export function initialChoices(assignments: StaffAssignmentDto[]): Record<string, RoleChoice> {
  const choices: Record<string, RoleChoice> = {};
  for (const a of assignments) {
    choices[a.storeId] = a.role === 'Manager' ? 'Manager' : a.role === 'Staff' ? 'Staff' : 'none';
  }
  return choices;
}

/**
 * Formdan sunucu isteği üretir. Owner: tüm şubelerde Owner (sunucu da böyle normalleştirir). Aksi halde yalnızca seçilen şubeler;
 * Manager'ın göremediği/yönetemediği şubeler listeye girmez (sunucu onları aynen korur).
 */
export function buildAssignments(isOwner: boolean, choices: Record<string, RoleChoice>, stores: CandidateStore[]): StaffAssignmentRequest[] {
  if (isOwner) {
    return stores.map((s) => ({ storeId: s.id, role: 'Owner' }));
  }
  return stores.flatMap((s): StaffAssignmentRequest[] => {
    const choice = choices[s.id];
    return choice && choice !== 'none' ? [{ storeId: s.id, role: choice }] : [];
  });
}

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export type InviteForm = { displayName: string; email: string };

type InviteErrors = Partial<Record<'displayName' | 'email' | 'assignments', string>>;

export function validateInvite(form: InviteForm, assignments: StaffAssignmentRequest[]): InviteErrors {
  const errors: InviteErrors = {};
  if (form.displayName.trim().length < 2) errors.displayName = 'Ad soyad en az 2 karakter olmalı.';
  if (form.displayName.trim().length > 80) errors.displayName = 'Ad soyad en fazla 80 karakter olabilir.';
  if (!EMAIL.test(form.email.trim()) || form.email.trim().length > 254) errors.email = 'Geçerli bir e-posta adresi girin.';
  if (assignments.length === 0) errors.assignments = 'En az bir şube seçin.';
  return errors;
}

export type StoreFormValues = { name: string; code: string; timezone: string; quota: string };

export function validateStore(form: StoreFormValues): Partial<Record<keyof StoreFormValues, string>> {
  const errors: Partial<Record<keyof StoreFormValues, string>> = {};
  if (form.name.trim().length < 2 || form.name.trim().length > 120) errors.name = 'Şube adı 2–120 karakter olmalı.';
  if (!/^[A-Za-z0-9]+(-[A-Za-z0-9]+)*$/.test(form.code.trim()) || form.code.trim().length > 30) errors.code = 'Kod yalnızca harf, rakam ve tire içermeli (en fazla 30).';
  if (!form.timezone.trim()) errors.timezone = 'Saat dilimi gerekli.';
  if (form.quota.trim() !== '') {
    const n = Number(form.quota);
    if (!Number.isInteger(n) || n < 1 || n > 100_000) errors.quota = 'Kota 1–100000 arasında bir tam sayı olmalı (boş bırakılırsa firma ayarı geçerlidir).';
  }
  return errors;
}

export const quotaValue = (quota: string): number | null => (quota.trim() === '' ? null : Number(quota));
