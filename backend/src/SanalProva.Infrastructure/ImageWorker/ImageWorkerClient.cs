using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SanalProva.Domain.Catalog;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai.Abstractions;

namespace SanalProva.Infrastructure.ImageWorker;

// Sözleşme: image-worker/app/contracts.py ile birebir aynı tutulmalıdır (JSON alanları snake_case).

/// <param name="X">0–1 arası normalize yatay konum.</param>
/// <param name="Y">0–1 arası normalize dikey konum.</param>
public sealed record PointPrompt(double X, double Y);

/// <summary>Maskelenecek kıyafet parçası. Personelin görselde dokunduğu nokta(lar) SAM 2'ye istem olarak gider.</summary>
public sealed record SegmentationTarget(BodyZone Zone, string? Hint, IReadOnlyList<PointPrompt>? Points);

public sealed record SegmentationResult(byte[] MaskPng, double Confidence, double AreaRatio);

public sealed record RecolorQualityReport(double DeltaE, double EdgeLeak, double StructureSimilarity, bool PatternPreserved, bool Passed);

public sealed record RecolorResult(byte[] Image, string ContentType, RecolorQualityReport Quality);

public sealed record ImageWorkerHealth(string Status, string Segmentation, string OpenCvVersion);

/// <summary>Hedef kıyafetin maskesini çıkarır (SAM 2 veya mock).</summary>
public interface IImageSegmentationService
{
    Task<SegmentationResult> SegmentAsync(ImageInput image, SegmentationTarget target, CancellationToken ct);
}

/// <summary>Maske içindeki kumaşın rengini değiştirir; nakış ve kıvrımları korumaya çalışır, kalite raporu döner.</summary>
public interface IRecolorService
{
    Task<RecolorResult> RecolorAsync(ImageInput image, byte[] maskPng, string targetHex, CancellationToken ct);
}

public sealed class ImageWorkerOptions
{
    public const string Section = "ImageWorker";

    [Required]
    [Url]
    public string BaseUrl { get; set; } = "http://127.0.0.1:8090";

    /// <summary>İç ağ paylaşılan anahtarı (X-Internal-Key). Yalnızca gizli yapılandırmadan gelir.</summary>
    public string InternalKey { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>Python image-worker servisinin HTTP istemcisi. Servis durumsuzdur ve görselleri diske yazmaz.</summary>
public sealed class ImageWorkerClient(HttpClient http) : IImageSegmentationService, IRecolorService
{
    public const string InternalKeyHeader = "X-Internal-Key";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async Task<SegmentationResult> SegmentAsync(ImageInput image, SegmentationTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var form = new MultipartFormDataContent
        {
            { ToContent(image.Content.ToArray(), image.ContentType), "image", "image" },
            { new StringContent(JsonSerializer.Serialize(target, Json)), "params" },
        };
        var response = await SendAsync("v1/segment", form, ct);
        var dto = await ReadAsync<SegmentResponse>(response, ct);
        return new SegmentationResult(Convert.FromBase64String(dto.MaskPngBase64), dto.Confidence, dto.AreaRatio);
    }

    public async Task<RecolorResult> RecolorAsync(ImageInput image, byte[] maskPng, string targetHex, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var form = new MultipartFormDataContent
        {
            { ToContent(image.Content.ToArray(), image.ContentType), "image", "image" },
            { ToContent(maskPng, "image/png"), "mask", "mask" },
            { new StringContent(JsonSerializer.Serialize(new { target_hex = targetHex, preserve_texture = true })), "params" },
        };
        var response = await SendAsync("v1/recolor", form, ct);
        var dto = await ReadAsync<RecolorResponse>(response, ct);
        return new RecolorResult(Convert.FromBase64String(dto.ImageBase64), dto.ContentType, dto.Quality);
    }

    public async Task<ImageWorkerHealth> GetHealthAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri("health", UriKind.Relative), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ImageWorkerHealth>(Json, ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(string path, HttpContent content, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(new Uri(path, UriKind.Relative), content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException(GenerationErrorCode.ProviderUnavailable, "Görsel işleme servisine ulaşılamadı.", retryable: true, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(GenerationErrorCode.Timeout, "Görsel işleme servisi zaman aşımına uğradı.", retryable: true, ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw response.StatusCode switch
            {
                HttpStatusCode.NotImplemented => new ProviderException(GenerationErrorCode.ProviderUnavailable, "Bu işlem görsel serviste henüz uygulanmadı.", retryable: false),
                HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest => new ProviderException(GenerationErrorCode.InvalidInput, "Görsel serviste girdi doğrulaması başarısız.", retryable: false),
                HttpStatusCode.Unauthorized => new ProviderException(GenerationErrorCode.ProviderUnavailable, "Görsel servis iç anahtarı geçersiz.", retryable: false),
                _ => new ProviderException(GenerationErrorCode.ProviderUnavailable, $"Görsel servis hata döndü ({(int)response.StatusCode}).", retryable: true),
            };
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                ?? throw new ProviderException(GenerationErrorCode.InvalidOutput, "Görsel servis boş yanıt döndü.", retryable: false);
        }
    }

    private static ByteArrayContent ToContent(byte[] bytes, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return content;
    }

    private sealed record SegmentResponse(string MaskPngBase64, double Confidence, double AreaRatio);

    private sealed record RecolorResponse(string ImageBase64, string ContentType, RecolorQualityReport Quality);
}
