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
public sealed class StaffHub(AppDbContext db, SanalProva.Api.Features.Agreement.AgreementGate agreement) : Hub<IStaffClient>
{
    public const string Path = "/hubs/staff";

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

        // Sözleşmenin yeni sürümü yayımlandıysa yeni abonelik verilmez (bağlantı açıkken sürüm değişmiş olabilir; ara katman yalnızca bağlanmayı denetler)
        if (!await agreement.IsAcceptedAsync(db, organizationId.Value, Context.ConnectionAborted))
        {
            return false;
        }

        var allowed = await db.TryOnSessions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Id == sessionId && s.OrganizationId == organizationId)
            .AnyAsync(s => db.UserStoreRoles.IgnoreQueryFilters().Any(r => r.UserId == userId && r.StoreId == s.StoreId && r.OrganizationId == organizationId), Context.ConnectionAborted);
        if (!allowed)
        {
            return false;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId), Context.ConnectionAborted);
        return true;
    }

    public Task LeaveSession(Guid sessionId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, SessionGroup(sessionId), Context.ConnectionAborted);

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

        // Sözleşmenin yeni sürümü yayımlandıysa yeni abonelik verilmez (bağlantı açıkken sürüm değişmiş olabilir; ara katman yalnızca bağlanmayı denetler)
        if (!await agreement.IsAcceptedAsync(db, organizationId.Value, Context.ConnectionAborted))
        {
            return false;
        }

        var allowed = await db.UserStoreRoles.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(r => r.UserId == userId && r.StoreId == storeId && r.OrganizationId == organizationId, Context.ConnectionAborted);
        if (!allowed)
        {
            return false;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, StoreGroup(storeId), Context.ConnectionAborted);
        return true;
    }

    public Task LeaveStore(Guid storeId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, StoreGroup(storeId), Context.ConnectionAborted);
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
