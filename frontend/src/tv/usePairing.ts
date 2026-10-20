import { useEffect, useState } from 'react';
import { pairingStatus, startPairing, type PairingStarted } from './tvApi';

const POLL_MS = 3_000;
const RETRY_MS = 5_000;

type Pairing = {
  code: string | null;
  /** Kodun bitimine kalan saniye (yalnızca görüntü içindir; geçerlilik kararını sunucu verir). */
  remaining: number;
  problem: string | null;
};

/**
 * Eşleştirme kodu üretir, 3 sn'de bir durumu sorar. Sunucu "Claimed" derse gizli değer cihaz belirteci olarak teslim edilir
 * (düz değer yalnızca bu TV'de bilinir). Kod sona ererse (sunucu "Expired") yenisi üretilir. TV saatine güvenilmez.
 */
export function usePairing(onClaimed: (token: string) => void): Pairing {
  const [started, setStarted] = useState<PairingStarted | null>(null);
  const [remaining, setRemaining] = useState(0);
  const [problem, setProblem] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    const wait = (ms: number) => new Promise<void>((resolve) => window.setTimeout(resolve, ms));

    const run = async () => {
      while (active) {
        let pairing: PairingStarted;
        try {
          pairing = await startPairing();
        } catch {
          if (active) setProblem('Sunucuya ulaşılamıyor. Yeniden deneniyor…');
          await wait(RETRY_MS);
          continue;
        }
        if (!active) return;
        setStarted(pairing);
        setProblem(null);

        let watching = true;
        while (active && watching) {
          await wait(POLL_MS);
          if (!active) return;
          try {
            const status = await pairingStatus(pairing.pairingSecret);
            if (status === 'Claimed') {
              onClaimed(pairing.pairingSecret);
              return;
            }
            if (status === 'Expired') watching = false; // yeni kod üret
            else setProblem(null);
          } catch {
            setProblem('Sunucuya ulaşılamıyor. Yeniden deneniyor…');
          }
        }
      }
    };
    void run();
    return () => {
      active = false;
    };
  }, [onClaimed]);

  useEffect(() => {
    if (!started) return;
    const startedAt = Date.now();
    const total = Math.max(0, (new Date(started.expiresAt).getTime() - Date.now()) / 1000);
    const timer = window.setInterval(() => setRemaining(Math.max(0, total - (Date.now() - startedAt) / 1000)), 1_000);
    return () => window.clearInterval(timer);
  }, [started]);

  return { code: started?.code ?? null, remaining, problem };
}
