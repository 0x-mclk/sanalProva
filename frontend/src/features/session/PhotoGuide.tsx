import { Checkbox } from '@mantine/core';
import { useState } from 'react';
import { CheckIcon } from '../../shared/ui';
import { BodyGuide } from './BodyGuide';
import { useApprovePhoto, useRevokePhotoApproval } from '../../shared/api/hooks';
import { PHOTO_CHECKS } from './photoApproval';
import classes from './PhotoPage.module.css';

const TIPS = [
  'Tableti dikey tutun; müşteri kadrajın ortasında, ekran göğüs hizasında olsun.',
  '2–3 metre uzaktan çekin: baş ve ayaklar silüetin içinde kalsın.',
  'Sade, açık renk bir arka plan ve önden gelen yumuşak ışık seçin.',
  'Dar veya düz kesim giysi, sade ayakkabı; kollar gövdeden hafifçe açık.',
  'Kadrajda yalnızca müşteri olsun; çanta, ceket veya kalabalık olmasın.',
];

/** Çekim öncesi rehber: silüet ve kısa kontrol listesi (kamera üzerindeki kesikli çerçevenin açıklaması). */
export function PhotoGuide() {
  return (
    <section className={classes.guideCard} aria-label="Tam boy çekim rehberi">
      <BodyGuide className={classes.guideFigure} />
      <div>
        <h2 className={classes.guideTitle}>Tam boy çekim rehberi</h2>
        <ul className={classes.guideList}>
          {TIPS.map((tip) => (
            <li key={tip}>
              <CheckIcon size={14} /> <span>{tip}</span>
            </li>
          ))}
        </ul>
      </div>
    </section>
  );
}

/**
 * Personel onayı: üç madde de işaretlenince SUNUCUYA kaydedilir (onay fotoğraf satırına bağlıdır); bir madde geri alınırsa onay sunucuda da kaldırılır.
 * Görünen durum sunucudaki kayıttır (`approved`), yerel kutular yalnızca giriş alanıdır.
 */
export function PhotoApproval({ sessionId, photoId, approved }: { sessionId: string; photoId: string; approved: boolean }) {
  const approve = useApprovePhoto(sessionId);
  const revoke = useRevokePhotoApproval(sessionId);
  const [checked, setChecked] = useState<boolean[]>(() => PHOTO_CHECKS.map(() => approved));
  const busy = approve.isPending || revoke.isPending;
  const update = (index: number, value: boolean) => {
    const next = checked.map((c, i) => (i === index ? value : c));
    setChecked(next);
    const all = next.every(Boolean);
    if (all && !approved) approve.mutate(photoId);
    else if (!all && approved) revoke.mutate(photoId);
  };
  const failed = approve.isError || revoke.isError;
  return (
    <section className={classes.approval} aria-label="Fotoğraf uygunluk onayı">
      <h2 className={classes.guideTitle}>Üretimden önce kontrol edin</h2>
      {PHOTO_CHECKS.map((label, index) => (
        <Checkbox key={label} label={label} checked={checked[index] ?? false} disabled={busy} onChange={(e) => update(index, e.currentTarget.checked)} />
      ))}
      <p className={classes.checkNote}>
        {failed ? 'Onay kaydedilemedi; tekrar deneyin.' : approved ? 'Fotoğraf uygun bulundu ve kaydedildi; kıyafet seçimine geçebilirsiniz.' : 'Üç maddeyi de onaylamadan devam edilemez. Sistem kişi sayısını veya tam boyu kendisi ölçmez; onay sizin kontrolünüzdür.'}
      </p>
    </section>
  );
}
