import { useNavigate, useParams } from 'react-router';
import { AppBar } from './AppBar';
import { TvBadge } from './TvBadge';

/**
 * Akış ekranlarının ortak üst çubuğu: geri + başlık + TV durumu.
 * TV rozeti, rota `/prova/:id/...` altındaysa o provanın bağlı olduğu TV'nin gerçek durumunu gösterir.
 */
export function FlowHeader({ title, back }: { title: string; back?: string | (() => void) }) {
  const navigate = useNavigate();
  const { id } = useParams();
  const onBack = typeof back === 'function' ? back : back ? () => void navigate(back) : () => void navigate(-1);
  return <AppBar title={title} onBack={onBack} trailing={<TvBadge sessionId={id} />} />;
}
