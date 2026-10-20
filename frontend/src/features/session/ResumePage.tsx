import { Navigate, useParams } from 'react-router';
import { useSession } from '../../shared/api/hooks';
import type { SessionDetail } from '../../shared/api/types';
import { ErrorState, LoadingState } from '../../shared/ui';

/** Oturumun sunucudaki durumundan bir sonraki ekranın yolunu çıkarır. Sayfa yenileme ve "devam eden prova" bu yolu kullanır. */
export function nextRoute(session: Pick<SessionDetail, 'id' | 'nextStep' | 'draft' | 'activeJobId' | 'images'>): string {
  const base = `/prova/${session.id}`;
  switch (session.nextStep) {
    case 'Consent':
      return `${base}/onay`;
    case 'Photo':
      return `${base}/fotograf`;
    case 'Mode':
      return `${base}/tur`;
    case 'Outfit':
      return session.draft?.mode === 'OutfitSet' ? `${base}/takim` : `${base}/kombin`;
    case 'Generating':
      return session.activeJobId ? `${base}/uretim/${session.activeJobId}` : `${base}/sonuc`;
    case 'Result':
      return `${base}/sonuc`;
    case 'Closed':
      return session.images.length > 0 ? `${base}/sonuc` : '/gecmis';
  }
}

export function ResumePage() {
  const { id } = useParams();
  const session = useSession(id);

  if (session.isLoading) {
    return (
      <main className="sp-page">
        <LoadingState label="Prova açılıyor" />
      </main>
    );
  }
  if (session.isError || !session.data) {
    return (
      <main className="sp-page">
        <ErrorState error={session.error} onRetry={() => void session.refetch()} title="Prova açılamadı" />
      </main>
    );
  }
  return <Navigate to={nextRoute(session.data)} replace />;
}
