using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Identity;
using SanalProva.Api.Features.Account;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Tenancy;

namespace SanalProva.Api.Infrastructure;

public static class AppClaims
{
    public const string OrganizationId = "org_id";

    /// <summary>Sunucu tarafı oturum (<c>auth_sessions</c>) kimliği; girişte çereze yazılır.</summary>
    public const string SessionId = "sp_sid";
}

/// <summary>Girişte kullanıcının firma kimliğini çereze claim olarak ekler.</summary>
public sealed class AppUserClaimsPrincipalFactory(UserManager<AppUser> userManager, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser>(userManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(AppClaims.OrganizationId, user.OrganizationId.ToString()));
        return identity;
    }
}

public static class PrincipalExtensions
{
    public static Guid? GetUserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static Guid? GetOrganizationId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(AppClaims.OrganizationId), out var id) ? id : null;
}

/// <summary>
/// Doğrulanmış istek bağlamı: kim, hangi firma, hangi şube ve hangi rolle. Yalnızca endpoint filtreleri doldurur;
/// iş mantığı istemciden gelen firma/şube kimliğine değil bu nesneye güvenir.
/// </summary>
public sealed class CurrentUser
{
    public Guid UserId { get; internal set; }
    public Guid OrganizationId { get; internal set; }
    public Guid? StoreId { get; internal set; }
    public StoreRole Role { get; internal set; }

    public Guid RequireStoreId() => StoreId ?? throw new InvalidOperationException("Bu uç nokta şube erişim filtresi gerektirir.");
}

/// <summary>Kimliği doğrulanmış isteğin firma kimliğini sorgu filtrelerinin okuduğu bağlama yazar.</summary>
public static class TenantMiddleware
{
    public static IApplicationBuilder UseTenantContext(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var organizationId = context.User.Identity?.IsAuthenticated == true ? context.User.GetOrganizationId() : null;
            context.RequestServices.GetRequiredService<TenantContext>().OrganizationId = organizationId;
            await next();
        });
}

/// <summary>
/// Şube seviyesinde yetkilendirme. <c>X-Store-Id</c> başlığındaki şubede kullanıcının en az <see cref="Minimum"/> rolü
/// yoksa istek reddedilir. Var olmayan şube ile başka firmanın şubesi aynı şekilde 403 döner (şube kimliği sızdırılmaz).
/// Başarılıysa şube kimliği sorgu filtresine yazılır: oturum, iş ve cihaz sorguları otomatik olarak o şubeyle sınırlanır.
/// </summary>
public sealed class StoreAccessFilter(StoreRole minimum) : IEndpointFilter
{
    public const string HeaderName = "X-Store-Id";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var userId = http.User.GetUserId();
        var organizationId = http.User.GetOrganizationId();
        if (userId is null || organizationId is null)
        {
            return Results.Unauthorized();
        }

        if (!Guid.TryParse(http.Request.Headers[HeaderName].ToString(), out var storeId))
        {
            return Problem(StatusCodes.Status400BadRequest, "STORE_REQUIRED", "Şube seçimi gerekli.");
        }

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var role = await db.UserStoreRoles.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.UserId == userId && r.StoreId == storeId && r.OrganizationId == organizationId)
            .Join(db.Stores.IgnoreQueryFilters().Where(s => s.IsActive && s.OrganizationId == organizationId), r => r.StoreId, s => s.Id, (r, _) => (StoreRole?)r.Role)
            .SingleOrDefaultAsync(http.RequestAborted);

        if (role is null)
        {
            return Problem(StatusCodes.Status403Forbidden, "STORE_FORBIDDEN", "Bu şubeye erişim yetkiniz yok.");
        }

        if (role < minimum)
        {
            return Problem(StatusCodes.Status403Forbidden, "INSUFFICIENT_ROLE", "Bu işlem için yetkiniz yok.");
        }

        var current = http.RequestServices.GetRequiredService<CurrentUser>();
        current.UserId = userId.Value;
        current.OrganizationId = organizationId.Value;
        current.StoreId = storeId;
        current.Role = role.Value;
        http.RequestServices.GetRequiredService<TenantContext>().StoreId = storeId;
        return await next(context);
    }

    internal static IResult Problem(int status, string code, string title) =>
        Results.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}

/// <summary>
/// Firma seviyesindeki yönetim (katalog) için: kullanıcının firmadaki herhangi bir şubede en az <see cref="Minimum"/> rolü olmalı.
/// Katalog firma seviyesinde paylaşıldığı için belirli bir şube seçimi gerekmez.
/// </summary>
public sealed class OrganizationRoleFilter(StoreRole minimum) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var userId = http.User.GetUserId();
        var organizationId = http.User.GetOrganizationId();
        if (userId is null || organizationId is null)
        {
            return Results.Unauthorized();
        }

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        // Roller veritabanında metin olarak saklanır: SQL MAX() alfabetik çalışır ("Staff" > "Manager"), bu yüzden en yüksek rol bellekte seçilir
        var roles = await db.UserStoreRoles.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.UserId == userId && r.OrganizationId == organizationId)
            .Select(r => r.Role)
            .ToListAsync(http.RequestAborted);
        StoreRole? best = roles.Count == 0 ? null : roles.Max();

        if (best is null || best < minimum)
        {
            return StoreAccessFilter.Problem(StatusCodes.Status403Forbidden, "INSUFFICIENT_ROLE", "Bu işlem için yetkiniz yok.");
        }

        var current = http.RequestServices.GetRequiredService<CurrentUser>();
        current.UserId = userId.Value;
        current.OrganizationId = organizationId.Value;
        current.Role = best.Value;
        return await next(context);
    }
}

/// <summary>
/// Platform (SaaS işletmecisi) yöneticisi yetkisi. Mağaza rollerinden bağımsızdır: Owner bile platform yöneticisi olamaz ve hiçbir API bu bayrağı veremez
/// (<see cref="AppUser.IsPlatformAdmin"/> yalnızca veritabanında/dağıtım betiğiyle ayarlanır). Bayrak <b>her istekte</b> veritabanından okunur:
/// yetki geri alınınca çerezin süresini beklemez. Pasif hesap reddedilir. Reddedilen isteklerde yanıt aynıdır (yetki yapısı sızdırılmaz).
/// </summary>
public sealed class PlatformAdminFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var userId = http.User.GetUserId();
        var organizationId = http.User.GetOrganizationId();
        if (userId is null || organizationId is null)
        {
            return Results.Unauthorized();
        }

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var allowed = await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.OrganizationId == organizationId && u.IsActive && u.IsPlatformAdmin, http.RequestAborted);
        if (!allowed)
        {
            return StoreAccessFilter.Problem(StatusCodes.Status403Forbidden, "PLATFORM_ADMIN_REQUIRED", "Bu işlem için yetkiniz yok.");
        }

        var current = http.RequestServices.GetRequiredService<CurrentUser>();
        current.UserId = userId.Value;
        current.OrganizationId = organizationId.Value;
        return await next(context);
    }
}

public static class AccessExtensions
{
    public static TBuilder RequirePlatformAdmin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization().AddEndpointFilter(new PlatformAdminFilter())
            .WithMetadata(new SanalProva.Api.Features.Agreement.AgreementExemptMetadata("Platform yönetimi: platform yöneticisi firma sözleşmesine tabi değildir; yetki her istekte veritabanından doğrulanır"));

    public static TBuilder RequireStore<TBuilder>(this TBuilder builder, StoreRole minimum = StoreRole.Staff)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization().AddEndpointFilter(new StoreAccessFilter(minimum));

    public static TBuilder RequireOrganizationRole<TBuilder>(this TBuilder builder, StoreRole minimum)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization().AddEndpointFilter(new OrganizationRoleFilter(minimum));
}

/// <summary>
/// Çerez doğrulaması (M5.2a): Identity'nin varsayılan doğrulayıcısı güvenlik damgasını yalnızca 30 dakikada bir denetler ve pasif hesabı hiç denetlemez;
/// pasife alınan, rolü/şifresi değişen kullanıcının açık oturumu bu yüzden sürerdi. Bu doğrulayıcı HER istekte kullanıcıyı (yalnızca üç alan) okur:
/// hesap yok/pasif, firma uyuşmuyor veya güvenlik damgası değişmişse çerez reddedilir. Çerezi yeniden yazmaz (başlık/Set-Cookie davranışı değişmez).
/// </summary>
public static class ActiveUserCookieValidator
{
    public static async Task ValidateAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var principal = context.Principal;
        var userId = principal?.GetUserId();
        var organizationId = principal?.GetOrganizationId();
        var stamp = principal?.FindFirstValue(context.HttpContext.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType);
        if (userId is null || organizationId is null || stamp is null)
        {
            await RejectAsync(context);
            return;
        }

        // Sunucu tarafı oturum: sp_sid claim'i olmayan (eski) çerez geçersizdir; satır yoksa (çıkış yapılmış) çerez düşer; kilitliyse istek kilitli işaretlenir
        if (!Guid.TryParse(principal!.FindFirstValue(AppClaims.SessionId), out var sessionId))
        {
            await RejectAsync(context);
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var current = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.IsActive, u.SecurityStamp, u.OrganizationId }).SingleOrDefaultAsync(context.HttpContext.RequestAborted);
        if (current is null || !current.IsActive || current.OrganizationId != organizationId || !string.Equals(current.SecurityStamp, stamp, StringComparison.Ordinal))
        {
            await RejectAsync(context);
            return;
        }

        var services = context.HttpContext.RequestServices;
        var check = await ScreenLockState.EvaluateAsync(db, userId.Value, sessionId, services.GetRequiredService<TimeProvider>().GetUtcNow(),
            PinEndpoints.IdleSeconds(services.GetRequiredService<IConfiguration>()), context.HttpContext.RequestAborted);
        if (check == SessionCheck.Invalid)
        {
            await RejectAsync(context);
        }
        else if (check == SessionCheck.Locked)
        {
            context.HttpContext.Items[ScreenLockState.ItemKey] = true;
        }
    }

    private static async Task RejectAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
    }
}
