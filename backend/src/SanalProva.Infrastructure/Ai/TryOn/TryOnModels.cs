using SanalProva.Domain.Tenancy;

namespace SanalProva.Infrastructure.Ai.TryOn;

/// <param name="Key">Kalite anahtarı (ör. <c>medium</c>); sağlayıcıya giden değerle aynıdır.</param>
/// <param name="FlatUsd">Sabit fiyatlı modellerde görsel başına tutar; jeton fiyatlı modellerde (GPT Image) null — tutar <see cref="GptImagePricing"/> ile hesaplanır.</param>
/// <param name="Parameters">Sağlayıcıya giden kaliteye özgü parametreler.</param>
/// <param name="EnabledByDefault">Yönetici ayrıca seçmedikçe personele açık mı (pahalı kaliteler kapalı başlar).</param>
/// <param name="HighCost">Arayüzde "yüksek maliyet" uyarısı gösterilir.</param>
/// <param name="MeasuredUsd">Proje sahibinin gerçek faturadan ölçtüğü tutar (bilgi amaçlı; tahmin formülü değiştirmez).</param>
public sealed record TryOnQuality(
    string Key, string Label, decimal? FlatUsd, IReadOnlyDictionary<string, string> Parameters,
    bool EnabledByDefault = true, bool HighCost = false, decimal? MeasuredUsd = null);

public enum TryOnPricingKind
{
    /// <summary>Görsel başına sabit fiyat.</summary>
    Flat,

    /// <summary>Jeton fiyatı: kalite × çıktı çözünürlüğü + referans görseller + prompt (<see cref="GptImagePricing"/>).</summary>
    GptImageTokens,
}

/// <param name="Key">Kalıcı model anahtarı (işe ve ayarlara yazılır).</param>
/// <param name="Provider">Anahtarlı DI sağlayıcı adı (<c>FalSeedream</c>, <c>FalGptImage</c>, <c>Pruna</c>).</param>
/// <param name="ModelId">Sağlayıcıdaki model/uç nokta kimliği.</param>
/// <param name="Legacy">Eski model: yönetici/personel seçemez, kod korunur.</param>
public sealed record TryOnModel(
    string Key, string Provider, string ModelId, string DisplayName, string Note, IReadOnlyList<TryOnQuality> Qualities, string DefaultQuality,
    bool Legacy, bool LiveVerified, string PriceSource, TryOnPricingKind Pricing = TryOnPricingKind.Flat)
{
    public TryOnQuality? Quality(string key) => Qualities.FirstOrDefault(q => q.Key == key);
}

public sealed record ImageDimensions(int Width, int Height)
{
    public long Pixels => (long)Width * Height;
}

/// <summary>Maliyet hesabının girdileri: sağlayıcıya giden görseller, istenen görsel sayısı, prompt uzunluğu ve (biliniyorsa) gerçek çıktı boyutu.</summary>
/// <param name="Person">Kişi fotoğrafı; <c>image_size: auto</c> iken çıktı boyutu bundan türetilir.</param>
/// <param name="References">Sağlayıcıya gönderilen tüm girdi görselleri (kişi + ürün); her biri ayrıca ücretlendirilir.</param>
/// <param name="Output">Çağrı sonrası gerçek çıktı boyutu (tahmin bunu kullanır).</param>
public sealed record TryOnCostInput(ImageDimensions? Person, IReadOnlyList<ImageDimensions> References, int NumImages = 1, int PromptChars = 0, ImageDimensions? Output = null)
{
    /// <summary>Gösterim için tipik istek: 3:4 telefon fotoğrafı (yükleme sonrası 1536×2048), 1200×1600 ürün görseli, orta uzunlukta prompt.</summary>
    public static readonly TryOnCostInput Typical = new(new(1536, 2048), [new(1536, 2048), new(1200, 1600)], 1, 1200);

    /// <summary>Gösterim için üst sınır: yükleme sınırlarındaki en büyük girdiler (müşteri 2048, katalog 1600 uzun kenar) ve uzun prompt.</summary>
    public static readonly TryOnCostInput UpperBound = new(new(2048, 2048), [new(2048, 2048), new(1600, 1600)], 1, 3000);
}

/// <summary>
/// Tek ürün (tek kaftan / hazır takım) giydirme modelleri. Uç nokta kimlikleri ve girdi şemaları 2026-10-22'de resmî OpenAPI şemalarından
/// (fal: <c>queue.fal.run</c> şeması, Pruna: <c>docs.api.pruna.ai/openapi.yaml</c>) doğrulandı; hiçbiri bu projede canlı çağrıyla doğrulanmadı.
/// Fiyatlar: Seedream fal model sayfası (0,027 $/görsel); GPT Image 2.5 fal model sayfasındaki kalite × çözünürlük tablosu (<see cref="GptImagePricing"/>);
/// Pruna resmî sayfa (standart 0,015 $ ilk parça, turbo 0,008 $). Tutarlar yapılandırmadan (<c>Generation:Pricing:TryOn*</c>) ezilebilir.
/// </summary>
public static class TryOnModels
{
    public const string Seedream = "seedream-5-flash";
    public const string GptImage = "gpt-image-2.5";
    public const string Pruna = "pruna-tryon";
    public const string Fashn = "fashn-tryon-max";

    /// <summary>Sistem varsayılanı (yönetici panelinden değiştirilebilir).</summary>
    public const string DefaultModel = Seedream;

    private static Dictionary<string, string> P(params (string Key, string Value)[] items) => items.ToDictionary(i => i.Key, i => i.Value);

    private static TryOnQuality Gpt(string key, string label, bool enabledByDefault, bool highCost, decimal? measured) =>
        new(key, label, null, P(("quality", key), ("image_size", "auto")), enabledByDefault, highCost, measured);

    public static readonly IReadOnlyList<TryOnModel> All =
    [
        new(Seedream, "FalSeedream", "bytedance/seedream/v5/flash/edit", "Seedream 5 Flash", "Varsayılan model. Tek fiyat (görsel başına).",
            [new("standard", "Standart (auto 2K)", 0.027m, P(("image_size", "auto_2K")))], "standard",
            Legacy: false, LiveVerified: false, PriceSource: "fal.ai model sayfası: 0,027 $/görsel (2026-10-22)"),
        new(GptImage, "FalGptImage", "openai/gpt-image-2.5/sunburst/edit", "GPT Image 2.5",
            "Güçlü ve ekonomik alternatif; varsayılan kalite medium. auto, xhigh ve max yalnızca yönetici açarsa kullanılabilir (auto maliyeti max'a çıkabilir).",
            [
                Gpt("auto", "Otomatik (model seçer)", enabledByDefault: false, highCost: true, measured: null),
                Gpt("low", "Düşük", enabledByDefault: true, highCost: false, measured: 0.0135m),
                Gpt("medium", "Orta", enabledByDefault: true, highCost: false, measured: 0.0173m),
                Gpt("high", "Yüksek", enabledByDefault: true, highCost: false, measured: 0.0391m),
                Gpt("xhigh", "Çok yüksek", enabledByDefault: false, highCost: true, measured: null),
                Gpt("max", "Maksimum", enabledByDefault: false, highCost: true, measured: null),
            ], "medium",
            Legacy: false, LiveVerified: false,
            PriceSource: "fal.ai model sayfası (2026-10-22): kalite × çözünürlük çıktı tablosu + referans görsel başına ≈ 0,008–0,012 $ + prompt 5 $/1M jeton. Ölçülen (image_size auto): low 0,0135 / medium 0,0173 / high ort. 0,0391 $",
            Pricing: TryOnPricingKind.GptImageTokens),
        new(Pruna, "Pruna", "p-image-try-on", "Pruna P-Image Try-On", "Ekonomik alternatif. Görseller Pruna'ya geçici dosya olarak yüklenir (30 dk sonra otomatik silinir).",
            [
                new("standard", "Standart", 0.015m, P(("turbo", "false"))),
                new("turbo", "Turbo", 0.008m, P(("turbo", "true"))),
            ], "standard",
            Legacy: false, LiveVerified: false, PriceSource: "Pruna resmî sayfa: standart 0,015 $ (ilk parça), turbo 0,008 $/parça"),
        new(Fashn, "Fashn", "tryon-max", "FASHN Try-On Max (eski)", "Pasif: seçilemez; kod ve testler korunur.",
            [new("balanced-1k", "Balanced 1K", 0.15m, P(("resolution", "1k"), ("generation_mode", "balanced")))], "balanced-1k",
            Legacy: true, LiveVerified: false, PriceSource: "FASHN: 2 kredi × 0,075 $"),
    ];

    public static TryOnModel? Find(string? key) => key is null ? null : All.FirstOrDefault(m => m.Key == key);

    public static IEnumerable<TryOnModel> Selectable => All.Where(m => !m.Legacy);

    /// <summary>İşlem geçmişi ve kayıtlarda gösterilen ad (ör. "GPT Image 2.5 · Orta").</summary>
    public static string? Describe(string? modelKey, string? quality)
    {
        if (Find(modelKey) is not { } model)
        {
            return null;
        }

        var tier = model.Quality(quality ?? model.DefaultQuality);
        return model.Qualities.Count > 1 && tier is not null ? $"{model.DisplayName} · {tier.Label}" : model.DisplayName;
    }

    /// <summary>
    /// Tahmini ve en kötü durum maliyeti (istek başına, USD). <paramref name="input"/> verilmezse <see cref="TryOnCostInput.Typical"/> kullanılır.
    /// Yapılandırmada "model:kalite" ezmesi varsa görsel başına sabit tutar olarak uygulanır. En kötü durum hiçbir zaman tahminden küçük değildir.
    /// </summary>
    public static (decimal Estimated, decimal WorstCase) Price(TryOnModel model, TryOnQuality quality, PricingSettings pricing, TryOnCostInput? input = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(pricing);
        input ??= TryOnCostInput.Typical;
        var images = Math.Max(1, input.NumImages);

        var (estimated, worst) = model.Pricing == TryOnPricingKind.GptImageTokens
            ? GptImagePricing.Estimate(quality.Key, quality.Parameters.TryGetValue("image_size", out var size) ? size : "auto", input)
            : (quality.FlatUsd!.Value * images, quality.FlatUsd!.Value * images);

        var key = $"{model.Key}:{quality.Key}";
        if (pricing.TryOnEstimatedUsd.TryGetValue(key, out var e))
        {
            estimated = e * images;
        }

        if (pricing.TryOnWorstCaseUsd.TryGetValue(key, out var w))
        {
            worst = w * images;
        }

        return (estimated, Math.Max(estimated, worst));
    }

    /// <summary>Gösterim (yönetici/personel ekranı): tipik istek tahmini ve yükleme sınırlarına göre en kötü durum.</summary>
    public static (decimal Estimated, decimal WorstCase) DisplayPrice(TryOnModel model, TryOnQuality quality, PricingSettings pricing)
    {
        var estimated = Price(model, quality, pricing, TryOnCostInput.Typical).Estimated;
        var worst = Price(model, quality, pricing, TryOnCostInput.UpperBound).WorstCase;
        return (estimated, Math.Max(estimated, worst));
    }
}

/// <summary>
/// GPT Image 2.5 (fal) maliyet hesabı. Kaynak: fal model sayfası "Pricing" (2026-10-22):
/// toplam = çıktı (kalite × boyut tablosu) × görsel sayısı + her referans görsel (girdi jetonu; ≤ 1024² ≈ 0,008 $, 3840×2160 ≈ 0,012 $)
/// + prompt (5 $/1M jeton). Toplam 0,0001 $'a yukarı yuvarlanır. <c>quality: auto</c> iken model kaliteyi seçer ve maliyet max sütununa çıkabilir.
/// Varsayımlar (canlı testte doğrulanacak): <c>image_size: auto</c> çıktısı kişi fotoğrafının boyutunu izler; tablo dışı boyutlar piksel sayısına
/// göre bir üst satırdan (gerekirse orantılı büyütülerek) fiyatlanır; prompt ≈ 4 karakter/jeton.
/// </summary>
public static class GptImagePricing
{
    public static readonly IReadOnlyList<string> Qualities = ["auto", "low", "medium", "high", "xhigh", "max"];

    private sealed record Row(int Width, int Height, decimal Low, decimal Medium, decimal High, decimal XHigh, decimal Max)
    {
        public long Pixels => (long)Width * Height;

        public decimal For(string quality) => quality switch
        {
            "low" => Low,
            "medium" => Medium,
            "high" => High,
            "xhigh" => XHigh,
            "max" => Max,
            _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Bilinmeyen GPT Image kalitesi."),
        };
    }

    // Çıktı fiyatı (tek görsel; referans ve prompt hariç)
    private static readonly Row[] Rows =
    [
        new(1024, 768, 0.00402m, 0.00903m, 0.03612m, 0.06420m, 0.14445m),
        new(1024, 1024, 0.00588m, 0.01317m, 0.05268m, 0.09366m, 0.21072m),
        new(1024, 1536, 0.00474m, 0.01029m, 0.04116m, 0.07377m, 0.16464m),
        new(1920, 1072, 0.00441m, 0.00951m, 0.03945m, 0.07014m, 0.15780m),
        new(2560, 1440, 0.00615m, 0.01434m, 0.05529m, 0.09828m, 0.22110m),
        new(3840, 2160, 0.01113m, 0.02595m, 0.10008m, 0.17790m, 0.40026m),
    ];

    private static readonly Dictionary<string, ImageDimensions> Presets = new()
    {
        ["square_hd"] = new(1024, 1024),
        ["square"] = new(512, 512),
        ["portrait_4_3"] = new(768, 1024),
        ["portrait_16_9"] = new(576, 1024),
        ["landscape_4_3"] = new(1024, 768),
        ["landscape_16_9"] = new(1024, 576),
    };

    private const decimal ReferenceSmallUsd = 0.008m;   // ≤ 1024×1024
    private const decimal ReferenceLargeUsd = 0.012m;   // 3840×2160
    private const decimal PromptUsdPerToken = 5m / 1_000_000m;
    private const decimal PromptWorstUsd = 0.003m;      // ≈ 500 kelime (fal sayfası)
    private static readonly ImageDimensions DefaultOutput = new(1024, 1536);

    public static (decimal Estimated, decimal WorstCase) Estimate(string quality, string imageSize, TryOnCostInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Qualities.Contains(quality))
        {
            throw new ArgumentOutOfRangeException(nameof(quality), quality, "Bilinmeyen GPT Image kalitesi.");
        }

        var images = Math.Max(1, input.NumImages);
        var output = input.Output ?? (Presets.TryGetValue(imageSize, out var preset) ? preset : Snap(input.Person) ?? DefaultOutput);

        // auto: model kaliteyi seçer — tahmin fal varsayılanı (high), en kötü durum max
        var estimatedQuality = quality == "auto" ? "high" : quality;
        var worstQuality = quality == "auto" ? "max" : quality;

        var outputEstimated = OutputPrice(estimatedQuality, output.Pixels, worst: false);
        var outputWorst = OutputPrice(worstQuality, output.Pixels, worst: true);
        var references = input.References.Sum(r => ReferencePrice(r.Pixels));
        var prompt = Math.Ceiling(input.PromptChars / 4m) * PromptUsdPerToken;

        var estimated = RoundUp((outputEstimated * images) + references + prompt);
        var worstCase = RoundUp((outputWorst * images) + references + Math.Max(prompt, PromptWorstUsd));
        return (estimated, Math.Max(estimated, worstCase));
    }

    /// <summary>
    /// Tahmin: piksel sayısına göre bir üst satır. En kötü durum: o satıra kadarki satırların en pahalısı (tablo boyutla tekdüze artmaz:
    /// 1024×1024, 1024×1536'dan pahalıdır). Tablonun üstündeki boyutlar 4K satırından orantılı büyütülür.
    /// </summary>
    private static decimal OutputPrice(string quality, long pixels, bool worst)
    {
        var index = Array.FindIndex(Rows, r => r.Pixels >= pixels);
        if (index < 0)
        {
            var last = Rows[^1];
            return last.For(quality) * pixels / last.Pixels;
        }

        return worst ? Rows.Take(index + 1).Max(r => r.For(quality)) : Rows[index].For(quality);
    }

    private static decimal ReferencePrice(long pixels)
    {
        const long small = 1024L * 1024;
        const long large = 3840L * 2160;
        return pixels <= small ? ReferenceSmallUsd : ReferenceSmallUsd + ((ReferenceLargeUsd - ReferenceSmallUsd) * (pixels - small) / (large - small));
    }

    /// <summary>Genişlik ve yükseklik 16'nın katı olmalıdır (fal: 1920×1080 → 1920×1072).</summary>
    private static ImageDimensions? Snap(ImageDimensions? size) => size is null ? null : new(Math.Max(16, size.Width / 16 * 16), Math.Max(16, size.Height / 16 * 16));

    private static decimal RoundUp(decimal usd) => Math.Ceiling(usd * 10_000m) / 10_000m;
}

/// <summary>Firma ayarlarına göre etkin model/kalite listesi.</summary>
public sealed record TryOnPolicy(string DefaultModel, IReadOnlyList<TryOnModelPolicy> Models);

public sealed record TryOnModelPolicy(TryOnModel Model, IReadOnlyList<string> AllowedQualities, string DefaultQuality);

public enum TryOnSelectionError
{
    UnknownModel,
    ModelNotAllowed,
    UnknownQuality,
    QualityNotAllowed,
}

/// <summary>
/// Firma ayarlarını (<see cref="OrganizationSettings"/>) yorumlar: hangi modeller etkin, hangi kaliteler izinli, varsayılanlar ne.
/// Ayar yoksa yalnızca <see cref="TryOnQuality.EnabledByDefault"/> kaliteler açıktır (GPT: low/medium/high; auto/xhigh/max yöneticinin açmasını bekler).
/// Personel yalnızca bu politikanın izin verdiği model/kaliteyi kullanabilir; kontrol sunucudadır.
/// </summary>
public static class TryOnPolicyResolver
{
    public static TryOnPolicy Resolve(OrganizationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var enabled = settings.EnabledTryOnModels is { Count: > 0 } list
            ? TryOnModels.Selectable.Where(m => list.Contains(m.Key)).ToList()
            : TryOnModels.Selectable.ToList();
        if (enabled.Count == 0)
        {
            enabled = [TryOnModels.Find(TryOnModels.DefaultModel)!];
        }

        var models = enabled.Select(model =>
        {
            var prefix = model.Key + ":";
            var configured = (settings.AllowedTryOnQualities ?? []).Where(q => q.StartsWith(prefix, StringComparison.Ordinal)).Select(q => q[prefix.Length..])
                .Where(q => model.Quality(q) is not null).ToList();
            var allowed = configured.Count > 0
                ? model.Qualities.Select(q => q.Key).Where(configured.Contains).ToList()
                : model.Qualities.Where(q => q.EnabledByDefault).Select(q => q.Key).ToList();
            var preferred = (settings.DefaultTryOnQualities ?? []).FirstOrDefault(q => q.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
            var defaultQuality = preferred is not null && allowed.Contains(preferred) ? preferred
                : allowed.Contains(model.DefaultQuality) ? model.DefaultQuality
                : allowed[0];
            return new TryOnModelPolicy(model, allowed, defaultQuality);
        }).ToList();

        var defaultModel = settings.DefaultTryOnModel is { } d && models.Any(m => m.Model.Key == d) ? d
            : models.Any(m => m.Model.Key == TryOnModels.DefaultModel) ? TryOnModels.DefaultModel
            : models[0].Model.Key;
        return new TryOnPolicy(defaultModel, models);
    }

    /// <summary>İstenen (veya varsayılan) model ve kaliteyi doğrular.</summary>
    public static (TryOnModelPolicy? Model, string? Quality, TryOnSelectionError? Error) Select(TryOnPolicy policy, string? modelKey, string? quality)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var key = string.IsNullOrWhiteSpace(modelKey) ? policy.DefaultModel : modelKey.Trim();
        if (TryOnModels.Find(key) is not { } model)
        {
            return (null, null, TryOnSelectionError.UnknownModel);
        }

        if (policy.Models.FirstOrDefault(m => m.Model.Key == key) is not { } allowedModel)
        {
            return (null, null, TryOnSelectionError.ModelNotAllowed);
        }

        var q = string.IsNullOrWhiteSpace(quality) ? allowedModel.DefaultQuality : quality.Trim().ToLowerInvariant();
        if (model.Quality(q) is null)
        {
            return (null, null, TryOnSelectionError.UnknownQuality);
        }

        return allowedModel.AllowedQualities.Contains(q) ? (allowedModel, q, null) : (null, null, TryOnSelectionError.QualityNotAllowed);
    }
}
