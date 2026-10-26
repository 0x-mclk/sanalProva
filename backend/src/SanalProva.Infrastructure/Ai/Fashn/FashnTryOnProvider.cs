using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai.Abstractions;

namespace SanalProva.Infrastructure.Ai.Fashn;

/// <summary>
/// FASHN Try-On Max (hazır takım: tek kişi fotoğrafı + tek ürün görseli). Akış: <c>POST /v1/run</c> → <c>GET /v1/status/{id}</c> yoklaması.
/// Davranışlar (docs/07 §1, resmî dokümandan doğrulandı):
/// <list type="bullet">
/// <item>Girdiler satır içi data URI olarak gider; müşteri fotoğrafı için kalıcı/public URL oluşturulmaz.</item>
/// <item><c>return_base64: true</c>: çıktı FASHN CDN'inde 3 gün yerine 60 dk kalır ve geçmişte yalnızca yer tutucu saklanır (KVKK).</item>
/// <item>Tahmin kimliği alınır alınmaz <see cref="ProviderRequestOptions.OnSubmitted"/> ile çağırana bildirilir; zaman aşımı/çökme sonrası aynı tahmin sorgulanır, yeniden gönderilmez.</item>
/// <item>Başarısız tahmin kredi harcamaz; kredi <c>x-fashn-credits-used</c> başlığından (yoksa kredi tablosundan) okunur.</item>
/// </list>
/// Hata iletileri sabittir; istek/yanıt gövdesi, API anahtarı veya görsel hiçbir zaman iletiye ya da günlüğe girmez.
/// </summary>
public sealed class FashnTryOnProvider(HttpClient http, IOptions<FashnOptions> configuration, CostEstimator costs) : ITryOnProvider
{
    public const string ProviderName = "Fashn";
    public const string CreditsHeader = "x-fashn-credits-used";

    private static readonly string[] CdnHosts = ["cdn.fashn.ai", "media.fashn.ai"];

    public async Task<ProviderResult> TryOnAsync(ImageInput person, ImageInput garment, ProviderRequestOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(garment);
        ArgumentNullException.ThrowIfNull(options);
        var config = configuration.Value;
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new ProviderException(GenerationErrorCode.ProviderNotConfigured, "FASHN API anahtarı yapılandırılmamış.", retryable: false);
        }

        var stopwatch = Stopwatch.StartNew();
        decimal? headerCredits = null;
        var id = options.ResumeRequestId;
        if (string.IsNullOrEmpty(id))
        {
            if (options.OnSubmitting is { } onSubmitting)
            {
                await onSubmitting(); // gönderim niyeti kalıcı: yanıt kaybolursa iş yeniden gönderilmez
            }

            // FASHN Error Handling: yanıtlı API hatasında tahmin kimliği verilmez ve istek işlenmez (kesin ret). Yanıt hiç gelmezse veya
            // başarılı yanıtta kimlik yoksa sonuç belirsizdir (idempotency anahtarı belgelenmemiş)
            using var run = await SendAsync(HttpMethod.Post, "v1/run", BuildRunBody(person, garment, options), config, ct, submitting: true);
            try
            {
                using var runJson = await ReadJsonAsync(run, ct);
                id = runJson.RootElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            }
            catch (ProviderException)
            {
                throw ProviderHttp.UncertainWithoutId("FASHN");
            }

            if (string.IsNullOrEmpty(id))
            {
                throw ProviderHttp.UncertainWithoutId("FASHN");
            }

            if (options.OnSubmitted is { } onSubmitted)
            {
                await onSubmitted(id);
            }
        }

        var wait = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using var status = await SendAsync(HttpMethod.Get, $"v1/status/{Uri.EscapeDataString(id)}", null, config, ct, resuming: options.ResumeRequestId is not null);
            using var json = await ReadJsonAsync(status, ct);
            if (TryReadCredits(status, out var credits))
            {
                headerCredits = credits;
            }

            var state = json.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            switch (state)
            {
                case "completed":
                    return await BuildResultAsync(json.RootElement, id, headerCredits, options, stopwatch.ElapsedMilliseconds, config, ct);
                case "failed":
                    throw MapRuntimeError(json.RootElement);
            }

            if (wait.Elapsed.TotalSeconds >= config.MaxWaitSeconds)
            {
                // Tahmin kimliği çağırana zaten bildirildi: yeniden denemede aynı tahmin sorgulanır
                throw new ProviderException(GenerationErrorCode.Timeout, "FASHN sonucu zamanında hazır olmadı.", retryable: true);
            }

            await Task.Delay(config.PollIntervalMs, ct);
        }
    }

    // ------------------------------------------------------------------ istek

    private static string BuildRunBody(ImageInput person, ImageInput garment, ProviderRequestOptions request)
    {
        var p = request.Parameters;
        var inputs = new Dictionary<string, object>
        {
            ["model_image"] = ToDataUri(person),
            ["product_image"] = ToDataUri(garment),
            ["num_images"] = 1, // her iş tek görsel üretir (maliyet öngörülebilir)
            ["output_format"] = "jpeg",
            ["return_base64"] = true, // KVKK: FASHN'da çıktı 60 dk kalır
        };

        void CopyIfSet(string key)
        {
            if (p.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                inputs[key] = value;
            }
        }

        CopyIfSet("resolution");
        CopyIfSet("generation_mode");
        CopyIfSet("prompt");
        if (request.Seed is { } seed)
        {
            inputs["seed"] = seed;
        }
        else if (p.TryGetValue("seed", out var configuredSeed) && int.TryParse(configuredSeed, CultureInfo.InvariantCulture, out var parsed))
        {
            inputs["seed"] = parsed;
        }

        return JsonSerializer.Serialize(new Dictionary<string, object> { ["model_name"] = request.Model, ["inputs"] = inputs });
    }

    private static string ToDataUri(ImageInput image) =>
        $"data:{(string.IsNullOrWhiteSpace(image.ContentType) ? "image/jpeg" : image.ContentType)};base64,{Convert.ToBase64String(image.Content.Span)}";

    /// <param name="submitting">Ücretli <c>/v1/run</c> gönderimi: yanıt gelmezse sonuç belirsizdir (<see cref="ProviderException.SubmissionUncertain"/>).</param>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, FashnOptions config, CancellationToken ct, bool resuming = false, bool submitting = false)
    {
        using var message = new HttpRequestMessage(method, path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        if (body is not null)
        {
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.RequestTimeoutSeconds));
            response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var timedOut = new ProviderException(GenerationErrorCode.Timeout, "FASHN isteği zaman aşımına uğradı.", retryable: true);
            throw submitting ? ProviderHttp.Uncertain(timedOut) : timedOut;
        }
        catch (HttpRequestException ex)
        {
            var unreachable = new ProviderException(GenerationErrorCode.ProviderUnavailable, "FASHN'a ulaşılamadı.", retryable: true, ex);
            throw submitting ? ProviderHttp.Uncertain(unreachable) : unreachable;
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            if (resuming && response.StatusCode == HttpStatusCode.NotFound)
            {
                // Önceki tahmin artık yok (60 dk doldu): yeniden göndermek ikinci kez ücretlendirir. Otomatik denenmez.
                throw new ProviderException(GenerationErrorCode.InvalidOutput, "Önceki sonuç artık alınamıyor. Gerekirse üretimi tekrar başlatın.", retryable: false) { RequestEnded = true };
            }

            var name = await TryReadErrorNameAsync(response, ct);
            throw MapApiError((int)response.StatusCode, name);
        }
    }

    private static async Task<string?> TryReadErrorNameAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return json.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ yanıt

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new ProviderException(GenerationErrorCode.ProviderUnavailable, "FASHN yanıtı okunamadı.", retryable: true, ex);
        }
    }

    private static bool TryReadCredits(HttpResponseMessage response, out decimal credits)
    {
        credits = 0;
        return response.Headers.TryGetValues(CreditsHeader, out var values)
            && decimal.TryParse(values.FirstOrDefault(), NumberStyles.Number, CultureInfo.InvariantCulture, out credits);
    }

    private async Task<ProviderResult> BuildResultAsync(
        JsonElement root, string id, decimal? headerCredits, ProviderRequestOptions request, long elapsedMs, FashnOptions config, CancellationToken ct)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array || output.GetArrayLength() == 0)
        {
            throw new ProviderException(GenerationErrorCode.InvalidOutput, "FASHN görsel döndürmedi.", retryable: false);
        }

        var first = output[0].GetString() ?? string.Empty;
        var image = await DecodeOutputAsync(first, ct);
        var computed = costs.FashnCredits(request.Parameters);
        var credits = headerCredits ?? computed;
        var estimated = costs.FashnUsd(computed);
        var actual = headerCredits is null ? (decimal?)null : costs.FashnUsd(headerCredits.Value);
        _ = config;
        return new ProviderResult([image], ProviderName, request.Model, id, new ProviderUsage((int)elapsedMs, credits, null, null, estimated, actual));
    }

    private async Task<GeneratedOutput> DecodeOutputAsync(string value, CancellationToken ct)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',', StringComparison.Ordinal);
            if (comma > 0)
            {
                var mime = value[5..value.IndexOf(';', StringComparison.Ordinal)];
                try
                {
                    return new GeneratedOutput(Convert.FromBase64String(value[(comma + 1)..]), mime);
                }
                catch (FormatException ex)
                {
                    throw new ProviderException(GenerationErrorCode.InvalidOutput, "FASHN çıktısı çözümlenemedi.", retryable: false, ex);
                }
            }
        }

        // return_base64 isteniyor; yine de URL gelirse yalnızca FASHN CDN adreslerinden indirilir (SSRF koruması)
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && CdnHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var response = await http.GetAsync(uri, ct);
                response.EnsureSuccessStatusCode();
                return new GeneratedOutput(await response.Content.ReadAsByteArrayAsync(ct), response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderException(GenerationErrorCode.ProviderUnavailable, "FASHN çıktısı indirilemedi.", retryable: true, ex);
            }
        }

        throw new ProviderException(GenerationErrorCode.InvalidOutput, "FASHN çıktı biçimi tanınmadı.", retryable: false);
    }

    // ------------------------------------------------------------------ hata eşleme (resmî Error Handling sayfası)

    /// <summary>HTTP düzeyi hata (tahmin kimliği verilmemiştir). 400/401/404 yeniden denenmez; 429 ve 5xx denenir.</summary>
    internal static ProviderException MapApiError(int status, string? name) => (status, name) switch
    {
        (401, _) => new(GenerationErrorCode.ProviderNotConfigured, "FASHN API anahtarı geçersiz.", retryable: false),
        (429, "OutOfCredits") => new(GenerationErrorCode.QuotaExceeded, "FASHN kredisi bitti.", retryable: false),
        (429, _) => new(GenerationErrorCode.RateLimited, "FASHN istek sınırına ulaşıldı.", retryable: true),
        (400, _) => new(GenerationErrorCode.InvalidInput, "FASHN isteği geçersiz.", retryable: false),
        (404, _) => new(GenerationErrorCode.Unknown, "FASHN uç noktası bulunamadı.", retryable: false),
        ( >= 500, _) => new(GenerationErrorCode.ProviderUnavailable, "FASHN geçici olarak hizmet vermiyor.", retryable: true),
        _ => new(GenerationErrorCode.Unknown, "FASHN beklenmeyen bir hata döndürdü.", retryable: false),
    };

    /// <summary>Tahmin <c>failed</c> durumunda: hata adına göre. Tahmin sağlayıcıda kesin bitti; başarısız tahmin kredi harcamaz (FASHN belgesi).</summary>
    internal static ProviderException MapRuntimeError(JsonElement root)
    {
        var name = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("name", out var n) ? n.GetString() : null;
        return name switch
        {
            "ContentModerationError" => new(GenerationErrorCode.ContentRejected, "FASHN girdiyi içerik politikası nedeniyle reddetti.", retryable: false) { RequestEnded = true },
            "ImageLoadError" => new(GenerationErrorCode.InvalidInput, "FASHN görseli yükleyemedi.", retryable: false) { RequestEnded = true },
            "InputValidationError" => new(GenerationErrorCode.InvalidInput, "FASHN girdi doğrulaması başarısız.", retryable: false) { RequestEnded = true },
            "ThirdPartyError" or "UnavailableError" or "PipelineError" => new(GenerationErrorCode.ProviderUnavailable, "FASHN geçici olarak işleyemedi.", retryable: true) { RequestEnded = true },
            _ => new(GenerationErrorCode.Unknown, "FASHN üretimi başarısız oldu.", retryable: true) { RequestEnded = true },
        };
    }
}
