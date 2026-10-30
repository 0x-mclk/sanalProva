import { Button, Group } from '@mantine/core';
import { useState } from 'react';
import { Notice } from '../../shared/ui';
import classes from './platform.module.css';

/**
 * Kurulum bağlantısını YALNIZCA bu bileşen yaşarken gösterir. Bağlantı çağıranın yerel durumundadır: sorgu/mutation önbelleğine, localStorage/sessionStorage'a,
 * adres çubuğuna veya günlüğe yazılmaz; kapatılınca/sayfadan ayrılınca bir daha gösterilemez (sunucu da düz token'ı saklamaz — yalnızca özetini).
 */
export function SetupLinkReveal({ link, expiresAt, onClose, recipient = 'Firma sahibine' }: { link: string; expiresAt: string | null | undefined; onClose: () => void; recipient?: string }) {
  const [copied, setCopied] = useState(false);
  const absolute = `${window.location.origin}${link}`;
  const expires = expiresAt ? new Intl.DateTimeFormat('tr-TR', { dateStyle: 'long', timeStyle: 'short' }).format(new Date(expiresAt)) : null;

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(absolute);
      setCopied(true);
    } catch {
      setCopied(false); // pano kapalıysa metin elle seçilebilir (user-select: all)
    }
  };

  return (
    <section className={classes.linkBox} aria-label="Kurulum bağlantısı">
      <strong>Kurulum bağlantısı</strong>
      <p className={classes.linkText} data-testid="setup-link">
        {absolute}
      </p>
      <Notice tone="warning">
        Bu bağlantı <strong>bir daha gösterilmez</strong>. {recipient} güvenli bir kanaldan iletin. Tek kullanımlıktır{expires ? ` ve ${expires} tarihine kadar geçerlidir` : ''}. Kaybolursa yeniden üretebilirsiniz (önceki bağlantı iptal olur).
      </Notice>
      <Group mt="sm" gap="sm">
        <Button variant="default" onClick={() => void copy()}>
          {copied ? 'Kopyalandı' : 'Bağlantıyı kopyala'}
        </Button>
        <Button onClick={onClose}>Kapat (bir daha gösterilmez)</Button>
      </Group>
    </section>
  );
}
