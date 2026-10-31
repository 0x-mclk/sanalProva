using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Features.Account;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Infrastructure;

/// <summary>
/// Uç noktanın ekran KİLİTLİYKEN de çalışacağını belirtir. <b>Varsayılan zorlamadır</b>: kimlik isteyen her uç nokta (REST + hub), bu işareti taşımadıkça kilitli oturumda
/// <c>423 SCREEN_LOCKED</c> alır. İşaret yalnızca kilit durumu/kilit açma/çıkış/kimlik gibi gerekçeli uç noktalardadır (liste mimari testle sabitlenmiştir).
/// </summary>
public sealed record ScreenLockExemptMetadata(string Reason);

public static class ScreenLockExtensions
{
    public static TBuilder AllowWhileLocked<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return builder.WithMetadata(new ScreenLockExemptMetadata(reason));
    }

    public static IApplicationBuilder UseScreenLockEnforcement(this IApplicationBuilder app) => app.UseMiddleware<ScreenLockMiddleware>();
}

public enum SessionCheck
{
    Ok,
    Locked,
    Invalid,
}

/// <summary>Çerez oturumunun (<c>sp_sid</c>) sunucu tarafı durumu. Çerez doğrulayıcı, hub ve süpürücü aynı kuralı kullanır.</summary>
public static class ScreenLockState
{
    public const string ItemKey = "sp.screen_locked";

    /// <summary>İstemci etkileşim sinyali gecikmesi için tolerans: süre bu kadar saniye aşılırsa sunucu oturumu kendisi kilitler.</summary>
    public const int IdleGraceSeconds = 60;

    public static async Task<SessionCheck> EvaluateAsync(AppDbContext db, Guid userId, Guid sessionId, DateTimeOffset now, int idleSeconds, CancellationToken ct)
    {
        var row = await db.AuthSessions.AsNoTracking().Where(s => s.Id == sessionId && s.UserId == userId).Select(s => new { s.LockedAt, s.LastInteractionAt }).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return SessionCheck.Invalid;
        }

        if (row.LockedAt is not null)
        {
            return SessionCheck.Locked;
        }

        if (now - row.LastInteractionAt > TimeSpan.FromSeconds(idleSeconds + IdleGraceSeconds))
        {
            // İstemci kilitlemeyi unutmuş/çalışmıyor (JS durmuş, ağ kopuk): sunucu kendisi kilitler (koşullu: yarış halinde bir kez)
            await db.AuthSessions.Where(s => s.Id == sessionId && s.LockedAt == null).ExecuteUpdateAsync(u => u.SetProperty(s => s.LockedAt, now), ct);
            return SessionCheck.Locked;
        }

        return SessionCheck.Ok;
    }

    public static bool IsLocked(HttpContext context) => context.Items.TryGetValue(ItemKey, out var value) && value is true;
}

/// <summary>
/// Kilitli oturumda kimlik isteyen ve muaf işareti olmayan her uç nokta bağlama/filtrelerden ÖNCE reddedilir (<c>423 SCREEN_LOCKED</c>). Kilit durumu çerez doğrulayıcıdan
/// gelir (her istekte veritabanından): sayfa yenileme, doğrudan API çağrısı veya ikinci sekme aynı oturum satırını paylaştığı için atlatamaz.
/// </summary>
public sealed class ScreenLockMiddleware(RequestDelegate next)
{
    public const string ErrorCode = "SCREEN_LOCKED";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.GetEndpoint();
        if (endpoint is not null && Enforced(endpoint) && context.User.Identity?.IsAuthenticated == true && ScreenLockState.IsLocked(context))
        {
            await ApiProblems.Create(StatusCodes.Status423Locked, ErrorCode, "Ekran kilitli. Devam etmek için PIN veya parola ile kilidi açın.").ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    public static bool Enforced(Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0 && endpoint.Metadata.GetMetadata<ScreenLockExemptMetadata>() is null;
    }
}
