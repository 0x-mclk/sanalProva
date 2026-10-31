import { Badge, Button } from '@mantine/core';
import { Link } from 'react-router';
import { usePlatformOrganizations } from '../../shared/api/platform';
import { EmptyState, ErrorState, LoadingState, PlusIcon } from '../../shared/ui';
import classes from './platform.module.css';

/** `/platform`: işletme listesi; kurulum (Owner şifresi), sözleşme ve ücretli AI durumları. Yalnızca özet bilgi: müşteri/katalog/oturum verisi bu ekrana gelmez. */
export function OrganizationsPage() {
  const query = usePlatformOrganizations();
  return (
    <>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12, margin: '8px 0 16px' }}>
        <h1 className="sp-title" style={{ margin: 0 }}>
          İşletmeler
        </h1>
        <Button component={Link} to="/platform/yeni" leftSection={<PlusIcon size={18} />}>
          Yeni işletme
        </Button>
      </div>

      {query.isLoading ? <LoadingState label="İşletmeler yükleniyor" /> : null}
      {query.isError ? <ErrorState error={query.error} onRetry={() => void query.refetch()} /> : null}
      {query.data && query.data.length === 0 ? <EmptyState title="Henüz işletme yok" description="İlk işletmeyi oluşturmak için “Yeni işletme”yi kullanın." /> : null}

      <ul className={classes.list}>
        {query.data?.map((organization) => (
          <li key={organization.id}>
            <Link to={`/platform/${organization.id}`} className={classes.card}>
              <p className={classes.cardTitle}>{organization.name}</p>
              <p className={classes.cardMeta}>{organization.slug}</p>
              <div className={classes.badges}>
                <Badge color={organization.ownerSetupPending ? 'yellow' : 'green'} variant="light">
                  {organization.ownerSetupPending ? 'Kurulum: şifre bekleniyor' : 'Kurulum: tamamlandı'}
                </Badge>
                <Badge color={organization.agreementAccepted ? 'green' : 'yellow'} variant="light">
                  {organization.agreementAccepted ? 'Sözleşme: kabul edildi' : 'Sözleşme: bekliyor'}
                </Badge>
                {organization.settingsCorrupt ? (
                  <Badge color="red" variant="light">
                    Ayarlar okunamıyor (bozuk)
                  </Badge>
                ) : null}
                <Badge color={organization.paidAiEnabled ? 'bordo' : 'gray'} variant="light">
                  {organization.paidAiEnabled ? 'Ücretli AI: açık' : 'Ücretli AI: kapalı'}
                </Badge>
              </div>
            </Link>
          </li>
        ))}
      </ul>
    </>
  );
}
