using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai;
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

/// <summary>Personel/Manager görünümü (Faz C): yalnızca kullanılabilir kredi ve uyarı bayrakları. Defter, rezerve, tarife ve ödeme bilgisi YOKTUR (Owner ekranında).</summary>
public sealed record CreditBalanceDto(bool Enforced, int Available, bool IsLow, bool IsEmpty);

/// <param name="Cost">Bu üretimin kredi bedeli (mock modda 0: kredi harcanmaz).</param>
/// <param name="Live">Gerçek (ücretli) sağlayıcı etkin mi; false ise sonuç DEMO/mock'tur.</param>
/// <param name="Sufficient">Kalan kredi bu üretime yetiyor mu.</param>
public sealed record CreditQuoteDto(bool Enforced, bool Live, int Cost, int Available, bool Sufficient, int AvailableAfter, bool LowAfter);

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

        // Faz C: şubede çalışan her personel için salt okunur, dar görünüm
        var staff = api.MapGroup("/credits").WithTags("Kredi").RequireStore(StoreRole.Staff);
        staff.MapGet("/balance", BalanceAsync).Produces<CreditBalanceDto>();
        staff.MapGet("/quote", QuoteAsync).Produces<CreditQuoteDto>();
        return api;
    }

    private static async Task<IResult> BalanceAsync(AppDbContext db, CurrentUser user, IOptionsMonitor<CreditSettings> settings, CancellationToken ct)
    {
        var available = await AvailableAsync(db, user, ct);
        var threshold = settings.CurrentValue.LowBalanceThreshold;
        var enforced = settings.CurrentValue.Enforce;
        return TypedResults.Ok(new CreditBalanceDto(enforced, available, enforced && threshold > 0 && available <= threshold, enforced && available <= 0));
    }

    /// <summary>Üretim başlamadan önce bedel bilgisi. Tarifeden okunur (kabuldeki hesapla aynı kaynak); hiçbir şey ayrılmaz veya yazılmaz.</summary>
    private static async Task<IResult> QuoteAsync(
        AppDbContext db, CurrentUser user, CreditTariff tariff, IOptionsMonitor<CreditSettings> settings, IOptionsMonitor<GenerationSettings> generation, CancellationToken ct,
        GenerationJobType jobType = GenerationJobType.OutfitSet, string? modelKey = null, string? quality = null)
    {
        var live = !generation.CurrentValue.UseMockProviders;
        var available = await AvailableAsync(db, user, ct);
        var enforced = tariff.Enforce && live;
        var cost = enforced ? tariff.Quote(jobType, modelKey, quality).Cost : 0;
        var after = Math.Max(0, available - cost);
        var threshold = settings.CurrentValue.LowBalanceThreshold;
        return TypedResults.Ok(new CreditQuoteDto(enforced, live, cost, available, !enforced || available >= cost, after, enforced && threshold > 0 && after <= threshold));
    }

    private static async Task<int> AvailableAsync(AppDbContext db, CurrentUser user, CancellationToken ct) =>
        await db.CreditAccounts.AsNoTracking().Where(a => a.OrganizationId == user.OrganizationId).Select(a => a.BalanceCredits - a.ReservedCredits).SingleOrDefaultAsync(ct);

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
