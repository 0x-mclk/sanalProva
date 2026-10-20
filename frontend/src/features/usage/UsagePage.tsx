import { Button, Select } from '@mantine/core';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { ApiError } from '../../shared/api/http';
import { errorMessage } from '../../shared/i18n/tr';
import { useAiQuality, useSetAiQuality, useUsageReport, type AiQuality, type UsageReport } from '../../shared/api/usage';
import { AppBar, ErrorState, Eyebrow, LoadingState, Notice } from '../../shared/ui';
import classes from './UsagePage.module.css';

const usd = (value: number) => `$${value.toFixed(value < 1 ? 4 : 2)}`;

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
        <QualitySettings />
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
              <tr><th>Sağlayıcı / model</th><th>Çağrı</th><th>Başarısız</th><th>Yeniden</th><th>Süre</th><th>Tahmini</th></tr>
            </thead>
            <tbody>
              {report.byModel.map((row) => (
                <tr key={`${row.provider}/${row.model}`}>
                  <td>{row.provider} · {row.model}</td>
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

const modeLabel: Record<string, string> = { fast: 'Hızlı (fast)', balanced: 'Dengeli (balanced)', quality: 'Yüksek kalite (quality)' };

/**
 * Hazır takım üretim kalitesi (FASHN Try-On Max). Varsayılan fiyat/performans seçimi Dengeli + 1K'dır (2 kredi); 2K (3 kredi) alternatiftir.
 * Maliyet tahmini ve bütçe kapısı seçime göre güncellenir. Ucuz seçeneğin yüz/kumaş/nakış sadakatini koruduğu varsayılmaz: gerçek testte 1K ile 2K karşılaştırılacak.
 */
function QualitySettings() {
  const quality = useAiQuality();
  const save = useSetAiQuality();
  const [draft, setDraft] = useState<{ resolution?: string; mode?: string }>({});
  const [message, setMessage] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null);
  if (!quality.data) {
    return quality.isError ? <ErrorState error={quality.error} onRetry={() => void quality.refetch()} /> : null;
  }

  const current: AiQuality = quality.data;
  const resolution = draft.resolution ?? current.resolution;
  const mode = draft.mode ?? current.mode;
  const option = current.options.find((o) => o.resolution === resolution && o.mode === mode);
  const dirty = resolution !== current.resolution || mode !== current.mode;

  return (
    <section className={classes.quality} aria-labelledby="quality-title">
      <h2 id="quality-title" className={classes.heading}>
        Hazır takım kalitesi
      </h2>
      <div className={classes.qualityFields}>
        <Select
          label="Çözünürlük"
          value={resolution}
          allowDeselect={false}
          data={[...new Set(current.options.map((o) => o.resolution))].map((r) => ({ value: r, label: r.toUpperCase() }))}
          onChange={(v) => v && setDraft((d) => ({ ...d, resolution: v }))}
        />
        <Select
          label="Mod"
          value={mode}
          allowDeselect={false}
          data={[...new Set(current.options.map((o) => o.mode))].map((m) => ({ value: m, label: modeLabel[m] ?? m }))}
          onChange={(v) => v && setDraft((d) => ({ ...d, mode: v }))}
        />
      </div>
      <p className={classes.qualityHint}>
        {option ? `${option.credits} kredi · görsel başına ≈ ${usd(option.estimatedUsdPerImage)}` : ''}
        {current.isOverride ? '' : ` · varsayılan (${current.defaultMode} + ${current.defaultResolution.toUpperCase()})`}
      </p>
      <Button
        size="md"
        loading={save.isPending}
        disabled={!dirty}
        onClick={() =>
          save.mutate(
            { resolution, mode },
            {
              onSuccess: () => {
                setDraft({});
                setMessage({ tone: 'ok', text: 'Kalite ayarı kaydedildi; sonraki üretimler bu ayarla yapılır.' });
              },
              onError: (error) => setMessage({ tone: 'error', text: errorMessage(error instanceof ApiError ? error.code : undefined) }),
            },
          )
        }
      >
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
