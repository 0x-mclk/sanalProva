using System.Diagnostics;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai.Abstractions;

namespace SanalProva.Infrastructure.Ai.Fal;

/// <summary>
/// fal.ai üzerindeki görsel düzenleme modelleriyle tek ürün giydirme. Ortak akış: girdi (Görsel 1 = kişi, Görsel 2 = ürün; data URI) →
/// kuyruğa gönder → istek kimliği hemen bildirilir (<see cref="ProviderRequestOptions.OnSubmitted"/>) → yokla → sonucu al.
/// Önceki denemede gönderilmiş istek varsa (<see cref="ProviderRequestOptions.ResumeRequestId"/>) yeni (ücretli) istek açılmaz.
/// Prompt sunucuda üretilir ve <c>prompt</c> parametresiyle gelir (personel yazmaz). fal istek başına maliyet bildirmez: tutar tahmindir.
/// </summary>
public abstract class FalTryOnProviderBase(FalQueueClient client, IOptionsMonitor<GenerationSettings> settings) : ITryOnProvider
{
    protected abstract string ProviderName { get; }

    /// <summary>Modelin OpenAPI şemasına göre girdi gövdesi.</summary>
    protected abstract Dictionary<string, object> BuildInput(string prompt, ImageInput person, ImageInput garment, ProviderRequestOptions options);

    public async Task<ProviderResult> TryOnAsync(ImageInput person, ImageInput garment, ProviderRequestOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(garment);
        ArgumentNullException.ThrowIfNull(options);
        client.EnsureConfigured();
        if (!options.Parameters.TryGetValue("prompt", out var prompt) || string.IsNullOrWhiteSpace(prompt))
        {
            throw new ProviderException(GenerationErrorCode.InvalidInput, "Giydirme talimatı (prompt) üretilmedi.", retryable: false);
        }

        var input = BuildInput(prompt, person, garment, options);
        var stopwatch = Stopwatch.StartNew();
        var requestId = options.ResumeRequestId;
        if (string.IsNullOrEmpty(requestId))
        {
            requestId = await client.SubmitAsync(options.Model, input, ct);
            if (options.OnSubmitted is { } onSubmitted)
            {
                await onSubmitted(requestId);
            }
        }

        try
        {
            using var result = await client.WaitForResultAsync(options.Model, requestId, resuming: options.ResumeRequestId is not null, ct);
            var image = await client.ReadFirstImageAsync(result.RootElement, ct);
            var estimated = ProviderHttp.EstimatedCost(options, settings.CurrentValue.Pricing, person, garment, image); // gerçek çıktı boyutuyla
            return new ProviderResult([image], ProviderName, options.Model, requestId, new ProviderUsage((int)stopwatch.ElapsedMilliseconds, null, null, null, estimated, ActualCostUsd: null));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Personel iptal etti veya uygulama kapanıyor: fal tarafında işi durdurmayı dene (en iyi çaba)
            await client.TryCancelAsync(options.Model, requestId);
            throw;
        }
    }

    protected static string Required(ProviderRequestOptions options, string key, string fallback, IReadOnlyCollection<string> allowed, string provider)
    {
        var value = options.Parameters.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;
        return allowed.Contains(value)
            ? value
            : throw new ProviderException(GenerationErrorCode.InvalidInput, $"{provider}: '{key}' değeri desteklenmiyor.", retryable: false);
    }
}

/// <summary>
/// Seedream 5 Flash Edit (<c>bytedance/seedream/v5/flash/edit</c>). Şema (2026-10-22): <c>prompt</c>, <c>image_urls</c> (≤ 10),
/// <c>image_size</c> (enum veya özel; toplam piksel 1024² – 2048²), <c>num_images</c> 1–6, <c>output_format</c> jpeg/png, <c>sync_mode</c>,
/// <c>enable_safety_checker</c>. Tohum (seed) girdisi yoktur. Fiyat: 0,027 $/görsel (fal model sayfası).
/// </summary>
public sealed class FalSeedreamTryOnProvider(FalQueueClient client, IOptionsMonitor<GenerationSettings> settings) : FalTryOnProviderBase(client, settings)
{
    public const string Name = "FalSeedream";

    private static readonly string[] Sizes = ["square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9", "auto_1K", "auto_2K"];

    protected override string ProviderName => Name;

    protected override Dictionary<string, object> BuildInput(string prompt, ImageInput person, ImageInput garment, ProviderRequestOptions options) => new()
    {
        ["prompt"] = prompt,
        ["image_urls"] = new[] { ProviderHttp.DataUri(person), ProviderHttp.DataUri(garment) },
        ["image_size"] = Required(options, "image_size", "auto_2K", Sizes, "Seedream"),
        ["num_images"] = ProviderHttp.NumImages(options), // maliyet görsel sayısıyla çarpılır; akış ilk görseli kullanır
        ["output_format"] = "jpeg",
        ["sync_mode"] = true, // çıktı data URI olarak döner, fal istek geçmişinde tutulmaz
        ["enable_safety_checker"] = true,
    };
}

/// <summary>
/// GPT Image 2.5 Sunburst Edit (<c>openai/gpt-image-2.5/sunburst/edit</c>). Şema (2026-10-22): <c>prompt</c>, <c>image_urls</c> (≤ 16),
/// <c>quality</c> auto/low/medium/high/xhigh/max (varsayılan <b>high</b>), <c>image_size</c> (varsayılan auto), <c>num_images</c>, <c>output_format</c>,
/// <c>output_compression</c>, <c>sync_mode</c>, <c>mask_url</c>, <c>background</c>. Maliyet kaliteye göre çok değişir: kalite her zaman açıkça
/// gönderilir (şemanın varsayılanı high'a düşülmez). Altı değerin tamamı desteklenir; hangilerinin kullanılabileceğine yönetici karar verir
/// (<c>auto</c>, <c>xhigh</c>, <c>max</c> varsayılan olarak kapalıdır; <c>auto</c> maliyeti max sütununa çıkabilir).
/// </summary>
public sealed class FalGptImageTryOnProvider(FalQueueClient client, IOptionsMonitor<GenerationSettings> settings) : FalTryOnProviderBase(client, settings)
{
    public const string Name = "FalGptImage";

    private static readonly string[] Sizes = ["auto", "square_hd", "square", "portrait_4_3", "portrait_16_9", "landscape_4_3", "landscape_16_9"];

    protected override string ProviderName => Name;

    protected override Dictionary<string, object> BuildInput(string prompt, ImageInput person, ImageInput garment, ProviderRequestOptions options) => new()
    {
        ["prompt"] = prompt,
        ["image_urls"] = new[] { ProviderHttp.DataUri(person), ProviderHttp.DataUri(garment) },
        ["quality"] = Required(options, "quality", "medium", TryOn.GptImagePricing.Qualities, "GPT Image"),
        ["image_size"] = Required(options, "image_size", "auto", Sizes, "GPT Image"),
        ["num_images"] = ProviderHttp.NumImages(options), // maliyet görsel sayısıyla çarpılır; akış ilk görseli kullanır
        ["output_format"] = "jpeg",
        ["output_compression"] = 92,
        ["sync_mode"] = true,
    };
}
