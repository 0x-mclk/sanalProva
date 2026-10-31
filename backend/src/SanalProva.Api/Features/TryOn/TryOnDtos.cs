using System.ComponentModel.DataAnnotations;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Common;
using SanalProva.Domain.Generation;
using SanalProva.Domain.TryOn;

namespace SanalProva.Api.Features.TryOn;

public sealed record CreateSessionRequest([property: StringLength(40)] string? CustomerLabel);

public sealed record ConsentNoticeDto(string Version, string Title, IReadOnlyList<string> Paragraphs, string RetentionNote);

public sealed record ConsentRequest(
    [property: Required, StringLength(20)] string NoticeVersion,
    bool ProcessingConsent, bool ForeignTransferConsent, bool ExtendedRetentionConsent,
    bool IsMinor, bool? GuardianConsent, ConsentMethod Method);

public sealed record ConsentDto(string NoticeVersion, bool ProcessingConsent, bool ForeignTransferConsent, bool ExtendedRetentionConsent, bool? GuardianConsent, ConsentMethod Method, DateTimeOffset RecordedAt);

public sealed record PhotoQualityDto(bool Blurry, bool LowResolution, bool NotPortrait, bool HasWarnings, IReadOnlyList<string> Warnings);

/// <param name="StaffApprovedAt">Personelin "fotoğraf prova için uygun" onayı (sunucuda kayıtlı; ücretli üretim bu onay olmadan kabul edilmez). Yeniden çekimde yeni fotoğraf onaysız başlar.</param>
public sealed record PhotoDto(Guid Id, ImageAngle Angle, int Width, int Height, bool IsPrimary, PhotoQualityDto Quality, ImageUrls Urls, DateTimeOffset? StaffApprovedAt = null);

/// <summary>
/// Üç beyanın HEPSİ true olmalıdır (aksi halde 400). Bu bir otomatik analiz değildir: sistem kişi sayısını veya tam boyu ölçmez; onay, personelin sorumluluk beyanıdır.
/// </summary>
public sealed record PhotoApprovalRequest(bool SinglePersonFullBody, bool FrontalUprightPose, bool SharpAndWellLit);

public sealed class PhotoUploadForm
{
    public required IFormFile File { get; init; }
    public ImageAngle Angle { get; init; } = ImageAngle.Front;
}

public sealed record DraftItemRequest(Guid ProductId, Guid? VariantId);

public sealed record DraftRequest(OutfitMode Mode, IReadOnlyList<DraftItemRequest> Items);

public sealed record DraftItemDto(Guid CategoryId, Guid ProductId, Guid? VariantId);

/// <param name="Warnings">Birleştirme uyarıları (çakışan kategori vb.). Taslak kaydedilir; "Üret" sırasında engellenir.</param>
public sealed record DraftDto(OutfitMode Mode, IReadOnlyList<DraftItemDto> Items, IReadOnlyList<string> Warnings);

/// <param name="AiModel">Üreten model ve kalite (ör. "GPT Image 2.5 · Orta"). Demo sonuçta seçilen model gösterilir; görsel yine DEMO'dur.</param>
public sealed record GeneratedImageDto(
    Guid Id, int VersionNo, Guid? ParentImageId, Guid JobId, string Label, bool IsDemo, bool IsColorPreview, bool IsFavorite,
    DateTimeOffset CreatedAt, ImageUrls Urls, string? AiModel);

public sealed record JobDto(
    Guid Id, Guid SessionId, GenerationJobType JobType, GenerationJobStatus Status, GenerationStage? Stage, int? QueuePosition,
    int Attempt, int MaxAttempts, GenerationErrorCode? ErrorCode, string? Message, bool Retryable, Guid? ResultImageId,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Provider, string? Model, bool IsDemo,
    string? ModelKey, string? Quality, string? ModelLabel);

public sealed record SessionSummary(
    Guid Id, string? CustomerLabel, string Code, SessionStatus Status, SessionStep NextStep,
    DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, DateTimeOffset RetentionUntil, int VersionCount,
    ImageUrls? Photo, ImageUrls? Result, bool ResultIsDemo, string? ResultAiModel);

public sealed record SessionDetail(
    Guid Id, string? CustomerLabel, string Code, SessionStatus Status, SessionStep NextStep,
    DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, DateTimeOffset RetentionUntil, bool ExtendedRetention,
    ConsentDto? Consent, IReadOnlyList<PhotoDto> Photos, DraftDto? Draft,
    IReadOnlyList<GeneratedImageDto> Images, IReadOnlyList<JobDto> Jobs, Guid? ActiveJobId);

/// <param name="AllowVariation">Gerçek sağlayıcıda aynı fotoğraf+seçim için daha önce başarılı/süren bir iş varsa varsayılan olarak o iş döner (aynı isteğin ikinci kez ücretlendirilmesi önlenir). true ise bilerek yeni (ücretli) bir varyasyon üretilir.</param>
/// <param name="ModelKey">Tek ürün giydirmede model (ör. <c>seedream-5-flash</c>, <c>gpt-image-2.5</c>, <c>pruna-tryon</c>). Boşsa firmanın varsayılanı. Yalnızca yöneticinin izin verdiği modeller kabul edilir.</param>
/// <param name="Quality">Modelin kalite seviyesi (GPT Image: <c>auto</c>/<c>low</c>/<c>medium</c>/<c>high</c>/<c>xhigh</c>/<c>max</c>). Boşsa modelin varsayılanı. Yalnızca yöneticinin izin verdiği kaliteler kabul edilir.</param>
public sealed record StartGenerationRequest(Guid? PhotoId, bool AllowVariation = false, string? ModelKey = null, string? Quality = null);

public sealed record RevisionRequest(Guid BaseImageId, string Type, Guid? TargetCategoryId, Guid? ProductId, Guid? VariantId, string? Color);

public sealed record FavoriteRequest(bool IsFavorite);

/// <summary>İş hatalarının kullanıcıya gösterilen Türkçe karşılıkları. Teknik ayrıntı ve sağlayıcı yanıtı asla gösterilmez.</summary>
internal static class JobMessages
{
    public static (string Message, bool Retryable) For(GenerationErrorCode code) => code switch
    {
        GenerationErrorCode.ProviderUnavailable or GenerationErrorCode.Timeout or GenerationErrorCode.RateLimited
            => ("Üretim servisine şu an ulaşılamıyor. Lütfen tekrar deneyin.", true),
        GenerationErrorCode.ContentRejected => ("Bu fotoğrafla üretim yapılamadı. Farklı bir fotoğraf deneyin.", false),
        GenerationErrorCode.InvalidInput => ("Seçim veya fotoğraf geçersiz. Seçimlerinizi kontrol edip tekrar deneyin.", false),
        GenerationErrorCode.InvalidOutput => ("Üretilen görsel doğrulanamadı. Lütfen tekrar deneyin.", true),
        GenerationErrorCode.MaskQualityLow => ("Renk değişimi güvenilir yapılamadı.", false),
        GenerationErrorCode.QuotaExceeded => ("Üretim kotası doldu.", false),
        GenerationErrorCode.BudgetExceeded => ("AI bütçe sınırına ulaşıldı. Yöneticiye bildirin; sağlayıcı çağrılmadı, ücret doğmadı.", false),
        GenerationErrorCode.PaidAiNotEnabled => ("Bu işletme için ücretli AI henüz etkinleştirilmedi; sağlayıcı çağrılmadı, ücret doğmadı. Yöneticiyle iletişime geçin.", false),
        GenerationErrorCode.ProviderNotConfigured => ("Bu üretim şu an etkin değil (gerçek AI kapalı veya bu işlem henüz desteklenmiyor). Yöneticiyle iletişime geçin.", false),
        GenerationErrorCode.NotImplemented => ("Bu işlem henüz desteklenmiyor.", false),
        GenerationErrorCode.OutcomeUncertain => ("AI servisine gönderilen istek yanıtlanmadı; işlem gerçekleşmiş ve ücretlendirilmiş olabilir. Çift ücret olmaması için otomatik yeniden denenmedi. Yeniden denerseniz ikinci bir ücret oluşabilir.", true),
        _ => ("Beklenmeyen bir hata oluştu. Lütfen tekrar deneyin.", true),
    };

    public static IReadOnlyList<string> PhotoWarnings(PhotoQualityFlags flags)
    {
        var warnings = new List<string>();
        if (flags.NotPortrait)
        {
            warnings.Add("Fotoğraf dikey değil. Boydan çekim için telefonu dikey tutun.");
        }

        if (flags.LowResolution)
        {
            warnings.Add("Çözünürlük düşük. Daha yakın veya daha yüksek kalitede çekin.");
        }

        if (flags.Blurry)
        {
            warnings.Add("Fotoğraf bulanık görünüyor. Sabit durup yeniden çekin.");
        }

        return warnings;
    }
}
