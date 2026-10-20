import { tokens } from './tokens';

/**
 * Tüm token'ları `--sp-*` CSS değişkenlerine çevirir (tokens.color.textMuted → --sp-color-text-muted).
 * Mantine'e bağımlı değildir: personel uygulamasında Mantine çözümleyicisi, TV'de doğrudan :root kullanır.
 */
export function tokenCssVariables(): Record<string, string> {
  const vars: Record<string, string> = {};
  const kebab = (s: string) => s.replace(/[A-Z]/g, (m) => `-${m.toLowerCase()}`);
  const add = (group: string, values: Record<string, string | number>, unit = '') => {
    for (const [key, value] of Object.entries(values)) {
      vars[`--sp-${group}-${kebab(key)}`] = typeof value === 'number' ? `${value}${unit}` : value;
    }
  };

  add('color', tokens.color);
  add('tv', { bg: tokens.tv.bg, surface: tokens.tv.surface, text: tokens.tv.text, textMuted: tokens.tv.textMuted, accent: tokens.tv.accent, safeArea: tokens.tv.safeArea });
  add('font', tokens.font);
  add('font-size', tokens.fontSize, 'px');
  add('space', tokens.space, 'px');
  add('radius', tokens.radius, 'px');
  add('shadow', tokens.shadow);
  add('control', tokens.control, 'px');
  add('motion', { fast: `${tokens.motion.fast}ms`, normal: `${tokens.motion.normal}ms`, slow: `${tokens.motion.slow}ms`, easing: tokens.motion.easing });
  vars['--sp-gutter'] = `${tokens.layout.gutter.mobile}px`;
  vars['--sp-gutter-tablet'] = `${tokens.layout.gutter.tablet}px`;
  vars['--sp-content-max-width'] = `${tokens.layout.contentMaxWidth}px`;
  vars['--sp-wide-max-width'] = `${tokens.layout.wideMaxWidth}px`;
  return vars;
}
