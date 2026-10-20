import { Badge, Button, Card, Chip, TextInput, createTheme, rem, type CSSVariablesResolver, type MantineColorsTuple } from '@mantine/core';
import { tokenCssVariables } from './cssVariables';
import { tokens } from './tokens';

const px = (value: number) => rem(value);
const em = (value: number) => `${value / 16}em`;

/** Mantine teması: tüm değerler tokens.ts'den gelir. */
export const theme = createTheme({
  primaryColor: 'bordo',
  primaryShade: 6,
  colors: {
    bordo: [...tokens.palettes.bordo] as unknown as MantineColorsTuple,
    altin: [...tokens.palettes.altin] as unknown as MantineColorsTuple,
    krem: [...tokens.palettes.krem] as unknown as MantineColorsTuple,
    yesil: [...tokens.palettes.yesil] as unknown as MantineColorsTuple,
  },
  white: tokens.color.surface,
  black: tokens.color.text,
  autoContrast: true,
  focusRing: 'auto',
  respectReducedMotion: true,
  cursorType: 'pointer',

  fontFamily: tokens.font.body,
  fontFamilyMonospace: tokens.font.mono,
  headings: {
    fontFamily: tokens.font.heading,
    fontWeight: tokens.heading.weight,
    textWrap: 'balance',
    sizes: {
      h1: { fontSize: px(tokens.heading.h1.size), lineHeight: String(tokens.heading.h1.lineHeight) },
      h2: { fontSize: px(tokens.heading.h2.size), lineHeight: String(tokens.heading.h2.lineHeight) },
      h3: { fontSize: px(tokens.heading.h3.size), lineHeight: String(tokens.heading.h3.lineHeight) },
      h4: { fontSize: px(tokens.heading.h4.size), lineHeight: String(tokens.heading.h4.lineHeight) },
      h5: { fontSize: px(tokens.fontSize.lg), lineHeight: '1.35' },
      h6: { fontSize: px(tokens.fontSize.md), lineHeight: '1.4' },
    },
  },
  fontSizes: {
    xs: px(tokens.fontSize.xs),
    sm: px(tokens.fontSize.sm),
    md: px(tokens.fontSize.md),
    lg: px(tokens.fontSize.lg),
    xl: px(tokens.fontSize.xl),
  },
  lineHeights: {
    xs: String(tokens.lineHeight.tight),
    sm: String(tokens.lineHeight.normal),
    md: String(tokens.lineHeight.normal),
    lg: String(tokens.lineHeight.relaxed),
    xl: String(tokens.lineHeight.relaxed),
  },
  spacing: {
    xs: px(tokens.space.xs),
    sm: px(tokens.space.sm),
    md: px(tokens.space.md),
    lg: px(tokens.space.lg),
    xl: px(tokens.space.xl),
  },
  radius: {
    xs: px(tokens.radius.xs),
    sm: px(tokens.radius.sm),
    md: px(tokens.radius.md),
    lg: px(tokens.radius.lg),
    xl: px(tokens.radius.xl),
  },
  defaultRadius: 'md',
  shadows: {
    xs: tokens.shadow.card,
    sm: tokens.shadow.card,
    md: tokens.shadow.raised,
    lg: tokens.shadow.raised,
    xl: tokens.shadow.raised,
  },
  breakpoints: {
    xs: em(tokens.breakpoint.xs),
    sm: em(tokens.breakpoint.sm),
    md: em(tokens.breakpoint.md),
    lg: em(tokens.breakpoint.lg),
    xl: em(tokens.breakpoint.xl),
  },

  components: {
    // Dokunmatik öncelikli: varsayılan buton 52px birincil eylem yüksekliği
    Button: Button.extend({
      defaultProps: { size: 'lg', radius: 'md' },
      vars: (_theme, props) => {
        const height = props.size === 'md' ? tokens.control.secondary : props.size === 'lg' ? tokens.control.primary : undefined;
        return { root: height ? { '--button-height': px(height), '--button-fz': px(tokens.fontSize.md) } : {} };
      },
      styles: { label: { fontWeight: 600, letterSpacing: '0.005em' } },
    }),
    Card: Card.extend({ defaultProps: { radius: 'lg', padding: 'md', withBorder: true, shadow: 'xs' } }),
    Badge: Badge.extend({ defaultProps: { radius: 'xl', variant: 'light' }, styles: { root: { textTransform: 'none', fontWeight: 600 } } }),
    Chip: Chip.extend({ defaultProps: { radius: 'xl', size: 'md' } }),
    TextInput: TextInput.extend({
      defaultProps: { size: 'lg', radius: 'md' },
      vars: () => ({ wrapper: { '--input-height': px(tokens.control.input), '--input-fz': px(tokens.fontSize.md) } }),
    }),
  },
  other: { tokens },
});

/** Tüm token'ları `--sp-*` CSS değişkeni olarak yayınlar ve Mantine'in yüzey renklerini krem paletine bağlar. */
export const cssVariablesResolver: CSSVariablesResolver = () => ({
  variables: tokenCssVariables(),
  light: {
    '--mantine-color-body': tokens.color.bg,
    '--mantine-color-text': tokens.color.text,
    '--mantine-color-dimmed': tokens.color.textMuted,
    '--mantine-color-default-border': tokens.color.border,
    '--mantine-color-placeholder': tokens.color.textSubtle,
  },
  dark: {},
});
