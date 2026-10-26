using System.Text.Json;
using SanalProva.Domain.Common;

namespace SanalProva.Domain.Generation;

public enum GenerationJobType
{
    OutfitSet,
    FreeCombination,
    Recolor,
    ProductSwap,
    VariantPreview,
}

public enum GenerationJobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Sağlayıcıdan bağımsız hata kodları. Frontend bunları Türkçe mesaja eşler.</summary>
/// <summary>Kullanıcıya gösterilen üretim aşaması.</summary>
public enum GenerationStage
{
    Preparing,
    Generating,
    Validating,
}

public enum GenerationErrorCode
{
    InvalidInput,
    ContentRejected,
    RateLimited,
    Timeout,
    ProviderUnavailable,
    InvalidOutput,
    MaskQualityLow,
    QuotaExceeded,
    /// <summary>Bu iş tipinin motoru henüz uygulanmadı (ör. renk değişimi — Aşama 5).</summary>
    NotImplemented,

    /// <summary>
    /// Sağlayıcıdaki işlemin sonucu bilinmiyor: ücretli istek gönderildi ama kabul edilip edilmediği (kimlik dönmedi) veya nasıl bittiği
    /// öğrenilemedi. İşlem gerçekleşmiş ve ücretlendirilmiş olabilir; çift ücret olmaması için otomatik yeniden gönderilmez,
    /// en kötü durum maliyeti kaydedilir. "Kesin başarısız" değildir.
    /// </summary>
    OutcomeUncertain,

    /// <summary>Günlük/aylık/iş başı AI bütçe sınırı aşılacaktı; sağlayıcı çağrılmadı (ücret yok).</summary>
    BudgetExceeded,

    /// <summary>Gerçek sağlayıcı kapalı (ücretli çağrılara izin verilmemiş) veya API anahtarı yok; sağlayıcı çağrılmadı.</summary>
    ProviderNotConfigured,
    Unknown,
}

/// <summary>Bir AI işi. Aynı tablo iş kuyruğu olarak da kullanılır (UPDLOCK + READPAST).</summary>
public sealed class GenerationJob : Entity, IStoreOwned
{
    public Guid OrganizationId { get; init; }
    public Guid StoreId { get; init; }
    public Guid SessionId { get; init; }
    public GenerationJobType JobType { get; init; }
    public GenerationJobStatus Status { get; set; } = GenerationJobStatus.Queued;

    public Guid? SelectionId { get; init; }
    public Guid? SourcePhotoId { get; init; }

    /// <summary>Revizyonlarda temel alınan önceki sürüm.</summary>
    public Guid? BaseImageId { get; init; }

    /// <summary>Yapılandırılmış revizyon parametreleri (hedef kategori, renk, yeni ürün...).</summary>
    public JsonDocument? RevisionParams { get; init; }

    // İzlenebilirlik: hangi sağlayıcı/model/şablon kullanıldı
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? PromptTemplateVersion { get; set; }
    public string? ProviderRequestId { get; set; }

    /// <summary>
    /// Ücretli gönderim niyeti (önce yaz, sonra gönder): sağlayıcıya POST yapılmadan hemen önce yazılır. Doluysa ve <see cref="ProviderRequestId"/>
    /// boşsa gönderimin sonucu bilinmiyordur (yanıt kayboldu, süreç çöktü): iş yeniden gönderilmez, <see cref="GenerationErrorCode.OutcomeUncertain"/> olur.
    /// Sağlayıcı isteği kesin reddettiğinde veya istek kesin bittiğinde temizlenir.
    /// </summary>
    public DateTimeOffset? ProviderSubmitStartedAt { get; set; }

    public GenerationStage? Stage { get; set; }

    /// <summary>Aynı fotoğraf + aynı seçim + aynı iş tipi için özet. Gerçek sağlayıcıda aynı isteğin ikinci kez ücretlendirilmesini önler.</summary>
    public string? RequestFingerprint { get; init; }

    /// <summary>
    /// Yalnızca ücretli (gerçek sağlayıcı) ve "yeni varyasyon" istenmemiş işlerde <see cref="RequestFingerprint"/>'e eşittir, aksi halde boştur.
    /// Oturumda aynı anahtarla yalnızca bir etkin (Queued/Running/Succeeded) iş olabilir: veritabanı benzersiz indeksiyle zorunludur.
    /// </summary>
    public string? DedupeKey { get; init; }

    /// <summary>Tek ürün giydirmede seçilen model anahtarı (ör. <c>seedream-5-flash</c>). İstek anında sabitlenir; yeniden denemeler aynı modeli kullanır.</summary>
    public string? ModelKey { get; init; }

    /// <summary>Seçilen kalite (ör. GPT Image için <c>medium</c>). Maliyet tahmini ve kullanım kaydı buna göre yapılır.</summary>
    public string? Quality { get; init; }

    public required string IdempotencyKey { get; init; }
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }

    public GenerationErrorCode? ErrorCode { get; set; }

    /// <summary>Kişisel veri içermez; sağlayıcı yanıtı kırpılıp temizlenerek yazılır.</summary>
    public string? ErrorMessage { get; set; }

    public Guid RequestedByUserId { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>Bir üretim sonucu. Revizyonlar <see cref="ParentImageId"/> ile sürüm ağacı oluşturur; hiçbir sonuç üzerine yazılmaz.</summary>
public sealed class GeneratedImage : Entity, ITenantOwned
{
    public Guid OrganizationId { get; init; }
    public Guid SessionId { get; init; }
    public Guid JobId { get; init; }
    public Guid? ParentImageId { get; init; }

    /// <summary>Oturum içinde artan sürüm numarası (1, 2, 3...).</summary>
    public int VersionNo { get; init; }

    public Guid FileId { get; init; }
    public ImageAngle Angle { get; init; } = ImageAngle.Front;

    /// <summary>Aynı sonucun farklı açıları (360° hazırlığı).</summary>
    public Guid? ViewGroupId { get; init; }

    public required string Label { get; set; }

    /// <summary>Katalogda olmayan renk: "Renk önizlemesi — stokta olmayabilir" (K3).</summary>
    public bool IsColorPreview { get; init; }

    /// <summary>
    /// Mock sağlayıcıdan gelen sonuç: müşterinin gerçekten giydirilmiş fotoğrafı DEĞİLDİR.
    /// UI her zaman "DEMO" olarak işaretler; görselin üzerine de filigran basılıdır.
    /// </summary>
    public bool IsDemo { get; init; }

    public bool IsFavorite { get; set; }
}

/// <summary>Her sağlayıcı çağrısının maliyet/süre kaydı. Kişisel veri içermez; oturum silinince JobId NULL'lanır.</summary>
public sealed class AiUsageLog : Entity, IStoreOwned
{
    public Guid OrganizationId { get; init; }
    public Guid StoreId { get; init; }
    public Guid? JobId { get; set; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string Operation { get; init; }

    /// <summary>Kullanılan kalite seviyesi (varsa). Her kalite için ayrı maliyet izlenir.</summary>
    public string? Quality { get; init; }
    public int Attempt { get; init; }
    public bool Success { get; init; }
    public int DurationMs { get; init; }
    public decimal? CreditsUsed { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public decimal EstimatedCostUsd { get; init; }

    /// <summary>Sağlayıcının bildirdiği gerçekleşen kullanım (ör. FASHN kredisi × birim fiyat). Bildirmiyorsa null: tahmin geçerlidir.</summary>
    public decimal? ActualCostUsd { get; init; }

    /// <summary>Sağlayıcının istek/tahmin kimliği (fatura mutabakatı için; kişisel veri içermez).</summary>
    public string? ProviderRequestId { get; init; }

    public GenerationErrorCode? ErrorCode { get; init; }
}
