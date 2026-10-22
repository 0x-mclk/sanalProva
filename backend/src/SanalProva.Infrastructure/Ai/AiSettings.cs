namespace SanalProva.Infrastructure.Ai;

/// <summary>FASHN bağlantı ayarları. API anahtarı yalnızca ortam değişkeni (<c>Ai__Fashn__ApiKey</c>) veya user-secrets'tan gelir; kaynağa yazılmaz.</summary>
public sealed class FashnOptions
{
    public const string Section = "Ai:Fashn";

    public string BaseUrl { get; set; } = "https://api.fashn.ai/";
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Tek HTTP isteği (run/status) zaman aşımı.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Tahmin tamamlanana kadar toplam bekleme. Aşılırsa iş "Timeout" olur ve tahmin kimliği saklandığı için yeniden denemede aynı tahmin sorgulanır (çift ücret yok).</summary>
    public int MaxWaitSeconds { get; set; } = 150;

    /// <summary>Durum yoklama aralığı. FASHN sınırı: /status için 10 sn'de 50 istek.</summary>
    public int PollIntervalMs { get; set; } = 2000;
}

/// <summary>Gemini bağlantı ayarları (Interactions API). Anahtar: <c>Ai__Gemini__ApiKey</c>.</summary>
public sealed class GeminiOptions
{
    public const string Section = "Ai:Gemini";

    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/";
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Yanıt şeması sürümü sabitlenir (2026-05-20: <c>steps</c> şeması). Resmî geçiş rehberi: interactions-breaking-changes-may-2026.</summary>
    public string ApiRevision { get; set; } = "2026-05-20";

    /// <summary>Görsel üretimi senkron çalışır ve düşünme süresi dahil uzun sürebilir.</summary>
    public int RequestTimeoutSeconds { get; set; } = 150;

    /// <summary>Tek istekte satır içi gönderilen toplam görsel üst sınırı (Gemini satır içi istek sınırı 20 MB).</summary>
    public int MaxInlineBytes { get; set; } = 18 * 1024 * 1024;
}

/// <summary>
/// fal.ai kuyruk API'si (Seedream 5 Flash Edit, GPT Image 2.5 Sunburst Edit). Anahtar yalnızca <c>Ai__Fal__ApiKey</c> ortam değişkeni/user-secrets'tan gelir.
/// Doğrulanan davranış (fal Queue dokümanı ve model OpenAPI şemaları, 2026-10-22): <c>POST https://queue.fal.run/{model}</c>, <c>Authorization: Key …</c>,
/// durum <c>IN_QUEUE / IN_PROGRESS / COMPLETED</c>, iptal <c>PUT …/requests/{id}/cancel</c>.
/// </summary>
public sealed class FalOptions
{
    public const string Section = "Ai:Fal";

    public string BaseUrl { get; set; } = "https://queue.fal.run/";
    public string ApiKey { get; set; } = string.Empty;
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Sonuç bekleme üst sınırı. Aşılırsa iş "Timeout" olur; istek kimliği saklandığı için yeniden denemede aynı istek sorgulanır.</summary>
    public int MaxWaitSeconds { get; set; } = 180;

    public int PollIntervalMs { get; set; } = 1500;
}

/// <summary>
/// Pruna API (P-Image Try-On). Anahtar: <c>Ai__Pruna__ApiKey</c>. Doğrulanan davranış (docs.api.pruna.ai/openapi.yaml, 2026-10-22):
/// base64 desteklenmez; girdiler <c>POST /v1/files</c> ile geçici yüklenir (30 dk sonra otomatik silinir, silme uç noktası yok);
/// tahmin <c>POST /v1/predictions</c> (<c>Model: p-image-try-on</c>), durum <c>GET /v1/predictions/status/{id}</c>, çıktı 24 saat erişilebilir.
/// </summary>
public sealed class PrunaOptions
{
    public const string Section = "Ai:Pruna";

    public string BaseUrl { get; set; } = "https://api.pruna.ai/";
    public string ApiKey { get; set; } = string.Empty;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int MaxWaitSeconds { get; set; } = 120;
    public int PollIntervalMs { get; set; } = 1000;
}

/// <summary>
/// Maliyet tahmini birim fiyatları (USD). Değerler resmî fiyat sayfalarından 2026-10-20'de alındı (bkz. docs/07 §1); fiyat
/// değişirse yalnızca yapılandırma güncellenir. Faturalama sağlayıcıdadır: bunlar tahmindir, mutabakat faturayla yapılır.
/// </summary>
public sealed class PricingSettings
{
    /// <summary>FASHN: kredi başına USD (on-demand, en yüksek birim fiyat — tahmin güvenli tarafta kalır).</summary>
    public decimal FashnUsdPerCredit { get; set; } = 0.075m;

    /// <summary>FASHN Try-On Max kredi tablosu: "mod:çözünürlük" → kredi/görsel.</summary>
    public Dictionary<string, decimal> FashnCredits { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fast:1k"] = 1, ["fast:2k"] = 2, ["fast:4k"] = 3,
        ["balanced:1k"] = 2, ["balanced:2k"] = 3, ["balanced:4k"] = 4,
        ["quality:1k"] = 3, ["quality:2k"] = 4, ["quality:4k"] = 5,
    };

    /// <summary>Gemini model kimliği → fiyatlar.</summary>
    public Dictionary<string, GeminiModelPricing> Gemini { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini-3-pro-image"] = new() { InputUsdPer1M = 2.00m, OutputTextUsdPer1M = 12.00m, OutputImageUsdPer1M = 120.00m, ImageUsd = new() { ["1K"] = 0.134m, ["2K"] = 0.134m, ["4K"] = 0.24m } },
        ["gemini-nano-banana-2.1"] = new() { InputUsdPer1M = 1.50m, OutputTextUsdPer1M = 7.50m, OutputImageUsdPer1M = 30.00m, ImageUsd = new() { ["1K"] = 0.0336m, ["2K"] = 0.0504m, ["4K"] = 0.113m } },
        ["gemini-3.1-flash-image"] = new() { InputUsdPer1M = 0.50m, OutputTextUsdPer1M = 3.00m, OutputImageUsdPer1M = 60.00m, ImageUsd = new() { ["1K"] = 0.067m, ["2K"] = 0.101m, ["4K"] = 0.151m } },
        ["gemini-3.1-flash-lite-image"] = new() { InputUsdPer1M = 0.25m, OutputTextUsdPer1M = 1.50m, OutputImageUsdPer1M = 30.00m, ImageUsd = new() { ["1K"] = 0.0336m } },
    };

    /// <summary>Tek ürün giydirme modelleri için tahmini maliyet ezmesi ("model:kalite" → USD/görsel). Boşsa katalog değeri (TryOnModels) kullanılır.</summary>
    public Dictionary<string, decimal> TryOnEstimatedUsd { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tek ürün giydirme modelleri için bütçe kapısında kullanılan en kötü durum ezmesi ("model:kalite" → USD).</summary>
    public Dictionary<string, decimal> TryOnWorstCaseUsd { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Çağrı öncesi en kötü durum tahmini için: bir giriş görseli ortalama kaç token (jeton) sayılır.</summary>
    public int AssumedTokensPerInputImage { get; set; } = 1300;

    /// <summary>Çağrı öncesi en kötü durum tahmini için: düşünme + metin çıktısı jeton payı (Gemini 3 görsel modellerinde düşünme kapatılamaz).</summary>
    public int AssumedThoughtTokens { get; set; } = 4000;
}

public sealed class GeminiModelPricing
{
    public decimal InputUsdPer1M { get; set; }
    public decimal OutputTextUsdPer1M { get; set; }
    public decimal OutputImageUsdPer1M { get; set; }

    /// <summary>Çözünürlük ("1K", "2K", "4K") → görsel başına USD (jeton bilgisi yoksa kullanılır).</summary>
    public Dictionary<string, decimal> ImageUsd { get; set; } = [];
}

/// <summary>
/// Ücretli çağrı kapıları ve harcama sınırları. <see cref="AllowPaidCalls"/> varsayılan <c>false</c>: yapılandırmada gerçek sağlayıcı
/// seçili olsa bile ücretli çağrı yalnızca bu bayrak (ve <c>UseMockProviders=false</c>) birlikte açıldığında yapılır.
/// </summary>
public sealed class BudgetSettings
{
    public bool AllowPaidCalls { get; set; }

    /// <summary>Firma başına günlük (UTC günü) tahmini harcama üst sınırı. 0 = sınırsız değil, 0 USD izin (hiç harcama yok).</summary>
    public decimal DailyLimitUsd { get; set; } = 10m;

    /// <summary>Firma başına takvim ayı (UTC) tahmini harcama üst sınırı.</summary>
    public decimal MonthlyLimitUsd { get; set; } = 150m;

    /// <summary>Tek bir işin (yeniden denemeler dahil) tahmini maliyet üst sınırı.</summary>
    public decimal MaxJobCostUsd { get; set; } = 0.50m;

    /// <summary>Sağlayıcı çağrısı öncesi tüm sınırlar tahmini en kötü maliyetle denetlenir.</summary>
    public bool Enforce { get; set; } = true;
}
