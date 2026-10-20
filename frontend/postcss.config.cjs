// Mantine PostCSS ön ayarı + kırılım değişkenleri (tokens.ts → breakpoint ile aynı tutulmalı)
module.exports = {
  plugins: {
    'postcss-preset-mantine': {},
    'postcss-simple-vars': {
      variables: {
        mobile: '30em', // 480px
        tablet: '48em', // 768px
        desktop: '64em', // 1024px
        wide: '80em', // 1280px
        tv: '120em', // 1920px
      },
    },
  },
};
