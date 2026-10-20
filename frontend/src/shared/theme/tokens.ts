/**
 * Sanal Prova tasarım token'ları — tek kaynak.
 *
 * Değerler docs/design/reference.png (Figma taslağı) incelenerek çıkarıldı:
 * krem zemin, beyaz kartlar, bordo birincil eylem, altın vurgu, serif başlık + sans gövde.
 * Mantine teması (theme.ts) ve `--sp-*` CSS değişkenleri bu dosyadan türetilir;
 * bir değeri değiştirmek için yalnızca burayı düzenleyin.
 */

type Shades = readonly [string, string, string, string, string, string, string, string, string, string];

/** Mantine için 10 tonluk paletler (0 = en açık). */
export const palettes = {
  /** Birincil marka rengi. Ana ton: index 6. */
  bordo: ['#FBF0F3', '#F3DCE2', '#E6B5C2', '#D88B9F', '#C7647F', '#A93E5C', '#7A1F3A', '#661931', '#521427', '#3F0E1D'],
  /** Vurgu. Ana ton: index 5. */
  altin: ['#FBF6EA', '#F3E8C9', '#E7D3A0', '#D9BE7C', '#CBA95A', '#B8913A', '#9A782E', '#7C6025', '#5F491C', '#433313'],
  /** Sıcak nötr (gri yerine). Zemin, çerçeve ve ikincil metin. */
  krem: ['#FDFBF7', '#F7F3EC', '#EFE8DD', '#E5DCCE', '#D4C8B7', '#B5A895', '#8F8373', '#6F6560', '#4A423D', '#2A2220'],
  /** Durum: bağlı / hazır. */
  yesil: ['#EEF6F0', '#DCEDE1', '#B9DBC3', '#93C7A4', '#6DB386', '#4E9E6C', '#3C8D5A', '#2F6B45', '#234F33', '#173422'],
} as const satisfies Record<string, Shades>;

export const color = {
  /** Uygulama zemini (krem). */
  bg: palettes.krem[1],
  /** Kart ve giriş alanı yüzeyi. */
  surface: '#FFFFFF',
  /** İkincil yüzey: ikon kutuları, bilgi şeritleri ("Zeynep · PR-024" satırı). */
  surfaceMuted: '#F1EADF',
  /** Seçili bilgi şeridi (bej). */
  surfaceAccent: '#EFE4D3',
  border: '#E8E0D4',
  borderStrong: palettes.krem[4],

  text: palettes.krem[9],
  textMuted: palettes.krem[7],
  textSubtle: '#9A8F86',
  textOnPrimary: '#FFFFFF',

  primary: palettes.bordo[6],
  primaryHover: palettes.bordo[7],
  primaryActive: palettes.bordo[8],
  primarySoft: palettes.bordo[0],

  accent: palettes.altin[5],
  /** Küçük altın metin (eyebrow) — beyaz/krem üzerinde AA kontrast için koyulaştırılmış. */
  accentText: '#8A6A24',

  successBg: '#E4F0E7',
  successText: palettes.yesil[7],
  successDot: palettes.yesil[6],

  warningBg: '#FBF1DC',
  warningText: '#7A5A12',
  dangerBg: '#FBE9E7',
  dangerText: '#9B2C1F',

  /** Kamera önizleme / koyu medya alanı. */
  mediaDark: '#2B2422',
  overlayScrim: 'rgba(30, 18, 16, 0.55)',
  focusRing: palettes.altin[5],
} as const;

/** Smart TV: koyu bordo zemin, büyük tipografi (10-foot UI). */
export const tv = {
  bg: '#1E080E',
  surface: palettes.bordo[9],
  text: palettes.krem[0],
  textMuted: palettes.altin[2],
  accent: palettes.altin[4],
  /** Kenarlarda overscan güvenli alanı. */
  safeArea: '5vw',
  fontScale: 1.6,
} as const;

export const font = {
  heading: "'Playfair Display', 'Times New Roman', serif",
  body: "'Inter Variable', 'Inter', system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif",
  mono: "ui-monospace, 'Cascadia Code', Consolas, monospace",
} as const;

/** px. Ölçek referans ekranlardaki metinlerden alındı. */
export const fontSize = {
  caption: 11, // eyebrow: "AI SANAL PROVA", "ÇEKİM SONRASI"
  xs: 12,
  sm: 13,
  md: 15, // gövde
  lg: 17,
  xl: 20,
} as const;

export const heading = {
  display: { size: 30, lineHeight: 1.15 }, // hero: "Hayalindeki kıyafet, birkaç adımda."
  h1: { size: 28, lineHeight: 1.2 }, // "Önce, boydan bir fotoğraf."
  h2: { size: 24, lineHeight: 1.2 }, // "Hazır Takım Seç"
  h3: { size: 20, lineHeight: 1.25 },
  h4: { size: 17, lineHeight: 1.3 }, // "Devam Eden Provalar"
  weight: '400',
} as const;

export const lineHeight = { tight: 1.2, normal: 1.5, relaxed: 1.65 } as const;

/** 4px tabanlı aralık ölçeği (px). */
export const space = { xxs: 4, xs: 8, sm: 12, md: 16, lg: 20, xl: 24, xxl: 32, xxxl: 48 } as const;

export const radius = { xs: 6, sm: 8, md: 12, lg: 16, xl: 20, pill: 999 } as const;

export const shadow = {
  card: '0 1px 2px rgba(42, 34, 32, 0.04), 0 4px 16px rgba(42, 34, 32, 0.05)',
  raised: '0 2px 6px rgba(42, 34, 32, 0.06), 0 12px 32px rgba(42, 34, 32, 0.10)',
  /** Alt sabit eylem çubuğu ("1 takım seçildi · Üret"). */
  bar: '0 -4px 24px rgba(42, 34, 32, 0.08)',
} as const;

/** Dokunmatik hedef yükseklikleri (px). */
export const control = {
  primary: 52, // "Yeni Prova Başlat", "Fotoğraf Çek", "Üret"
  secondary: 44, // "Yeniden Çek" / "Devam Et"
  chip: 36, // "Tümü", "Bindallı", "Şalvar"
  input: 48, // arama alanı
  minTouch: 44,
  appBar: 56,
  stepBar: 3,
} as const;

/** Duyarlı kırılımlar (px). Mobil öncelikli: tasarım <480 için çizildi. */
export const breakpoint = {
  xs: 480,
  sm: 768, // tablet dikey
  md: 1024, // tablet yatay
  lg: 1280,
  xl: 1920, // TV
} as const;

export const layout = {
  /** Sayfa kenar boşluğu: mobil / tablet. */
  gutter: { mobile: space.lg, tablet: space.xxl },
  /** Form ve akış ekranlarında okunabilir en fazla genişlik. */
  contentMaxWidth: 560,
  wideMaxWidth: 1120,
  /** Ürün ızgarası sütun sayısı. */
  catalogColumns: { mobile: 2, tablet: 3, desktop: 4 },
} as const;

export const motion = { fast: 120, normal: 200, slow: 320, easing: 'cubic-bezier(0.2, 0, 0, 1)' } as const;

export const tokens = { palettes, color, tv, font, fontSize, heading, lineHeight, space, radius, shadow, control, breakpoint, layout, motion } as const;

export type Tokens = typeof tokens;
