using SanalProva.Domain.Generation;

namespace SanalProva.Infrastructure.Ai.Abstractions;

/// <summary>Sağlayıcıya gönderilen görsel. Bellekte tutulur; dosya adı veya müşteri bilgisi taşımaz.</summary>
public sealed record ImageInput(ReadOnlyMemory<byte> Content, string ContentType);

/// <summary>Serbest kombindeki bir ürünün referans görseli ve prompt'a girecek yapılandırılmış bilgisi.</summary>
public sealed record GarmentReference(
    ImageInput Image,
    string CategoryName,
    string ProductName,
    string? ColorName,
    int LayerOrder,
    string? AiHint);

/// <summary>Sağlayıcıya özgü çağrı seçenekleri. Model ve parametreler yapılandırmadan gelir.</summary>
/// <param name="ResumeRequestId">Önceki denemede sağlayıcıya gönderilmiş ama sonucu alınamamış tahminin kimliği. Verilirse yeni (ücretli) istek açılmaz, aynı tahmin sorgulanır.</param>
/// <param name="OnSubmitted">Sağlayıcı isteği kabul edip kimlik verdiği anda çağrılır; çağıran kimliği hemen saklar (çökme/zaman aşımında çift ücreti önler).</param>
public sealed record ProviderRequestOptions(
    string Model,
    IReadOnlyDictionary<string, string> Parameters,
    int? Seed = null,
    string? ResumeRequestId = null,
    Func<string, Task>? OnSubmitted = null);

public sealed record GeneratedOutput(byte[] Content, string ContentType);

public sealed record ProviderUsage(
    int DurationMs,
    decimal? Credits = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    decimal EstimatedCostUsd = 0m,
    decimal? ActualCostUsd = null);

public sealed record ProviderResult(
    IReadOnlyList<GeneratedOutput> Images,
    string Provider,
    string Model,
    string? ProviderRequestId,
    ProviderUsage Usage);

/// <summary>Sağlayıcı hatası, ortak hata koduna çevrilmiş halde. Mesaj kişisel veri içermez.</summary>
public sealed class ProviderException : Exception
{
    public ProviderException(GenerationErrorCode code, string message, bool retryable, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Retryable = retryable;
    }

    public GenerationErrorCode Code { get; }
    public bool Retryable { get; }

    /// <summary>Hata olsa da sağlayıcı bu çağrı için ücret doğurduysa tahmini tutar (ör. görselsiz yanıtta giriş jetonları). Maliyet kaydı bunu yazar.</summary>
    public decimal ChargedUsd { get; init; }

    /// <summary>Hata anında bilinen sağlayıcı istek/tahmin kimliği.</summary>
    public string? ProviderRequestId { get; init; }
}

/// <summary>Kişi fotoğrafı + tek ürün görseli ile giydirme (ör. FASHN Try-On Max — hazır takım).</summary>
public interface ITryOnProvider
{
    Task<ProviderResult> TryOnAsync(ImageInput person, ImageInput garment, ProviderRequestOptions options, CancellationToken ct);
}

/// <summary>Kişi fotoğrafı + çoklu ürün referansı ile kombin oluşturma (ör. Gemini Nano Banana Pro — serbest kombin).</summary>
public interface IImageGenerationProvider
{
    Task<ProviderResult> ComposeOutfitAsync(
        ImageInput person,
        IReadOnlyList<GarmentReference> garments,
        string prompt,
        ProviderRequestOptions options,
        CancellationToken ct);
}

/// <summary>Önceki sonuç + yapılandırılmış talimat (+ referanslar) ile düzenleme (ör. Gemini — ürün değiştirme).</summary>
public interface IImageEditProvider
{
    Task<ProviderResult> EditAsync(
        ImageInput baseImage,
        string instruction,
        IReadOnlyList<ImageInput> references,
        ProviderRequestOptions options,
        CancellationToken ct);
}
