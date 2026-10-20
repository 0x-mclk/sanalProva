import type { CSSProperties } from 'react';
import type { VariantDto } from '../../shared/api/types';
import classes from './VariantSwatches.module.css';

/** Seçili ürünün renk varyantları. Renk adı hem etiket hem erişilebilir isimdir (yalnızca renkle ayırt etmeye bağlı kalmaz). */
export function VariantSwatches({ variants, selectedId, onSelect, label }: { variants: readonly VariantDto[]; selectedId: string | null; onSelect: (id: string) => void; label: string }) {
  const active = variants.filter((v) => v.isActive);
  if (active.length < 2) {
    return null;
  }
  const current = active.find((v) => v.id === selectedId);
  return (
    <div className={classes.root} role="radiogroup" aria-label={`${label} renkleri`}>
      <div className={classes.dots}>
        {active.map((variant) => (
          <button
            key={variant.id}
            type="button"
            role="radio"
            aria-checked={variant.id === selectedId}
            aria-label={variant.colorName}
            title={variant.colorName}
            className={classes.dot}
            data-selected={variant.id === selectedId || undefined}
            style={{ '--swatch': variant.colorHex ?? '#d9cfc0' } as CSSProperties}
            onClick={() => onSelect(variant.id)}
          />
        ))}
      </div>
      <span className={classes.name}>{current?.colorName ?? 'Renk seçin'}</span>
    </div>
  );
}
