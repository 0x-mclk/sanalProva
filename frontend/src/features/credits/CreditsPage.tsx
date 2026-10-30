import { Button } from '@mantine/core';
import { useState } from 'react';
import { Link } from 'react-router';
import { useCreditLedger, useCredits, type CreditLedgerItem } from '../../shared/api/credits';
import { EmptyState, ErrorState, Eyebrow, LoadingState, Notice } from '../../shared/ui';
import classes from './credits.module.css';

const PAGE = 20;

const TYPE_LABEL: Record<CreditLedgerItem['type'], string> = {
  Grant: 'Kredi yükleme',
  Adjustment: 'Düzeltme',
  Reserve: 'Ayrıldı (üretim başladı)',
  Release: 'İade (üretim tamamlanmadı)',
  Consume: 'Kullanıldı (üretim tamamlandı)',
};

const SOURCE_LABEL: Record<CreditLedgerItem['source'], string> = { Operator: 'Platform yönetimi', System: 'Sistem', Payment: 'Ödeme' };

const dateText = (iso: string) => new Intl.DateTimeFormat('tr-TR', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(iso));

/** Hareketin müşteriye görünen etkisi: yükleme/düzeltme bakiyeyi, ayırma/iade kullanılabilir krediyi, düşüm bakiyeyi değiştirir. */
function effect(entry: CreditLedgerItem): { text: string; tone: 'positive' | 'negative' | 'neutral' } {
  switch (entry.type) {
    case 'Grant':
      return { text: `+${entry.amount}`, tone: 'positive' };
    case 'Adjustment':
      return { text: entry.amount > 0 ? `+${entry.amount}` : `${entry.amount}`, tone: entry.amount > 0 ? 'positive' : 'negative' };
    case 'Reserve':
      return { text: `${entry.amount} ayrıldı`, tone: 'neutral' };
    case 'Release':
      return { text: `${entry.amount} iade`, tone: 'positive' };
    case 'Consume':
      return { text: `−${entry.amount}`, tone: 'negative' };
  }
}

/**
 * `/yonetim/kredi`: işletme sahibinin kredi görünümü (M5.4). Bakiye, kullanılabilir kredi, devam eden işlerde bekleyen (bloke) kredi, tarife ve işlem geçmişi.
 * Salt okunur: kredi yüklemeyi platform yönetimi yapar; bu ekranda ödeme veya yükleme yoktur (ödeme entegrasyonu ileride).
 */
export function CreditsPage() {
  const summary = useCredits();
  const [skip, setSkip] = useState(0);
  const ledger = useCreditLedger(skip, PAGE);
  const data = summary.data;

  return (
    <main className="sp-page">
      <div className="sp-content">
        <Link to="/">← Ana sayfa</Link>
        <h1 className="sp-title" style={{ margin: '8px 0 0' }}>
          Kredi
        </h1>
        <p className="sp-lead">Sanal prova üretimleri kredi kullanır. Kredi yüklemek için platform yönetimiyle iletişime geçin.</p>

        {summary.isLoading ? <LoadingState label="Kredi bilgisi yükleniyor" /> : null}
        {summary.isError ? <ErrorState error={summary.error} onRetry={() => void summary.refetch()} /> : null}

        {data ? (
          <>
            {!data.enforced ? <Notice tone="info">Şu an kredi şartı uygulanmıyor; üretimler kredi tüketmez.</Notice> : null}
            {data.available === 0 && data.enforced ? (
              <Notice tone="warning">Kullanılabilir krediniz yok. Yeni prova başlatılamaz; kredi yüklenene kadar üretim yapılamaz.</Notice>
            ) : null}

            <section className={classes.cards} aria-label="Kredi özeti">
              <div className={classes.card} data-testid="credit-available">
                <Eyebrow tone="muted">Kullanılabilir</Eyebrow>
                <strong>{data.available}</strong>
                <span>yeni üretimlerde kullanılabilir</span>
              </div>
              <div className={classes.card} data-testid="credit-balance">
                <Eyebrow tone="muted">Bakiye</Eyebrow>
                <strong>{data.balance}</strong>
                <span>kesinleşmiş kredi</span>
              </div>
              <div className={classes.card} data-testid="credit-reserved">
                <Eyebrow tone="muted">Bekleyen</Eyebrow>
                <strong>{data.reserved}</strong>
                <span>{data.openJobs} üretim için ayrıldı</span>
              </div>
            </section>

            <h2 className={classes.heading}>Tarife</h2>
            <p className="sp-lead" style={{ marginTop: 0 }}>
              Her üretim varsayılan olarak <strong>{data.defaultCostPerJob} kredi</strong> kullanır
              {data.tariff.length > 0 ? '; aşağıdaki işler için farklıdır:' : '.'} Üretim başlarken geçerli bedel kayda alınır, sonradan tarife değişse de o üretimin bedeli değişmez.
            </p>
            {data.tariff.length > 0 ? (
              <ul className={classes.list}>
                {data.tariff.map((line) => (
                  <li key={line.key} className={classes.row}>
                    <p className={classes.rowTitle}>{line.key}</p>
                    <span className={classes.amount}>{line.credits} kredi</span>
                  </li>
                ))}
              </ul>
            ) : null}
          </>
        ) : null}

        <h2 className={classes.heading}>İşlem geçmişi</h2>
        {ledger.isLoading ? <LoadingState label="Geçmiş yükleniyor" /> : null}
        {ledger.isError ? <ErrorState error={ledger.error} onRetry={() => void ledger.refetch()} /> : null}
        {ledger.data && ledger.data.length === 0 && skip === 0 ? <EmptyState title="Henüz kredi hareketi yok" description="Kredi yüklendiğinde ve üretim yapıldığında burada görünür." /> : null}

        <ul className={classes.list}>
          {ledger.data?.map((entry) => {
            const e = effect(entry);
            return (
              <li key={entry.id} className={classes.row} data-testid="credit-entry">
                <div>
                  <p className={classes.rowTitle}>{TYPE_LABEL[entry.type]}</p>
                  <p className={classes.rowMeta}>
                    {dateText(entry.createdAt)} · {SOURCE_LABEL[entry.source]}
                    {entry.reference ? ` · Ref: ${entry.reference}` : ''}
                    {entry.note ? ` · ${entry.note}` : ''}
                  </p>
                  <p className={classes.rowMeta}>
                    Bakiye {entry.balanceAfter} · bekleyen {entry.reservedAfter}
                  </p>
                </div>
                <span className={`${classes.amount} ${e.tone === 'positive' ? classes.positive : e.tone === 'negative' ? classes.negative : ''}`}>{e.text}</span>
              </li>
            );
          })}
        </ul>

        {ledger.data ? (
          <div style={{ display: 'flex', gap: 8, marginTop: 12 }}>
            <Button variant="default" disabled={skip === 0} onClick={() => setSkip(Math.max(0, skip - PAGE))}>
              Önceki
            </Button>
            <Button variant="default" disabled={ledger.data.length < PAGE} onClick={() => setSkip(skip + PAGE)}>
              Sonraki
            </Button>
          </div>
        ) : null}
      </div>
    </main>
  );
}
