import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { slugify } from '../catalog-admin/CategoriesPage';
import { ImageCompare } from '../../shared/ui';
import { ThemeProvider } from '../../shared/theme/ThemeProvider';
import { retentionText, timeAgo } from '../../shared/i18n/tr';
import { nextRoute } from './ResumePage';

describe('nextRoute (devam eden prova)', () => {
  const base = { id: 's1', draft: null, activeJobId: null, images: [] };

  it.each([
    ['Consent', '/prova/s1/onay'],
    ['Photo', '/prova/s1/fotograf'],
    ['Mode', '/prova/s1/tur'],
    ['Result', '/prova/s1/sonuc'],
  ] as const)('%s adımı %s yoluna gider', (nextStep, expected) => {
    expect(nextRoute({ ...base, nextStep })).toBe(expected);
  });

  it('kıyafet seçimi taslağın moduna göre doğru kataloğu açar', () => {
    const draft = (mode: 'OutfitSet' | 'FreeCombination') => ({ mode, items: [], warnings: [] });
    expect(nextRoute({ ...base, nextStep: 'Outfit', draft: draft('OutfitSet') })).toBe('/prova/s1/takim');
    expect(nextRoute({ ...base, nextStep: 'Outfit', draft: draft('FreeCombination') })).toBe('/prova/s1/kombin');
  });

  it('üretim sürüyorsa aktif işin durum ekranına gider', () => {
    expect(nextRoute({ ...base, nextStep: 'Generating', activeJobId: 'j9' })).toBe('/prova/s1/uretim/j9');
  });

  it('kapalı provada sonuç varsa sonucu, yoksa geçmişi açar', () => {
    expect(nextRoute({ ...base, nextStep: 'Closed', images: [{}] as never })).toBe('/prova/s1/sonuc');
    expect(nextRoute({ ...base, nextStep: 'Closed' })).toBe('/gecmis');
  });
});

describe('slugify', () => {
  it('Türkçe karakterleri sadeleştirir', () => {
    expect(slugify('Şalvar Takımı')).toBe('salvar-takimi');
    expect(slugify('  Gömlek / Yelek!  ')).toBe('gomlek-yelek');
    expect(slugify('İpek Çiçek Ğ')).toBe('ipek-cicek-g');
  });
});

describe('zaman metinleri', () => {
  const now = new Date('2026-10-20T12:00:00Z').getTime();

  it('göreli zamanı Türkçe verir', () => {
    expect(timeAgo('2026-10-20T11:59:50Z', now)).toBe('az önce');
    expect(timeAgo('2026-10-20T11:58:00Z', now)).toMatch(/2 dakika önce/);
    expect(timeAgo('2026-10-20T09:00:00Z', now)).toMatch(/3 saat önce/);
    expect(timeAgo('2026-10-19T12:00:00Z', now)).toMatch(/dün/);
  });

  it('saklama bitiş tarihini Türkçe biçimler', () => {
    expect(retentionText('2026-10-21T14:30:00Z')).toMatch(/Ekim/);
  });
});

describe('ImageCompare', () => {
  it('kaydırıcı erişilebilir bir aralık girdisidir ve görseli kırpar', () => {
    render(
      <ThemeProvider>
        <ImageCompare left={{ src: '/a.jpg', label: 'Orijinal fotoğraf' }} right={{ src: '/b.jpg', label: 'Sürüm 1' }} />
      </ThemeProvider>,
    );

    const slider = screen.getByLabelText('Karşılaştırma konumu') as HTMLInputElement;
    expect(slider.type).toBe('range');
    expect(slider.value).toBe('50');

    fireEvent.change(slider, { target: { value: '80' } });

    expect(slider.value).toBe('80');
    expect((screen.getByAltText('Sürüm 1') as HTMLImageElement).style.clipPath).toBe('inset(0 0 0 80%)');
    expect(screen.getByAltText('Orijinal fotoğraf')).toBeInTheDocument();
  });
});
