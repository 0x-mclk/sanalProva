import type { ImageUrls } from '../api/types';
import { CheckIcon } from './icons';
import classes from './ProductCard.module.css';

type ProductCardProps = {
  name: string;
  code: string;
  subtitle?: string | null;
  image?: ImageUrls | null;
  selected: boolean;
  onToggle: () => void;
  /** Demo katalogda kartlarda "Örnek" rozeti gösterilir (referans tasarım). */
  sampleBadge?: boolean;
  disabled?: boolean;
  /** Seçili kartta görünen etiket ("Seçili"). */
  selectedLabel?: string;
};

/**
 * Ürün kartı (referans: Hazır takım kataloğu). Tüm kart tek düğmedir: büyük dokunma alanı, seçili durumu aria-pressed ile bildirir.
 * Görsel 3:4, "Örnek" etiketi sol üstte, seçim halkası sağ üstte, "Seçili" etiketi sağ altta.
 */
export function ProductCard({ name, code, subtitle, image, selected, onToggle, sampleBadge, disabled, selectedLabel = 'Seçili' }: ProductCardProps) {
  return (
    <button type="button" className={classes.card} aria-pressed={selected} disabled={disabled} onClick={onToggle} data-selected={selected || undefined}>
      <span className={classes.media}>
        {image ? <img className={classes.image} src={image.thumbUrl} alt="" loading="lazy" decoding="async" /> : <span className={classes.placeholder} aria-hidden="true" />}
        {sampleBadge ? <span className={classes.sample}>Örnek</span> : null}
        <span className={classes.ring} aria-hidden="true">
          {selected ? <CheckIcon size={14} strokeWidth={2.5} /> : null}
        </span>
        {selected ? <span className={classes.selectedTag}>{selectedLabel}</span> : null}
      </span>
      <span className={classes.body}>
        <span className={classes.name}>{name}</span>
        <span className={classes.code}>{code}</span>
        {subtitle ? <span className={classes.subtitle}>{subtitle}</span> : null}
      </span>
    </button>
  );
}
