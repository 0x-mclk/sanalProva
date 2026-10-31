import { render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ThemeProvider } from '../../shared/theme/ThemeProvider';
import { CatalogStarter } from './CatalogStarter';

let products: { id: string; isActive: boolean; isReadyForAi: boolean }[];
let categories: { id: string }[];

vi.mock('../../shared/api/hooks', async (original) => ({
  ...(await original<typeof import('../../shared/api/hooks')>()),
  useProducts: () => ({ data: { items: products, total: products.length } }),
  useCategories: () => ({ data: categories }),
}));

const view = () =>
  render(
    <ThemeProvider>
      <MemoryRouter>
        <CatalogStarter />
      </MemoryRouter>
    </ThemeProvider>,
  );

beforeEach(() => {
  products = [];
  categories = [];
});

describe('katalog başlangıç kontrol listesi', () => {
  it('boş katalogda hiçbir adım tamam değildir; çekim rehberi açıktır', () => {
    view();
    const box = screen.getByTestId('catalog-starter');
    expect(within(box).getByText('Katalog başlangıcı: 0/4 adım')).toBeInTheDocument();
    expect(within(box).getByText(/Ürün fotoğrafı çekim rehberi/)).toBeInTheDocument();
    expect(within(box).getAllByRole('link', { name: 'Git' }).length).toBeGreaterThan(0);
  });

  it('ilerleme mevcut katalog verisinden hesaplanır: ürün var ama AI referansı yoksa 2/4', () => {
    categories = [{ id: 'k1' }];
    products = [{ id: 'p1', isActive: true, isReadyForAi: false }];
    view();
    expect(screen.getByText('Katalog başlangıcı: 2/4 adım')).toBeInTheDocument();
    expect(screen.getByText(/AI referans görseli yüklendi/)).toBeInTheDocument();
  });

  it('pasif ürün provaya hazır sayılmaz', () => {
    categories = [{ id: 'k1' }];
    products = [{ id: 'p1', isActive: false, isReadyForAi: true }];
    view();
    expect(screen.getByText('Katalog başlangıcı: 2/4 adım')).toBeInTheDocument();
  });

  it('üç hazır ürünle liste tamamlanır ve küçülür', () => {
    categories = [{ id: 'k1' }];
    products = [1, 2, 3].map((n) => ({ id: `p${n}`, isActive: true, isReadyForAi: true }));
    view();
    expect(screen.getByText('Katalog başlangıç listesi tamamlandı')).toBeInTheDocument();
    expect(screen.queryByText(/Ürün fotoğrafı çekim rehberi/)).not.toBeInTheDocument();
  });
});
