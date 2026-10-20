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

public sealed record PhotoDto(Guid Id, ImageAngle Angle, int Width, int Height, bool IsPrimary, PhotoQualityDto Quality, ImageUrls Urls);

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

public sealed record GeneratedImageDto(
    Guid Id, int VersionNo, Guid? ParentImageId, Guid JobId, string Label, bool IsDemo, bool IsColorPreview, bool IsFavorite,
    DateTimeOffset CreatedAt, ImageUrls Urls);

public sealed record JobDto(
    Guid Id, Guid SessionId, GenerationJobType JobType, GenerationJobStatus Status, GenerationStage? Stage, int? QueuePosition,
    int Attempt, int MaxAttempts, GenerationErrorCode? ErrorCode, string? Message, bool Retryable, Guid? ResultImageId,
    DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Provider, string? Model, bool IsDemo);

public sealed record SessionSummary(
    Guid Id, string? CustomerLabel, string Code, SessionStatus Status, SessionStep NextStep,
    DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, DateTimeOffset RetentionUntil, int VersionCount,
    ImageUrls? Photo, ImageUrls? Result, bool ResultIsDemo);

public sealed record SessionDetail(
    Guid Id, string? CustomerLabel, string Code, SessionStatus Status, SessionStep NextStep,
    DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt, DateTimeOffset RetentionUntil, bool ExtendedRetention,
    ConsentDto? Consent, IReadOnlyList<PhotoDto> Photos, DraftDto? Draft,
    IReadOnlyList<GeneratedImageDto> Images, IReadOnlyList<JobDto> Jobs, Guid? ActiveJobId);

/// <param name="AllowVariation">Gerçek sağlayıcıda aynı fotoğraf+seçim için daha önce başarılı/süren bir iş varsa varsayılan olarak o iş döner (aynı isteğin ikinci kez ücretlendirilmesi önlenir). true ise bilerek yeni (ücretli) bir varyasyon üretilir.</param>
public sealed record StartGenerationRequest(Guid? PhotoId, bool AllowVariation = false);

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
        GenerationErrorCode.ProviderNotConfigured => ("Gerçek AI üretimi şu an etkin değil. Yöneticiyle iletişime geçin.", false),
        GenerationErrorCode.NotImplemented => ("Bu işlem henüz desteklenmiyor.", false),
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
