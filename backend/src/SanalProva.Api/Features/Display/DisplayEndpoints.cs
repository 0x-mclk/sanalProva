using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SanalProva.Api.Hubs;
using SanalProva.Api.Features.Agreement;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Display;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Displays;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Storage;

namespace SanalProva.Api.Features.Displays;

/// <summary>
/// Smart TV: personel tarafı (çerez + şube rolü) ve TV tarafı (cihaz belirteci) birbirinden ayrı grup ve kimlik şemalarıyla çalışır.
/// Tasarım: docs/06-tv-signalr.md. Personel uç noktaları <c>/api/v1/displays</c>, TV uç noktaları <c>/api/v1/display</c> altındadır.
/// </summary>
public static class DisplayEndpoints
{
    public const string PairingRateLimit = "display-pairing";
    public const string PairingStatusRateLimit = "display-pairing-status";
    public const string ClaimRateLimit = "display-claim";
    public const string DeviceStateRateLimit = "display-state";

    // ------------------------------------------------------------------ Personel (çerez)

    public static RouteGroupBuilder MapDisplayManagementEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/displays").WithTags("Display");
        group.MapGet("", ListAsync).RequireStore(StoreRole.Staff).Produces<IReadOnlyList<DisplayDto>>();
        group.MapPost("/claim", ClaimAsync).RequireStore(StoreRole.Manager).RequireRateLimiting(ClaimRateLimit).Produces<DisplayDto>(StatusCodes.Status201Created);
        group.MapPatch("/{id:guid}", RenameAsync).RequireStore(StoreRole.Manager).Produces<DisplayDto>();
        group.MapDelete("/{id:guid}", RevokeAsync).RequireStore(StoreRole.Manager).Produces(StatusCodes.Status204NoContent);
        group.MapPut("/{id:guid}/session", AttachSessionAsync).RequireStore(StoreRole.Staff).Produces<DisplayDto>();
        group.MapDelete("/{id:guid}/session", DetachSessionAsync).RequireStore(StoreRole.Staff).Produces<DisplayDto>();
        group.MapPut("/{id:guid}/state", SetStateAsync).RequireStore(StoreRole.Staff).Produces<DisplayDto>();
        group.MapPost("/{id:guid}/clear", ClearAsync).RequireStore(StoreRole.Staff).Produces<DisplayDto>();
        return api;
    }

    private static async Task<IResult> ListAsync(AppDbContext db, DisplayPresence presence, CancellationToken ct)
    {
        // Şube filtresi (X-Store-Id) sorguya otomatik uygulanır: başka şubenin TV'si hiç görünmez
        var devices = await db.DisplayDevices.AsNoTracking().Where(d => d.RevokedAt == null).OrderBy(d => d.Name).ToListAsync(ct);
        return TypedResults.Ok(await ToDtosAsync(db, presence, devices, ct));
    }

    private static async Task<IResult> ClaimAsync(
        ClaimDisplayRequest request, AppDbContext db, CurrentUser user, ClaimAttemptLimiter limiter, DisplayPresence presence, IDisplayNotifier notifier,
        IOptions<StorageOptions> storage, AuditTrail audit, TimeProvider clock, CancellationToken ct)
    {
        if (limiter.IsLocked(user.UserId))
        {
            return ApiProblems.Create(StatusCodes.Status429TooManyRequests, "CLAIM_LOCKED", "Çok fazla hatalı kod denemesi yapıldı. Bir süre sonra tekrar deneyin.");
        }

        // Geçersiz, süresi dolmuş, kullanılmış ve var olmayan kod aynı yanıtı verir: hangisinin olduğu sızdırılmaz
        var invalid = ApiProblems.NotFound("Kod geçersiz veya süresi dolmuş. TV ekranındaki güncel kodu girin.", "PAIRING_CODE_INVALID");
        var code = DisplayCredentials.NormalizeCode(request.Code);
        if (code is null)
        {
            limiter.RecordFailure(user.UserId);
            return invalid;
        }

        var now = clock.GetUtcNow();
        var hash = DisplayCredentials.HashCode(code, storage.Value.SigningKey);
        var pairing = await db.DisplayPairings.AsNoTracking()
            .Where(p => p.CodeHash == hash && p.ClaimedDeviceId == null && p.ExpiresAt > now)
            .OrderByDescending(p => p.CreatedAt).FirstOrDefaultAsync(ct);
        if (pairing is null)
        {
            limiter.RecordFailure(user.UserId);
            return invalid;
        }

        // Cihaz belirteci = TV'nin eşleştirme anından beri bildiği gizli değer; hash'i cihaza taşınır. Düz belirteç hiçbir yerde saklanmaz.
        var device = new DisplayDevice
        {
            OrganizationId = user.OrganizationId, StoreId = user.RequireStoreId(), Name = request.Name.Trim(), TokenHash = pairing.PairingSecretHash,
            State = new DisplayState(), StateVersion = 1, StateChangedAt = now,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.DisplayDevices.Add(device);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // Aynı kod aynı anda başka istekle kullanıldı (cihaz belirteci özeti benzersizdir): bu istek kaybetti
            return invalid;
        }

        // Tek kullanımlık: koşullu güncelleme atomiktir; aynı kodu aynı anda kullanan iki istekten yalnızca biri satırı günceller
        var claimed = await db.DisplayPairings
            .Where(p => p.Id == pairing.Id && p.ClaimedDeviceId == null && p.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ClaimedDeviceId, device.Id).SetProperty(p => p.ClaimedByUserId, user.UserId), ct);
        if (claimed == 0)
        {
            await transaction.RollbackAsync(ct);
            return invalid;
        }

        audit.Record("Display.Paired", nameof(DisplayDevice), device.Id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        limiter.Reset(user.UserId);
        await TryNotifyAsync(() => notifier.NotifyPresenceAsync(DisplayStateService.ToChange(device, null), online: false, ct));
        return TypedResults.Created($"/api/v1/displays/{device.Id}", (await ToDtosAsync(db, presence, [device], ct))[0]);
    }

    private static async Task<IResult> RenameAsync(Guid id, RenameDisplayRequest request, AppDbContext db, DisplayPresence presence, AuditTrail audit, CancellationToken ct)
    {
        var device = await db.DisplayDevices.SingleOrDefaultAsync(d => d.Id == id && d.RevokedAt == null, ct);
        if (device is null)
        {
            return ApiProblems.NotFound("TV bulunamadı.");
        }

        device.Name = request.Name.Trim();
        audit.Record("Display.Renamed", nameof(DisplayDevice), id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok((await ToDtosAsync(db, presence, [device], ct))[0]);
    }

    /// <summary>Eşleştirmeyi kaldırır: belirteç hemen geçersiz olur, TV'ye "iptal edildi" gider ve ekran boşalır.</summary>
    private static async Task<IResult> RevokeAsync(Guid id, AppDbContext db, DisplayStateService service, AuditTrail audit, TimeProvider clock, CancellationToken ct)
    {
        if (!await db.DisplayDevices.AsNoTracking().AnyAsync(d => d.Id == id && d.RevokedAt == null, ct))
        {
            return ApiProblems.NotFound("TV bulunamadı.");
        }

        try
        {
            await service.MutateAsync(id, device =>
            {
                device.RevokedAt = clock.GetUtcNow();
                device.State = new DisplayState();
                device.CurrentSessionId = null;
                return DisplayStateReason.DeviceRevoked;
            }, null, ct);
        }
        catch (DisplayNotFoundException)
        {
            return ApiProblems.NotFound("TV bulunamadı.");
        }

        audit.Record("Display.Revoked", nameof(DisplayDevice), id);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> AttachSessionAsync(
        Guid id, AttachSessionRequest request, AppDbContext db, DisplayStateService service, DisplayPresence presence, AuditTrail audit, CancellationToken ct)
    {
        var device = await db.DisplayDevices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.RevokedAt == null, ct);
        if (device is null)
        {
            return ApiProblems.NotFound("TV bulunamadı.");
        }

        // Oturum şube filtresinden geçer: başka şubenin veya firmanın oturumu "yok" gibi davranır
        var session = await db.TryOnSessions.AsNoTracking().Include(s => s.Consent).SingleOrDefaultAsync(s => s.Id == request.SessionId, ct);
        if (session is null)
        {
            return ApiProblems.NotFound("Prova bulunamadı.");
        }

        if (session.Status != SessionStatus.Open)
        {
            return ApiProblems.Conflict("SESSION_CLOSED", "Bu prova kapatılmış; TV'ye bağlanamaz.");
        }

        if (session.Consent?.AllowsProcessing != true)
        {
            return ApiProblems.Conflict("CONSENT_REQUIRED", "Müşteri onayı olmayan prova TV'ye bağlanamaz.");
        }

        if (device.CurrentSessionId == session.Id)
        {
            return TypedResults.Ok((await ToDtosAsync(db, presence, [device], ct))[0]);
        }

        if (device.CurrentSessionId is not null && !request.Force)
        {
            return ApiProblems.Conflict("DISPLAY_IN_USE", "Bu TV başka bir provaya bağlı. Devralmak için onaylayın.");
        }

        var otherDisplayId = await db.DisplayDevices.AsNoTracking()
            .Where(d => d.CurrentSessionId == session.Id && d.Id != id && d.RevokedAt == null).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
        try
        {
            if (otherDisplayId is { } other)
            {
                if (!request.Force)
                {
                    return ApiProblems.Conflict("SESSION_ON_OTHER_DISPLAY", "Bu prova başka bir TV'ye bağlı. Buraya taşımak için onaylayın.");
                }

                await service.MutateAsync(other, d =>
                {
                    if (d.CurrentSessionId != session.Id)
                    {
                        return null;
                    }

                    d.CurrentSessionId = null;
                    d.State = new DisplayState();
                    return DisplayStateReason.SessionDetached;
                }, null, ct);
            }

            await service.MutateAsync(id, d =>
            {
                d.CurrentSessionId = session.Id;
                d.State = new DisplayState();
                return DisplayStateReason.SessionAttached;
            }, null, ct);
        }
        catch (DisplayNotFoundException)
        {
            return ApiProblems.NotFound("TV bulunamadı.");
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            return ApiProblems.Conflict("SESSION_ON_OTHER_DISPLAY", "Bu prova başka bir TV'ye bağlı.");
        }

        db.ChangeTracker.Clear();
        audit.Record("Display.Assigned", nameof(DisplayDevice), id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await LoadDtoAsync(db, presence, id, ct));
    }

    private static async Task<IResult> DetachSessionAsync(Guid id, AppDbContext db, DisplayStateService service, DisplayPresence presence, AuditTrail audit, CancellationToken ct)
    {
        if (!await db.DisplayDevices.AsNoTracking().AnyAsync(d => d.Id == id && d.RevokedAt == null, ct))
        {
            return ApiProblems.NotFound("TV bulunamadı.");
        }

        try
        {
            await service.MutateAsync(id, d =>
            {
                if (d.CurrentSessionId is null && d.State.Mode == DisplayMode.Idle)
                {
                    return null;
                }

                d.CurrentSessionId = null;
                d.State = new DisplayState();
                return DisplayStateReason.SessionDetached;
            }, null, ct);
        }
        catch (DisplayNotFoundException)
        {
            return ApiProblems.NotFound("TV bulunamadı.");
        }

        db.ChangeTracker.Clear();
        audit.Record("Display.Detached", nameof(DisplayDevice), id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await LoadDtoAsync(db, presence, id, ct));
    }

    private static async Task<IResult> SetStateAsync(
        Guid id, SetDisplayStateRequest request, AppDbContext db, DisplayStateService service, DisplayPresence presence, AuditTrail audit, CancellationToken ct)
    {
        var device = await db.DisplayDevices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.RevokedAt == null, ct);
        if (device is null)
        {
            return ApiProblems.NotFound("TV bulunamadı.");
        }

        if (request.Mode is not (DisplayMode.Single or DisplayMode.Compare))
        {
            return ApiProblems.BadRequest("INVALID_MODE", "Mod yalnızca tekli veya karşılaştırma olabilir; ekranı boşaltmak için temizle işlemini kullanın.");
        }

        var expected = request.Mode == DisplayMode.Single ? 1 : 2;
        var ids = request.ImageIds.Distinct().ToList();
        if (request.ImageIds.Count != expected || ids.Count != expected)
        {
            return ApiProblems.BadRequest("INVALID_IMAGES", request.Mode == DisplayMode.Single ? "Tekli gösterim için bir görsel seçin." : "Karşılaştırma için birbirinden farklı iki görsel seçin.");
        }

        if (device.CurrentSessionId is not { } sessionId)
        {
            return ApiProblems.Conflict("NO_SESSION", "Önce prova oturumunu bu TV'ye bağlayın.");
        }

        var session = await db.TryOnSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null || session.Status != SessionStatus.Open)
        {
            return ApiProblems.Conflict("SESSION_CLOSED", "Bağlı prova kapatılmış.");
        }

        // Görseller her zaman TV'nin bağlı olduğu oturuma ait olmak zorunda; var olmayan ve başkasının görseli aynı yanıtı alır
        var owned = await db.GeneratedImages.AsNoTracking().CountAsync(i => i.SessionId == sessionId && ids.Contains(i.Id), ct)
            + await db.SessionPhotos.AsNoTracking().CountAsync(p => p.SessionId == sessionId && ids.Contains(p.Id), ct);
        if (owned != ids.Count)
        {
            return ApiProblems.Forbidden("IMAGE_NOT_IN_SESSION", "Seçilen görsel bu TV'ye bağlı provaya ait değil.");
        }

        try
        {
            await service.MutateAsync(id, d =>
            {
                if (d.CurrentSessionId != sessionId)
                {
                    throw new DisplayStateConflictException(d.StateVersion);
                }

                var next = new DisplayState { Mode = request.Mode, ImageIds = ids };
                var reason = DisplayStateService.ReasonFor(d.State, next);
                d.State = next;
                return reason;
            }, request.ExpectedVersion, ct);
        }
        catch (DisplayStateConflictException ex)
        {
            return ApiProblems.Create(StatusCodes.Status409Conflict, "STATE_CONFLICT", "TV durumu başka biri tarafından değiştirildi. Güncel durumu yenileyin.",
                new Dictionary<string, object?> { ["currentVersion"] = ex.CurrentVersion });
        }
        catch (DisplayNotFoundException)
        {
            return ApiProblems.NotFound("TV bulunamadı.");
        }

        db.ChangeTracker.Clear();
        audit.Record("Display.StateChanged", nameof(DisplayDevice), id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await LoadDtoAsync(db, presence, id, ct));
    }

    private static async Task<IResult> ClearAsync(Guid id, AppDbContext db, DisplayStateService service, DisplayPresence presence, AuditTrail audit, CancellationToken ct)
    {
        if (!await db.DisplayDevices.AsNoTracking().AnyAsync(d => d.Id == id && d.RevokedAt == null, ct))
        {
            return ApiProblems.NotFound("TV bulunamadı.");
        }

        try
        {
            await service.MutateAsync(id, d =>
            {
                if (d.State.Mode == DisplayMode.Idle)
                {
                    return null;
                }

                d.State = new DisplayState();
                return DisplayStateReason.Cleared;
            }, null, ct);
        }
        catch (DisplayNotFoundException)
        {
            return ApiProblems.NotFound("TV bulunamadı.");
        }

        db.ChangeTracker.Clear();
        audit.Record("Display.Cleared", nameof(DisplayDevice), id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await LoadDtoAsync(db, presence, id, ct));
    }

    private static async Task<DisplayDto> LoadDtoAsync(AppDbContext db, DisplayPresence presence, Guid id, CancellationToken ct)
    {
        var device = await db.DisplayDevices.AsNoTracking().SingleAsync(d => d.Id == id, ct);
        return (await ToDtosAsync(db, presence, [device], ct))[0];
    }

    private static async Task<List<DisplayDto>> ToDtosAsync(AppDbContext db, DisplayPresence presence, IReadOnlyList<DisplayDevice> devices, CancellationToken ct)
    {
        var sessionIds = devices.Where(d => d.CurrentSessionId is not null).Select(d => d.CurrentSessionId!.Value).Distinct().ToList();
        var labels = sessionIds.Count == 0
            ? []
            : await db.TryOnSessions.AsNoTracking().Where(s => sessionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.CustomerLabel, ct);
        return devices.Select(d => new DisplayDto(
            d.Id, d.Name, presence.IsOnline(d.Id), d.LastSeenAt, d.CurrentSessionId,
            d.CurrentSessionId is { } sid ? SessionProgress.DisplayCode(sid) : null,
            d.CurrentSessionId is { } sid2 && labels.TryGetValue(sid2, out var label) ? label : null,
            d.State.Mode, d.StateVersion, d.StateChangedAt)).ToList();
    }

    // ------------------------------------------------------------------ TV (cihaz belirteci)

    public static IEndpointRouteBuilder MapDisplayDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        // Çerez kullanılmaz: CSRF filtresi (çerez tabanlı personel API'si için) bu grupta yoktur
        var group = app.MapGroup("/api/v1/display").WithTags("Display");
        group.MapPost("/pairing", StartPairingAsync).AllowAnonymous().RequireRateLimiting(PairingRateLimit).Produces<PairingStartedDto>();
        group.MapGet("/pairing/status", PairingStatusAsync).AllowAnonymous().RequireRateLimiting(PairingStatusRateLimit).Produces<PairingStatusDto>();
        group.MapGet("/state", GetStateAsync).RequireAuthorization(DisplayHub.DevicePolicy).AllowWithoutAgreement("TV cihazı: ayrı cihaz belirteci kimliği (personel oturumu değil)").RequireRateLimiting(DeviceStateRateLimit).Produces<TvStateDto>();
        return app;
    }

    private static async Task<IResult> StartPairingAsync(AppDbContext db, IOptions<StorageOptions> storage, IOptions<DisplaySettings> settings, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (await db.DisplayPairings.CountAsync(p => p.ClaimedDeviceId == null && p.ExpiresAt > now, ct) >= settings.Value.MaxPendingPairings)
        {
            return ApiProblems.Create(StatusCodes.Status429TooManyRequests, "PAIRING_BUSY", "Şu anda çok fazla eşleştirme bekliyor. Biraz sonra tekrar deneyin.");
        }

        string? code = null;
        string codeHash = string.Empty;
        for (var attempt = 0; attempt < 8 && code is null; attempt++)
        {
            var candidate = DisplayCredentials.NewCode();
            var candidateHash = DisplayCredentials.HashCode(candidate, storage.Value.SigningKey);
            if (!await db.DisplayPairings.AnyAsync(p => p.CodeHash == candidateHash && p.ClaimedDeviceId == null && p.ExpiresAt > now, ct))
            {
                code = candidate;
                codeHash = candidateHash;
            }
        }

        if (code is null)
        {
            return ApiProblems.Create(StatusCodes.Status503ServiceUnavailable, "PAIRING_BUSY", "Kod üretilemedi. Tekrar deneyin.");
        }

        var secret = DisplayCredentials.NewSecret();
        var pairing = new DisplayPairing
        {
            CodeHash = codeHash, PairingSecretHash = DisplayCredentials.HashSecret(secret), ExpiresAt = now.AddMinutes(settings.Value.PairingTtlMinutes),
        };
        db.DisplayPairings.Add(pairing);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new PairingStartedDto(code, secret, pairing.ExpiresAt));
    }

    private static async Task<IResult> PairingStatusAsync(HttpRequest request, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var header = request.Headers.Authorization.ToString();
        const string Prefix = "Pairing ";
        if (!header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || header.Length <= Prefix.Length)
        {
            return TypedResults.Unauthorized();
        }

        var hash = DisplayCredentials.HashSecret(header[Prefix.Length..].Trim());
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        var pairing = await db.DisplayPairings.AsNoTracking().SingleOrDefaultAsync(p => p.PairingSecretHash == hash, ct);

        // Eşleştirme kaydı temizlenmiş olsa bile cihaz aynı gizli değerle kayıtlıysa eşleştirme tamamlanmıştır
        var device = await db.DisplayDevices.IgnoreQueryFilters().AsNoTracking().Where(d => d.TokenHash == hash).Select(d => new { d.RevokedAt }).SingleOrDefaultAsync(ct);
        if (device is not null)
        {
            return TypedResults.Ok(new PairingStatusDto(device.RevokedAt is null ? PairingStatus.Claimed : PairingStatus.Expired, pairing?.ExpiresAt));
        }

        if (pairing is null || pairing.ExpiresAt <= clock.GetUtcNow())
        {
            return TypedResults.Ok(new PairingStatusDto(PairingStatus.Expired, pairing?.ExpiresAt));
        }

        return TypedResults.Ok(new PairingStatusDto(PairingStatus.Pending, pairing.ExpiresAt));
    }

    /// <summary>
    /// TV'nin güncel yetkili görünümü. Yalnızca cihazın bağlı olduğu oturumun görselleri (<c>current_session_id</c>) ve yalnızca sunucunun
    /// kayıtlı durumundaki kimlikler döner. İstemciden hiçbir kimlik alınmaz.
    /// </summary>
    private static async Task<IResult> GetStateAsync(ClaimsPrincipal principal, HttpContext http, AppDbContext db, ImageLinks links, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        var deviceId = principal.GetDisplayDeviceId();
        var device = deviceId is null ? null : await db.DisplayDevices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deviceId && d.RevokedAt == null, ct);
        if (device is null)
        {
            return TypedResults.Unauthorized();
        }

        var images = new List<TvImageDto>();
        if (device.CurrentSessionId is { } sessionId && device.State.Mode != DisplayMode.Idle
            && await db.TryOnSessions.AsNoTracking().AnyAsync(s => s.Id == sessionId && s.Status == SessionStatus.Open, ct))
        {
            var ids = device.State.ImageIds;
            var results = await db.GeneratedImages.AsNoTracking().Where(i => i.SessionId == sessionId && ids.Contains(i.Id)).ToListAsync(ct);
            var photos = await db.SessionPhotos.AsNoTracking().Where(p => p.SessionId == sessionId && ids.Contains(p.Id)).ToListAsync(ct);
            var files = await ImageLinks.LoadFilesAsync(db, results.Select(i => i.FileId).Concat(photos.Select(p => p.FileId)), ct);
            foreach (var id in ids)
            {
                if (results.SingleOrDefault(i => i.Id == id) is { } result && files.TryGetValue(result.FileId, out var resultFile))
                {
                    images.Add(new TvImageDto(id, links.Customer(resultFile).Url, $"{result.Label} · Sürüm {result.VersionNo}", result.IsDemo, TvImageKind.Result));
                }
                else if (photos.SingleOrDefault(p => p.Id == id) is { } photo && files.TryGetValue(photo.FileId, out var photoFile))
                {
                    images.Add(new TvImageDto(id, links.Customer(photoFile).Url, "Orijinal fotoğraf", false, TvImageKind.Photo));
                }
            }
        }

        // Karşılaştırma için iki görsel çözülemediyse (biri silindi) ekran tutarlı kalır: kalan tek görsel veya boş
        var mode = images.Count >= 2 && device.State.Mode == DisplayMode.Compare ? DisplayMode.Compare : images.Count >= 1 ? DisplayMode.Single : DisplayMode.Idle;
        if (mode == DisplayMode.Single && images.Count > 1)
        {
            images = [images[0]];
        }

        return TypedResults.Ok(new TvStateDto(device.StateVersion, mode, device.Name, device.CurrentSessionId is not null, images));
    }

    private static async Task TryNotifyAsync(Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Bildirim yalnızca hızlandırıcıdır; personel listesi REST'ten alınır
        }
    }
}
