import { Badge } from '@mantine/core';
import { Link } from 'react-router';
import { usePlatformFinance, usePlatformMonitoring, type MonitorCheck, type OrganizationFinance } from '../../shared/api/platform';
import { EmptyState, ErrorState, LoadingState } from '../../shared/ui';
import classes from './platform.module.css';

const LEVEL_COLOR = { Ok: 'green', Warning: 'yellow', Critical: 'red' } as const;
const LEVEL_LABEL = { Ok: 'Tamam', Warning: 'Uyarı', Critical: 'Kritik' } as const;

const usd = (value: number) => `$${value.toFixed(2)}`;

function ChecksSection({ checks }: { checks: MonitorCheck[] }) {
  return (
    <ul className={classes.list} aria-label="Servis ve izleme kontrolleri">
      {checks.map((check) => (
        <li key={check.name} className={classes.card} data-testid={`check-${check.name}`}>
          <div className={classes.badges}>
            <Badge color={LEVEL_COLOR[check.level]} variant="light">
              {LEVEL_LABEL[check.level]}
            </Badge>
            <span className={classes.cardMeta}>{check.name}</span>
          </div>
          <p className={classes.cardMeta}>{check.message}</p>
        </li>
      ))}
    </ul>
  );
}

function OrganizationRow({ f }: { f: OrganizationFinance }) {
  return (
    <li>
      <Link to={`/platform/${f.organizationId}`} className={classes.card} data-testid={`finance-${f.slug}`}>
        <p className={classes.cardTitle}>{f.name}</p>
        <p className={classes.cardMeta}>{f.slug}</p>
        <div className={classes.badges}>
          <Badge color={f.paidAiEnabled ? 'bordo' : 'gray'} variant="light">
            {f.paidAiEnabled ? 'Ücretli AI: açık' : 'Ücretli AI: kapalı'}
          </Badge>
          <Badge color={f.lowCredit ? (f.creditAvailable <= 0 ? 'red' : 'yellow') : 'green'} variant="light">
            Kredi: {f.creditAvailable} kullanılabilir ({f.creditBalance} bakiye, {f.creditReserved} rezerve)
          </Badge>
          <Badge color={LEVEL_COLOR[f.budgetLevel]} variant="light">
            Bütçe: {LEVEL_LABEL[f.budgetLevel]}
          </Badge>
          {f.settingsCorrupt ? (
            <Badge color="red" variant="light">
              Ayarlar okunamıyor (bozuk): ücretli AI ve bütçe kapalı sayılıyor
            </Badge>
          ) : null}
          {f.openUncertainCases > 0 ? (
            <Badge color="red" variant="light">
              {f.openUncertainCases} belirsiz maliyet
            </Badge>
          ) : null}
        </div>
        <p className={classes.cardMeta}>
          Bugün {usd(f.spentTodayUsd)} / {usd(f.dailyLimitUsd)} · Ay {usd(f.spentMonthUsd)} / {usd(f.monthlyLimitUsd)} · İş başı en çok {usd(f.maxJobCostUsd)} · Açık rezervasyon{' '}
          {usd(f.openReservedUsd)}
        </p>
      </Link>
    </li>
  );
}

/**
 * `/platform/finans`: işletmelerin kredi/bütçe/açık rezervasyon özeti ve pilot izleme kontrolleri (Faz C). Salt okunur: kredi ve bütçe yazma yalnızca operatör komutlarındadır
 * (docs/19), mutabakat kararı Mutabakat ekranındadır. Kişisel veri yoktur. Sunucu yalnızca platform yöneticisine yanıt verir.
 */
export function FinancePage() {
  const finance = usePlatformFinance();
  const monitoring = usePlatformMonitoring();
  return (
    <>
      <h1 className="sp-title" style={{ margin: '8px 0 16px' }}>
        Finans ve izleme
      </h1>

      <h2 className={classes.sectionTitle}>İzleme</h2>
      {monitoring.isLoading ? <LoadingState label="Kontroller çalışıyor" /> : null}
      {monitoring.isError ? <ErrorState error={monitoring.error} onRetry={() => void monitoring.refetch()} /> : null}
      {monitoring.data ? <ChecksSection checks={monitoring.data} /> : null}

      <h2 className={classes.sectionTitle}>İşletmeler</h2>
      {finance.isLoading ? <LoadingState label="Finans özeti yükleniyor" /> : null}
      {finance.isError ? <ErrorState error={finance.error} onRetry={() => void finance.refetch()} /> : null}
      {finance.data && finance.data.length === 0 ? <EmptyState title="Henüz işletme yok" description="İşletme oluşturulunca kredi ve bütçe özeti burada görünür." /> : null}
      <ul className={classes.list}>{finance.data?.map((f) => <OrganizationRow key={f.organizationId} f={f} />)}</ul>
    </>
  );
}
