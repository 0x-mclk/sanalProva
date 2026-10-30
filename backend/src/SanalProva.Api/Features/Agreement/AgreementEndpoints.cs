using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.Agreement;

/// <param name="Accepted">Çağıranın firması bu sürümü kabul etti mi.</param>
/// <param name="CanAccept">Çağıran firmanın Owner'ı mı (yalnızca Owner kabul edebilir).</param>
public sealed record AgreementDto(string Version, string Title, string Text, string TextSha256, bool Accepted, DateTimeOffset? AcceptedAt, bool CanAccept);

/// <param name="Version">Kabul edilen sürüm: güncel sürümle birebir aynı olmalı (eski ekrandan eski metin kabul edilemez).</param>
/// <param name="TextSha256">Kullanıcının gördüğü metnin SHA-256 özeti (hex): metin değiştiyse kabul reddedilir.</param>
public sealed record AcceptAgreementRequest(
    [property: Required, StringLength(20)] string Version,
    [property: Required, StringLength(64, MinimumLength = 64)] string TextSha256);

/// <summary>
/// İşletme hizmet sözleşmesi (M5.1e). Müşteri KVKK onayından (<c>ConsentNotice</c>) tamamen ayrıdır. Bu uç noktalar sözleşme kabul edilmeden de çalışır
/// (aksi halde kabul edilemezdi); firma kapsamlı hiçbir mağaza işlemi bu muafiyeti paylaşmaz.
/// </summary>
public static class AgreementEndpoints
{
    public static RouteGroupBuilder MapAgreementEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/agreement/current", GetCurrentAsync).WithTags("Agreement").RequireAuthorization()
            .AllowWithoutAgreement("Sözleşme görüntüleme: kabul edilemeyen firma metni görebilmelidir").Produces<AgreementDto>();
        api.MapPost("/organization/agreement-acceptance", AcceptAsync).WithTags("Agreement").RequireOrganizationRole(StoreRole.Owner)
            .AllowWithoutAgreement("Sözleşme kabulü: yalnızca Owner (rol filtresi); kabul edilmeden çağrılabilmelidir")
            .Produces<AgreementDto>(StatusCodes.Status201Created).Produces<AgreementDto>();
        return api;
    }

    internal static async Task<bool> IsOwnerAsync(AppDbContext db, Guid userId, Guid organizationId, CancellationToken ct)
    {
        // Roller metin olarak saklanır; eşitlik karşılaştırması güvenlidir (MAX/ORDER BY değil)
        return await db.UserStoreRoles.IgnoreQueryFilters().AsNoTracking().AnyAsync(r => r.UserId == userId && r.OrganizationId == organizationId && r.Role == StoreRole.Owner, ct);
    }

    private static async Task<IResult> GetCurrentAsync(System.Security.Claims.ClaimsPrincipal principal, AppDbContext db, AgreementGate gate, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var organizationId = principal.GetOrganizationId();
        if (userId is null || organizationId is null)
        {
            return Results.Unauthorized();
        }

        return TypedResults.Ok(await BuildAsync(db, gate, userId.Value, organizationId.Value, ct));
    }

    private static async Task<IResult> AcceptAsync(
        AcceptAgreementRequest request, AppDbContext db, AgreementGate gate, CurrentUser user, AuditTrail audit, TimeProvider clock, CancellationToken ct)
    {
        var current = gate.Current;
        // Kullanıcının gördüğü metin güncel değilse (yeni sürüm yayımlandı veya metin değişti) kabul yazılmaz
        if (!string.Equals(request.Version, current.Version, StringComparison.Ordinal)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(request.TextSha256.ToLowerInvariant()), Encoding.ASCII.GetBytes(current.TextSha256Hex)))
        {
            return ApiProblems.Conflict("AGREEMENT_VERSION_MISMATCH", "Sözleşme metni güncellendi. Güncel metni görüntüleyip tekrar kabul edin.");
        }

        var existing = await db.OrganizationAgreementAcceptances.AsNoTracking().SingleOrDefaultAsync(a => a.AgreementVersion == current.Version, ct);
        if (existing is not null)
        {
            gate.MarkAccepted(user.OrganizationId, current.Version);
            return TypedResults.Ok(await BuildAsync(db, gate, user.UserId, user.OrganizationId, ct));
        }

        var acceptance = new OrganizationAgreementAcceptance
        {
            OrganizationId = user.OrganizationId, UserId = user.UserId, AgreementVersion = current.Version, TextSha256 = current.TextSha256,
            AcceptedAt = clock.GetUtcNow(), IpHash = audit.RequestIpHash(),
        };
        db.OrganizationAgreementAcceptances.Add(acceptance);
        audit.Record("Organization.AgreementAccepted", nameof(OrganizationAgreementAcceptance), acceptance.Id);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            // İki Owner aynı anda kabul etti: (firma, sürüm) benzersizliği ikincisini eler; sonuç aynıdır (kabul edilmiş)
            db.ChangeTracker.Clear();
            gate.MarkAccepted(user.OrganizationId, current.Version);
            return TypedResults.Ok(await BuildAsync(db, gate, user.UserId, user.OrganizationId, ct));
        }

        gate.MarkAccepted(user.OrganizationId, current.Version);
        return TypedResults.Created("/api/v1/agreement/current", await BuildAsync(db, gate, user.UserId, user.OrganizationId, ct));
    }

    private static async Task<AgreementDto> BuildAsync(AppDbContext db, AgreementGate gate, Guid userId, Guid organizationId, CancellationToken ct)
    {
        var current = gate.Current;
        var acceptedAt = await db.OrganizationAgreementAcceptances.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && a.AgreementVersion == current.Version).Select(a => (DateTimeOffset?)a.AcceptedAt).SingleOrDefaultAsync(ct);
        return new AgreementDto(current.Version, current.Title, current.Text, current.TextSha256Hex, acceptedAt is not null, acceptedAt, await IsOwnerAsync(db, userId, organizationId, ct));
    }
}
