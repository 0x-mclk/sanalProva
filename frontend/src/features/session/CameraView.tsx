import { useCallback, useEffect, useImperativeHandle, useRef, useState, type Ref } from 'react';
import { CameraFlipIcon, CameraIcon } from '../../shared/ui';
import { BodyGuide } from './BodyGuide';
import classes from './CameraView.module.css';

export type CameraStatus = 'starting' | 'live' | 'denied' | 'unsupported' | 'error';

export type CameraHandle = {
  /** Geçerli kareyi JPEG olarak yakalar. Kamera hazır değilse null. */
  capture: () => Promise<Blob | null>;
};

type CameraViewProps = {
  active: boolean;
  onStatusChange?: (status: CameraStatus) => void;
  ref?: Ref<CameraHandle>;
};

const MESSAGES: Record<Exclude<CameraStatus, 'starting' | 'live'>, { title: string; text: string }> = {
  denied: { title: 'Kamera izni verilmedi', text: 'Tarayıcı ayarlarından kamera iznini açabilir veya fotoğrafı galeriden yükleyebilirsiniz.' },
  unsupported: { title: 'Kamera kullanılamıyor', text: 'Bu cihazda veya bağlantıda kamera açılamadı. Fotoğrafı cihazın kamerasıyla çekip yükleyebilir ya da galeriden seçebilirsiniz.' },
  error: { title: 'Kamera açılamadı', text: 'Kamera başka bir uygulama tarafından kullanılıyor olabilir. Tekrar deneyin veya galeriden yükleyin.' },
};

/**
 * Canlı kamera görüntüsü. Yalnızca `active` iken kamera açık tutulur (ekran değişince veya fotoğraf alınınca kapanır).
 * İzin reddedilirse/kamera yoksa açıklayıcı bir durum gösterir; sayfadaki galeri yükleme her durumda çalışır.
 */
export function CameraView({ active, onStatusChange, ref }: CameraViewProps) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const streamRef = useRef<MediaStream | null>(null);
  const supported = typeof navigator !== 'undefined' && Boolean(navigator.mediaDevices?.getUserMedia);
  const [status, setStatus] = useState<CameraStatus>(supported ? 'starting' : 'unsupported');
  const [facing, setFacing] = useState<'environment' | 'user'>('environment');
  const [attempt, setAttempt] = useState(0);

  const update = useCallback(
    (next: CameraStatus) => {
      setStatus(next);
      onStatusChange?.(next);
    },
    [onStatusChange],
  );

  useEffect(() => {
    if (!active || !supported) {
      return;
    }

    let cancelled = false;
    const video = videoRef.current;
    navigator.mediaDevices
      .getUserMedia({ video: { facingMode: { ideal: facing }, width: { ideal: 1920 }, height: { ideal: 1440 } }, audio: false })
      .then((stream) => {
        if (cancelled) {
          stream.getTracks().forEach((track) => track.stop());
          return;
        }
        streamRef.current = stream;
        if (video) {
          video.srcObject = stream;
          void video.play().catch(() => undefined);
        }
        update('live');
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        const name = error instanceof DOMException ? error.name : '';
        update(name === 'NotAllowedError' || name === 'SecurityError' ? 'denied' : name === 'NotFoundError' || name === 'OverconstrainedError' ? 'unsupported' : 'error');
      });

    return () => {
      cancelled = true;
      streamRef.current?.getTracks().forEach((track) => track.stop());
      streamRef.current = null;
      if (video) {
        video.srcObject = null;
      }
    };
  }, [active, supported, facing, attempt, update]);

  useImperativeHandle(
    ref,
    () => ({
      capture: async () => {
        const video = videoRef.current;
        if (!video || status !== 'live' || !video.videoWidth) {
          return null;
        }
        const canvas = document.createElement('canvas');
        canvas.width = video.videoWidth;
        canvas.height = video.videoHeight;
        canvas.getContext('2d')?.drawImage(video, 0, 0);
        return new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/jpeg', 0.92));
      },
    }),
    [status],
  );

  const problem = status === 'denied' || status === 'unsupported' || status === 'error' ? MESSAGES[status] : null;

  return (
    <div className={classes.root}>
      <video ref={videoRef} className={classes.video} playsInline muted aria-label="Kamera önizlemesi" data-live={status === 'live' || undefined} />

      {status === 'live' ? (
        <>
          <span className={classes.pill}>
            <span className={classes.rec} aria-hidden="true" /> Kamera
          </span>
          <button type="button" className={classes.flip} onClick={() => {
              update('starting');
              setFacing((f) => (f === 'environment' ? 'user' : 'environment'));
            }} aria-label="Kamerayı değiştir">
            <CameraFlipIcon size={18} />
          </button>
        </>
      ) : null}

      <BodyGuide className={classes.guide} />
      <p className={classes.caption}>Düz duruş · İyi ışık · Baş ve ayaklar görünür</p>

      {status === 'starting' ? (
        <div className={classes.overlay} role="status">
          <CameraIcon size={28} />
          <span>Kamera açılıyor…</span>
        </div>
      ) : null}

      {problem ? (
        <div className={classes.overlay} role="alert">
          <CameraIcon size={28} />
          <strong>{problem.title}</strong>
          <span className={classes.overlayText}>{problem.text}</span>
          {status !== 'unsupported' ? (
            <button type="button" className={classes.retry} onClick={() => {
                update('starting');
                setAttempt((n) => n + 1);
              }}>
              Tekrar dene
            </button>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
