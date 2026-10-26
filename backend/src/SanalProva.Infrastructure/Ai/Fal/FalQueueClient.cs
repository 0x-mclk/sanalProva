using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai.Abstractions;

namespace SanalProva.Infrastructure.Ai.Fal;

/// <summary>
/// fal.ai kuyruk API istemcisi (Seedream ve GPT Image adapter'larının ortak parçası). Resmî Queue dokümanı ve model OpenAPI şemalarına göre:
/// <list type="bullet">
/// <item>Gönder: <c>POST https://queue.fal.run/{model}</c> → <c>request_id</c>, <c>status_url</c>, <c>response_url</c>;</item>
/// <item>Durum: <c>GET {app}/requests/{id}/status</c> (<c>IN_QUEUE</c>, <c>IN_PROGRESS</c>, <c>COMPLETED</c>; başarısızlıkta <c>error</c>/<c>error_type</c>);</item>
/// <item>Sonuç: <c>GET {app}/requests/{id}</c>; iptal: <c>PUT {app}/requests/{id}/cancel</c>.</item>
/// </list>
/// Gizlilik: girdiler data URI olarak gider (fal deposuna yükleme yok), <c>X-Fal-Store-IO: 0</c> ile yük saklama kapatılır, model girdisinde
/// <c>sync_mode: true</c> çıktıyı data URI olarak döndürür ve istek geçmişinde tutulmaz.
/// </summary>
public sealed class FalQueueClient(HttpClient http, IOptions<FalOptions> configuration)
{
    private const string Provider = "fal.ai";
    private static readonly string[] MediaHosts = ["fal.media", "fal.run"];

    public FalOptions Options => configuration.Value;

    public void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(Options.ApiKey))
        {
            throw new ProviderException(GenerationErrorCode.ProviderNotConfigured, "fal.ai API anahtarı yapılandırılmamış.", retryable: false);
        }
    }

    /// <summary>
    /// Durum/sonuç/iptal adresleri için uygulama yolu. Doküman: kolaylık adresleri yalnızca sahip/uygulama içerir (ör. <c>openai/gpt-image-2.5</c>);
    /// tam uç nokta yolu bu adreslerde 405 döner.
    /// </summary>
    internal static string AppPath(string modelId)
    {
        var parts = modelId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : modelId;
    }

    /// <summary>
    /// Ücretli isteği kuyruğa gönderir. fal gönderim için idempotency anahtarı belgelemiyor: sonucu bilinmeyen gönderim yeniden yapılmaz.
    /// Kesin ret: yanıtlı 4xx (istek kuyruğa alınmadı). Belirsiz: yanıt yok, 5xx (ağ geçidi isteği iletmiş olabilir) veya kimliksiz başarılı yanıt.
    /// </summary>
    /// <returns>fal istek kimliği.</returns>
    public async Task<string> SubmitAsync(string modelId, IReadOnlyDictionary<string, object> input, CancellationToken ct)
    {
        using var message = Request(HttpMethod.Post, modelId);
        message.Content = new StringContent(JsonSerializer.Serialize(input), Encoding.UTF8, "application/json");
        using var response = await ProviderHttp.SendSubmitAsync(http, message, Options.RequestTimeoutSeconds, Provider, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = MapHttpError((int)response.StatusCode, await ProviderHttp.TryReadBodyAsync(response, ct), submitting: true);
            throw (int)response.StatusCode >= 500 ? ProviderHttp.Uncertain(error) : error;
        }

        string? id;
        try
        {
            using var json = await ProviderHttp.ReadJsonAsync(response, Provider, ct);
            id = json.RootElement.TryGetProperty("request_id", out var r) ? r.GetString() : null;
        }
        catch (ProviderException)
        {
            throw ProviderHttp.UncertainWithoutId(Provider);
        }

        return string.IsNullOrEmpty(id) ? throw ProviderHttp.UncertainWithoutId(Provider) : id;
    }

    /// <summary>İstek tamamlanana kadar yoklar ve sonuç gövdesini döner. Süre aşılırsa yeniden denenebilir zaman aşımı (istek kimliği çağıranda saklıdır).</summary>
    public async Task<JsonDocument> WaitForResultAsync(string modelId, string requestId, bool resuming, CancellationToken ct)
    {
        var app = AppPath(modelId);
        var id = Uri.EscapeDataString(requestId);
        var started = DateTime.UtcNow;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using (var statusMessage = Request(HttpMethod.Get, $"{app}/requests/{id}/status"))
            using (var status = await SendCheckedAsync(statusMessage, submitting: false, resuming, ct))
            using (var json = await ProviderHttp.ReadJsonAsync(status, Provider, ct))
            {
                var root = json.RootElement;
                if (root.TryGetProperty("error", out var error) && error.ValueKind is JsonValueKind.String or JsonValueKind.Object)
                {
                    throw MapRuntimeError(root.TryGetProperty("error_type", out var type) ? type.GetString() : null);
                }

                if (root.TryGetProperty("status", out var s) && s.GetString() == "COMPLETED")
                {
                    using var resultMessage = Request(HttpMethod.Get, $"{app}/requests/{id}");
                    var result = await SendCheckedAsync(resultMessage, submitting: false, resuming, ct);
                    using (result)
                    {
                        return await ProviderHttp.ReadJsonAsync(result, Provider, ct);
                    }
                }
            }

            if ((DateTime.UtcNow - started).TotalSeconds >= Options.MaxWaitSeconds)
            {
                throw new ProviderException(GenerationErrorCode.Timeout, "fal.ai sonucu zamanında hazır olmadı.", retryable: true);
            }

            await Task.Delay(Options.PollIntervalMs, ct);
        }
    }

    /// <summary>Kuyruktaki/çalışan isteği iptal etmeyi dener (en iyi çaba; hata yutulur).</summary>
    public async Task TryCancelAsync(string modelId, string requestId)
    {
        try
        {
            using var message = Request(HttpMethod.Put, $"{AppPath(modelId)}/requests/{Uri.EscapeDataString(requestId)}/cancel");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var _ = await http.SendAsync(message, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // iptal en iyi çabadır: sonuç zaten atılır
        }
    }

    /// <summary>Sonuçtaki ilk görseli alır: data URI (sync_mode) ya da yalnızca fal medya alan adlarından HTTPS indirme.</summary>
    public async Task<GeneratedOutput> ReadFirstImageAsync(JsonElement result, CancellationToken ct)
    {
        if (!result.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0
            || !images[0].TryGetProperty("url", out var urlElement) || urlElement.GetString() is not { Length: > 0 } url)
        {
            throw new ProviderException(GenerationErrorCode.InvalidOutput, "fal.ai görsel döndürmedi.", retryable: false);
        }

        if (ProviderHttp.TryDecodeDataUri(url, Provider) is { } decoded)
        {
            return decoded;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !ProviderHttp.IsAllowedHost(uri, MediaHosts))
        {
            throw new ProviderException(GenerationErrorCode.InvalidOutput, "fal.ai çıktı adresi tanınmadı.", retryable: false);
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await ProviderHttp.SendAsync(http, message, Options.RequestTimeoutSeconds, Provider, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ProviderException(GenerationErrorCode.ProviderUnavailable, "fal.ai çıktısı indirilemedi.", retryable: true);
        }

        return new GeneratedOutput(await response.Content.ReadAsByteArrayAsync(ct), response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Key", Options.ApiKey);
        message.Headers.TryAddWithoutValidation("X-Fal-Store-IO", "0"); // istek/yanıt yükünü fal tarafında saklama
        return message;
    }

    private async Task<HttpResponseMessage> SendCheckedAsync(HttpRequestMessage message, bool submitting, bool resuming, CancellationToken ct)
    {
        var response = await ProviderHttp.SendAsync(http, message, Options.RequestTimeoutSeconds, Provider, ct);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            if (!submitting && response.StatusCode == HttpStatusCode.NotFound)
            {
                // İstek kimliği artık yok: yeniden göndermek ikinci ücret olabilir; otomatik denenmez
                throw new ProviderException(GenerationErrorCode.InvalidOutput, resuming ? "Önceki fal.ai sonucu artık alınamıyor." : "fal.ai isteği bulunamadı.", retryable: false)
                {
                    RequestEnded = true,
                };
            }

            var body = await ProviderHttp.TryReadBodyAsync(response, ct);
            throw MapHttpError((int)response.StatusCode, body, submitting);
        }
    }

    /// <summary>HTTP hataları (fal Queue dokümanı + genel REST kuralları). Yanıt gövdesi iletiye eklenmez; yalnızca içerik politikası işareti aranır.</summary>
    internal static ProviderException MapHttpError(int status, string? body, bool submitting)
    {
        var contentPolicy = body is not null && body.Contains("content_policy", StringComparison.OrdinalIgnoreCase);
        return status switch
        {
            401 or 403 => new(GenerationErrorCode.ProviderNotConfigured, "fal.ai API anahtarı geçersiz veya yetkisiz.", retryable: false),
            404 when submitting => new(GenerationErrorCode.ProviderNotConfigured, "fal.ai model uç noktası bulunamadı.", retryable: false),
            400 or 422 when contentPolicy => new(GenerationErrorCode.ContentRejected, "fal.ai girdiyi içerik politikası nedeniyle reddetti.", retryable: false),
            400 or 422 => new(GenerationErrorCode.InvalidInput, "fal.ai isteği geçersiz.", retryable: false),
            429 => new(GenerationErrorCode.RateLimited, "fal.ai istek sınırına ulaşıldı.", retryable: true),
            504 => new(GenerationErrorCode.Timeout, "fal.ai zaman aşımı.", retryable: true),
            >= 500 => new(GenerationErrorCode.ProviderUnavailable, "fal.ai geçici olarak hizmet vermiyor.", retryable: true),
            _ => new(GenerationErrorCode.Unknown, "fal.ai beklenmeyen bir hata döndürdü.", retryable: false),
        };
    }

    /// <summary>
    /// Tamamlanmış ama başarısız istek (<c>error_type</c>): istek sağlayıcıda kesin bitti (<see cref="ProviderException.RequestEnded"/>), yeniden denemede
    /// yeni istek açılabilir. fal hata türü listesi ve başarısız isteğin ücretlendirilip ücretlendirilmediği bu projede doğrulanmadı.
    /// </summary>
    internal static ProviderException MapRuntimeError(string? errorType)
    {
        var type = errorType ?? string.Empty;
        if (type.Contains("content_policy", StringComparison.OrdinalIgnoreCase) || type.Contains("safety", StringComparison.OrdinalIgnoreCase) || type.Contains("nsfw", StringComparison.OrdinalIgnoreCase))
        {
            return new(GenerationErrorCode.ContentRejected, "fal.ai içeriği güvenlik nedeniyle reddetti.", retryable: false) { RequestEnded = true };
        }

        if (type.Contains("image", StringComparison.OrdinalIgnoreCase) || type.Contains("file", StringComparison.OrdinalIgnoreCase) || type.Contains("validation", StringComparison.OrdinalIgnoreCase))
        {
            return new(GenerationErrorCode.InvalidInput, "fal.ai girdiyi işleyemedi.", retryable: false) { RequestEnded = true };
        }

        return type.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            ? new(GenerationErrorCode.Timeout, "fal.ai işlemi zaman aşımına uğradı.", retryable: true) { RequestEnded = true }
            : new(GenerationErrorCode.ProviderUnavailable, "fal.ai üretimi başarısız oldu.", retryable: true) { RequestEnded = true };
    }

}
