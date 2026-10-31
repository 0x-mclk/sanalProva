using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Features.Agreement;
using SanalProva.Api.Hubs;
using SanalProva.Api.Infrastructure;
using SanalProva.Infrastructure.Identity;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.Account;

/// <param name="HasPin">Kullanıcının PIN'i var mı (yoksa kilit parolayla açılır).</param>
/// <param name="IdleLockSeconds">Bu kadar süre işlem yapılmazsa arayüz kilitlenir (sunucu yapılandırması).</param>
/// <param name="Locked">Bu çerez oturumu SUNUCUDA kilitli mi (sayfa yenileme/ikinci sekme de bu değeri okur).</param>
public sealed record LockStatusDto(bool HasPin, int IdleLockSeconds, bool Locked);

/// <param name="CurrentPassword">PIN belirlemek/değiştirmek hesap parolasını gerektirir: açık bırakılmış bir tablet PIN'i değiştiremez.</param>
public sealed record SetPinRequest(
    [property: Required, StringLength(128, MinimumLength = 1)] string CurrentPassword,
    [property: Required, StringLength(8, MinimumLength = 4)] string Pin);

/// <summary>PIN <b>veya</b> parola (PIN'i olmayan hesaplar için). İkisi birden/hiçbiri geçersizdir.</summary>
public sealed record UnlockRequest([property: StringLength(8)] string? Pin, [property: StringLength(128)] string? Password);

/// <summary>PIN kuralları: yalnızca rakam, 4–8 hane, tek rakam tekrarı ve ardışık diziler (1234, 4321) reddedilir.</summary>
public static class PinPolicy
{
    public const int MaxAttempts = 5;
    public const string UnlockRateLimit = "unlock";

    public static bool IsAcceptable(string? pin)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length is < 4 or > 8 || !pin.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (pin.Distinct().Count() == 1)
        {
            return false;
        }

        var steps = pin.Zip(pin.Skip(1), (a, b) => b - a).Distinct().ToList();
        return !(steps.Count == 1 && steps[0] is 1 or -1);
    }
}

/// <summary>
/// Ekran kilidi (Faz C): boşta kilitlenen tabletin kilidi sunucuda doğrulanan PIN (veya PIN'siz hesap için parola) ile açılır. PIN Identity hasher'ıyla saklanır
/// (<c>users.pin_hash</c>); doğrulama kullanıcı başına hız sınırlıdır ve <see cref="PinPolicy.MaxAttempts"/> ardışık hatada kullanıcının TÜM oturumları kapatılır
/// (güvenlik damgası yenilenir): PIN kaba kuvvetle denenemez, tablet çalınsa bile parola gerekir. Pasife alma/parola sıfırlama PIN'i siler. Kilit ARAYÜZ kalkanıdır:
/// çerez oturumu açık kalır, API yetkisi (rol/şube) her istekte yine sunucuda denetlenir.
/// </summary>
public static class PinEndpoints
{
    public static RouteGroupBuilder MapPinEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/account").WithTags("Account");
        group.MapGet("/lock", GetStatusAsync).RequireAuthorization().AllowWithoutAgreement("Kilit durumu: sözleşmesi kabul edilmemiş firmanın kullanıcısı da kilidini açabilmeli")
            .AllowWhileLocked("Kilit durumu: kilit ekranı ve diğer sekmeler sunucudaki durumu buradan okur").Produces<LockStatusDto>();
        // Kilitleme (boşta veya elle): sunucu bu çerez oturumunu kilitler ve açık hub bağlantılarını keser
        group.MapPost("/lock", LockAsync).RequireAuthorization().AllowWithoutAgreement("Kilitleme: sözleşmesi kabul edilmemiş firmanın kullanıcısı da kilitleyebilmeli")
            .AllowWhileLocked("Kilitleme tekrar edilebilir (zaten kilitliyken de güvenli)").Produces(StatusCodes.Status204NoContent);
        // Etkileşim sinyali: boşta süresini sıfırlar. Kilitliyken ÇAĞRILAMAZ (423): kilit yalnızca PIN/parola ile açılır.
        group.MapPost("/activity", ActivityAsync).RequireAuthorization().AllowWithoutAgreement("Etkileşim sinyali: ekran kilidi sözleşme kabulünden bağımsızdır").Produces(StatusCodes.Status204NoContent);
        group.MapPut("/pin", SetPinAsync).RequireAuthorization().RequireRateLimiting(PinPolicy.UnlockRateLimit).Produces(StatusCodes.Status204NoContent);
        group.MapPost("/unlock", UnlockAsync).RequireAuthorization().RequireRateLimiting(PinPolicy.UnlockRateLimit)
            .AllowWithoutAgreement("Kilit açma: kilit ekranı sözleşme kabulünden bağımsız çalışmalı")
            .AllowWhileLocked("Kilit açma: kilitliyken çalışabilmesi gereken tek yol (PIN/parola sunucuda doğrulanır, hız sınırlı)").Produces(StatusCodes.Status204NoContent);
        return api;
    }

    private static async Task<IResult> GetStatusAsync(ClaimsPrincipal principal, AppDbContext db, IConfiguration configuration, HttpContext http, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var hasPin = userId is { } id && await db.Users.AsNoTracking().AnyAsync(u => u.Id == id && u.PinHash != null, ct);
        return TypedResults.Ok(new LockStatusDto(hasPin, IdleSeconds(configuration), ScreenLockState.IsLocked(http)));
    }

    private static Guid? SessionOf(ClaimsPrincipal principal) => Guid.TryParse(principal.FindFirstValue(AppClaims.SessionId), out var id) ? id : null;

    private static async Task<IResult> LockAsync(ClaimsPrincipal principal, AppDbContext db, StaffConnectionRegistry connections, TimeProvider clock, CancellationToken ct)
    {
        if (SessionOf(principal) is not { } sessionId)
        {
            return TypedResults.Unauthorized();
        }

        var now = clock.GetUtcNow();
        await db.AuthSessions.Where(s => s.Id == sessionId && s.LockedAt == null).ExecuteUpdateAsync(u => u.SetProperty(s => s.LockedAt, now), ct);
        connections.AbortSession(sessionId); // müşteri/TV bildirimleri bu cihaza akmaz; kilit açılınca istemci yeniden bağlanır
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ActivityAsync(ClaimsPrincipal principal, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (SessionOf(principal) is not { } sessionId)
        {
            return TypedResults.Unauthorized();
        }

        var now = clock.GetUtcNow();
        await db.AuthSessions.Where(s => s.Id == sessionId && s.LockedAt == null).ExecuteUpdateAsync(u => u.SetProperty(s => s.LastInteractionAt, now), ct);
        return TypedResults.NoContent();
    }

    public static int IdleSeconds(IConfiguration configuration) => Math.Clamp(configuration.GetValue("Security:IdleLockSeconds", 300), 60, 3600);

    private static async Task<IResult> SetPinAsync(
        SetPinRequest request, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AuditTrail audit, StaffConnectionRegistry connections, CancellationToken ct)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null || !user.IsActive)
        {
            return TypedResults.Unauthorized();
        }

        if (!PinPolicy.IsAcceptable(request.Pin))
        {
            return ApiProblems.BadRequest("PIN_POLICY", "PIN 4–8 rakam olmalı; tekrar eden (1111) veya ardışık (1234) olamaz.");
        }

        // Yanlış parola da aynı sayaca girer: PIN değiştirme yoluyla parola denenemez
        if (!await users.CheckPasswordAsync(user, request.CurrentPassword))
        {
            return await FailAsync(user, users, db, audit, connections, "INVALID_PASSWORD", ct);
        }

        user.PinHash = users.PasswordHasher.HashPassword(user, request.Pin);
        user.PinFailedAttempts = 0;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await users.UpdateAsync(user);
        audit.RecordFor(user.OrganizationId, user.Id, "Account.PinSet", nameof(AppUser), user.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> UnlockAsync(
        UnlockRequest request, ClaimsPrincipal principal, UserManager<AppUser> users, AppDbContext db, AuditTrail audit, StaffConnectionRegistry connections, TimeProvider clock, CancellationToken ct)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null || !user.IsActive)
        {
            return TypedResults.Unauthorized();
        }

        var usePin = !string.IsNullOrEmpty(request.Pin);
        var usePassword = !string.IsNullOrEmpty(request.Password);
        if (usePin == usePassword)
        {
            return ApiProblems.BadRequest("UNLOCK_CREDENTIAL_REQUIRED", "PIN veya parola (yalnızca biri) gönderin.");
        }

        // PIN'i olan hesap yalnızca PIN ile açılır (parola yolu PIN'i atlatmak için kullanılamaz); PIN'siz hesap parolayla açılır
        var ok = usePin
            ? user.PinHash is not null && users.PasswordHasher.VerifyHashedPassword(user, user.PinHash, request.Pin!) != PasswordVerificationResult.Failed
            : user.PinHash is null && await users.CheckPasswordAsync(user, request.Password!);
        if (!ok)
        {
            return await FailAsync(user, users, db, audit, connections, usePin ? "INVALID_PIN" : "INVALID_PASSWORD", ct);
        }

        if (user.PinFailedAttempts != 0)
        {
            user.PinFailedAttempts = 0;
            await users.UpdateAsync(user);
        }

        // Sunucu tarafı kilit açılır (yalnızca BU çerez oturumu) ve boşta süresi baştan başlar
        if (SessionOf(principal) is { } sessionId)
        {
            var now = clock.GetUtcNow();
            await db.AuthSessions.Where(s => s.Id == sessionId && s.UserId == user.Id).ExecuteUpdateAsync(u => u.SetProperty(s => s.LockedAt, (DateTimeOffset?)null).SetProperty(s => s.LastInteractionAt, now), ct);
        }

        return TypedResults.NoContent();
    }

    private static async Task<IResult> FailAsync(AppUser user, UserManager<AppUser> users, AppDbContext db, AuditTrail audit, StaffConnectionRegistry connections, string code, CancellationToken ct)
    {
        // Atomik artış: paralel denemeler sayacı atlatamaz
        await users.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.PinFailedAttempts, u => u.PinFailedAttempts + 1), ct);
        var attempts = await users.Users.AsNoTracking().Where(u => u.Id == user.Id).Select(u => u.PinFailedAttempts).SingleAsync(ct);
        if (attempts >= PinPolicy.MaxAttempts)
        {
            var fresh = await users.FindByIdAsync(user.Id.ToString());
            if (fresh is not null)
            {
                fresh.PinFailedAttempts = 0;
                await users.UpdateSecurityStampAsync(fresh); // tüm oturumlar bir sonraki istekte düşer (ActiveUserCookieValidator)
                connections.AbortUser(fresh.Id); // açık hub bağlantıları da hemen kesilir (damga yalnızca bağlanırken denetlenir)
                await db.AuthSessions.Where(a => a.UserId == fresh.Id).ExecuteDeleteAsync(ct);
                audit.RecordFor(fresh.OrganizationId, fresh.Id, "Account.PinLockout", nameof(AppUser), fresh.Id);
                await db.SaveChangesAsync(ct);
            }

            return ApiProblems.Create(StatusCodes.Status401Unauthorized, "PIN_LOCKED_OUT", "Çok fazla hatalı deneme. Oturum kapatıldı; parolanızla yeniden giriş yapın.");
        }

        return ApiProblems.Create(StatusCodes.Status401Unauthorized, code, code == "INVALID_PIN" ? "PIN hatalı." : "Parola hatalı.",
            new Dictionary<string, object?> { ["remainingAttempts"] = PinPolicy.MaxAttempts - attempts });
    }
}
