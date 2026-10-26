using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai.Abstractions;

namespace SanalProva.Infrastructure.Ai.Pruna;

/// <summary>
/// Pruna P-Image Try-On (doğrudan Pruna API). Resmî OpenAPI şeması (docs.api.pruna.ai/openapi.yaml, 2026-10-22):
/// <list type="number">
/// <item>Base64 desteklenmez: kişi ve ürün görseli <c>POST /v1/files</c> (multipart <c>content</c>) ile geçici yüklenir. Şemaya göre dosyalar
/// 30 dakika sonra silinir; ancak 2026-10-22 gerçek yanıtında <c>expires_at</c> yüklemeden <b>24 saat</b> sonraydı. Silme uç noktası <b>yoktur</b> (KVKK riski, docs/12 §4).</item>
/// <item><c>POST /v1/predictions</c>, başlıklar <c>apikey</c> + <c>Model: p-image-try-on</c>, gövde <c>{ input: { person_image, garment_images, turbo, seed, output_format, output_quality, preserve_input_size } }</c> → <c>id</c>.</item>
/// <item><c>GET /v1/predictions/status/{id}</c>: <c>starting / processing / succeeded / failed / canceled</c>; başarıda <c>generation_url</c>.</item>
/// <item>Çıktı <c>generation_url</c>'den API anahtarıyla indirilir; Pruna'da 24 saat erişilebilir kalır.</item>
/// </list>
/// İptal uç noktası belgelenmemiştir. Pruna istek başına maliyet bildirmez: tutar tahmindir (standart 0,015 $, turbo 0,008 $).
/// </summary>
public sealed class PrunaTryOnProvider(HttpClient http, IOptions<PrunaOptions> configuration, IOptionsMonitor<GenerationSettings> settings) : ITryOnProvider
{
    public const string Name = "Pruna";
    public const string ModelHeader = "p-image-try-on";
    private const string Provider = "Pruna";
    private static readonly string[] Hosts = ["api.pruna.ai"];

    public async Task<ProviderResult> TryOnAsync(ImageInput person, ImageInput garment, ProviderRequestOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(garment);
        ArgumentNullException.ThrowIfNull(options);
        var config = configuration.Value;
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new ProviderException(GenerationErrorCode.ProviderNotConfigured, "Pruna API anahtarı yapılandırılmamış.", retryable: false);
        }

        var stopwatch = Stopwatch.StartNew();
        var id = options.ResumeRequestId;
        if (string.IsNullOrEmpty(id))
        {
            // Dosya yükleme ücretsiz ön adımdır (hatası normal yeniden denenir); ücretli adım tahmin isteğidir
            var personUrl = await UploadAsync(person, "person.jpg", config, ct);
            var garmentUrl = await UploadAsync(garment, "garment.jpg", config, ct);
            if (options.OnSubmitting is { } onSubmitting)
            {
                await onSubmitting(); // gönderim niyeti kalıcı: yanıt kaybolursa iş yeniden gönderilmez
            }

            id = await SubmitAsync(personUrl, garmentUrl, options, config, ct);
            if (options.OnSubmitted is { } onSubmitted)
            {
                await onSubmitted(id);
            }
        }

        var generationUrl = await WaitAsync(id, config, ct);
        var image = await DownloadAsync(generationUrl, config, ct);
        var estimated = ProviderHttp.EstimatedCost(options, settings.CurrentValue.Pricing, person, garment, image);
        return new ProviderResult([image], Name, options.Model, id, new ProviderUsage((int)stopwatch.ElapsedMilliseconds, null, null, null, estimated, ActualCostUsd: null));
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, PrunaOptions config)
    {
        var message = new HttpRequestMessage(method, path);
        message.Headers.TryAddWithoutValidation("apikey", config.ApiKey);
        return message;
    }

    private async Task<string> UploadAsync(ImageInput image, string fileName, PrunaOptions config, CancellationToken ct)
    {
        using var message = Request(HttpMethod.Post, "v1/files", config);
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(image.Content.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(image.ContentType) ? "image/jpeg" : image.ContentType);
        // Pruna yalnızca standart biçimi (name="content"; filename="x.jpg") ayrıştırır; .NET'in varsayılanı (tırnaksız ad + filename*) 500 döndürür
        // (2026-10-22 ücretsiz yükleme denemesiyle doğrulandı)
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"content\"", FileName = $"\"{fileName}\"" };
        form.Add(content);
        message.Content = form;
        using var response = await SendCheckedAsync(message, resuming: false, config, ct);
        using var json = await ProviderHttp.ReadJsonAsync(response, Provider, ct);
        var url = json.RootElement.TryGetProperty("urls", out var urls) && urls.TryGetProperty("get", out var get) ? get.GetString() : null;
        return url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && ProviderHttp.IsAllowedHost(uri, Hosts)
            ? url
            : throw new ProviderException(GenerationErrorCode.ProviderUnavailable, "Pruna dosya adresi döndürmedi.", retryable: true);
    }

    private async Task<string> SubmitAsync(string personUrl, string garmentUrl, ProviderRequestOptions options, PrunaOptions config, CancellationToken ct)
    {
        var turbo = options.Parameters.TryGetValue("turbo", out var t) && bool.TryParse(t, out var parsed) && parsed;
        var input = new Dictionary<string, object>
        {
            ["person_image"] = personUrl,
            ["garment_images"] = new[] { garmentUrl },
            ["turbo"] = turbo,
            ["output_format"] = "jpg",
            ["output_quality"] = 95,
            ["preserve_input_size"] = true,
        };
        if (options.Seed is { } seed)
        {
            input["seed"] = seed;
        }

        using var message = Request(HttpMethod.Post, "v1/predictions", config);
        message.Headers.TryAddWithoutValidation("Model", ModelHeader); // asenkron mod: Try-Sync gönderilmez
        message.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object> { ["input"] = input }), Encoding.UTF8);
        // Pruna "application/json; charset=utf-8" başlığını 400 ("specified Content-Type is not allowed") ile reddeder (2026-10-22 doğrulandı)
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        // Pruna idempotency anahtarı ve tahmin listesi belgelemiyor: sonucu bilinmeyen gönderim yeniden yapılmaz.
        // Kesin ret: yanıtlı 4xx. Belirsiz: yanıt yok, 5xx veya kimliksiz başarılı yanıt.
        using var response = await ProviderHttp.SendSubmitAsync(http, message, config.RequestTimeoutSeconds, Provider, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = MapHttpError((int)response.StatusCode);
            throw (int)response.StatusCode >= 500 ? ProviderHttp.Uncertain(error) : error;
        }

        string? id;
        try
        {
            using var json = await ProviderHttp.ReadJsonAsync(response, Provider, ct);
            id = json.RootElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        }
        catch (ProviderException)
        {
            throw ProviderHttp.UncertainWithoutId(Provider);
        }

        return string.IsNullOrEmpty(id) ? throw ProviderHttp.UncertainWithoutId(Provider) : id;
    }

    private async Task<string> WaitAsync(string id, PrunaOptions config, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using (var message = Request(HttpMethod.Get, $"v1/predictions/status/{Uri.EscapeDataString(id)}", config))
            using (var response = await SendCheckedAsync(message, resuming: true, config, ct)) // gönderilmiş tahmin bulunamazsa yeniden gönderilmez
            using (var json = await ProviderHttp.ReadJsonAsync(response, Provider, ct))
            {
                var root = json.RootElement;
                switch (root.TryGetProperty("status", out var s) ? s.GetString() : null)
                {
                    case "succeeded":
                        var url = root.TryGetProperty("generation_url", out var g) ? g.GetString() : null;
                        return url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && ProviderHttp.IsAllowedHost(uri, Hosts)
                            ? url
                            : throw new ProviderException(GenerationErrorCode.InvalidOutput, "Pruna çıktı adresi tanınmadı.", retryable: false);
                    case "failed":
                        throw MapRuntimeError(root.TryGetProperty("error", out var e) ? e.GetString() : null);
                    case "canceled":
                        throw new ProviderException(GenerationErrorCode.ProviderUnavailable, "Pruna tahmini iptal edildi.", retryable: false) { RequestEnded = true };
                }
            }

            if ((DateTime.UtcNow - started).TotalSeconds >= config.MaxWaitSeconds)
            {
                throw new ProviderException(GenerationErrorCode.Timeout, "Pruna sonucu zamanında hazır olmadı.", retryable: true);
            }

            await Task.Delay(config.PollIntervalMs, ct);
        }
    }

    private async Task<GeneratedOutput> DownloadAsync(string url, PrunaOptions config, CancellationToken ct)
    {
        using var message = Request(HttpMethod.Get, url, config);
        using var response = await ProviderHttp.SendAsync(http, message, config.RequestTimeoutSeconds, Provider, ct);
        if (response.StatusCode == HttpStatusCode.Gone)
        {
            throw new ProviderException(GenerationErrorCode.InvalidOutput, "Pruna çıktısının süresi dolmuş.", retryable: false) { RequestEnded = true };
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ProviderException(GenerationErrorCode.ProviderUnavailable, "Pruna çıktısı indirilemedi.", retryable: true);
        }

        return new GeneratedOutput(await response.Content.ReadAsByteArrayAsync(ct), response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }

    private async Task<HttpResponseMessage> SendCheckedAsync(HttpRequestMessage message, bool resuming, PrunaOptions config, CancellationToken ct)
    {
        var response = await ProviderHttp.SendAsync(http, message, config.RequestTimeoutSeconds, Provider, ct);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            if (resuming && response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new ProviderException(GenerationErrorCode.InvalidOutput, "Pruna tahmini bulunamadı veya süresi doldu.", retryable: false) { RequestEnded = true };
            }

            throw MapHttpError((int)response.StatusCode);
        }
    }

    /// <summary>HTTP hataları (Pruna şeması: 400, 401, 404, 410, 413, 422, 500). İletiye yanıt gövdesi eklenmez.</summary>
    internal static ProviderException MapHttpError(int status) => status switch
    {
        401 or 403 => new(GenerationErrorCode.ProviderNotConfigured, "Pruna API anahtarı geçersiz veya yetkisiz.", retryable: false),
        400 or 413 or 422 => new(GenerationErrorCode.InvalidInput, "Pruna isteği geçersiz.", retryable: false),
        429 => new(GenerationErrorCode.RateLimited, "Pruna istek sınırına ulaşıldı.", retryable: true),
        >= 500 => new(GenerationErrorCode.ProviderUnavailable, "Pruna geçici olarak hizmet vermiyor.", retryable: true),
        _ => new(GenerationErrorCode.Unknown, "Pruna beklenmeyen bir hata döndürdü.", retryable: false),
    };

    /// <summary>
    /// Başarısız tahmin: yalnızca serbest metin hata verilir (şema). Tahmin sağlayıcıda kesin bitti (<see cref="ProviderException.RequestEnded"/>).
    /// Güvenlik/NSFW anahtar kelimeleri içerik reddi sayılır. Başarısız tahminin ücretlendirilip ücretlendirilmediği belgede yok (doğrulanmadı).
    /// </summary>
    internal static ProviderException MapRuntimeError(string? error)
    {
        var text = error ?? string.Empty;
        if (text.Contains("nsfw", StringComparison.OrdinalIgnoreCase) || text.Contains("safety", StringComparison.OrdinalIgnoreCase) || text.Contains("moderation", StringComparison.OrdinalIgnoreCase) || text.Contains("content policy", StringComparison.OrdinalIgnoreCase))
        {
            return new(GenerationErrorCode.ContentRejected, "Pruna içeriği güvenlik nedeniyle reddetti.", retryable: false) { RequestEnded = true };
        }

        if (text.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return new(GenerationErrorCode.Timeout, "Pruna işlemi zaman aşımına uğradı.", retryable: true) { RequestEnded = true };
        }

        return text.Contains("invalid", StringComparison.OrdinalIgnoreCase) || text.Contains("must", StringComparison.OrdinalIgnoreCase)
            ? new(GenerationErrorCode.InvalidInput, "Pruna girdiyi işleyemedi.", retryable: false) { RequestEnded = true }
            : new(GenerationErrorCode.ProviderUnavailable, "Pruna üretimi başarısız oldu.", retryable: true) { RequestEnded = true };
    }
}
