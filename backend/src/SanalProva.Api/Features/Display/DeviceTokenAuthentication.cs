using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SanalProva.Api.Hubs;
using SanalProva.Api.Infrastructure;
using SanalProva.Infrastructure.Displays;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Tenancy;

namespace SanalProva.Api.Features.Displays;

public static class DeviceTokenDefaults
{
    /// <summary>TV cihaz belirteci şeması. Personel çerezinden tamamen ayrıdır: bu şemayla yalnızca <c>DisplayDevice</c> politikalı uç noktalara girilir.</summary>
    public const string Scheme = "DeviceToken";

    public const string DeviceIdClaim = "display_device_id";
    public const string StoreIdClaim = "display_store_id";
}

public static class DevicePrincipalExtensions
{
    public static Guid? GetDisplayDeviceId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(DeviceTokenDefaults.DeviceIdClaim), out var id) ? id : null;

    public static Guid? GetDisplayStoreId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(DeviceTokenDefaults.StoreIdClaim), out var id) ? id : null;
}

/// <summary>
/// Cihaz belirtecini (Authorization: Bearer, SignalR için <c>access_token</c> sorgu parametresi) doğrular. Belirteç düz haliyle saklanmaz,
/// yalnızca SHA-256 özeti aranır; iptal edilmiş cihaz hemen reddedilir. Başarılıysa istek kiracı bağlamı cihazın firma/şubesine sabitlenir
/// (sorgu filtreleri defansif ek katman); asıl sınır her sorguda cihazın <c>current_session_id</c>'sidir.
/// </summary>
public sealed class DeviceTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, AppDbContext db, TenantContext tenant)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken();
        if (token is null)
        {
            return AuthenticateResult.NoResult();
        }

        var hash = DisplayCredentials.HashSecret(token);
        var device = await db.DisplayDevices.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TokenHash == hash && d.RevokedAt == null)
            .Select(d => new { d.Id, d.OrganizationId, d.StoreId })
            .SingleOrDefaultAsync(Context.RequestAborted);
        if (device is null)
        {
            return AuthenticateResult.Fail("Geçersiz veya iptal edilmiş cihaz belirteci.");
        }

        tenant.OrganizationId = device.OrganizationId;
        tenant.StoreId = device.StoreId;
        var identity = new ClaimsIdentity(
        [
            new Claim(DeviceTokenDefaults.DeviceIdClaim, device.Id.ToString()),
            new Claim(DeviceTokenDefaults.StoreIdClaim, device.StoreId.ToString()),
            new Claim(AppClaims.OrganizationId, device.OrganizationId.ToString()),
        ], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private string? ReadToken()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var value = header["Bearer ".Length..].Trim();
            return value.Length == 0 ? null : value;
        }

        // Tarayıcı WebSocket/SSE başlık gönderemez: SignalR belirteci yalnızca hub yolunda sorgu parametresiyle taşır
        if (Request.Path.StartsWithSegments(DisplayHub.Path, StringComparison.OrdinalIgnoreCase))
        {
            var query = Request.Query["access_token"].ToString();
            return query.Length == 0 ? null : query;
        }

        return null;
    }
}
