import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ThemeProvider } from '../../shared/theme/ThemeProvider';
import { PhotoApproval, PhotoGuide } from './PhotoGuide';
import { PHOTO_CHECKS } from './photoApproval';

const approve = vi.fn();
const revoke = vi.fn();

vi.mock('../../shared/api/hooks', async (original) => ({
  ...(await original<typeof import('../../shared/api/hooks')>()),
  useApprovePhoto: () => ({ mutate: approve, isPending: false, isError: false }),
  useRevokePhotoApproval: () => ({ mutate: revoke, isPending: false, isError: false }),
}));

beforeEach(() => {
  approve.mockReset();
  revoke.mockReset();
});

const view = (node: React.ReactNode) => render(<ThemeProvider>{node}</ThemeProvider>);

describe('fotoğraf çekim rehberi ve personel onayı (sunucuya kaydedilir)', () => {
  it('rehber silüeti ve çekim ipuçlarını gösterir', () => {
    view(<PhotoGuide />);
    expect(screen.getByLabelText('Tam boy çekim rehberi')).toBeInTheDocument();
    expect(screen.getByText(/2–3 metre uzaktan/)).toBeInTheDocument();
  });

  it('üç madde de işaretlenmeden sunucuya onay gönderilmez; üçüncüde fotoğraf kimliğiyle gönderilir', () => {
    view(<PhotoApproval sessionId="s1" photoId="p1" approved={false} />);
    const boxes = screen.getAllByRole('checkbox');
    expect(boxes).toHaveLength(PHOTO_CHECKS.length);
    fireEvent.click(boxes[0]!);
    fireEvent.click(boxes[1]!);
    expect(approve).not.toHaveBeenCalled();
    fireEvent.click(boxes[2]!);
    expect(approve).toHaveBeenCalledTimes(1);
    expect(approve.mock.calls[0]![0]).toBe('p1');
    expect(screen.getByText(/Sistem kişi sayısını veya tam boyu kendisi ölçmez/)).toBeInTheDocument(); // otomatik analiz varmış gibi davranılmaz
  });

  it('onaylı fotoğrafta bir madde geri alınınca onay SUNUCUDAN kaldırılır', () => {
    view(<PhotoApproval sessionId="s1" photoId="p2" approved />);
    const boxes = screen.getAllByRole('checkbox');
    expect(boxes.every((b) => (b as HTMLInputElement).checked)).toBe(true);
    fireEvent.click(boxes[1]!);
    expect(revoke).toHaveBeenCalledTimes(1);
    expect(revoke.mock.calls[0]![0]).toBe('p2');
    expect(approve).not.toHaveBeenCalled();
  });
});
