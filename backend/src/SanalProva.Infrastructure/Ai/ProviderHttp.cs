using System.Text.Json;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai.Abstractions;

namespace SanalProva.Infrastructure.Ai;

/// <summary>Sağlayıcı adapter'larının ortak HTTP yardımcıları. Hata iletileri sabittir: gövde, anahtar veya görsel iletiye/günlüğe girmez.</summary>
internal static class ProviderHttp
{
    /// <summary>İstek başına zaman aşımıyla gönderir; ağ hatası ve zaman aşımını yeniden denenebilir sağlayıcı hatasına çevirir.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage message, int timeoutSeconds, string provider, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            return await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(GenerationErrorCode.Timeout, $"{provider} isteği zaman aşımına uğradı.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException(GenerationErrorCode.ProviderUnavailable, $"{provider} sağlayıcısına ulaşılamadı.", retryable: true, ex);
        }
    }

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string provider, CancellationToken ct)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new ProviderException(GenerationErrorCode.ProviderUnavailable, $"{provider} yanıtı okunamadı.", retryable: true, ex);
        }
    }

    public static async Task<string?> TryReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public static string DataUri(ImageInput image) =>
        $"data:{(string.IsNullOrWhiteSpace(image.ContentType) ? "image/jpeg" : image.ContentType)};base64,{Convert.ToBase64String(image.Content.Span)}";

    /// <summary>Data URI'yi çözer; değilse null.</summary>
    public static GeneratedOutput? TryDecodeDataUri(string value, string provider)
    {
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var comma = value.IndexOf(',', StringComparison.Ordinal);
        var semicolon = value.IndexOf(';', StringComparison.Ordinal);
        if (comma < 0 || semicolon < 0 || semicolon > comma)
        {
            throw new ProviderException(GenerationErrorCode.InvalidOutput, $"{provider} çıktısı çözümlenemedi.", retryable: false);
        }

        try
        {
            return new GeneratedOutput(Convert.FromBase64String(value[(comma + 1)..]), value[5..semicolon]);
        }
        catch (FormatException ex)
        {
            throw new ProviderException(GenerationErrorCode.InvalidOutput, $"{provider} çıktısı çözümlenemedi.", retryable: false, ex);
        }
    }

    /// <summary>Yalnızca HTTPS ve izin verilen ana makine (veya alt alan adı) — SSRF koruması.</summary>
    public static bool IsAllowedHost(Uri uri, IEnumerable<string> hosts) =>
        uri.Scheme == Uri.UriSchemeHttps && hosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Tahmini maliyet: işleyicinin eklediği <c>model_key</c> ve <c>tier</c> parametrelerinden (yoksa 0). Jeton fiyatlı modellerde girdi
    /// görsellerinin boyutu, prompt uzunluğu, görsel sayısı ve (varsa) gerçek çıktı boyutu hesaba katılır.
    /// </summary>
    public static decimal EstimatedCost(ProviderRequestOptions options, PricingSettings pricing, ImageInput person, ImageInput garment, GeneratedOutput? output = null)
    {
        if (options.Parameters.TryGetValue("model_key", out var key) && TryOn.TryOnModels.Find(key) is { } model
            && options.Parameters.TryGetValue("tier", out var tier) && model.Quality(tier) is { } quality)
        {
            return TryOn.TryOnModels.Price(model, quality, pricing, CostInput(options, person, garment, output)).Estimated;
        }

        return 0m;
    }

    /// <summary>Maliyet hesabının girdisi: gönderilen görsellerin boyutları, prompt uzunluğu, <c>num_images</c> ve (varsa) çıktı boyutu.</summary>
    public static TryOn.TryOnCostInput CostInput(ProviderRequestOptions options, ImageInput person, ImageInput garment, GeneratedOutput? output = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(garment);
        var personSize = Dimensions(person.Content);
        var references = new[] { personSize, Dimensions(garment.Content) }.Select(d => d ?? new TryOn.ImageDimensions(2048, 2048)).ToList();
        var prompt = options.Parameters.TryGetValue("prompt", out var p) ? p.Length : 0;
        return new TryOn.TryOnCostInput(personSize, references, NumImages(options), prompt, output is null ? null : Dimensions(output.Content));
    }

    /// <summary>İstenen görsel sayısı (<c>num_images</c>, 1–4; varsayılan 1).</summary>
    public static int NumImages(ProviderRequestOptions options) =>
        options.Parameters.TryGetValue("num_images", out var n) && int.TryParse(n, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count)
            ? Math.Clamp(count, 1, 4) : 1;

    /// <summary>Görsel başlığından boyut (tam çözme yok); çözülemezse null — çağıran üst sınırı kullanır.</summary>
    public static TryOn.ImageDimensions? Dimensions(ReadOnlyMemory<byte> content)
    {
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        using var codec = SkiaSharp.SKCodec.Create(stream);
        return codec is null ? null : new TryOn.ImageDimensions(codec.Info.Width, codec.Info.Height);
    }
}
