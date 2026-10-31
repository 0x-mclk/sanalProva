import { useState } from 'react';
import { Link } from 'react-router';
import { useCategories, useProducts } from '../../shared/api/hooks';
import { CheckIcon, Notice } from '../../shared/ui';
import classes from './CatalogAdmin.module.css';

/** Pilot katalogu için asgari ürün sayısı (vitrin: müşteri en az birkaç seçenek görmeli). Bilgi amaçlıdır, sunucu kuralı değildir. */
export const STARTER_MIN_PRODUCTS = 3;

export const PRODUCT_PHOTO_TIPS = [
  'Ürünü düz, açık renk bir zeminde veya mankende, önden ve tam görünecek şekilde çekin; kenarlar kadrajdan taşmasın.',
  'Gün ışığına yakın, gölgesiz ve yumuşak ışık kullanın; renk gerçeğe yakın görünsün (filtre kullanmayın).',
  'Telefonu ürüne paralel, sabit tutun; fotoğraf net olsun. Sistem en az 200 piksel kısa kenar ve en çok 10 MB kabul eder; kaliteli sonuç için 1000 pikselden büyük çekin (1600 pikseli aşan kenar küçültülür).',
  'Kıyafeti ütülü ve düzgün serin; askı, etiket ve arka plandaki eşyalar görünmesin.',
  'Hazır takımda takımın tamamını gösteren TEK bir ön cephe fotoğrafı çekin; bunu “AI referansı” olarak işaretleyin.',
  'Her renk için ayrı varyant ve ayrı fotoğraf ekleyin; katalogda olmayan renkler için “Renk önizlemesi” uyarısı gösterilir.',
];

type Step = { label: string; done: boolean; hint: string; to?: string };

/**
 * Katalog başlangıç desteği (Faz C): ilk ürünlerin girilmesi için kontrol listesi ve ürün fotoğrafı çekim rehberi. İlerleme mevcut katalog verisinden hesaplanır
 * (yeni uç nokta/yazma yoktur); liste tamamlanınca kendiliğinden küçülür. Yalnızca katalog yöneticilerine gösterilir.
 */
export function CatalogStarter() {
  const products = useProducts({ status: 'all', pageSize: 100 });
  const categories = useCategories(false);
  const [open, setOpen] = useState(false);
  if (!products.data || !categories.data) {
    return null;
  }

  const items = products.data.items;
  const active = items.filter((p) => p.isActive);
  const ready = active.filter((p) => p.isReadyForAi);
  const steps: Step[] = [
    { label: 'Kategori tanımlı', done: categories.data.length > 0, hint: 'Ürünleri gruplamak için en az bir kategori ekleyin.', to: '/katalog/kategoriler' },
    { label: 'İlk ürün eklendi', done: items.length > 0, hint: 'Ad, kod ve kategoriyle bir ürün oluşturun.', to: '/katalog/urun/yeni' },
    { label: 'Ürünün AI referans görseli yüklendi', done: ready.length > 0, hint: 'Ürün sayfasında ön cephe fotoğrafını yükleyip “AI referansı” işaretleyin.' },
    { label: `Provaya hazır en az ${STARTER_MIN_PRODUCTS} aktif ürün`, done: ready.length >= STARTER_MIN_PRODUCTS, hint: `Şu an ${ready.length} ürün hazır. Müşteriye birkaç seçenek sunmak için ${STARTER_MIN_PRODUCTS} ürüne tamamlayın.` },
  ];
  const doneCount = steps.filter((s) => s.done).length;
  const complete = doneCount === steps.length;

  return (
    <section className={classes.starter} aria-label="Katalog başlangıç kontrol listesi" data-testid="catalog-starter">
      <button type="button" className={classes.starterHead} onClick={() => setOpen((v) => !v)} aria-expanded={open || !complete}>
        <strong>{complete ? 'Katalog başlangıç listesi tamamlandı' : `Katalog başlangıcı: ${doneCount}/${steps.length} adım`}</strong>
        <span>{open || !complete ? 'Gizle' : 'Göster'}</span>
      </button>
      {open || !complete ? (
        <>
          <ul className={classes.starterList}>
            {steps.map((step) => (
              <li key={step.label} data-done={step.done ? true : undefined}>
                <span className={classes.starterMark}>{step.done ? <CheckIcon size={14} /> : null}</span>
                <span>
                  {step.label}
                  {step.done ? null : (
                    <>
                      {' — '}
                      {step.hint}
                      {step.to ? (
                        <>
                          {' '}
                          <Link to={step.to}>Git</Link>
                        </>
                      ) : null}
                    </>
                  )}
                </span>
              </li>
            ))}
          </ul>
          <h2 className={classes.starterTitle}>Ürün fotoğrafı çekim rehberi</h2>
          <ul className={classes.starterTips}>
            {PRODUCT_PHOTO_TIPS.map((tip) => (
              <li key={tip}>{tip}</li>
            ))}
          </ul>
          <Notice tone="info">Yüklenen görseller sunucuda yeniden kodlanır (konum/EXIF bilgisi silinir); sınırlar aşılırsa yükleme anlaşılır bir hata ile reddedilir.</Notice>
        </>
      ) : null}
    </section>
  );
}
