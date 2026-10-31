import { render, screen } from '@testing-library/react';
import { fireEvent } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ThemeProvider } from '../../shared/theme/ThemeProvider';
import { PhotoApproval, PhotoGuide } from './PhotoGuide';
import { isPhotoApproved, PHOTO_CHECKS } from './photoApproval';

beforeEach(() => window.sessionStorage.clear());

const view = (node: React.ReactNode) => render(<ThemeProvider>{node}</ThemeProvider>);

describe('fotoğraf çekim rehberi ve personel onayı', () => {
  it('rehber silüeti ve çekim ipuçlarını gösterir', () => {
    view(<PhotoGuide />);
    expect(screen.getByLabelText('Tam boy çekim rehberi')).toBeInTheDocument();
    expect(screen.getByText(/2–3 metre uzaktan/)).toBeInTheDocument();
  });

  it('üç madde de işaretlenmeden onay verilmez; hepsi işaretlenince fotoğraf kimliğine bağlı onay yazılır', () => {
    const onChange = vi.fn();
    view(<PhotoApproval photoId="p1" approved={false} onChange={onChange} />);
    const boxes = screen.getAllByRole('checkbox');
    expect(boxes).toHaveLength(PHOTO_CHECKS.length);
    fireEvent.click(boxes[0]!);
    fireEvent.click(boxes[1]!);
    expect(onChange).toHaveBeenLastCalledWith(false);
    expect(isPhotoApproved('p1')).toBe(false);
    fireEvent.click(boxes[2]!);
    expect(onChange).toHaveBeenLastCalledWith(true);
    expect(isPhotoApproved('p1')).toBe(true);
    expect(isPhotoApproved('baska-foto')).toBe(false); // başka fotoğrafın onayı yok
  });

  it('bir madde geri alınırsa onay düşer', () => {
    view(<PhotoApproval photoId="p2" approved={false} onChange={vi.fn()} />);
    const boxes = screen.getAllByRole('checkbox');
    boxes.forEach((b) => fireEvent.click(b));
    expect(isPhotoApproved('p2')).toBe(true);
    fireEvent.click(boxes[1]!);
    expect(isPhotoApproved('p2')).toBe(false);
  });
});
