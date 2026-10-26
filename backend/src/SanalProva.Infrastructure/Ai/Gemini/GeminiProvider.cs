using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai.Abstractions;
using SkiaSharp;

namespace SanalProva.Infrastructure.Ai.Gemini;

/// <summary>
/// Gemini görsel modelleri (Nano Banana ailesi) — Interactions API (<c>POST /v1beta/interactions</c>). Resmî dokümana göre
/// (ai.google.dev/gemini-api/docs/image-generation, 2026-10-20) görsel üretimi bu uç noktayla yapılır; girdi <c>input</c> dizisinde
/// <c>text</c> + <c>image</c> (<c>mime_type</c>, base64 <c>data</c>) parçalarıdır, çıktı <c>steps[model_output].content[image].data</c>'dadır.
/// <list type="bullet">
/// <item><c>store: false</c>: istek/yanıt Google tarafında saklanmaz; müşteri görseli kalıcı kayda girmez.</item>
/// <item><c>Api-Revision</c> sabitlenir (yanıt şeması değişikliğine karşı).</item>
/// <item>Düşünme (thought) adımlarındaki ara görseller yok sayılır; yalnızca <c>model_output</c> görseli kullanılır.</item>
/// <item>Aynı sınıf hem kombin (<see cref="IImageGenerationProvider"/>) hem düzenleme (<see cref="IImageEditProvider"/>) yapar.</item>
/// </list>
/// Hata iletileri sabittir; gövde, anahtar veya görsel iletiye/günlüğe girmez.
/// </summary>
public sealed class GeminiProvider(HttpClient http, IOptions<GeminiOptions> configuration, CostEstimator costs) : IImageGenerationProvider, IImageEditProvider
{
    public const string ProviderName = "Gemini";

    public Task<ProviderResult> ComposeOutfitAsync(
        ImageInput person, IReadOnlyList<GarmentReference> garments, string prompt, ProviderRequestOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(garments);
        if (garments.Count is 0 or > 5)
        {
            throw new ProviderException(GenerationErrorCode.InvalidInput, "Kombin 1 ile 5 parça arasında olmalı.", retryable: false);
        }

        // Giriş sırası prompt ile aynıdır: Görsel 1 = kişi, Görsel 2..N = katman sırasındaki ürünler
        var images = new List<ImageInput> { person };
        images.AddRange(garments.OrderBy(g => g.LayerOrder).Select(g => g.Image));
        return RunAsync(prompt, images, options, ct);
    }

    public Task<ProviderResult> EditAsync(
        ImageInput baseImage, string instruction, IReadOnlyList<ImageInput> references, ProviderRequestOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseImage);
        ArgumentNullException.ThrowIfNull(references);
        var images = new List<ImageInput> { baseImage };
        images.AddRange(references);
        return RunAsync(instruction, images, options, ct);
    }

    private async Task<ProviderResult> RunAsync(string prompt, List<ImageInput> images, ProviderRequestOptions request, CancellationToken ct)
    {
        var config = configuration.Value;
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new ProviderException(GenerationErrorCode.ProviderNotConfigured, "Gemini API anahtarı yapılandırılmamış.", retryable: false);
        }

        var info = ModelCatalog.Find(ProviderName, request.Model)
            ?? throw new ProviderException(GenerationErrorCode.ProviderNotConfigured, "Gemini model kimliği doğrulanmış listede yok.", retryable: false);
        var size = CostEstimator.NormalizeSize(request.Parameters.TryGetValue("image_size", out var s) ? s : null);
        if (!info.Resolutions.Contains(size, StringComparer.Ordinal))
        {
            throw new ProviderException(GenerationErrorCode.InvalidInput, $"{info.DisplayName} {size} çözünürlüğü desteklemiyor.", retryable: false);
        }

        var body = BuildBody(prompt, images, request, size, config);
        var stopwatch = Stopwatch.StartNew();

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1beta/interactions") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        message.Headers.Add("x-goog-api-key", config.ApiKey);
        message.Headers.Add("Api-Revision", config.ApiRevision);

        // Senkron çağrı: yanıt sonucu da taşır, sorgulanacak istek kimliği yoktur (store:false). Yanıt gelmezse, 5xx gelirse veya başarılı yanıt
        // okunamazsa istek işlenmiş ve ücretlendirilmiş olabilir: sonuç belirsizdir, yeniden gönderilmez
        if (request.OnSubmitting is { } onSubmitting)
        {
            await onSubmitting();
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
            throw ProviderHttp.Uncertain(new ProviderException(GenerationErrorCode.Timeout, "Gemini isteği zaman aşımına uğradı.", retryable: true));
        }
        catch (HttpRequestException ex)
        {
            throw ProviderHttp.Uncertain(new ProviderException(GenerationErrorCode.ProviderUnavailable, "Gemini'ye ulaşılamadı.", retryable: true, ex));
        }

        using (response)
        {
            JsonDocument json;
            try
            {
                json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            }
            catch (JsonException ex)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw Classified((int)response.StatusCode, MapHttpError((int)response.StatusCode, null));
                }

                throw ProviderHttp.Uncertain(new ProviderException(GenerationErrorCode.ProviderUnavailable, "Gemini yanıtı okunamadı.", retryable: true, ex));
            }

            using (json)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw Classified((int)response.StatusCode, MapHttpError((int)response.StatusCode, ReadErrorStatus(json.RootElement)));
                }

                return ParseResult(json.RootElement, request, size, images.Count, (int)stopwatch.ElapsedMilliseconds);
            }
        }
    }

    // ------------------------------------------------------------------ istek

    internal static string BuildBody(string prompt, IReadOnlyList<ImageInput> images, ProviderRequestOptions request, string size, GeminiOptions config)
    {
        var total = images.Sum(i => (long)i.Content.Length);
        if (total * 4 / 3 > config.MaxInlineBytes)
        {
            throw new ProviderException(GenerationErrorCode.InvalidInput, "Görseller tek istek için çok büyük.", retryable: false);
        }

        var input = new List<object> { new Dictionary<string, object> { ["type"] = "text", ["text"] = prompt } };
        foreach (var image in images)
        {
            input.Add(new Dictionary<string, object>
            {
                ["type"] = "image",
                ["mime_type"] = string.IsNullOrWhiteSpace(image.ContentType) ? "image/jpeg" : image.ContentType,
                ["data"] = Convert.ToBase64String(image.Content.Span),
            });
        }

        var responseFormat = new Dictionary<string, object> { ["type"] = "image", ["image_size"] = size };
        var aspect = request.Parameters.TryGetValue("aspect_ratio", out var configured) && !string.IsNullOrWhiteSpace(configured)
            ? configured
            : AspectRatioFor(images[0]);
        if (aspect is not null)
        {
            responseFormat["aspect_ratio"] = aspect;
        }

        var body = new Dictionary<string, object>
        {
            ["model"] = request.Model,
            ["input"] = input,
            ["response_format"] = responseFormat,
            ["store"] = false, // müşteri görseli Google tarafında saklanmasın
        };

        var generation = new Dictionary<string, object>();
        if (request.Parameters.TryGetValue("thinking_level", out var thinking) && !string.IsNullOrWhiteSpace(thinking))
        {
            generation["thinking_level"] = thinking;
        }

        if (request.Seed is { } seed)
        {
            generation["seed"] = seed;
        }

        if (generation.Count > 0)
        {
            body["generation_config"] = generation;
        }

        return JsonSerializer.Serialize(body);
    }

    /// <summary>Kişi fotoğrafının oranına en yakın desteklenen dikey/kare oranı (çıktı çerçevesi girdiyle aynı kalsın).</summary>
    internal static string? AspectRatioFor(ImageInput person)
    {
        try
        {
            using var data = SKData.CreateCopy(person.Content.Span);
            using var codec = SKCodec.Create(data);
            if (codec is null || codec.Info.Height == 0)
            {
                return null;
            }

            var ratio = (double)codec.Info.Width / codec.Info.Height;
            var options = new (string Name, double Value)[] { ("9:16", 9d / 16), ("2:3", 2d / 3), ("3:4", 3d / 4), ("4:5", 4d / 5), ("1:1", 1d), ("5:4", 5d / 4), ("4:3", 4d / 3), ("3:2", 1.5), ("16:9", 16d / 9) };
            return options.OrderBy(o => Math.Abs(o.Value - ratio)).First().Name;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ yanıt

    internal ProviderResult ParseResult(JsonElement root, ProviderRequestOptions request, string size, int inputImages, int elapsedMs)
    {
        _ = inputImages;
        var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var usage = ReadUsage(root);
        var cost = costs.GeminiUsd(request.Model, size, usage);

        var status = root.TryGetProperty("status", out var st) ? st.GetString() : null;
        if (status is "failed" or "cancelled")
        {
            throw MapInteractionErrors(root, retryableByDefault: true);
        }

        var image = FindOutputImage(root);
        if (image is null)
        {
            // Görsel yok: güvenlik engeli/ret ya da yarım sonuç. Giriş jetonları ücretlendirilmiş olabilir.
            var hasText = HasModelText(root);
            throw status == "incomplete" || !hasText
                ? new ProviderException(GenerationErrorCode.InvalidOutput, "Gemini görsel döndürmedi.", retryable: false) { ChargedUsd = cost, ProviderRequestId = id }
                : new ProviderException(GenerationErrorCode.ContentRejected, "Gemini isteği görselle yanıtlamadı (içerik/güvenlik).", retryable: false) { ChargedUsd = cost, ProviderRequestId = id };
        }

        var actual = (decimal?)null; // Gemini fatura bilgisini yanıtta vermez: yalnızca tahmin
        var tokensIn = usage?.InputTokens;
        var tokensOut = (usage?.OutputImageTokens ?? 0) + (usage?.OutputOtherTokens ?? 0) + (usage?.ThoughtTokens ?? 0);
        return new ProviderResult([image], ProviderName, request.Model, id, new ProviderUsage(elapsedMs, null, tokensIn, tokensOut == 0 ? null : tokensOut, cost, actual));
    }

    private static GeneratedOutput? FindOutputImage(JsonElement root)
    {
        if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        GeneratedOutput? last = null;
        foreach (var step in steps.EnumerateArray())
        {
            // Yalnızca nihai model çıktısı; "thought" adımlarındaki ara görseller sayılmaz
            if (!step.TryGetProperty("type", out var type) || type.GetString() != "model_output" || !step.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var blockType) && blockType.GetString() == "image" && block.TryGetProperty("data", out var data) && data.GetString() is { Length: > 0 } base64)
                {
                    try
                    {
                        var mime = block.TryGetProperty("mime_type", out var m) ? m.GetString() ?? "image/png" : "image/png";
                        last = new GeneratedOutput(Convert.FromBase64String(base64), mime);
                    }
                    catch (FormatException)
                    {
                        throw new ProviderException(GenerationErrorCode.InvalidOutput, "Gemini görsel verisi çözümlenemedi.", retryable: false);
                    }
                }
            }
        }

        return last;
    }

    private static bool HasModelText(JsonElement root)
    {
        if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return steps.EnumerateArray().Any(step =>
            step.TryGetProperty("type", out var type) && type.GetString() == "model_output"
            && step.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            && content.EnumerateArray().Any(b => b.TryGetProperty("type", out var bt) && bt.GetString() == "text"));
    }

    internal static GeminiUsageTokens? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

        var imageTokens = 0;
        if (usage.TryGetProperty("output_tokens_by_modality", out var byModality) && byModality.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in byModality.EnumerateArray())
            {
                if (entry.TryGetProperty("modality", out var modality) && string.Equals(modality.GetString(), "image", StringComparison.OrdinalIgnoreCase))
                {
                    imageTokens += Int(entry, "tokens") ?? 0;
                }
            }
        }

        var total = Int(usage, "total_output_tokens");
        var thought = Int(usage, "total_thought_tokens");
        int? other = total is null ? null : Math.Max(0, total.Value - imageTokens);
        return new GeminiUsageTokens(Int(usage, "total_input_tokens"), imageTokens == 0 ? null : imageTokens, other, thought);
    }

    // ------------------------------------------------------------------ hata eşleme

    private static string? ReadErrorStatus(JsonElement root) =>
        root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("status", out var status) ? status.GetString() : null;

    /// <summary>5xx: istek işlenmiş olabilir (senkron çağrıda kimlik yok): sonucu belirsiz. 4xx: kesin ret.</summary>
    private static ProviderException Classified(int status, ProviderException error) => status >= 500 ? ProviderHttp.Uncertain(error) : error;

    /// <summary>Google API HTTP hataları. 400/403/404 yeniden denenmez; 429 ve 5xx denenir. Yanıt gövdesi iletiye eklenmez.</summary>
    internal static ProviderException MapHttpError(int status, string? googleStatus) => (status, googleStatus) switch
    {
        (401 or 403, _) => new(GenerationErrorCode.ProviderNotConfigured, "Gemini API anahtarı geçersiz veya yetkisiz.", retryable: false),
        (404, _) => new(GenerationErrorCode.ProviderNotConfigured, "Gemini modeli veya uç noktası bulunamadı.", retryable: false),
        (429, _) => new(GenerationErrorCode.RateLimited, "Gemini kota/istek sınırına ulaşıldı.", retryable: true),
        (400, "FAILED_PRECONDITION") => new(GenerationErrorCode.ProviderNotConfigured, "Gemini bu bölgede/hesapta kullanılamıyor.", retryable: false),
        (400, _) => new(GenerationErrorCode.InvalidInput, "Gemini isteği geçersiz.", retryable: false),
        (408 or 504, _) => new(GenerationErrorCode.Timeout, "Gemini zaman aşımı.", retryable: true),
        ( >= 500, _) => new(GenerationErrorCode.ProviderUnavailable, "Gemini geçici olarak hizmet vermiyor.", retryable: true),
        _ => new(GenerationErrorCode.Unknown, "Gemini beklenmeyen bir hata döndürdü.", retryable: false),
    };

    private static ProviderException MapInteractionErrors(JsonElement root, bool retryableByDefault)
    {
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var error in errors.EnumerateArray())
            {
                var code = error.TryGetProperty("code", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                if (code.Contains("safety", StringComparison.OrdinalIgnoreCase) || code.Contains("blocked", StringComparison.OrdinalIgnoreCase) || code.Contains("prohibited", StringComparison.OrdinalIgnoreCase))
                {
                    return new ProviderException(GenerationErrorCode.ContentRejected, "Gemini isteği güvenlik nedeniyle engelledi.", retryable: false);
                }
            }
        }

        return new ProviderException(GenerationErrorCode.ProviderUnavailable, "Gemini etkileşimi tamamlanamadı.", retryableByDefault);
    }
}
