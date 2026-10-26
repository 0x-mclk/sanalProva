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

    /// <summary>
    /// Ücretli gönderim niyetiyle <b>aynı kayıtta</b> yazılan en kötü durum maliyeti (USD). Çökme/kurtarma sırasında işlemci yeniden hesaplayamaz;
    /// sonucu belirsiz iş bu tutarla (SQL tarafında bile) bütçeye yazılır. Null: niyet yazılmamış veya eski kayıt.
    /// </summary>
    public decimal? ReservedCostUsd { get; set; }

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
public sealed class GeneratedImage : Entity, ISessionOwned
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

    /// <summary>
    /// Sistemin ürettiği olayın benzersiz anahtarı (ör. <c>uncertain:{jobId}</c>, <c>reconcile:{kararId}</c>). Aynı olay ikinci kez yazılamaz:
    /// veritabanı filtreli benzersiz indeksiyle zorunludur. Sıradan deneme kayıtlarında boştur (bir iş birden çok deneme yazar).
    /// </summary>
    public string? EventKey { get; init; }

    /// <summary>Düzeltme satırı: kayıt silinmez/değiştirilmez, fark (±) yeni satır olarak eklenir. Çağrı sayacına girmez, tutar toplamlarına girer.</summary>
    public bool IsAdjustment { get; init; }

    /// <summary>Düzeltmenin ilişkili olduğu ilk (belirsiz) kayıt.</summary>
    public Guid? AdjustsLogId { get; init; }

    /// <summary>
    /// Maliyetin doğduğu an (muhasebe tarihi). Düzeltme satırlarında asıl kaydın zamanıdır: bir günün/ayın bütçesi, kararın verildiği güne değil
    /// harcamanın doğduğu güne yazılır. Boşsa <see cref="Entity.CreatedAt"/> geçerlidir.
    /// </summary>
    public DateTimeOffset? OccurredAt { get; init; }

    /// <summary>Yalnızca sonucu belirsiz kayıtlarda dolu: mutabakat durumu (Open → platform yöneticisinin son kararı).</summary>
    public CostReconciliationState? ReconciliationState { get; set; }
}

/// <summary>Sonucu belirsiz maliyetin mutabakat durumu.</summary>
public enum CostReconciliationState
{
    /// <summary>Sağlayıcıda ücretlenip ücretlenmediği bilinmiyor; en kötü durum tutarı bütçede tutuluyor.</summary>
    Open,

    /// <summary>Sağlayıcı faturasında/panelinde ücretlendiği görüldü; tutar gerçek değere çekildi.</summary>
    ConfirmedCharged,

    /// <summary>Sağlayıcıda ücretlenmediği görüldü; bütçeden düşüldü.</summary>
    ConfirmedNotCharged,

    /// <summary>Kanıtlanamadı; maliyet platformun zararı olarak kapatıldı (müşteriye yansıtılmaz).</summary>
    WrittenOff,
}

/// <summary>Platform yöneticisinin verebileceği mutabakat kararları (<see cref="CostReconciliationState.Open"/> karar değildir).</summary>
public enum CostDecision
{
    ConfirmedCharged,
    ConfirmedNotCharged,
    WrittenOff,
}

/// <summary>
/// Belirsiz maliyet için verilmiş değişmez karar kaydı. Ekleme-yalnızca: düzeltme yeni <see cref="Sequence"/> ile yeni karar eklemektir.
/// Oturum silinse bile kalır (muhasebe kaydı; kişisel veri içermez), bu yüzden işe yabancı anahtarı yoktur.
/// </summary>
public sealed class AiCostReconciliation : Entity, ITenantOwned
{
    public Guid OrganizationId { get; init; }

    /// <summary>Belirsiz maliyet kaydı (ilk <see cref="AiUsageLog"/> satırı).</summary>
    public Guid UsageLogId { get; init; }

    /// <summary>Bilgi amaçlı iş kimliği (oturum silinince iş kaybolur; anahtar değildir).</summary>
    public Guid? JobId { get; init; }

    /// <summary>Bu vakadaki kararın sırası (1, 2, 3…). (UsageLogId, Sequence) benzersizdir: iki yönetici aynı sırayı yazamaz.</summary>
    public int Sequence { get; init; }

    public CostDecision Decision { get; init; }

    /// <summary>Karar sonrası bu vakanın geçerli (efektif) maliyeti (USD). Sonraki karar farkı buna göre hesaplar.</summary>
    public decimal EffectiveCostUsd { get; init; }

    /// <summary>Sağlayıcının gerçek tutarı (yalnızca ConfirmedCharged).</summary>
    public decimal? ConfirmedAmountUsd { get; init; }

    /// <summary>Kısa açıklama (≤ 200 karakter). Kişisel veri yazılmaz; arayüz bunu hatırlatır.</summary>
    public string? Note { get; init; }

    public Guid DecidedByUserId { get; init; }

    /// <summary>İstemcinin Idempotency-Key'i: aynı anahtar aynı kararı tekrar yazmaz, farklı gövdeyle kullanılırsa reddedilir.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>İstek gövdesinin özeti: aynı anahtarla farklı karar gönderilirse yakalanır.</summary>
    public required string RequestHash { get; init; }
}
