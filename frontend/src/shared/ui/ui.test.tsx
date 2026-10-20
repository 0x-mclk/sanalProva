import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ThemeProvider } from '../theme/ThemeProvider';
import { StepProgress } from './StepProgress';
import { TvStatusBadge } from './TvStatusBadge';
import { AppBar } from './AppBar';

const renderUi = (ui: React.ReactElement) => render(<ThemeProvider>{ui}</ThemeProvider>);

describe('StepProgress', () => {
  it('tamamlanan, etkin ve sonraki adımları işaretler', () => {
    renderUi(<StepProgress steps={['Fotoğraf', 'Prova türü', 'Kıyafet']} current={1} />);

    const steps = screen.getAllByRole('listitem');
    expect(screen.getByRole('list')).toHaveAccessibleName('Adım 2 / 3');
    expect(steps.map((s) => s.getAttribute('data-state'))).toEqual(['done', 'active', 'todo']);
    expect(steps[1]).toHaveAttribute('aria-current', 'step');
    expect(steps[2]).toHaveTextContent('3Kıyafet');
  });
});

describe('TvStatusBadge', () => {
  it('TV adını ve Türkçe durumu canlı bölge olarak gösterir', () => {
    renderUi(<TvStatusBadge name="Salon TV" state="connected" />);

    const badge = screen.getByRole('status');
    expect(badge).toHaveTextContent('Salon TV · Bağlı');
    expect(badge).toHaveAttribute('data-state', 'connected');
  });
});

describe('AppBar', () => {
  it('geri butonu yalnızca onBack verildiğinde görünür', () => {
    const { rerender } = renderUi(<AppBar title="Yeni prova" />);
    expect(screen.queryByRole('button', { name: 'Geri' })).not.toBeInTheDocument();

    rerender(
      <ThemeProvider>
        <AppBar title="Yeni prova" onBack={() => {}} />
      </ThemeProvider>,
    );
    expect(screen.getByRole('button', { name: 'Geri' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Yeni prova' })).toBeInTheDocument();
  });
});
