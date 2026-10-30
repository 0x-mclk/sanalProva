using Microsoft.EntityFrameworkCore;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Infrastructure.Generation;

/// <summary>
/// Bir firma için ETKİN AI bütçe sınırları (M5.3): platform üst sınırı (<c>Generation:Budget</c>, her firma için tavan) ile firma sınırının (<see cref="OrganizationSettings"/>) küçüğü.
/// Firma değeri platform tavanını yükseltemez; boş (<c>null</c>) firma değeri tavanı aynen bırakır. Her firmanın sınırı yalnızca kendi harcamasına uygulanır:
/// başka firmanın harcaması/sınırı bu hesaba girmez (platform çapında toplam sınır bilinçli olarak yoktur). M5.4 kredi sistemi bu çözümleyicinin yanına
/// "kalan kredi" denetimini ekler; sınırlar ve kredi ayrı kavramlardır.
/// </summary>
public sealed record BudgetLimits(decimal DailyUsd, decimal MonthlyUsd, decimal MaxJobUsd, bool DailyFromOrganization, bool MonthlyFromOrganization, bool MaxJobFromOrganization)
{
    public static BudgetLimits Resolve(BudgetSettings platform, OrganizationSettings? organization)
    {
        ArgumentNullException.ThrowIfNull(platform);
        // Negatif/bozuk firma değeri 0 sayılır (fail-closed: harcama yapılamaz); null = tavan
        static (decimal Value, bool FromOrganization) Min(decimal ceiling, decimal? own) =>
            own is null ? (ceiling, false) : (Math.Min(ceiling, Math.Max(0m, own.Value)), own.Value < ceiling);

        var (daily, d) = Min(platform.DailyLimitUsd, organization?.DailyBudgetUsd);
        var (monthly, m) = Min(platform.MonthlyLimitUsd, organization?.MonthlyBudgetUsd);
        var (job, j) = Min(platform.MaxJobCostUsd, organization?.MaxJobCostUsd);
        return new BudgetLimits(daily, monthly, job, d, m, j);
    }

    /// <summary>Firma ayarını okur. Firma bulunamazsa tüm sınırlar 0'dır (fail-closed). Veritabanı hataları çağırana yayılır.</summary>
    public static async Task<BudgetLimits> ForAsync(AppDbContext db, BudgetSettings platform, Guid organizationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var organization = await db.Organizations.AsNoTracking().Where(o => o.Id == organizationId).Select(o => o.Settings).SingleOrDefaultAsync(ct);
        return organization is null ? new BudgetLimits(0m, 0m, 0m, true, true, true) : Resolve(platform, organization);
    }
}
