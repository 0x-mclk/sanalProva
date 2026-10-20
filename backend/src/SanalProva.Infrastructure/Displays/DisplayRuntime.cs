using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Display;

namespace SanalProva.Infrastructure.Displays;

public sealed class DisplaySettings
{
    public const string Section = "Display";

    /// <summary>Eşleştirme kodunun geçerlilik süresi.</summary>
    public int PairingTtlMinutes { get; set; } = 5;

    /// <summary>Durumu bu kadar dakikadır değişmeyen TV'nin görseli temizlenir (müşteri gittiyse ekranda kalmasın).</summary>
    public int IdleClearMinutes { get; set; } = 15;

    /// <summary>Bakım süpürmesi aralığı. 0 veya negatifse arka plan işi çalışmaz.</summary>
    public int SweepIntervalSeconds { get; set; } = 60;

    /// <summary>Bir kullanıcının art arda hatalı kod denemesi bu sayıya ulaşınca <see cref="ClaimLockoutMinutes"/> süreyle kilitlenir.</summary>
    public int ClaimMaxFailures { get; set; } = 5;

    public int ClaimLockoutMinutes { get; set; } = 15;

    /// <summary>Aynı anda bekleyen (kullanılmamış, süresi dolmamış) eşleştirme kodu üst sınırı: kod uzayının doldurulmasını engeller.</summary>
    public int MaxPendingPairings { get; set; } = 200;
}

/// <summary>Neden bir TV durumu değişti. TV ve personel arayüzü bunu yalnızca bilgi/animasyon için kullanır; doğruluk kaynağı sunucudaki durumdur.</summary>
public enum DisplayStateReason
{
    SessionAttached,
    ImageSent,
    ImageChanged,
    CompareOpened,
    CompareClosed,
    Cleared,
    /// <summary>Prova oturumu kapatıldı veya silindi.</summary>
    SessionEnded,
    /// <summary>Personel TV bağlantısını kesti.</summary>
    SessionDetached,
    /// <summary>Gizlilik gereği temizlendi (fotoğraf silindi, onay geri alındı...).</summary>
    PrivacyCleared,
    /// <summary>Boşta kalma süresi doldu.</summary>
    IdleTimeout,
    /// <summary>Cihaz eşleştirmesi kaldırıldı.</summary>
    DeviceRevoked,
}

/// <summary>Yayınlanacak durum değişikliği. <see cref="Reason"/> null ise yalnızca çevrimiçi durumu değişmiştir.</summary>
public sealed record DisplayChange(Guid DeviceId, Guid StoreId, long Version, DisplayStateReason? Reason, DisplayMode Mode, Guid? SessionId);

/// <summary>Durum değişikliklerini TV'ye ve personel arayüzüne iletir (SignalR uygulaması Api katmanındadır). Gönderim hatası işlemi bozmaz.</summary>
public interface IDisplayNotifier
{
    /// <summary>TV'ye "durum değişti, sürüm N" der (görsel taşımaz); personel grubuna güncel özet gider.</summary>
    Task NotifyStateChangedAsync(DisplayChange change, CancellationToken ct);

    /// <summary>TV'ye eşleştirmenin kaldırıldığını bildirir; personel grubuna silindi olayı gider.</summary>
    Task NotifyRevokedAsync(DisplayChange change, CancellationToken ct);

    /// <summary>Yalnızca personel grubuna: TV bağlandı / bağlantısı koptu.</summary>
    Task NotifyPresenceAsync(DisplayChange snapshot, bool online, CancellationToken ct);
}

public sealed class NullDisplayNotifier : IDisplayNotifier
{
    public Task NotifyStateChangedAsync(DisplayChange change, CancellationToken ct) => Task.CompletedTask;

    public Task NotifyRevokedAsync(DisplayChange change, CancellationToken ct) => Task.CompletedTask;

    public Task NotifyPresenceAsync(DisplayChange snapshot, bool online, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Hangi TV'lerin SignalR bağlantısı açık. Tek sunucu örneği için bellek içidir; çoklu sunucuda son görülme zamanı
/// (<c>last_seen_at</c>) ve SignalR backplane ile birlikte değerlendirilmelidir (docs/06-tv-signalr.md §4).
/// </summary>
public sealed class DisplayPresence
{
    private readonly ConcurrentDictionary<Guid, int> _connections = new();

    /// <returns>Bağlantı sayısı bu çağrıyla 0'dan 1'e çıktıysa true (TV yeni çevrimiçi oldu).</returns>
    public bool Connected(Guid deviceId) => _connections.AddOrUpdate(deviceId, 1, (_, n) => n + 1) == 1;

    /// <returns>Son bağlantı kapandıysa true (TV çevrimdışı oldu).</returns>
    public bool Disconnected(Guid deviceId)
    {
        while (_connections.TryGetValue(deviceId, out var count))
        {
            if (count <= 1)
            {
                if (_connections.TryRemove(new KeyValuePair<Guid, int>(deviceId, count)))
                {
                    return true;
                }
            }
            else if (_connections.TryUpdate(deviceId, count - 1, count))
            {
                return false;
            }
        }

        return false;
    }

    public bool IsOnline(Guid deviceId) => _connections.ContainsKey(deviceId);

    /// <summary>Cihaz iptal edildiğinde kayıt temizlenir (açık bağlantılar zaten reddedilir).</summary>
    public void Forget(Guid deviceId) => _connections.TryRemove(deviceId, out _);
}

/// <summary>
/// Eşleştirme kodu tahmin saldırısına karşı kullanıcı başına hatalı deneme sınırı. Kod hash'lendiği için hatalı bir tahmin
/// belirli bir eşleştirme kaydına bağlanamaz; bu yüzden sayaç "kim denedi" üzerinden tutulur. Bellek içidir (tek örnek).
/// </summary>
public sealed class ClaimAttemptLimiter(TimeProvider clock, IOptions<DisplaySettings> settings)
{
    private readonly ConcurrentDictionary<Guid, List<DateTimeOffset>> _failures = new();

    public bool IsLocked(Guid userId)
    {
        if (!_failures.TryGetValue(userId, out var list))
        {
            return false;
        }

        lock (list)
        {
            Prune(list);
            return list.Count >= settings.Value.ClaimMaxFailures;
        }
    }

    public void RecordFailure(Guid userId)
    {
        var list = _failures.GetOrAdd(userId, _ => []);
        lock (list)
        {
            Prune(list);
            list.Add(clock.GetUtcNow());
        }
    }

    public void Reset(Guid userId) => _failures.TryRemove(userId, out _);

    private void Prune(List<DateTimeOffset> list)
    {
        var cutoff = clock.GetUtcNow().AddMinutes(-settings.Value.ClaimLockoutMinutes);
        list.RemoveAll(t => t <= cutoff);
    }
}
