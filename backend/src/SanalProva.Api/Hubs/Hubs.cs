using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Features.Displays;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Display;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Displays;
using SanalProva.Infrastructure.Generation;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Hubs;

// Tasarım: docs/06-tv-signalr.md. Hub'lar içerik taşımaz; yalnızca "şu değişti" bildirir,
// istemci güncel durumu REST'ten çeker (sunucu-otoritatif durum).

/// <summary>Personel uygulamasına gönderilen olaylar.</summary>
public interface IStaffClient
{
    Task JobUpdated(JobUpdatedMessage message);
    Task SessionUpdated(Guid sessionId);
    Task DisplayStatusChanged(DisplayStatusMessage message);
}

public sealed record JobUpdatedMessage(Guid JobId, Guid SessionId, string Status, string? Stage, Guid? ResultImageId, string? ErrorCode);

/// <summary>
/// TV durumu değişti (çevrimiçi/çevrimdışı, oturum bağlandı, görsel gönderildi...). Personel arayüzü bunu görünce güncel listeyi REST'ten çeker;
/// <see cref="Removed"/> eşleştirmenin kaldırıldığını belirtir.
/// </summary>
public sealed record DisplayStatusMessage(Guid DisplayId, bool Online, Guid? CurrentSessionId, DisplayMode Mode, long StateVersion, DisplayStateReason? Reason, bool Removed);

/// <summary>
/// Personel hub'ı (çerezle kimlik doğrulama). Grup adları: <c>session:{id}</c>, <c>store:{id}</c>.
/// <see cref="JoinSession"/> yalnızca kullanıcının o oturumun şubesinde rolü varsa gruba ekler; yetkisiz katılma sessizce reddedilir
/// (oturum kimliğinin varlığı sızdırılmaz).
/// </summary>
[Authorize]
public sealed class StaffHub(
    AppDbContext db, SanalProva.Api.Features.Agreement.AgreementGate agreement, StaffConnectionRegistry connections, TimeProvider clock, IConfiguration configuration) : Hub<IStaffClient>
{
    public const string Path = "/hubs/staff";

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.GetUserId();
        if (userId is null || !await IsSessionUsableAsync(userId.Value))
        {
            Context.Abort(); // kilitli/geçersiz oturum bağlanamaz (ara katman negotiate'i zaten reddeder; bu ikinci kapıdır)
            return;
        }

        connections.Add(userId.Value, Guid.Parse(Context.User!.FindFirst(AppClaims.SessionId)!.Value), Context);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.User?.GetUserId() is { } userId)
        {
            connections.Remove(userId, Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Çerez oturumu hâlâ geçerli ve KİLİTLİ DEĞİL mi (her abonelik isteğinde; bağlantı açıkken kilitlenmiş/çıkış yapılmış olabilir).</summary>
    private async Task<bool> IsSessionUsableAsync(Guid userId)
    {
        if (!Guid.TryParse(Context.User?.FindFirst(AppClaims.SessionId)?.Value, out var sessionId))
        {
            return false;
        }

        return await ScreenLockState.EvaluateAsync(db, userId, sessionId, clock.GetUtcNow(), SanalProva.Api.Features.Account.PinEndpoints.IdleSeconds(configuration), Context.ConnectionAborted) == SessionCheck.Ok;
    }

    /// <summary>Hesap pasife alınmış veya şube pasifse yeni abonelik verilmez (açık bağlantı pasife alma anında ayrıca kesilir).</summary>
    private async Task<bool> IsAccountAndStoreActiveAsync(Guid userId, Guid organizationId, Guid? storeId)
    {
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.OrganizationId == organizationId && u.IsActive, Context.ConnectionAborted))
        {
            return false;
        }

        return storeId is null || await db.Stores.IgnoreQueryFilters().AsNoTracking().AnyAsync(s => s.Id == storeId && s.OrganizationId == organizationId && s.IsActive, Context.ConnectionAborted);
    }

    public static string SessionGroup(Guid sessionId) => $"session:{sessionId:N}";

    public static string StoreGroup(Guid storeId) => $"store:{storeId:N}";

    /// <returns>Gruba katılındıysa true.</returns>
    public async Task<bool> JoinSession(Guid sessionId)
    {
        var userId = Context.User?.GetUserId();
        var organizationId = Context.User?.GetOrganizationId();
        if (userId is null || organizationId is null)
        {
            return false;
        }

        if (!await IsSessionUsableAsync(userId.Value))
        {
            Context.Abort();
            return false;
        }

        // Sözleşmenin yeni sürümü yayımlandıysa yeni abonelik verilmez (bağlantı açıkken sürüm değişmiş olabilir; ara katman yalnızca bağlanmayı denetler)
        if (!await agreement.IsAcceptedAsync(db, organizationId.Value, Context.ConnectionAborted))
        {
            return false;
        }

        var allowed = await db.TryOnSessions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Id == sessionId && s.OrganizationId == organizationId)
            .AnyAsync(s => db.UserStoreRoles.IgnoreQueryFilters().Any(r => r.UserId == userId && r.StoreId == s.StoreId && r.OrganizationId == organizationId), Context.ConnectionAborted);
        if (!allowed || !await IsAccountAndStoreActiveAsync(userId.Value, organizationId.Value, null))
        {
            return false;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId), Context.ConnectionAborted);
        connections.Track(Context.ConnectionId, userId.Value, sessionId: sessionId, storeId: null, join: true);
        return true;
    }

    public Task LeaveSession(Guid sessionId)
    {
        if (Context.User?.GetUserId() is { } userId)
        {
            connections.Track(Context.ConnectionId, userId, sessionId: sessionId, storeId: null, join: false);
        }

        return Groups.RemoveFromGroupAsync(Context.ConnectionId, SessionGroup(sessionId), Context.ConnectionAborted);
    }

    /// <summary>
    /// Şubenin TV olaylarına (<see cref="IStaffClient.DisplayStatusChanged"/>) abone olur. Kullanıcının o şubede rolü yoksa sessizce reddedilir.
    /// Bağlantı kimliği tek başına yetki değildir: üyelik her seferinde sunucuda rolden doğrulanır.
    /// </summary>
    /// <returns>Gruba katılındıysa true.</returns>
    public async Task<bool> JoinStore(Guid storeId)
    {
        var userId = Context.User?.GetUserId();
        var organizationId = Context.User?.GetOrganizationId();
        if (userId is null || organizationId is null)
        {
            return false;
        }

        if (!await IsSessionUsableAsync(userId.Value))
        {
            Context.Abort();
            return false;
        }

        // Sözleşmenin yeni sürümü yayımlandıysa yeni abonelik verilmez (bağlantı açıkken sürüm değişmiş olabilir; ara katman yalnızca bağlanmayı denetler)
        if (!await agreement.IsAcceptedAsync(db, organizationId.Value, Context.ConnectionAborted))
        {
            return false;
        }

        var allowed = await db.UserStoreRoles.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(r => r.UserId == userId && r.StoreId == storeId && r.OrganizationId == organizationId, Context.ConnectionAborted);
        if (!allowed || !await IsAccountAndStoreActiveAsync(userId.Value, organizationId.Value, storeId))
        {
            return false;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, StoreGroup(storeId), Context.ConnectionAborted);
        connections.Track(Context.ConnectionId, userId.Value, sessionId: null, storeId: storeId, join: true);
        return true;
    }

    public Task LeaveStore(Guid storeId)
    {
        if (Context.User?.GetUserId() is { } userId)
        {
            connections.Track(Context.ConnectionId, userId, sessionId: null, storeId: storeId, join: false);
        }

        return Groups.RemoveFromGroupAsync(Context.ConnectionId, StoreGroup(storeId), Context.ConnectionAborted);
    }
}

/// <summary>Üretim worker'ının iş durumunu oturum grubuna iletir. Gönderim hatası işi etkilemez (worker yakalar).</summary>
public sealed class SignalRJobNotifier(IHubContext<StaffHub, IStaffClient> hub) : IJobNotifier
{
    public Task NotifyAsync(JobUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        return hub.Clients.Group(StaffHub.SessionGroup(update.SessionId)).JobUpdated(new JobUpdatedMessage(
            update.JobId, update.SessionId, update.Status.ToString(), update.Stage?.ToString(), update.ResultImageId, update.ErrorCode?.ToString()));
    }
}

/// <summary>TV'ye gönderilen olaylar. İçerik taşımazlar: TV güncel durumu kendi belirteciyle REST'ten çeker.</summary>
public interface IDisplayClient
{
    /// <summary>Durum değişti. Sürümü eldekinden büyük değilse yok sayılır; neden yalnızca bilgi içindir.</summary>
    Task StateChanged(DisplayStateChangedMessage message);

    /// <summary>Ekran boşaltıldı: TV görseli hemen gizler (güncel durumu çekmeyi beklemeden).</summary>
    Task Cleared();

    /// <summary>Eşleştirme kaldırıldı: TV belirteci siler ve eşleştirme ekranına döner.</summary>
    Task Revoked();
}

public sealed record DisplayStateChangedMessage(long StateVersion, DisplayStateReason Reason);

/// <summary>
/// Smart TV hub'ı. Kimlik: yalnızca cihaz belirteci (<see cref="DeviceTokenDefaults.Scheme"/>); personel çerezi bu hub'a kabul edilmez.
/// Grup: <c>display:{deviceId}</c> — her mesajın tek hedefi vardır. Cihaz kimliği her zaman doğrulanmış belirteçten gelir, istemci parametresinden değil.
/// </summary>
[Authorize(Policy = DisplayHub.DevicePolicy, AuthenticationSchemes = DeviceTokenDefaults.Scheme)]
public sealed class DisplayHub(AppDbContext db, DisplayPresence presence, IDisplayNotifier notifier, TimeProvider clock) : Hub<IDisplayClient>
{
    public const string Path = "/hubs/display";
    public const string DevicePolicy = "DisplayDevice";

    public static string DisplayGroup(Guid deviceId) => $"display:{deviceId:N}";

    public override async Task OnConnectedAsync()
    {
        var deviceId = Context.User?.GetDisplayDeviceId();
        var device = deviceId is null
            ? null
            : await db.DisplayDevices.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(d => d.Id == deviceId && d.RevokedAt == null, Context.ConnectionAborted);
        if (device is null)
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, DisplayGroup(device.Id), Context.ConnectionAborted);
        await TouchAsync(device.Id);
        if (presence.Connected(device.Id))
        {
            await notifier.NotifyPresenceAsync(DisplayStateService.ToChange(device, null), online: true, Context.ConnectionAborted);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.User?.GetDisplayDeviceId() is { } deviceId && presence.Disconnected(deviceId))
        {
            var device = await db.DisplayDevices.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(d => d.Id == deviceId);
            if (device is not null)
            {
                await TouchAsync(deviceId);
                await notifier.NotifyPresenceAsync(DisplayStateService.ToChange(device, null), online: false, CancellationToken.None);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// TV her 30 sn gönderir. Cihaz iptal edildiyse bağlantı kesilir; TV'nin sürümü sunucudakinden farklıysa
    /// (kaçırılmış mesaj) TV'ye yeniden <c>StateChanged</c> gider. Dönüş: sunucudaki güncel sürüm.
    /// </summary>
    public async Task<long> Heartbeat(long stateVersion)
    {
        var deviceId = Context.User?.GetDisplayDeviceId();
        var device = deviceId is null
            ? null
            : await db.DisplayDevices.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(d => d.Id == deviceId && d.RevokedAt == null, Context.ConnectionAborted);
        if (device is null)
        {
            await Clients.Caller.Revoked();
            Context.Abort();
            return -1;
        }

        await TouchAsync(device.Id);
        if (device.StateVersion != stateVersion)
        {
            await Clients.Caller.StateChanged(new DisplayStateChangedMessage(device.StateVersion, DisplayStateReason.Cleared));
        }

        return device.StateVersion;
    }

    // Son görülme zamanı ExecuteUpdate ile yazılır: durum değişim zamanını ve eşzamanlılık belirtecini etkilemez
    private async Task TouchAsync(Guid deviceId)
    {
        var now = clock.GetUtcNow();
        await db.DisplayDevices.IgnoreQueryFilters().Where(d => d.Id == deviceId).ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, now));
    }
}

/// <summary>TV ve personel gruplarına SignalR ile bildirim gönderir. Hata fırlatabilir; çağıran (<c>DisplayStateService</c>) yakalar.</summary>
public sealed class SignalRDisplayNotifier(IHubContext<DisplayHub, IDisplayClient> displays, IHubContext<StaffHub, IStaffClient> staff, DisplayPresence presence) : IDisplayNotifier
{
    public async Task NotifyStateChangedAsync(DisplayChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        var tv = displays.Clients.Group(DisplayHub.DisplayGroup(change.DeviceId));
        if (change.Mode == DisplayMode.Idle)
        {
            await tv.Cleared();
        }

        await tv.StateChanged(new DisplayStateChangedMessage(change.Version, change.Reason ?? DisplayStateReason.Cleared));
        await staff.Clients.Group(StaffHub.StoreGroup(change.StoreId)).DisplayStatusChanged(ToStatus(change, removed: false));
    }

    public async Task NotifyRevokedAsync(DisplayChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        await displays.Clients.Group(DisplayHub.DisplayGroup(change.DeviceId)).Revoked();
        await staff.Clients.Group(StaffHub.StoreGroup(change.StoreId)).DisplayStatusChanged(ToStatus(change, removed: true));
    }

    public Task NotifyPresenceAsync(DisplayChange snapshot, bool online, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return staff.Clients.Group(StaffHub.StoreGroup(snapshot.StoreId)).DisplayStatusChanged(ToStatus(snapshot, removed: false) with { Online = online });
    }

    private DisplayStatusMessage ToStatus(DisplayChange change, bool removed) =>
        new(change.DeviceId, presence.IsOnline(change.DeviceId), change.SessionId, change.Mode, change.Version, change.Reason, removed);
}

public sealed record DiagnosticsPong(string Message, DateTimeOffset ServerTime, string ConnectionId);

/// <summary>Yalnızca geliştirme ortamında: frontend–backend SignalR bağlantısını (WebSocket, proxy) doğrulamak için.</summary>
[AllowAnonymous]
public sealed class DiagnosticsHub(TimeProvider clock) : Hub
{
    public const string Path = "/hubs/diagnostics";

    public DiagnosticsPong Ping(string message) =>
        new(message.Length > 100 ? message[..100] : message, clock.GetUtcNow(), Context.ConnectionId);
}

/// <summary>
/// Açık personel SignalR bağlantıları (M5.2; Pilot öncesi kapanış ile genişletildi). Her bağlantı için kullanıcı, çerez oturumu (<c>sp_sid</c>) ve ABONE OLUNAN şube/oturum
/// grupları izlenir: yetki değişince (pasife alma, rol/şube ataması, şube pasifleşmesi, kilitleme, çıkış, parola sıfırlama) bağlantı kesilir; yeniden bağlanan istemci
/// abonelikleri sunucuda yeniden doğrulatır. Başka bir SÜREÇTE yapılan değişiklikler (operatör komutları) için <see cref="StaffConnectionSweeper"/> periyodik doğrular.
/// Tek sunucu kapsamlıdır (bellek içi): çok sunuculu dağıtımda backplane ve ortak kayıt gerekir (bkz. docs/06 §8, docs/21).
/// </summary>
public sealed class StaffConnectionRegistry
{
    public sealed class Entry(Guid userId, Guid sessionId, HubCallerContext context)
    {
        public Guid UserId { get; } = userId;
        public Guid SessionId { get; } = sessionId;
        public HubCallerContext Context { get; } = context;
        public System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> Stores { get; } = new();
        public System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> Sessions { get; } = new();
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> _byConnection = new();

    public void Add(Guid userId, Guid sessionId, HubCallerContext context) => _byConnection[context.ConnectionId] = new Entry(userId, sessionId, context);

    public void Remove(Guid userId, string connectionId) => _byConnection.TryRemove(connectionId, out _);

    /// <summary>Gruba katılma/ayrılma kaydı (süpürücü yalnızca gerçekten abone olunan gruplar için rol doğrular).</summary>
    public void Track(string connectionId, Guid userId, Guid? sessionId, Guid? storeId, bool join)
    {
        if (!_byConnection.TryGetValue(connectionId, out var entry) || entry.UserId != userId)
        {
            return;
        }

        if (storeId is { } store)
        {
            _ = join ? entry.Stores.TryAdd(store, 0) : entry.Stores.TryRemove(store, out _);
        }

        if (sessionId is { } session)
        {
            _ = join ? entry.Sessions.TryAdd(session, 0) : entry.Sessions.TryRemove(session, out _);
        }
    }

    public IReadOnlyList<Entry> Snapshot() => [.. _byConnection.Values];

    /// <summary>Kullanıcının tüm açık bağlantılarını keser; kesilen bağlantı sayısını döner.</summary>
    public int AbortUser(Guid userId) => Abort(e => e.UserId == userId);

    /// <summary>Tek çerez oturumunun (kilitlenen/çıkış yapan cihaz) bağlantılarını keser; kullanıcının diğer cihazlarına dokunmaz.</summary>
    public int AbortSession(Guid sessionId) => Abort(e => e.SessionId == sessionId);

    /// <summary>Verilen şubede abone olan TÜM bağlantıları keser (şube pasife alındı / erişim kaldırıldı).</summary>
    public int AbortStore(Guid storeId) => Abort(e => e.Stores.ContainsKey(storeId));

    public void AbortConnection(string connectionId)
    {
        if (_byConnection.TryRemove(connectionId, out var entry))
        {
            entry.Context.Abort();
        }
    }

    private int Abort(Func<Entry, bool> match)
    {
        var count = 0;
        foreach (var entry in _byConnection.Values.Where(match))
        {
            if (_byConnection.TryRemove(entry.Context.ConnectionId, out _))
            {
                entry.Context.Abort();
                count++;
            }
        }

        return count;
    }
}

/// <summary>
/// Açık personel bağlantılarını periyodik yeniden doğrular (varsayılan 30 sn, <c>Security:HubRevalidateSeconds</c>; 0 = kapalı). Çerez doğrulayıcısı yalnızca bağlanırken çalışır;
/// bu süpürücü bağlantı AÇIKKEN olan değişiklikleri (özellikle başka süreçte çalışan operatör komutları: erişim sıfırlama, veritabanı elle düzeltmesi) en geç bir periyotta yakalar.
/// Kontroller: hesap etkin ve güvenlik damgası aynı, oturum satırı var ve kilitli değil, abone olunan her şubede rol var ve şube etkin, abone olunan her oturum için şubede rol var.
/// </summary>
public sealed class StaffConnectionSweeper(IServiceScopeFactory scopes, StaffConnectionRegistry registry, IConfiguration configuration, TimeProvider clock, ILogger<StaffConnectionSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("Security:HubRevalidateSeconds", 30);
        if (seconds <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Hub yeniden doğrulaması başarısız oldu; bir sonraki periyotta denenecek.");
            }
        }
    }

    /// <summary>Tek tur; testler doğrudan çağırır. Kesilen bağlantı sayısını döner.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var aborted = 0;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stampType = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Identity.IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType;
        var idle = SanalProva.Api.Features.Account.PinEndpoints.IdleSeconds(configuration);
        foreach (var entry in registry.Snapshot())
        {
            if (!await IsStillValidAsync(db, entry, stampType, idle, ct))
            {
                registry.AbortConnection(entry.Context.ConnectionId);
                aborted++;
            }
        }

        return aborted;
    }

    private async Task<bool> IsStillValidAsync(AppDbContext db, StaffConnectionRegistry.Entry entry, string stampType, int idleSeconds, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == entry.UserId).Select(u => new { u.IsActive, u.SecurityStamp, u.OrganizationId }).SingleOrDefaultAsync(ct);
        if (user is null || !user.IsActive || !string.Equals(user.SecurityStamp, entry.Context.User?.FindFirst(stampType)?.Value, StringComparison.Ordinal))
        {
            return false;
        }

        if (await ScreenLockState.EvaluateAsync(db, entry.UserId, entry.SessionId, clock.GetUtcNow(), idleSeconds, ct) != SessionCheck.Ok)
        {
            return false;
        }

        var stores = entry.Stores.Keys.ToList();
        if (stores.Count > 0)
        {
            var allowed = await db.UserStoreRoles.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.UserId == entry.UserId && r.OrganizationId == user.OrganizationId && stores.Contains(r.StoreId) && db.Stores.IgnoreQueryFilters().Any(s => s.Id == r.StoreId && s.IsActive))
                .Select(r => r.StoreId).Distinct().CountAsync(ct);
            if (allowed != stores.Count)
            {
                return false;
            }
        }

        var sessions = entry.Sessions.Keys.ToList();
        if (sessions.Count > 0)
        {
            var ok = await db.TryOnSessions.IgnoreQueryFilters().AsNoTracking()
                .Where(s => sessions.Contains(s.Id) && s.OrganizationId == user.OrganizationId
                    && db.UserStoreRoles.IgnoreQueryFilters().Any(r => r.UserId == entry.UserId && r.StoreId == s.StoreId && r.OrganizationId == user.OrganizationId)
                    && db.Stores.IgnoreQueryFilters().Any(st => st.Id == s.StoreId && st.IsActive))
                .CountAsync(ct);
            // Silinmiş oturum (KVKK) abonelik sayısını düşürür: bağlantı kesilir, istemci yeniden bağlanıp yalnızca geçerli oturuma abone olur
            if (ok != sessions.Count)
            {
                return false;
            }
        }

        return true;
    }
}
