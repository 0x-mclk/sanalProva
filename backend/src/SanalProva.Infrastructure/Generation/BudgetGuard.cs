using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Ai.Abstractions;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Infrastructure.Generation;

public sealed record SpendSummary(decimal Today, decimal Month, decimal Job);

/// <summary>
/// Ücretli bir sağlayıcı çağrısından <b>önce</b> çalışan kapı. İki katman:
/// 1) <see cref="BudgetSettings.AllowPaidCalls"/> kapalıysa hiçbir ücretli çağrı yapılmaz (varsayılan);
/// 2) firmanın günlük/aylık tahmini harcaması ve işin kendi toplamı, bu çağrının <b>en kötü durum</b> tahminiyle birlikte sınırları aşıyorsa çağrı yapılmaz.
/// Harcama <c>ai_usage_logs.estimated_cost_usd</c> toplamıdır (başarısız-ücretli çağrılar, iptal edilen çağrılar, sonucu belirsiz işlemlerin en kötü durum
/// tutarı ve mutabakat düzeltmeleri dahil). Düzeltme satırları (±) asıl harcamanın doğduğu güne/aya yazılır (<see cref="AiUsageLog.OccurredAt"/>):
/// üç gün sonra verilen "ücretlenmedi" kararı bugünün bütçesini değil, o günün bütçesini serbest bırakır.
/// Eşzamanlı işlerde aşım en fazla <c>MaxParallelJobs × çağrı başı en kötü maliyet</c> kadardır (kilitsiz denetim; bilinçli sade tasarım).
/// </summary>
public sealed class BudgetGuard(IOptionsMonitor<GenerationSettings> settings, TimeProvider clock)
{
    public async Task<SpendSummary> GetSpendAsync(AppDbContext db, Guid organizationId, Guid? jobId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var now = clock.GetUtcNow();
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        var logs = db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().Where(l => l.OrganizationId == organizationId && (l.OccurredAt ?? l.CreatedAt) >= monthStart);
        var month = await logs.SumAsync(l => (decimal?)l.EstimatedCostUsd, ct) ?? 0m;
        var today = await logs.Where(l => (l.OccurredAt ?? l.CreatedAt) >= dayStart).SumAsync(l => (decimal?)l.EstimatedCostUsd, ct) ?? 0m;
        var job = jobId is null ? 0m : await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().Where(l => l.JobId == jobId).SumAsync(l => (decimal?)l.EstimatedCostUsd, ct) ?? 0m;
        return new SpendSummary(today, month, job);
    }

    /// <summary>
    /// Açık rezervasyonlar: sağlayıcıda sürüyor olabilecek, henüz maliyet kaydına dönüşmemiş çağrıların ayrılmış en kötü durum tutarı (<see cref="GenerationJob.ReservedCostUsd"/>).
    /// Ayrı bir defter yoktur: rezervasyon iş satırındadır ve işin maliyet kaydı yazıldığında (aynı kayıtla durum değişir) kendiliğinden kapanır.
    /// <list type="bullet">
    /// <item><b>Running</b>: çağrı sürüyor (veya gönderilmek üzere).</item>
    /// <item><b>Queued + (kimlik veya gönderim niyeti var)</b>: yoklamada geçici hata ya da worker çökmesi; istek sağlayıcıda sürüyor/ücretlenmiş olabilir.</item>
    /// <item><b>Cancelled + o deneme için maliyet kaydı yok</b>: iptal edildi ama worker henüz en kötü durum kaydını yazmadı.</item>
    /// </list>
    /// Succeeded/Failed işler sayılmaz: maliyetleri (kesin, belirsiz veya 0) maliyet kaydındadır; böylece aynı tutar iki kez sayılmaz.
    /// </summary>
    internal static IQueryable<GenerationJob> OpenReservations(AppDbContext db, Guid organizationId) =>
        db.GenerationJobs.IgnoreQueryFilters().AsNoTracking().Where(j => j.OrganizationId == organizationId && j.ReservedCostUsd != null
            && (j.Status == GenerationJobStatus.Running
                || (j.Status == GenerationJobStatus.Queued && (j.ProviderRequestId != null || j.ProviderSubmitStartedAt != null))
                || (j.Status == GenerationJobStatus.Cancelled && !db.AiUsageLogs.IgnoreQueryFilters().Any(l => l.JobId == j.Id && l.Attempt == j.Attempt && !l.IsAdjustment))));

    /// <summary>
    /// Ücretli çağrıdan önce <b>atomik</b> bütçe kapısı + rezervasyon. Tek transaction ve firma başına uygulama kilidi (<c>sp_getapplock</c>): kilit altında (1) kesinleşmiş harcama
    /// (maliyet kayıtları + düzeltmeler) ile (2) diğer işlerin açık rezervasyonları toplanır, bu çağrının en kötü durum tutarı eklenerek günlük/aylık/iş sınırlarına karşı denetlenir ve
    /// (3) sınır aşılmıyorsa rezervasyon (<c>reserved_cost_usd</c>) aynı transaction'da yazılır. Kilit transaction'a aittir; COMMIT ile bırakılır. Kilit ve veritabanı paylaşıldığı için
    /// farklı worker'lar ve uygulama örnekleri aynı bütçeyi kontrolsüz tüketemez. Kontrol ile rezervasyon arasında boşluk yoktur.
    /// </summary>
    /// <exception cref="ProviderException">Sınır aşılırsa (yeniden denenmez, sağlayıcıya hiçbir şey gitmez) veya kilit alınamazsa (yeniden denenir, ücret yok).</exception>
    /// <exception cref="JobOwnershipLostException">İş bu worker'ın elinden çıktıysa (rezervasyon yazılmaz).</exception>
    public async Task ReserveAsync(AppDbContext db, GenerationJob job, decimal worstCasePerCallUsd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(job);
        var budget = settings.CurrentValue.Budget;
        if (!budget.AllowPaidCalls)
        {
            throw new ProviderException(GenerationErrorCode.ProviderNotConfigured, "Gerçek AI çağrıları kapalı (ücretli çağrılar için yönetici onayı ve yapılandırma gerekir).", retryable: false);
        }

        // İkinci (firma) kapı, HİÇBİR yazmadan/kilitten önce: yetkisiz firma için rezervasyon, bütçe kilidi veya sağlayıcı çağrısı olmaz. Fail-closed.
        if (!await PaidAiPolicy.IsEnabledForAsync(db, job.OrganizationId, ct))
        {
            throw new ProviderException(GenerationErrorCode.PaidAiNotEnabled, "Bu işletme için ücretli AI etkin değil; sağlayıcı çağrılmadı.", retryable: false);
        }

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await AppLocks.AcquireExclusiveAsync(db, AppLocks.OrganizationBudget(job.OrganizationId), LockTimeout, ct);

            if (budget.Enforce)
            {
                // SIRA ÖNEMLİ: önce açık rezervasyonlar, sonra kesinleşmiş harcama. READ_COMMITTED_SNAPSHOT her deyimde yeni bir anlık görüntü okur; bu iki sorgu arasında bir iş
                // biterse (maliyet kaydı yazılır, rezervasyon kapanır) ters sırada okumak onu hiç saymazdı (eksik sayım → bütçe aşımı). Bu sırayla en kötü ihtimal çift sayımdır (güvenli taraf).
                var open = await OpenReservations(db, job.OrganizationId).Where(j => j.Id != job.Id).SumAsync(j => (decimal?)j.ReservedCostUsd, ct) ?? 0m;
                var spend = await GetSpendAsync(db, job.OrganizationId, job.Id, ct);
                if (spend.Job + worstCasePerCallUsd > budget.MaxJobCostUsd)
                {
                    throw new ProviderException(GenerationErrorCode.BudgetExceeded, "Bu işin AI maliyet sınırı aşılacaktı; sağlayıcı çağrılmadı.", retryable: false);
                }

                if (spend.Today + open + worstCasePerCallUsd > budget.DailyLimitUsd)
                {
                    throw new ProviderException(GenerationErrorCode.BudgetExceeded, "Günlük AI bütçesi doldu (devam eden işlemlerin ayrılmış tutarı dahil); sağlayıcı çağrılmadı.", retryable: false);
                }

                if (spend.Month + open + worstCasePerCallUsd > budget.MonthlyLimitUsd)
                {
                    throw new ProviderException(GenerationErrorCode.BudgetExceeded, "Aylık AI bütçesi doldu (devam eden işlemlerin ayrılmış tutarı dahil); sağlayıcı çağrılmadı.", retryable: false);
                }
            }

            // Rezervasyon: sahiplik (deneme numarası = çit belirteci) ve "Running" koşuluyla; iş elden çıktıysa hiçbir şey ayrılmaz
            var attempt = job.Attempt;
            var rows = await db.GenerationJobs.IgnoreQueryFilters()
                .Where(j => j.Id == job.Id && j.Attempt == attempt && j.Status == GenerationJobStatus.Running)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.ReservedCostUsd, worstCasePerCallUsd), ct);
            if (rows != 1)
            {
                throw new JobOwnershipLostException("İş bütçe rezervasyonu sırasında bu worker'ın elinden çıktı.");
            }

            await transaction.CommitAsync(ct);
            job.ReservedCostUsd = worstCasePerCallUsd; // izlenen varlık veritabanıyla aynı değere gelir
            db.Entry(job).Property(j => j.ReservedCostUsd).IsModified = false;
        }
        catch (Exception ex) when (ex is not ProviderException and not JobOwnershipLostException && DbErrors.IsTransientLockFailure(ex))
        {
            // Bütçe kilidi/kilitlenme: hiçbir şey ayrılmadı, sağlayıcıya gidilmedi; iş ücretsiz yeniden kuyruğa alınır
            throw new ProviderException(GenerationErrorCode.RateLimited, "Bütçe kontrolü meşgul; iş yeniden denenecek.", retryable: true);
        }
    }

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
}
