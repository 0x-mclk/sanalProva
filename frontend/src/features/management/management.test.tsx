import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../../shared/api/http';
import type { MeResponse, StaffMemberDto, StoreAdminDto } from '../../shared/api/types';
import { ThemeProvider } from '../../shared/theme/ThemeProvider';
import { StaffPage } from './StaffPage';
import { StoresPage } from './StoresPage';
import { buildAssignments, initialChoices, quotaValue, validateInvite, validateStore } from './staffForm';

const LINK = '/hesap/sifre-belirle#t=' + 'Q'.repeat(43);
const inviteMock = vi.fn();
const issueMock = vi.fn();
const statusMock = vi.fn();
let staff: StaffMemberDto[] = [];
let stores: StoreAdminDto[] = [];
let auth: { me: Partial<MeResponse>; isOwner: boolean };

vi.mock('../../shared/auth/AuthContext', () => ({ useAuth: () => auth }));
vi.mock('../../shared/api/management', async (original) => ({
  ...(await original<typeof import('../../shared/api/management')>()),
  useStaff: () => ({ data: staff, isLoading: false, isError: false, refetch: vi.fn() }),
  useManagedStores: () => ({ data: stores, isLoading: false, isError: false, refetch: vi.fn() }),
  inviteStaff: (...args: unknown[]) => inviteMock(...args),
  issueStaffLink: (...args: unknown[]) => issueMock(...args),
  useSetStaffStatus: () => ({ mutateAsync: (...args: unknown[]) => statusMock(...args), isPending: false }),
  useSetStoreStatus: () => ({ mutateAsync: (...args: unknown[]) => statusMock(...args), isPending: false }),
}));

const member = (over: Partial<StaffMemberDto> = {}): StaffMemberDto => ({
  id: 'u1', displayName: 'Elif Personel', email: 'elif@test.local', isActive: true, setupPending: false, isSelf: false, canManage: true, openLinkExpiresAt: null,
  assignments: [{ storeId: 's1', storeName: 'Merkez', storeCode: 'MRK', storeActive: true, role: 'Staff' }], ...over,
});

const store = (over: Partial<StoreAdminDto> = {}): StoreAdminDto => ({
  id: 's1', name: 'Merkez', code: 'MRK', timezone: 'Europe/Istanbul', dailyGenerationQuota: null, isActive: true, memberCount: 2, createdAt: '2026-01-01T00:00:00Z', ...over,
});

function renderPage(node: React.ReactNode) {
  return render(
    <ThemeProvider>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>{node}</MemoryRouter>
      </QueryClientProvider>
    </ThemeProvider>,
  );
}

beforeEach(() => {
  inviteMock.mockReset();
  issueMock.mockReset();
  statusMock.mockReset();
  window.localStorage.clear();
  window.sessionStorage.clear();
  stores = [store(), store({ id: 's2', name: 'Kadıköy', code: 'KAD' })];
  staff = [member()];
  auth = { isOwner: true, me: { stores: [{ id: 's1', name: 'Merkez', code: 'MRK', role: 'Owner' }] } };
});

describe('form yardımcıları', () => {
  it('Owner tüm şubelerde Owner isteği üretir; diğerleri yalnızca seçilenleri', () => {
    const candidates = [{ id: 'a', name: 'A', code: 'A', isActive: true }, { id: 'b', name: 'B', code: 'B', isActive: true }];
    expect(buildAssignments(true, {}, candidates)).toEqual([{ storeId: 'a', role: 'Owner' }, { storeId: 'b', role: 'Owner' }]);
    expect(buildAssignments(false, { a: 'Manager', b: 'none' }, candidates)).toEqual([{ storeId: 'a', role: 'Manager' }]);
    expect(buildAssignments(false, {}, candidates)).toEqual([]);
  });

  it('mevcut atamalardan form seçimlerini kurar', () => {
    expect(initialChoices(member({ assignments: [{ storeId: 'a', storeName: 'A', storeCode: 'A', storeActive: true, role: 'Manager' }] }).assignments)).toEqual({ a: 'Manager' });
  });

  it('davet doğrulaması Türkçe hata verir', () => {
    expect(validateInvite({ displayName: 'Ay', email: 'x' }, [])).toEqual({ email: 'Geçerli bir e-posta adresi girin.', assignments: 'En az bir şube seçin.' });
    expect(validateInvite({ displayName: 'Ayşe Y', email: 'a@b.co' }, [{ storeId: 'a', role: 'Staff' }])).toEqual({});
  });

  it('şube doğrulaması kod, saat dilimi ve kotayı denetler', () => {
    expect(validateStore({ name: 'Merkez', code: 'M R', timezone: 'Europe/Istanbul', quota: '0' })).toEqual(
      expect.objectContaining({ code: expect.any(String), quota: expect.any(String) }),
    );
    expect(validateStore({ name: 'Merkez', code: 'MRK-1', timezone: 'UTC', quota: '' })).toEqual({});
    expect(quotaValue('')).toBeNull();
    expect(quotaValue('25')).toBe(25);
  });
});

describe('StaffPage', () => {
  it('yalnızca sunucunun izin verdiği (canManage) kişiler için işlem düğmeleri gösterilir', () => {
    staff = [member(), member({ id: 'u2', email: 'sahip@test.local', displayName: 'Sahip', canManage: false, isSelf: true })];
    renderPage(<StaffPage />);
    const managed = screen.getByTestId('staff-elif@test.local');
    expect(within(managed).getByRole('button', { name: 'Yetkileri düzenle' })).toBeInTheDocument();
    const self = screen.getByTestId('staff-sahip@test.local');
    expect(within(self).queryByRole('button')).not.toBeInTheDocument();
    expect(within(self).getByText('Siz')).toBeInTheDocument();
  });

  it('Manager davet formunda yalnızca kendi şubelerini ve yalnızca Personel rolünü görür', () => {
    auth = { isOwner: false, me: { stores: [{ id: 's1', name: 'Merkez', code: 'MRK', role: 'Manager' }, { id: 's9', name: 'Başka', code: 'BSK', role: 'Staff' }] } };
    renderPage(<StaffPage />);
    expect(screen.getByText(/yalnızca kendi şubelerinizdeki/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Personel davet et' }));
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getByText(/Merkez/)).toBeInTheDocument();
    expect(within(dialog).queryByText(/Başka/)).not.toBeInTheDocument();
    expect(within(dialog).queryByText(/Firma sahibi \(tüm şubelerde/)).not.toBeInTheDocument();
  });

  it('davet sonrası bağlantı bir kez gösterilir, kapatılınca DOM’dan silinir ve depolamaya yazılmaz', async () => {
    inviteMock.mockResolvedValue({ member: member({ id: 'u3', displayName: 'Yeni Kişi', setupPending: true }), setupLink: LINK, setupLinkExpiresAt: '2026-11-04T10:00:00Z' });
    stores = [store()];
    const { container } = renderPage(<StaffPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Personel davet et' }));
    fireEvent.change(screen.getByLabelText(/Ad soyad/), { target: { value: 'Yeni Kişi' } });
    fireEvent.change(screen.getByLabelText(/E-posta/), { target: { value: 'yeni@test.local' } });
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Davet et' }));

    expect(await screen.findByTestId('setup-link')).toHaveTextContent(LINK);
    expect(inviteMock).toHaveBeenCalledWith({ email: 'yeni@test.local', displayName: 'Yeni Kişi', assignments: [{ storeId: 's1', role: 'Staff' }] });
    expect(window.localStorage.length + window.sessionStorage.length).toBe(0);
    fireEvent.click(screen.getByRole('button', { name: /Kapat/ }));
    expect(container.innerHTML).not.toContain('Q'.repeat(43));
  });

  it('sunucu hatası Türkçe gösterilir (e-posta alınmış)', async () => {
    inviteMock.mockRejectedValue(new ApiError(409, 'EMAIL_TAKEN', 'x'));
    stores = [store()];
    renderPage(<StaffPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Personel davet et' }));
    fireEvent.change(screen.getByLabelText(/Ad soyad/), { target: { value: 'Yeni Kişi' } });
    fireEvent.change(screen.getByLabelText(/E-posta/), { target: { value: 'yeni@test.local' } });
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Davet et' }));
    expect(await screen.findByText('Bu e-posta adresiyle zaten bir hesap var.')).toBeInTheDocument();
  });

  it('pasife alma onay ister, onaylanınca istek gider', async () => {
    statusMock.mockResolvedValue({});
    renderPage(<StaffPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Pasife al' }));
    expect(statusMock).not.toHaveBeenCalled();
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Pasife al' }));
    await waitFor(() => expect(statusMock).toHaveBeenCalledWith({ id: 'u1', isActive: false }));
  });

  it('son Owner hatası Türkçe gösterilir', async () => {
    statusMock.mockRejectedValue(new ApiError(409, 'LAST_OWNER', 'x'));
    renderPage(<StaffPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Pasife al' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Pasife al' }));
    expect(await screen.findByText(/son etkin firma sahibi/)).toBeInTheDocument();
  });

  it('şifre sıfırlama bağlantısı üretilince bir kez gösterilir', async () => {
    issueMock.mockResolvedValue({ userId: 'u1', setupLink: LINK, expiresAt: '2026-11-04T10:00:00Z', purpose: 'PasswordReset' });
    renderPage(<StaffPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Şifre sıfırlama bağlantısı' }));
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Bağlantı üret' }));
    expect(await screen.findByTestId('setup-link')).toHaveTextContent(LINK);
  });
});

describe('StoresPage', () => {
  it('şubeleri durum ve üye sayısıyla listeler; pasife alma onay ister', async () => {
    statusMock.mockResolvedValue({});
    renderPage(<StoresPage />);
    expect(screen.getByTestId('store-MRK')).toHaveTextContent('2 kişi');
    fireEvent.click(within(screen.getByTestId('store-KAD')).getByRole('button', { name: 'Pasife al' }));
    expect(statusMock).not.toHaveBeenCalled();
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Pasife al' }));
    await waitFor(() => expect(statusMock).toHaveBeenCalledWith({ id: 's2', isActive: false }));
  });

  it('yeni şube formu geçersiz kodu sunucuya göndermeden reddeder', async () => {
    renderPage(<StoresPage />);
    fireEvent.click(screen.getByRole('button', { name: 'Yeni şube' }));
    fireEvent.change(await screen.findByLabelText(/Şube adı/), { target: { value: 'Yeni Şube' } });
    fireEvent.change(screen.getByLabelText(/Şube kodu/), { target: { value: 'a b' } });
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Şube ekle' }));
    expect(screen.getByText(/yalnızca harf, rakam ve tire/)).toBeInTheDocument();
  });
});
