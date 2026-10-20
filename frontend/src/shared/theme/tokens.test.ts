import { describe, expect, it } from 'vitest';
import { tokenCssVariables } from './cssVariables';
import { theme } from './theme';
import { tokens } from './tokens';

/** WCAG göreli parlaklık ve kontrast oranı. */
function contrast(foreground: string, background: string) {
  const luminance = (hex: string) => {
    const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255).map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
    return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!;
  };
  const [light, dark] = [luminance(foreground), luminance(background)].sort((a, b) => b - a);
  return (light! + 0.05) / (dark! + 0.05);
}

describe('tasarım token’ları', () => {
  it('her paletin 10 tonu ve geçerli hex değerleri vardır', () => {
    for (const shades of Object.values(tokens.palettes)) {
      expect(shades).toHaveLength(10);
      shades.forEach((shade) => expect(shade).toMatch(/^#[0-9A-F]{6}$/i));
    }
  });

  it('Mantine teması birincil rengi token’dan alır', () => {
    expect(theme.primaryColor).toBe('bordo');
    expect(theme.colors?.bordo?.[theme.primaryShade as number]).toBe(tokens.color.primary);
  });

  it('token’ları birimli --sp-* CSS değişkenlerine çevirir', () => {
    const vars = tokenCssVariables();

    expect(vars['--sp-color-primary']).toBe(tokens.color.primary);
    expect(vars['--sp-color-text-muted']).toBe(tokens.color.textMuted);
    expect(vars['--sp-space-md']).toBe('16px');
    expect(vars['--sp-control-primary']).toBe('52px');
    expect(vars['--sp-motion-normal']).toBe('200ms');
    expect(vars['--sp-tv-bg']).toBe(tokens.tv.bg);
  });

  it.each([
    ['gövde metni / zemin', tokens.color.text, tokens.color.bg, 4.5],
    ['ikincil metin / yüzey', tokens.color.textMuted, tokens.color.surface, 4.5],
    ['buton metni / bordo', tokens.color.textOnPrimary, tokens.color.primary, 4.5],
    ['altın etiket / yüzey', tokens.color.accentText, tokens.color.surface, 4.5],
    ['bağlı rozeti', tokens.color.successText, tokens.color.successBg, 4.5],
    ['TV metni / TV zemini', tokens.tv.text, tokens.tv.bg, 7],
  ])('%s WCAG kontrastını sağlar', (_label, foreground, background, minimum) => {
    expect(contrast(foreground, background)).toBeGreaterThanOrEqual(minimum);
  });

  it('dokunmatik hedefler en az 44px’tir', () => {
    expect(tokens.control.primary).toBeGreaterThanOrEqual(tokens.control.minTouch);
    expect(tokens.control.secondary).toBeGreaterThanOrEqual(tokens.control.minTouch);
  });
});
