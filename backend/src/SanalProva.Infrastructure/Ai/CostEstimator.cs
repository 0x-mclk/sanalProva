using System.Globalization;
using Microsoft.Extensions.Options;

namespace SanalProva.Infrastructure.Ai;

/// <summary>Gemini <c>usage</c> nesnesinden okunan jeton sayıları (alan adları resmî Interactions API şemasından).</summary>
public sealed record GeminiUsageTokens(int? InputTokens, int? OutputImageTokens, int? OutputOtherTokens, int? ThoughtTokens);

/// <summary>
/// Tahmini maliyet hesabı. Faturalama sağlayıcıdadır; buradaki değerler yapılandırmadaki birim fiyatlardan türetilen tahmindir.
/// FASHN'da kredi tablosu kesin olduğu için tahmin ≈ gerçekleşen; Gemini'de jeton sayısı varsa jetona göre, yoksa görsel başı sabit fiyata göre.
/// </summary>
public sealed class CostEstimator(IOptionsMonitor<GenerationSettings> settings)
{
    private PricingSettings Pricing => settings.CurrentValue.Pricing;

    // ------------------------------------------------------------------ FASHN

    public static (string Mode, string Resolution) FashnKey(IReadOnlyDictionary<string, string> parameters)
    {
        var mode = parameters.TryGetValue("generation_mode", out var m) && !string.IsNullOrWhiteSpace(m) ? m.ToLowerInvariant() : "balanced";
        var resolution = parameters.TryGetValue("resolution", out var r) && !string.IsNullOrWhiteSpace(r) ? r.ToLowerInvariant() : "1k";
        return (mode, resolution);
    }

    public decimal FashnCredits(IReadOnlyDictionary<string, string> parameters, int numImages = 1)
    {
        var (mode, resolution) = FashnKey(parameters);
        // Bilinmeyen kombinasyonda en pahalı tablo değeri varsayılır (tahmin güvenli tarafta kalır)
        var perImage = Pricing.FashnCredits.TryGetValue($"{mode}:{resolution}", out var credits) ? credits : Pricing.FashnCredits.Values.DefaultIfEmpty(5m).Max();
        return perImage * numImages;
    }

    public decimal FashnUsd(decimal credits) => decimal.Round(credits * Pricing.FashnUsdPerCredit, 4);

    // ------------------------------------------------------------------ Gemini

    public static string NormalizeSize(string? size) => string.IsNullOrWhiteSpace(size) ? "2K" : size.Trim().ToUpperInvariant();

    /// <summary>Çağrı sonrası tahmin: jeton bilgisi varsa jetona göre, yoksa görsel başı sabit fiyat.</summary>
    public decimal GeminiUsd(string model, string imageSize, GeminiUsageTokens? usage)
    {
        if (!Pricing.Gemini.TryGetValue(model, out var price))
        {
            return 0m;
        }

        var size = NormalizeSize(imageSize);
        var flatImage = price.ImageUsd.TryGetValue(size, out var flat) ? flat : price.ImageUsd.Values.DefaultIfEmpty(0m).Max();
        if (usage is { InputTokens: not null, OutputImageTokens: > 0 })
        {
            var input = usage.InputTokens.Value * price.InputUsdPer1M / 1_000_000m;
            var image = usage.OutputImageTokens.Value * price.OutputImageUsdPer1M / 1_000_000m;
            var text = ((usage.OutputOtherTokens ?? 0) + (usage.ThoughtTokens ?? 0)) * price.OutputTextUsdPer1M / 1_000_000m;
            return decimal.Round(input + image + text, 4);
        }

        var inputOnly = (usage?.InputTokens ?? 0) * price.InputUsdPer1M / 1_000_000m;
        return decimal.Round(flatImage + inputOnly, 4);
    }

    /// <summary>Çağrı öncesi en kötü durum tahmini (bütçe kapısı için): görsel çıktısı + giriş görselleri + düşünme jetonları.</summary>
    public decimal GeminiWorstCaseUsd(string model, string imageSize, int inputImages)
    {
        if (!Pricing.Gemini.TryGetValue(model, out var price))
        {
            return 0m;
        }

        var size = NormalizeSize(imageSize);
        var image = price.ImageUsd.TryGetValue(size, out var flat) ? flat : price.ImageUsd.Values.DefaultIfEmpty(0m).Max();
        var input = inputImages * Pricing.AssumedTokensPerInputImage * price.InputUsdPer1M / 1_000_000m;
        var thought = Pricing.AssumedThoughtTokens * price.OutputTextUsdPer1M / 1_000_000m;
        return decimal.Round(image + input + thought, 4);
    }

    /// <summary>Sağlayıcı ve seçeneklere göre tek bir çağrının en kötü durum maliyeti. Mock = 0.</summary>
    public decimal WorstCasePerCallUsd(string provider, string model, IReadOnlyDictionary<string, string> options, int inputImages) => provider switch
    {
        "Fashn" => FashnUsd(FashnCredits(options)),
        "Gemini" => GeminiWorstCaseUsd(model, options.TryGetValue("image_size", out var s) ? s : null!, inputImages),
        _ => 0m,
    };

    public static string Format(decimal usd) => usd.ToString("0.0000", CultureInfo.InvariantCulture);
}
