import { useId, useState } from 'react';
import classes from './ImageCompare.module.css';

type Side = { src: string; label: string };

/**
 * Önce/sonra karşılaştırma: iki görsel üst üste, kaydırıcı sağ görselin ne kadarının görüneceğini belirler.
 * Kaydırıcı gerçek bir <input type="range">: klavye ve ekran okuyucu ile kullanılabilir, dokunmatikte sürüklenir.
 * Görseller aynı boyutta olmalıdır (aynı fotoğraftan türeyen sürümler).
 */
export function ImageCompare({ left, right }: { left: Side; right: Side }) {
  const [position, setPosition] = useState(50);
  const id = useId();

  return (
    <div className={classes.root}>
      <div className={classes.frame}>
        <img className={classes.base} src={left.src} alt={left.label} draggable={false} />
        <img className={classes.overlay} style={{ clipPath: `inset(0 0 0 ${position}%)` }} src={right.src} alt={right.label} draggable={false} />
        <span className={classes.handle} style={{ left: `${position}%` }} aria-hidden="true">
          <span className={classes.grip}>‹ ›</span>
        </span>
        <span className={classes.tag} data-side="left">
          {left.label}
        </span>
        <span className={classes.tag} data-side="right">
          {right.label}
        </span>
      </div>
      <label htmlFor={id} className={classes.srOnly}>
        Karşılaştırma konumu
      </label>
      <input id={id} className={classes.range} type="range" min={0} max={100} value={position} onChange={(e) => setPosition(Number(e.target.value))} />
    </div>
  );
}

/** Yan yana karşılaştırma (kaydırıcıya alternatif). */
export function SideBySide({ left, right }: { left: Side; right: Side }) {
  return (
    <div className={classes.sideBySide}>
      {[left, right].map((side) => (
        <figure key={side.label} className={classes.figure}>
          <img src={side.src} alt={side.label} draggable={false} />
          <figcaption>{side.label}</figcaption>
        </figure>
      ))}
    </div>
  );
}
