using SanalProva.Domain.Common;

namespace SanalProva.Domain.Display;

public enum DisplayMode
{
    Idle,
    Single,
    Compare,
}

/// <summary>Mağazaya kayıtlı Smart TV. Ne gösterdiği sunucu-otoritatif olarak <see cref="State"/> alanındadır.</summary>
public sealed class DisplayDevice : Entity, IStoreOwned
{
    public Guid OrganizationId { get; init; }
    public Guid StoreId { get; init; }
    public required string Name { get; set; }

    /// <summary>Cihaz belirtecinin SHA-256 hash'i. Düz hali saklanmaz.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Şu an bağlı oturum. Bir oturum aynı anda tek TV'de (kısmi benzersiz indeks).</summary>
    public Guid? CurrentSessionId { get; set; }

    public DisplayState State { get; set; } = new();

    /// <summary>Her durum değişiminde artar; eski mesajları elemek ve eşzamanlı değişikliği yakalamak için.</summary>
    public long StateVersion { get; set; }

    /// <summary>Durumun (mod, görseller, bağlı oturum) en son değiştiği an; yalnızca gerçek durum değişiminde güncellenir, boşta temizlemeyi bunun üzerinden yaparız.</summary>
    public DateTimeOffset StateChangedAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class DisplayState
{
    public DisplayMode Mode { get; set; } = DisplayMode.Idle;
    public List<Guid> ImageIds { get; set; } = [];
}

/// <summary>Geçici eşleştirme kodu (5 dk). Kod ve gizli değer yalnızca hash olarak saklanır.</summary>
public sealed class DisplayPairing : Entity
{
    public required string CodeHash { get; init; }
    public required string PairingSecretHash { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public Guid? ClaimedDeviceId { get; set; }
    public Guid? ClaimedByUserId { get; set; }
    public int FailedAttempts { get; set; }
}
