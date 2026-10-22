import { Button, Checkbox, Select } from '@mantine/core';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { ApiError } from '../../shared/api/http';
import { errorMessage } from '../../shared/i18n/tr';
import { useAiModels, useSetAiModels, useUsageReport, type AiModelSettings, type UsageReport } from '../../shared/api/usage';
import { AppBar, ErrorState, Eyebrow, LoadingState, Notice } from '../../shared/ui';
import classes from './UsagePage.module.css';

const usd = (value: number) => `$${value.toFixed(value < 1 ? 4 : 2)}`;

const QUALITY_LABELS: Record<string, string> = {
  auto: 'Otomatik', low: 'Düşük', medium: 'Orta', high: 'Yüksek', xhigh: 'Çok yüksek', max: 'Maksimum', standard: 'Standart', turbo: 'Turbo',
};

const qualityLabel = (key: string) => QUALITY_LABELS[key] ?? key;

/**
 * AI kullanım ve maliyet raporu (yönetici). Kişisel veri içermez. Tutarlar yapılandırmadaki birim fiyatlardan <b>tahmindir</b>;
 * kesin tutar sağlayıcı faturasındadır. Model satırlarındaki "Canlı doğrulanmadı" etiketi, o modelle henüz gerçek çağrı yapılıp
 * doğrulanmadığı anlamına gelir (resmî dokümana göre tanımlıdır).
 */
export function UsagePage() {
  const navigate = useNavigate();
  const report = useUsageReport();

  return (
    <main className="sp-page">
      <AppBar title="AI kullanım ve maliyet" onBack={() => void navigate('/')} />
      <div className="sp-content">
        {report.isLoading ? <LoadingState /> : null}
        {report.isError ? <ErrorState error={report.error} onRetry={() => void report.refetch()} /> : null}
        <ModelSettings />
        {report.data ? <Report report={report.data} /> : null}
      </div>
    </main>
  );
}

function Report({ report }: { report: UsageReport }) {
  const { budget, totals } = report;
  return (
    <>
      <Notice tone={budget.live && budget.allowPaidCalls ? 'warning' : 'info'}>
        {budget.live
          ? budget.allowPaidCalls
            ? 'Gerçek AI sağlayıcıları AÇIK: üretimler ücretlidir.'
            : 'Gerçek sağlayıcı seçili ancak ücretli çağrılar KAPALI: üretimler çalışmaz (ücret doğmaz).'
          : 'Mock mod: gerçek AI çağrısı yapılmıyor; sonuçlar DEMO işaretlidir.'}
      </Notice>

      <section className={classes.cards} aria-label="Bütçe">
        <div className={classes.card}>
          <Eyebrow tone="muted">Bugün (UTC)</Eyebrow>
          <strong>{usd(budget.spentTodayUsd)}</strong>
          <span>sınır {usd(budget.dailyLimitUsd)}</span>
        </div>
        <div className={classes.card}>
          <Eyebrow tone="muted">Bu ay (UTC)</Eyebrow>
          <strong>{usd(budget.spentMonthUsd)}</strong>
          <span>sınır {usd(budget.monthlyLimitUsd)}</span>
        </div>
        <div className={classes.card}>
          <Eyebrow tone="muted">İş başı sınır</Eyebrow>
          <strong>{usd(budget.maxJobCostUsd)}</strong>
          <span>{budget.enforce ? 'uygulanıyor' : 'KAPALI'}</span>
        </div>
      </section>

      <h2 className={classes.heading}>
        Son 30 gün <small>{report.from} – {report.to}</small>
      </h2>
      <dl className={classes.totals}>
        <div><dt>Çağrı</dt><dd>{totals.calls}</dd></div>
        <div><dt>Başarılı / başarısız</dt><dd>{totals.successes} / {totals.failures}</dd></div>
        <div><dt>Yeniden deneme</dt><dd>{totals.retries}</dd></div>
        <div><dt>Ort. süre</dt><dd>{(totals.averageDurationMs / 1000).toFixed(1)} sn</dd></div>
        <div><dt>Tahmini maliyet</dt><dd>{usd(totals.estimatedCostUsd)}</dd></div>
        <div><dt>Sağlayıcının bildirdiği</dt><dd>{totals.actualCostUsd > 0 ? usd(totals.actualCostUsd) : '—'}</dd></div>
      </dl>

      <h2 className={classes.heading}>Model bazında</h2>
      {report.byModel.length === 0 ? (
        <p className={classes.empty}>Bu aralıkta AI çağrısı yok.</p>
      ) : (
        <div className={classes.tableWrap}>
          <table className={classes.table}>
            <thead>
              <tr><th>Sağlayıcı / model / kalite</th><th>Çağrı</th><th>Başarısız</th><th>Yeniden</th><th>Süre</th><th>Tahmini</th></tr>
            </thead>
            <tbody>
              {report.byModel.map((row) => (
                <tr key={`${row.provider}/${row.model}/${row.quality ?? ''}`}>
                  <td>{row.provider} · {row.model}{row.quality ? ` · ${qualityLabel(row.quality)}` : ''}</td>
                  <td>{row.calls}</td>
                  <td>{row.failures}</td>
                  <td>{row.retries}</td>
                  <td>{(row.averageDurationMs / 1000).toFixed(1)} sn</td>
                  <td>{usd(row.estimatedCostUsd)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <h2 className={classes.heading}>Yapılandırılmış modeller</h2>
      <ul className={classes.models}>
        {report.models.map((model) => (
          <li key={`${model.jobType}`}>
            <strong>{model.jobType}</strong> → {model.displayName} <code>{model.model}</code>
            <span className={classes.badge} data-ok={model.liveVerified}>{model.liveVerified ? 'Canlı doğrulandı' : 'Canlı doğrulanmadı'}</span>
            <small>Doküman durumu: {model.docStatus} · doğrulama {model.docsVerifiedOn}</small>
            {model.warning ? <small className={classes.warning}>{model.warning}</small> : null}
          </li>
        ))}
      </ul>
    </>
  );
}

type ModelDraft = { enabled: boolean; allowed: string[]; defaultQuality: string };

/**
 * Tek ürün giydirme modelleri (yönetici). Hangi modellerin ve kalitelerin personelce kullanılabileceği ve varsayılanlar firma düzeyinde seçilir;
 * sunucu her üretim isteğinde bunu yeniden denetler. Tutarlar tahmindir. "Canlı doğrulanmadı": bu modelle henüz ücretli test yapılmadı.
 * Eski (pasif) modeller listelenir ama seçilemez.
 */
function ModelSettings() {
  const settings = useAiModels();
  const save = useSetAiModels();
  const [draft, setDraft] = useState<{ defaultModel?: string; models?: Record<string, ModelDraft> }>({});
  const [message, setMessage] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null);
  if (!settings.data) {
    return settings.isError ? <ErrorState error={settings.error} onRetry={() => void settings.refetch()} /> : null;
  }

  const current: AiModelSettings = settings.data;
  const selectable = current.models.filter((m) => !m.legacy);
  const legacy = current.models.filter((m) => m.legacy);
  const state = (key: string): ModelDraft => {
    const fromDraft = draft.models?.[key];
    if (fromDraft) return fromDraft;
    const m = current.models.find((x) => x.key === key)!;
    return { enabled: m.enabled, allowed: [...m.allowedQualities], defaultQuality: m.defaultQuality };
  };
  const update = (key: string, patch: Partial<ModelDraft>) =>
    setDraft((d) => ({ ...d, models: { ...d.models, [key]: { ...state(key), ...patch } } }));
  const defaultModel = draft.defaultModel ?? current.defaultModel;
  const enabledKeys = selectable.filter((m) => state(m.key).enabled).map((m) => m.key);

  const submit = () => {
    setMessage(null);
    save.mutate(
      { defaultModel, models: selectable.map((m) => ({ key: m.key, enabled: state(m.key).enabled, allowedQualities: state(m.key).allowed, defaultQuality: state(m.key).defaultQuality })) },
      {
        onSuccess: () => {
          setDraft({});
          setMessage({ tone: 'ok', text: 'Model ayarları kaydedildi; sonraki üretimler bu ayarlarla yapılır.' });
        },
        onError: (error) => setMessage({ tone: 'error', text: errorMessage(error instanceof ApiError ? error.code : undefined) }),
      },
    );
  };

  return (
    <section className={classes.quality} aria-labelledby="models-title">
      <h2 id="models-title" className={classes.heading}>
        Tek ürün giydirme modelleri
      </h2>
      {current.maxJobCostUsd != null ? (
        <p className={classes.qualityHint}>İş başı bütçe sınırı: {usd(current.maxJobCostUsd)}. En kötü durumu bu sınırı aşan kalite gerçek modda reddedilir.</p>
      ) : null}
      <Select
        label="Varsayılan model"
        value={enabledKeys.includes(defaultModel) ? defaultModel : null}
        allowDeselect={false}
        data={selectable.filter((m) => enabledKeys.includes(m.key)).map((m) => ({ value: m.key, label: m.displayName }))}
        onChange={(v) => v && setDraft((d) => ({ ...d, defaultModel: v }))}
      />
      <ul className={classes.models}>
        {selectable.map((m) => {
          const s = state(m.key);
          return (
            <li key={m.key}>
              <Checkbox label={<strong>{m.displayName}</strong>} checked={s.enabled} onChange={(e) => update(m.key, { enabled: e.currentTarget.checked })} />
              <span className={classes.badge} data-ok={m.liveVerified}>{m.liveVerified ? 'Canlı doğrulandı' : 'Canlı doğrulanmadı'}</span>
              <small>{m.note}</small>
              <div className={classes.qualityFields}>
                {m.qualities.map((q) => (
                  <div key={q.key} className={classes.qualityOption}>
                    <Checkbox
                      disabled={!s.enabled}
                      label={`${q.label} · ≈ ${usd(q.estimatedUsd)} (en fazla ${usd(q.worstCaseUsd)})`}
                      checked={s.allowed.includes(q.key)}
                      onChange={(e) => {
                        // katalog sırası korunur (personelin listesi de bu sırada görünür)
                        const allowed = m.qualities.map((x) => x.key).filter((k) => (k === q.key ? e.currentTarget.checked : s.allowed.includes(k)));
                        update(m.key, { allowed, defaultQuality: allowed.includes(s.defaultQuality) ? s.defaultQuality : (allowed[0] ?? s.defaultQuality) });
                      }}
                    />
                    {q.measuredUsd != null ? <small>Ölçülen: {usd(q.measuredUsd)}</small> : null}
                    {q.highCost ? <span className={classes.badge}>Yüksek maliyet{q.enabledByDefault ? '' : ' · varsayılan kapalı'}</span> : null}
                    {q.exceedsJobLimit ? <small className={classes.warning}>İş başı sınırı aşıyor: bu kaliteyle üretim reddedilir</small> : null}
                  </div>
                ))}
              </div>
              {m.qualities.length > 1 ? (
                <Select
                  label="Varsayılan kalite"
                  size="sm"
                  disabled={!s.enabled}
                  value={s.allowed.includes(s.defaultQuality) ? s.defaultQuality : null}
                  allowDeselect={false}
                  data={m.qualities.filter((q) => s.allowed.includes(q.key)).map((q) => ({ value: q.key, label: q.label }))}
                  onChange={(v) => v && update(m.key, { defaultQuality: v })}
                />
              ) : null}
              <small>Fiyat kaynağı: {m.priceSource}</small>
              {m.qualities.length > 1 ? (
                <small>Tahmin: tipik 3:4 fotoğraf ve tek ürün görseli; en fazla: yükleme sınırındaki en büyük görseller ve uzun talimat (bütçe kapısı bunu kullanır).</small>
              ) : null}
            </li>
          );
        })}
      </ul>
      {legacy.length > 0 ? (
        <p className={classes.qualityHint}>Eski modeller (pasif, seçilemez): {legacy.map((m) => m.displayName).join(', ')}</p>
      ) : null}
      <Button size="md" loading={save.isPending} disabled={!draft.models && !draft.defaultModel} onClick={submit}>
        Kaydet
      </Button>
      {message ? (
        <p className={classes.qualityHint} data-tone={message.tone} role={message.tone === 'error' ? 'alert' : 'status'}>
          {message.text}
        </p>
      ) : null}
    </section>
  );
}
