import { Badge, Button, Modal, NumberInput, Radio, Stack, Textarea } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError } from '../../shared/api/http';
import { newIdempotencyKey } from '../../shared/api/idempotency';
import { decideCostCase, financeKeys, useCostCase, useCostCases, type CostCase, type CostDecision } from '../../shared/api/platform';
import { errorMessage } from '../../shared/i18n/tr';
import { EmptyState, ErrorState, LoadingState, Notice } from '../../shared/ui';
import classes from './platform.module.css';

const STATE_LABEL: Record<CostCase['state'], string> = {
  Open: 'Bekliyor',
  ConfirmedCharged: 'Ücretlendi',
  ConfirmedNotCharged: 'Ücretlenmedi',
  WrittenOff: 'Zarar yazıldı',
};
const STATE_COLOR: Record<CostCase['state'], string> = { Open: 'red', ConfirmedCharged: 'blue', ConfirmedNotCharged: 'green', WrittenOff: 'gray' };

const DECISIONS: { value: CostDecision; label: string; hint: string }[] = [
  { value: 'ConfirmedCharged', label: 'Sağlayıcıda ücretlendi', hint: 'Faturadaki gerçek tutarı girin; bütçe/kredi bu tutara göre kapanır (kredi düşer).' },
  { value: 'ConfirmedNotCharged', label: 'Sağlayıcıda ücretlenmedi', hint: 'Tutar bütçeden düşülür; bloke kredi iade edilir.' },
  { value: 'WrittenOff', label: 'Kesin bilinemiyor, zarar yaz', hint: 'Tutar bütçeden düşülür; bloke kredi iade edilir (zararı işletmeci üstlenir).' },
];

const formatDate = (iso: string) => new Intl.DateTimeFormat('tr-TR', { dateStyle: 'short', timeStyle: 'short' }).format(new Date(iso));

function DecisionDialog({ caseId, onClose }: { caseId: string; onClose: () => void }) {
  const client = useQueryClient();
  const detail = useCostCase(caseId);
  // Anahtar diyalog açılışında üretilir: ağ hatasında aynı anahtarla tekrar gönderilir (çift karar oluşmaz)
  const [key] = useState(newIdempotencyKey);
  const [decision, setDecision] = useState<CostDecision>('ConfirmedNotCharged');
  const [amount, setAmount] = useState<number | string>('');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const current = detail.data;
  const amountOk = decision !== 'ConfirmedCharged' || (typeof amount === 'number' && amount > 0 && amount <= 1000);

  const submit = async () => {
    if (!current) return;
    setBusy(true);
    setError(null);
    try {
      await decideCostCase(
        caseId,
        { decision, amountUsd: decision === 'ConfirmedCharged' ? Number(amount) : undefined, note: note.trim() || undefined, expectedSequence: current.case.decisionCount },
        key,
      );
      await client.invalidateQueries({ queryKey: ['platform'] });
      onClose();
    } catch (e) {
      setError(e instanceof ApiError ? (e.code === 'DECISION_CONFLICT' ? 'Bu vakaya başka bir karar girilmiş. Pencereyi kapatıp listeyi yenileyin.' : errorMessage(e.code)) : errorMessage('UNKNOWN'));
      void client.invalidateQueries({ queryKey: financeKeys.case(caseId) });
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title="Mutabakat kararı" centered size="lg">
      {detail.isLoading ? <LoadingState /> : null}
      {detail.isError ? <ErrorState error={detail.error} onRetry={() => void detail.refetch()} /> : null}
      {current ? (
        <Stack gap="md">
          <p className={classes.cardMeta}>
            {current.case.organizationName} · {current.case.provider} / {current.case.model}
            {current.case.quality ? ` · ${current.case.quality}` : ''} · {formatDate(current.case.occurredAt)}
            <br />
            Ayrılan en kötü durum tutarı: ${current.case.reservedCostUsd.toFixed(4)} · Geçerli tutar: ${current.case.effectiveCostUsd.toFixed(4)}
            {current.case.providerRequestId ? ` · Sağlayıcı istek no: ${current.case.providerRequestId}` : ''}
          </p>
          {current.decisions.length > 0 ? (
            <ul className={classes.list} aria-label="Önceki kararlar">
              {current.decisions.map((d) => (
                <li key={d.sequence} className={classes.cardMeta}>
                  #{d.sequence}: {d.decision} · ${d.effectiveCostUsd.toFixed(4)}
                  {d.note ? ` · ${d.note}` : ''}
                </li>
              ))}
            </ul>
          ) : null}
          <Notice tone="warning">Karar silinemez; düzeltme yeni bir karar olarak eklenir. Kararı sağlayıcı panelindeki/faturadaki gerçek duruma göre verin.</Notice>
          <Radio.Group value={decision} onChange={(v) => setDecision(v as CostDecision)} label="Karar">
            <Stack gap="xs" mt="xs">
              {DECISIONS.map((d) => (
                <Radio key={d.value} value={d.value} label={d.label} description={d.hint} />
              ))}
            </Stack>
          </Radio.Group>
          {decision === 'ConfirmedCharged' ? (
            <NumberInput label="Gerçek tutar (USD)" value={amount} onChange={setAmount} min={0} max={1000} decimalScale={4} allowNegative={false} />
          ) : null}
          <Textarea label="Not (isteğe bağlı, kişisel veri yazmayın)" maxLength={200} value={note} onChange={(e) => setNote(e.currentTarget.value)} rows={2} />
          {error ? <Notice tone="danger">{error}</Notice> : null}
          <Button onClick={() => void submit()} loading={busy} disabled={!amountOk}>
            Kararı kaydet
          </Button>
        </Stack>
      ) : null}
    </Modal>
  );
}

/**
 * `/platform/mutabakat`: sonucu belirsiz AI maliyet vakaları (Faz C). Karar mevcut güvenli API ile verilir (idempotent, `expectedSequence` ile çakışma denetimli, değiştirilemez defter).
 * Yalnızca platform yöneticisi; kişisel veri yoktur.
 */
export function ReconciliationPage() {
  const [filter, setFilter] = useState<'Open' | 'all'>('Open');
  const [selected, setSelected] = useState<string | null>(null);
  const cases = useCostCases(filter);
  return (
    <>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12, margin: '8px 0 16px' }}>
        <h1 className="sp-title" style={{ margin: 0 }}>
          Mutabakat
        </h1>
        <Button variant={filter === 'Open' ? 'filled' : 'default'} size="compact-md" onClick={() => setFilter(filter === 'Open' ? 'all' : 'Open')}>
          {filter === 'Open' ? 'Tümünü göster' : 'Yalnızca bekleyenler'}
        </Button>
      </div>
      <p className={classes.cardMeta}>Sağlayıcıya gönderilmiş ama sonucu/ücreti bilinmeyen işler. Bekleyen vaka, işin kredisini ve bütçesini en kötü durum tutarıyla bloke tutar.</p>

      {cases.isLoading ? <LoadingState label="Vakalar yükleniyor" /> : null}
      {cases.isError ? <ErrorState error={cases.error} onRetry={() => void cases.refetch()} /> : null}
      {cases.data && cases.data.length === 0 ? <EmptyState title={filter === 'Open' ? 'Bekleyen vaka yok' : 'Vaka yok'} description="Belirsiz maliyet oluştuğunda burada listelenir." /> : null}

      <ul className={classes.list}>
        {cases.data?.map((c) => (
          <li key={c.id}>
            <button type="button" className={classes.card} style={{ width: '100%', textAlign: 'left', cursor: 'pointer' }} onClick={() => setSelected(c.id)} data-testid={`case-${c.id}`}>
              <p className={classes.cardTitle}>
                {c.organizationName} · {c.provider} / {c.model}
              </p>
              <p className={classes.cardMeta}>
                {formatDate(c.occurredAt)} · {c.operation} · en kötü durum ${c.reservedCostUsd.toFixed(4)} · geçerli ${c.effectiveCostUsd.toFixed(4)}
              </p>
              <div className={classes.badges}>
                <Badge color={STATE_COLOR[c.state]} variant="light">
                  {STATE_LABEL[c.state]}
                </Badge>
                {c.decisionCount > 0 ? <Badge variant="light">{c.decisionCount} karar</Badge> : null}
              </div>
            </button>
          </li>
        ))}
      </ul>

      {selected ? <DecisionDialog caseId={selected} onClose={() => setSelected(null)} /> : null}
    </>
  );
}
