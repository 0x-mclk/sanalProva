using SanalProva.Domain.Common;

namespace SanalProva.Domain.TryOn;

public enum SessionStatus
{
    Open,
    Closed,
    Purged,
}

public enum ConsentMethod
{
    StaffConfirmedVerbal,
    CustomerTapped,
    SignedOnTablet,
}

public enum OutfitMode
{
    OutfitSet,
    FreeCombination,
}

/// <summary>Bir müşteri için prova oturumu. Kişisel veri içerir; saklama süresi dolunca tamamen silinir.</summary>
public sealed class TryOnSession : Entity, IStoreOwned
{
    public Guid OrganizationId { get; init; }
    public Guid StoreId { get; init; }
    public Guid CreatedByUserId { get; init; }

    /// <summary>İsteğe bağlı takma ad (en fazla 40 karakter). AI sağlayıcılarına asla gönderilmez.</summary>
    public string? CustomerLabel { get; set; }

    public SessionStatus Status { get; set; } = SessionStatus.Open;
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset RetentionUntil { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>Henüz "Üret"e basılmamış seçim. Sayfa yenilense veya ekran değişse de seçimler korunur.</summary>
    public SessionDraft? Draft { get; set; }

    public ConsentRecord? Consent { get; set; }
    public List<SessionPhoto> Photos { get; } = [];
}

/// <summary>Taslak seçim (JSON sütunu). "Üret" anında değişmez <see cref="OutfitSelection"/> kaydına dönüşür.</summary>
public sealed class SessionDraft
{
    public OutfitMode Mode { get; set; }
    public List<DraftItem> Items { get; set; } = [];
}

public sealed class DraftItem
{
    public Guid CategoryId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? VariantId { get; set; }
}

public sealed class ConsentRecord : Entity, ITenantOwned
{
    public Guid OrganizationId { get; init; }
    public Guid SessionId { get; init; }
    public required string NoticeVersion { get; init; }
    public bool ProcessingConsent { get; init; }
    public bool ForeignTransferConsent { get; init; }
    public bool ExtendedRetentionConsent { get; init; }

    /// <summary>18 yaş altı için veli/yasal temsilci onayı. Süreç ayrıca netleştirilecek (K8).</summary>
    public bool? GuardianConsent { get; init; }

    public ConsentMethod Method { get; init; }
    public Guid RecordedByUserId { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public DateTimeOffset? WithdrawnAt { get; set; }

    /// <summary>Fotoğraf yüklemek ve AI'ya göndermek için gereken asgari onaylar verildi mi?</summary>
    public bool AllowsProcessing => ProcessingConsent && ForeignTransferConsent && WithdrawnAt is null;
}

public sealed class SessionPhoto : Entity, ITenantOwned
{
    public Guid OrganizationId { get; init; }
    public Guid SessionId { get; init; }
    public Guid FileId { get; init; }
    public ImageAngle Angle { get; init; } = ImageAngle.Front;
    public int Width { get; init; }
    public int Height { get; init; }
    public PhotoQualityFlags QualityFlags { get; set; } = new();
    public bool IsPrimary { get; set; }
}

/// <summary>
/// Yükleme sırasında görüntü istatistiklerinden hesaplanan kadraj uyarıları. Yalnızca basit sezgiseldir:
/// kişi sayısı, tam boy veya yüz tespiti yapılmaz (bu kontroller için model gerekir ve yapılmadığı UI'da belirtilir).
/// </summary>
public sealed class PhotoQualityFlags
{
    public bool Blurry { get; set; }
    public bool LowResolution { get; set; }

    /// <summary>Yatay veya kareye yakın kadraj: boydan çekim için dikey fotoğraf beklenir.</summary>
    public bool NotPortrait { get; set; }

    public bool HasWarnings => Blurry || LowResolution || NotPortrait;
}

/// <summary>"Üret"e basıldığı andaki seçimin değişmez kaydı.</summary>
public sealed class OutfitSelection : Entity, ITenantOwned
{
    public Guid OrganizationId { get; init; }
    public Guid SessionId { get; init; }
    public OutfitMode Mode { get; init; }
    public Guid CreatedByUserId { get; init; }
    public List<OutfitSelectionItem> Items { get; } = [];
}

public sealed class OutfitSelectionItem : Entity, ITenantOwned
{
    public Guid OrganizationId { get; init; }
    public Guid SelectionId { get; init; }
    public Guid ProductId { get; init; }
    public Guid? VariantId { get; init; }

    /// <summary>Anlık görüntü: ürünün kategorisi sonradan değişse bile kayıt bozulmaz.</summary>
    public Guid CategoryId { get; init; }

    public int LayerOrder { get; init; }
}
