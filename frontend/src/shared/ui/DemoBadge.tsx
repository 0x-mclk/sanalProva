import { InfoIcon } from './icons';
import classes from './DemoBadge.module.css';

/** Mock sağlayıcıdan gelen sonuç etiketi. Görselin üzerine ayrıca sunucuda filigran basılıdır. */
export function DemoBadge({ floating = false }: { floating?: boolean }) {
  return (
    <span className={classes.badge} data-floating={floating || undefined} title="Gerçek yapay zekâ çıktısı değildir">
      DEMO
    </span>
  );
}

export function DemoNotice() {
  return (
    <div className={classes.notice} role="note">
      <InfoIcon size={18} />
      <div>
        <strong>DEMO sonuç.</strong> Bu görsel gerçek bir yapay zekâ çıktısı değildir; müşterinin üzerinde giydirilmiş fotoğrafı gibi sunulmamalıdır.
        Seçilen ürünler fotoğrafın üzerine kart olarak eklenmiştir.
      </div>
    </div>
  );
}
