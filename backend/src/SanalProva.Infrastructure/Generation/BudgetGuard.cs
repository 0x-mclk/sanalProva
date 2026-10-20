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
/// Harcama <c>ai_usage_logs.estimated_cost_usd</c> toplamıdır (başarısız-ücretli çağrılar ve iptal edilen çağrılar dahil).
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

        var logs = db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().Where(l => l.OrganizationId == organizationId && l.CreatedAt >= monthStart);
        var month = await logs.SumAsync(l => (decimal?)l.EstimatedCostUsd, ct) ?? 0m;
        var today = await logs.Where(l => l.CreatedAt >= dayStart).SumAsync(l => (decimal?)l.EstimatedCostUsd, ct) ?? 0m;
        var job = jobId is null ? 0m : await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().Where(l => l.JobId == jobId).SumAsync(l => (decimal?)l.EstimatedCostUsd, ct) ?? 0m;
        return new SpendSummary(today, month, job);
    }

    /// <summary>Çağrı yapılabilirse sessizce döner; yapılamıyorsa yeniden denenmez bir <see cref="ProviderException"/> fırlatır (sağlayıcıya hiçbir şey gönderilmez).</summary>
    public async Task EnsureAllowedAsync(AppDbContext db, Guid organizationId, Guid jobId, decimal worstCasePerCallUsd, CancellationToken ct)
    {
        var budget = settings.CurrentValue.Budget;
        if (!budget.AllowPaidCalls)
        {
            throw new ProviderException(GenerationErrorCode.ProviderNotConfigured, "Gerçek AI çağrıları kapalı (ücretli çağrılar için yönetici onayı ve yapılandırma gerekir).", retryable: false);
        }

        if (!budget.Enforce)
        {
            return;
        }

        var spend = await GetSpendAsync(db, organizationId, jobId, ct);
        if (spend.Job + worstCasePerCallUsd > budget.MaxJobCostUsd)
        {
            throw new ProviderException(GenerationErrorCode.BudgetExceeded, "Bu işin AI maliyet sınırı aşılacaktı; sağlayıcı çağrılmadı.", retryable: false);
        }

        if (spend.Today + worstCasePerCallUsd > budget.DailyLimitUsd)
        {
            throw new ProviderException(GenerationErrorCode.BudgetExceeded, "Günlük AI bütçesi doldu; sağlayıcı çağrılmadı.", retryable: false);
        }

        if (spend.Month + worstCasePerCallUsd > budget.MonthlyLimitUsd)
        {
            throw new ProviderException(GenerationErrorCode.BudgetExceeded, "Aylık AI bütçesi doldu; sağlayıcı çağrılmadı.", retryable: false);
        }
    }
}
