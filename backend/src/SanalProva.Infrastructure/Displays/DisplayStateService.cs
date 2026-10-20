using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Display;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Infrastructure.Displays;

/// <summary>Beklenen durum sürümü ile sunucudaki sürüm uyuşmuyor (iki personel aynı anda değiştirdi veya eski ekran).</summary>
public sealed class DisplayStateConflictException(long currentVersion) : Exception("TV durumu başka biri tarafından değiştirildi.")
{
    public long CurrentVersion { get; } = currentVersion;
}

public sealed class DisplayNotFoundException() : Exception("TV bulunamadı veya eşleştirmesi kaldırılmış.");

/// <summary>
/// TV durumunun tek yazma noktası. Her değişiklik <c>StateVersion</c>'ı bir artırır (eşzamanlılık belirteci): eski bir istek veya
/// mesaj yeni durumun üzerine yazamaz. Değişiklik veritabanına işlendikten sonra bildirim gönderilir; bildirim başarısız olsa bile
/// doğruluk kaynağı veritabanıdır ve TV yeniden bağlanınca/yoklayınca durumu çeker.
/// </summary>
public sealed class DisplayStateService(AppDbContext db, IDisplayNotifier notifier, DisplayPresence presence, TimeProvider clock, ILogger<DisplayStateService> logger)
{
    private const int MaxAttempts = 30;

    /// <summary>
    /// <paramref name="mutate"/> cihazı değiştirir ve nedenini döner; null dönerse değişiklik yapılmaz (sürüm artmaz, bildirim yok).
    /// Eşzamanlı değişiklikte (sürüm uyuşmazlığı) <paramref name="expectedVersion"/> verilmişse çakışma hatası fırlatılır, verilmemişse güncel durum üzerinde yeniden denenir.
    /// </summary>
    public async Task<DisplayChange?> MutateAsync(Guid deviceId, Func<DisplayDevice, DisplayStateReason?> mutate, long? expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        for (var attempt = 1; ; attempt++)
        {
            var device = await db.DisplayDevices.IgnoreQueryFilters().SingleOrDefaultAsync(d => d.Id == deviceId, ct);
            if (device is null || device.RevokedAt is not null)
            {
                throw new DisplayNotFoundException();
            }

            if (expectedVersion is { } expected && expected != device.StateVersion)
            {
                throw new DisplayStateConflictException(device.StateVersion);
            }

            var reason = mutate(device);
            if (reason is null)
            {
                return null;
            }

            device.StateVersion++;
            device.StateChangedAt = clock.GetUtcNow();
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // Başka bir değişiklik sürümü ilerletti: izlenen varlığı bırak, güncel halini yükleyip yeniden uygula
                db.Entry(device).State = EntityState.Detached;
                await Task.Delay(Random.Shared.Next(1, 15), ct); // eşzamanlı yazanlar art arda çarpışmasın
                if (expectedVersion is not null)
                {
                    var current = await db.DisplayDevices.IgnoreQueryFilters().AsNoTracking().Where(d => d.Id == deviceId).Select(d => d.StateVersion).SingleOrDefaultAsync(ct);
                    throw new DisplayStateConflictException(current);
                }

                continue;
            }

            var change = ToChange(device, reason);
            if (reason == DisplayStateReason.DeviceRevoked)
            {
                presence.Forget(device.Id);
                await SafeAsync(() => notifier.NotifyRevokedAsync(change, ct), "Revoked");
            }
            else
            {
                await SafeAsync(() => notifier.NotifyStateChangedAsync(change, ct), "StateChanged");
            }

            return change;
        }
    }

    /// <summary>
    /// Oturuma bağlı TV'lerin görüntüsünü anında boşaltır (<paramref name="detach"/> true ise bağlantıyı da keser).
    /// Oturum kapanışı, silme, onay geri alma ve fotoğraf silme akışları bunu çağırır: müşteri verisi ekranda kalmaz.
    /// </summary>
    public async Task<IReadOnlyList<DisplayChange>> ResetBySessionAsync(Guid sessionId, DisplayStateReason reason, bool detach, CancellationToken ct)
    {
        var deviceIds = await db.DisplayDevices.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.CurrentSessionId == sessionId && d.RevokedAt == null).Select(d => d.Id).ToListAsync(ct);
        var changes = new List<DisplayChange>();
        foreach (var id in deviceIds)
        {
            try
            {
                var change = await MutateAsync(id, device =>
                {
                    if (device.CurrentSessionId != sessionId)
                    {
                        return null; // bu arada başka oturuma devredildi
                    }

                    if (!detach && device.State.Mode == DisplayMode.Idle)
                    {
                        return null; // zaten boş
                    }

                    device.State = new DisplayState();
                    if (detach)
                    {
                        device.CurrentSessionId = null;
                    }

                    return reason;
                }, null, ct);
                if (change is not null)
                {
                    changes.Add(change);
                }
            }
            catch (DisplayNotFoundException)
            {
                // iptal edilmiş cihaz: yapılacak bir şey yok
            }
        }

        return changes;
    }

    /// <summary>
    /// Oturuma bağlı bir TV bu oturumun bir üretim <b>sonucunu</b> tekli gösteriyorsa, yeni sonuca geçirir (ör. revizyon bitince TV güncellenir).
    /// Boş ekranı veya karşılaştırmayı ya da orijinal fotoğrafı göstermeyi değiştirmez: müşteriye personel onayı olmadan yeni görüntü çıkmaz.
    /// </summary>
    public async Task FollowLatestResultAsync(Guid sessionId, Guid newImageId, CancellationToken ct)
    {
        var devices = await db.DisplayDevices.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.CurrentSessionId == sessionId && d.RevokedAt == null && d.State.Mode == DisplayMode.Single)
            .Select(d => new { d.Id, d.State.ImageIds }).ToListAsync(ct);
        foreach (var device in devices)
        {
            if (device.ImageIds.Count != 1 || !await db.GeneratedImages.IgnoreQueryFilters().AnyAsync(i => i.Id == device.ImageIds[0] && i.SessionId == sessionId, ct))
            {
                continue; // orijinal fotoğraf gösteriliyor: dokunma
            }

            try
            {
                await MutateAsync(device.Id, d =>
                {
                    if (d.CurrentSessionId != sessionId || d.State.Mode != DisplayMode.Single)
                    {
                        return null;
                    }

                    d.State = new DisplayState { Mode = DisplayMode.Single, ImageIds = [newImageId] };
                    return DisplayStateReason.ImageChanged;
                }, null, ct);
            }
            catch (DisplayNotFoundException)
            {
            }
        }
    }

    /// <summary>Boşta kalma süresini aşan TV'lerin görselini temizler.</summary>
    public async Task<int> ClearIdleAsync(TimeSpan idleFor, CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - idleFor;
        var ids = await db.DisplayDevices.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.RevokedAt == null && d.StateChangedAt <= cutoff).Select(d => new { d.Id, d.State.Mode }).ToListAsync(ct);
        var cleared = 0;
        foreach (var id in ids.Where(d => d.Mode != DisplayMode.Idle).Select(d => d.Id))
        {
            try
            {
                var change = await MutateAsync(id, device =>
                {
                    if (device.State.Mode == DisplayMode.Idle || device.StateChangedAt > cutoff)
                    {
                        return null;
                    }

                    device.State = new DisplayState();
                    return DisplayStateReason.IdleTimeout;
                }, null, ct);
                cleared += change is null ? 0 : 1;
            }
            catch (DisplayNotFoundException)
            {
            }
        }

        return cleared;
    }

    public static DisplayChange ToChange(DisplayDevice device, DisplayStateReason? reason) =>
        new(device.Id, device.StoreId, device.StateVersion, reason, device.State.Mode, device.CurrentSessionId);

    /// <summary>Önceki ve yeni duruma göre kullanıcıya anlamlı değişiklik nedeni.</summary>
    public static DisplayStateReason ReasonFor(DisplayState previous, DisplayState next)
    {
        if (next.Mode == DisplayMode.Compare)
        {
            return previous.Mode == DisplayMode.Compare ? DisplayStateReason.ImageChanged : DisplayStateReason.CompareOpened;
        }

        if (next.Mode == DisplayMode.Single)
        {
            if (previous.Mode == DisplayMode.Compare)
            {
                return DisplayStateReason.CompareClosed;
            }

            return previous.Mode == DisplayMode.Single ? DisplayStateReason.ImageChanged : DisplayStateReason.ImageSent;
        }

        return DisplayStateReason.Cleared;
    }

    private async Task SafeAsync(Func<Task> send, string what)
    {
        try
        {
            await send();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TV bildirimi gönderilemedi ({What}); durum veritabanında güvende, TV yeniden bağlanınca çeker.", what);
        }
    }
}

/// <summary>Bakım: boşta kalan TV'leri temizler, süresi dolmuş eşleştirme kayıtlarını siler.</summary>
public sealed class DisplayMaintenanceJob(IServiceScopeFactory scopes, TimeProvider clock, IOptions<DisplaySettings> settings, ILogger<DisplayMaintenanceJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = settings.Value.SweepIntervalSeconds;
        if (interval <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "TV bakım süpürmesi başarısız oldu.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task SweepAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<DisplayStateService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cleared = await service.ClearIdleAsync(TimeSpan.FromMinutes(settings.Value.IdleClearMinutes), ct);
        if (cleared > 0)
        {
            logger.LogInformation("Boşta kalan {Count} TV temizlendi.", cleared);
        }

        // Süresi dolmuş veya tamamlanmış eşleştirme kayıtları bir saat sonra silinir
        var cutoff = clock.GetUtcNow().AddHours(-1);
        await db.DisplayPairings.Where(p => p.ExpiresAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
