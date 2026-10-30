using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Credits;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Credits;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.Credits;

public sealed record CreditTariffLineDto(string Key, int Credits);

/// <param name="Balance">Kesinleşmiş bakiye (tüketilmemiş kredi).</param>
/// <param name="Reserved">Devam eden / sonucu belirsiz işler için bloke edilen kredi.</param>
/// <param name="Available">Yeni işlere ayrılabilecek kredi (bakiye − rezerve).</param>
/// <param name="OpenJobs">Kredisi bloke edilmiş açık iş sayısı.</param>
/// <param name="Enforced">Gerçek (ücretli) sağlayıcıda kredi aranıyor mu; mock modda üretim kredi tüketmez.</param>
public sealed record CreditSummaryDto(
    int Balance, int Reserved, int Available, int OpenJobs, bool Enforced, int DefaultCostPerJob, IReadOnlyList<CreditTariffLineDto> Tariff);

public sealed record CreditLedgerItemDto(
    Guid Id, CreditEntryType Type, int Amount, int BalanceAfter, int ReservedAfter, Guid? JobId, string? Reference, CreditSource Source, string? Note, string? TariffKey, DateTimeOffset CreatedAt);

/// <summary>
/// Firma kredi görünümü (M5.4): yalnızca Owner ve yalnızca KENDİ firması (EF firma filtresi + açık firma koşulu). Salt okunur: kredi yükleme/düzeltme hiçbir API'den yapılamaz
/// (yalnızca operatör komutu; kaynak taraması testi). Ödeme alma yoktur (M5.5).
/// </summary>
public static class CreditEndpoints
{
    private const int MaxPage = 100;

    public static RouteGroupBuilder MapCreditEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/credits").WithTags("Kredi").RequireOrganizationRole(StoreRole.Owner);
        group.MapGet("", GetAsync).Produces<CreditSummaryDto>();
        group.MapGet("/ledger", LedgerAsync).Produces<IReadOnlyList<CreditLedgerItemDto>>();
        return api;
    }

    private static async Task<IResult> GetAsync(AppDbContext db, CurrentUser user, CreditTariff tariff, CancellationToken ct)
    {
        var account = await db.CreditAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.OrganizationId == user.OrganizationId, ct);
        var openJobs = await db.CreditLedger.AsNoTracking()
            .CountAsync(e => e.OrganizationId == user.OrganizationId && e.EntryType == CreditEntryType.Reserve
                && !db.CreditLedger.Any(t => t.OrganizationId == user.OrganizationId && t.JobId == e.JobId && (t.EntryType == CreditEntryType.Release || t.EntryType == CreditEntryType.Consume)), ct);
        var (defaultCost, lines) = tariff.Describe();
        return TypedResults.Ok(new CreditSummaryDto(
            account?.BalanceCredits ?? 0, account?.ReservedCredits ?? 0, account?.AvailableCredits ?? 0, openJobs, tariff.Enforce, defaultCost,
            lines.Select(l => new CreditTariffLineDto(l.Key, l.Value)).ToList()));
    }

    private static async Task<IResult> LedgerAsync(AppDbContext db, CurrentUser user, CancellationToken ct, int take = 50, int skip = 0)
    {
        take = Math.Clamp(take, 1, MaxPage);
        skip = Math.Max(0, skip);
        var rows = await db.CreditLedger.AsNoTracking().Where(e => e.OrganizationId == user.OrganizationId)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Skip(skip).Take(take).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(e => new CreditLedgerItemDto(e.Id, e.EntryType, e.Amount, e.BalanceAfter, e.ReservedAfter, e.JobId, e.Reference, e.Source, e.Note, e.TariffKey, e.CreatedAt)).ToList());
    }
}
