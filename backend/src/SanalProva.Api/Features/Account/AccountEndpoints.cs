using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using SanalProva.Api.Infrastructure;
using SanalProva.Infrastructure.Identity;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.Account;

/// <param name="Token">Kurulum bağlantısındaki (<c>#t=</c> fragment'ı) düz token. Günlüğe, denetim kaydına veya hata yanıtına yazılmaz.</param>
public sealed record SetupPasswordRequest(
    [property: Required, StringLength(200, MinimumLength = 20)] string Token,
    [property: Required, StringLength(128, MinimumLength = 1)] string Password);

/// <summary>Parola politikası ihlali: yalnızca Identity hata kodları döner (parola veya kullanıcı bilgisi asla).</summary>
public static class AccountEndpoints
{
    public const string SetupPasswordRateLimit = "setup-password";

    public static RouteGroupBuilder MapAccountEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/account").WithTags("Account");
        // Anonim (oturum yok): kimlik, 256 bitlik tek kullanımlık token'dır. CSRF grup filtresinden gelir; IP başına hız sınırı uygulanır.
        group.MapPost("/setup-password", SetupPasswordAsync).RequireRateLimiting(SetupPasswordRateLimit)
            .Produces(StatusCodes.Status204NoContent);
        return api;
    }

    /// <summary>
    /// Token tüketimi, parola yazımı ve denetim kaydı TEK veritabanı transaction'ındadır (M5.1 §2.2): herhangi bir adım başarısız olursa hepsi geri alınır,
    /// token kullanılmamış kalır ve parola yazılmaz. Geçersiz/süresi dolmuş/kullanılmış/iptal edilmiş/amacı yanlış token'lar tek bir yanıt verir
    /// (<c>SETUP_LINK_INVALID</c>): hangi nedenle reddedildiği dışarıya sızmaz.
    /// </summary>
    private static async Task<IResult> SetupPasswordAsync(
        SetupPasswordRequest request, AppDbContext db, UserManager<AppUser> users, AccountSetupTokenService tokens, AuditTrail audit, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Koşullu tek UPDATE: eşzamanlı isteklerden yalnızca biri geçer; ikincisi birincinin commit/rollback'ini bekler
        var consumed = await tokens.ConsumeAsync(request.Token, AccountSetupTokenPurpose.InitialPassword, ct);
        if (consumed.Status != SetupTokenConsumeStatus.Success)
        {
            return InvalidLink();
        }

        var user = await users.FindByIdAsync(consumed.UserId.ToString());
        if (user is null || !user.IsActive || user.OrganizationId != consumed.OrganizationId || user.PasswordHash is not null)
        {
            await transaction.RollbackAsync(ct);
            return InvalidLink();
        }

        // Parola politikası bu çağrıda doğrulanır; ihlalde transaction geri alınır (token tüketilmemiş sayılır, kullanıcı yeniden deneyebilir)
        var added = await users.AddPasswordAsync(user, request.Password);
        if (!added.Succeeded)
        {
            await transaction.RollbackAsync(ct);
            return ApiProblems.Create(StatusCodes.Status400BadRequest, "PASSWORD_POLICY", "Parola politikaya uymuyor.",
                new Dictionary<string, object?> { ["errors"] = added.Errors.Select(e => e.Code).ToList() });
        }

        audit.RecordFor(user.OrganizationId, user.Id, "Account.PasswordSet", nameof(AppUser), user.Id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return TypedResults.NoContent();
    }

    private static IResult InvalidLink() =>
        ApiProblems.BadRequest("SETUP_LINK_INVALID", "Bağlantı geçersiz veya süresi dolmuş. Yöneticinizden yeni bir bağlantı isteyin.");
}
