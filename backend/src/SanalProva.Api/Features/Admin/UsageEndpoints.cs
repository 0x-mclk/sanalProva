using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Generation;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Generation;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.Admin;

public sealed record UsageTotals(int Calls, int Successes, int Failures, int Retries, double AverageDurationMs, decimal Credits, int InputTokens, int OutputTokens, decimal EstimatedCostUsd, decimal ActualCostUsd);

public sealed record UsageByDay(DateOnly Day, int Calls, int Failures, decimal EstimatedCostUsd);

public sealed record UsageByModel(string Provider, string Model, string? Quality, int Calls, int Successes, int Failures, int Retries, double AverageDurationMs, decimal Credits, decimal EstimatedCostUsd, decimal ActualCostUsd);

public sealed record BudgetStatus(
    bool Live, bool AllowPaidCalls, bool Enforce, decimal DailyLimitUsd, decimal MonthlyLimitUsd, decimal MaxJobCostUsd, decimal SpentTodayUsd, decimal SpentMonthUsd);

public sealed record ConfiguredModel(
    string JobType, string Provider, string Model, string DisplayName, string DocStatus, string DocsVerifiedOn, bool LiveVerified, bool Known, string? Warning);

/// <summary>Maliyet/kullanım raporu: kişisel veri içermez (yalnızca sağlayıcı, model, süre, jeton, kredi ve tutar). Tutarlar tahmindir; fatura sağlayıcıdadır.</summary>
public sealed record UsageReport(DateOnly From, DateOnly To, UsageTotals Totals, IReadOnlyList<UsageByDay> ByDay, IReadOnlyList<UsageByModel> ByModel, BudgetStatus Budget, IReadOnlyList<ConfiguredModel> Models);

public static class UsageEndpoints
{
    private const int MaxRangeDays = 92;

    public static RouteGroupBuilder MapUsageEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/admin/usage", GetAsync).WithTags("Admin").RequireOrganizationRole(StoreRole.Manager).Produces<UsageReport>();
        return api;
    }

    private static async Task<IResult> GetAsync(
        AppDbContext db, CurrentUser user, BudgetGuard budget, IOptionsMonitor<GenerationSettings> generation, TimeProvider clock, CancellationToken ct, DateOnly? from = null, DateOnly? to = null)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var end = to ?? today;
        var start = from ?? end.AddDays(-29);
        if (start > end || end.DayNumber - start.DayNumber > MaxRangeDays)
        {
            return ApiProblems.BadRequest("INVALID_RANGE", $"Tarih aralığı geçersiz (en fazla {MaxRangeDays} gün).");
        }

        var startUtc = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var endUtc = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        // Firma filtresi sorgu filtresinden gelir (ITenantOwned); kayıtlar bellekte gruplanır (rol/enum metin olarak saklanır)
        var rows = await db.AiUsageLogs.AsNoTracking().Where(l => (l.OccurredAt ?? l.CreatedAt) >= startUtc && (l.OccurredAt ?? l.CreatedAt) < endUtc).OrderBy(l => l.OccurredAt ?? l.CreatedAt).Take(20_000).ToListAsync(ct);

        // Mutabakat düzeltme satırları (±) yalnızca tutar toplamlarına girer; çağrı/hata/deneme sayaçları gerçek çağrıları sayar
        static UsageTotals Totals(IReadOnlyCollection<AiUsageLog> items)
        {
            var calls = items.Where(i => !i.IsAdjustment).ToList();
            return new(
                calls.Count, calls.Count(i => i.Success), calls.Count(i => !i.Success), calls.Count(i => i.Attempt > 1),
                calls.Count == 0 ? 0 : calls.Average(i => i.DurationMs), items.Sum(i => i.CreditsUsed ?? 0), items.Sum(i => i.InputTokens ?? 0), items.Sum(i => i.OutputTokens ?? 0),
                items.Sum(i => i.EstimatedCostUsd), items.Sum(i => i.ActualCostUsd ?? 0));
        }

        var byDay = rows.GroupBy(r => DateOnly.FromDateTime((r.OccurredAt ?? r.CreatedAt).UtcDateTime)).OrderBy(g => g.Key)
            .Select(g => new UsageByDay(g.Key, g.Count(i => !i.IsAdjustment), g.Count(i => !i.IsAdjustment && !i.Success), g.Sum(i => i.EstimatedCostUsd))).ToList();
        var byModel = rows.GroupBy(r => (r.Provider, r.Model, r.Quality)).OrderByDescending(g => g.Sum(i => i.EstimatedCostUsd)).Select(g =>
        {
            var t = Totals(g.ToList());
            return new UsageByModel(g.Key.Provider, g.Key.Model, g.Key.Quality, t.Calls, t.Successes, t.Failures, t.Retries, t.AverageDurationMs, t.Credits, t.EstimatedCostUsd, t.ActualCostUsd);
        }).ToList();

        var settings = generation.CurrentValue;
        var spend = await budget.GetSpendAsync(db, user.OrganizationId, null, ct);
        var status = new BudgetStatus(
            !settings.UseMockProviders, settings.Budget.AllowPaidCalls, settings.Budget.Enforce, settings.Budget.DailyLimitUsd, settings.Budget.MonthlyLimitUsd,
            settings.Budget.MaxJobCostUsd, spend.Today, spend.Month);

        var models = settings.Strategies.Select(entry =>
        {
            var info = ModelCatalog.Find(entry.Value.Provider, entry.Value.Model);
            var warning = info is null
                ? (entry.Value.Provider == GenerationStrategy.ImageWorkerProvider ? null : "Model doğrulanmış listede yok: çağrılmaz.")
                : ModelCatalog.CheckCapability(entry.Value.Provider, entry.Value.Model, entry.Key);
            return new ConfiguredModel(entry.Key.ToString(), entry.Value.Provider, entry.Value.Model, info?.DisplayName ?? entry.Value.Model, info?.DocStatus ?? "—", info?.DocsVerifiedOn ?? "—", info?.LiveVerified ?? false, info is not null, warning);
        }).OrderBy(m => m.JobType).ToList();

        return TypedResults.Ok(new UsageReport(start, end, Totals(rows), byDay, byModel, status, models));
    }
}
