using SanalProva.Api.Infrastructure;
using SanalProva.Infrastructure.Monitoring;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.Platform;

/// <summary>
/// Platform yöneticisi finans özeti ve izleme (Faz C). <b>Yalnızca platform yöneticisi</b> (<c>RequirePlatformAdmin</c>: yetki her istekte veritabanından doğrulanır);
/// mağaza Owner/Manager rolleri erişemez. Salt okunurdur: kredi/bütçe yazma yalnızca operatör komutlarındadır, mutabakat kararı mevcut <c>/platform/cost-cases</c> API'sindedir.
/// Veriler firmalar arası okunur (bilinçli) ve kişisel veri içermez.
/// </summary>
public static class PlatformFinanceEndpoints
{
    public static RouteGroupBuilder MapPlatformFinanceEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/platform").WithTags("Platform").RequirePlatformAdmin();
        group.MapGet("/finance", async (AppDbContext db, PilotMonitoring monitoring, CancellationToken ct) =>
            TypedResults.Ok(await monitoring.FinanceAsync(db, ct))).Produces<IReadOnlyList<OrganizationFinance>>();
        group.MapGet("/monitoring", async (AppDbContext db, PilotMonitoring monitoring, CancellationToken ct) =>
            TypedResults.Ok(await monitoring.CheckAsync(db, ct))).Produces<IReadOnlyList<MonitorCheck>>();
        return api;
    }
}
