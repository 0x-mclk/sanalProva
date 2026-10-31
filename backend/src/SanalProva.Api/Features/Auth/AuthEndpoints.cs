using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Features.Agreement;
using SanalProva.Api.Hubs;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Identity;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.Auth;

public sealed record LoginRequest(string Email, string Password, bool RememberMe);

public sealed record StoreAccessDto(Guid Id, string Name, string Code, StoreRole Role);

public sealed record MeResponse(Guid Id, string DisplayName, string Email, Guid OrganizationId, string OrganizationName, bool DemoCatalog, IReadOnlyList<StoreAccessDto> Stores, bool AgreementAcceptanceRequired, bool CanAcceptAgreement, bool IsPlatformAdmin);

public sealed record CsrfResponse(string Token, string HeaderName);

public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/auth").WithTags("Auth");

        group.MapGet("/csrf", (HttpContext http, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(http);
            return TypedResults.Ok(new CsrfResponse(tokens.RequestToken!, tokens.HeaderName!));
        });

        group.MapPost("/login", LoginAsync).RequireRateLimiting(LoginRateLimitPolicy).Produces<MeResponse>();

        group.MapPost("/logout", async (SignInManager<AppUser> signIn, ClaimsPrincipal principal, AppDbContext db, StaffConnectionRegistry connections) =>
        {
            // Sunucu tarafı oturum satırı silinir (aynı çerez bir daha kullanılamaz) ve bu oturumun açık hub bağlantıları kesilir
            if (Guid.TryParse(principal.FindFirstValue(AppClaims.SessionId), out var sessionId))
            {
                await db.AuthSessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync();
                connections.AbortSession(sessionId);
            }

            await signIn.SignOutAsync();
            return TypedResults.NoContent();
        }).RequireAuthorization().AllowWithoutAgreement("Çıkış: kabul edilmemiş firmanın kullanıcısı da oturumu kapatabilmeli")
            .AllowWhileLocked("Çıkış: kilitli ekrandan da çıkış yapılabilmeli");

        group.MapGet("/me", GetMeAsync).RequireAuthorization().AllowWithoutAgreement("Oturum bilgisi: arayüz sözleşme yönlendirmesini buradan alır")
            .AllowWhileLocked("Kimlik bilgisi (ad, şube listesi): müşteri verisi içermez; arayüz kilit ekranını çizmek için kullanır").Produces<MeResponse>();

        return api;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request, UserManager<AppUser> users, SignInManager<AppUser> signIn, AppDbContext db, AgreementGate gate, TimeProvider clock, CancellationToken ct)
    {
        // Hangi alanın yanlış olduğu söylenmez (kullanıcı adı tahmini engellenir)
        var invalid = Results.Problem(
            title: "E-posta veya parola hatalı.",
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: new Dictionary<string, object?> { ["code"] = "INVALID_CREDENTIALS" });

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
        {
            return invalid;
        }

        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null || !user.IsActive)
        {
            return invalid;
        }

        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            return Results.Problem(
                title: "Çok fazla hatalı deneme. Hesap geçici olarak kilitlendi.",
                statusCode: StatusCodes.Status423Locked,
                extensions: new Dictionary<string, object?> { ["code"] = "ACCOUNT_LOCKED" });
        }

        if (!result.Succeeded)
        {
            return invalid;
        }

        // Sunucu tarafı oturum satırı (ekran kilidi durumu burada): çerezin sp_sid claim'i bu satıra bağlanır. Eski satırlar temizlenir (çerez ömrü 12 sa).
        var now = clock.GetUtcNow();
        await db.AuthSessions.Where(s => s.UserId == user.Id && s.LastInteractionAt < now.AddDays(-3)).ExecuteDeleteAsync(ct);
        var session = new AuthSession { UserId = user.Id, CreatedAt = now, LastInteractionAt = now };
        db.AuthSessions.Add(session);
        await db.SaveChangesAsync(ct);
        await signIn.SignInWithClaimsAsync(user, request.RememberMe, [new Claim(AppClaims.SessionId, session.Id.ToString())]);

        return TypedResults.Ok(await BuildMeAsync(db, gate, user, ct));
    }

    private static async Task<IResult> GetMeAsync(ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AgreementGate gate, CancellationToken ct)
    {
        var user = await users.GetUserAsync(principal);
        return user is null || !user.IsActive ? TypedResults.Unauthorized() : TypedResults.Ok(await BuildMeAsync(db, gate, user, ct));
    }

    private static async Task<MeResponse> BuildMeAsync(AppDbContext db, AgreementGate gate, AppUser user, CancellationToken ct)
    {
        // Girişte kiracı bağlamı henüz yok; sorgular kullanıcının kendi firmasıyla açıkça sınırlandırılır
        var organization = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == user.OrganizationId, ct);
        var stores = await db.UserStoreRoles.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.UserId == user.Id && r.OrganizationId == user.OrganizationId)
            .Join(db.Stores.IgnoreQueryFilters().Where(s => s.IsActive), r => r.StoreId, s => s.Id, (r, s) => new { r.Role, Store = s })
            .OrderBy(x => x.Store.Name)
            .Select(x => new StoreAccessDto(x.Store.Id, x.Store.Name, x.Store.Code, x.Role))
            .ToListAsync(ct);

        return new MeResponse(user.Id, user.DisplayName, user.Email!, organization.Id, organization.Name, organization.Settings.IsDemoCatalog, stores,
            AgreementAcceptanceRequired: !await gate.IsAcceptedAsync(db, user.OrganizationId, ct), CanAcceptAgreement: await AgreementEndpoints.IsOwnerAsync(db, user.Id, user.OrganizationId, ct), IsPlatformAdmin: user.IsPlatformAdmin);
    }
}
