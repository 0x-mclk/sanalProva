using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai.Abstractions;

namespace SanalProva.Infrastructure.Ai;

public sealed class GenerationSettings
{
    public const string Section = "Generation";

    /// <summary>true iken hiçbir gerçek AI çağrısı yapılmaz (Aşama 1–3 varsayılanı).</summary>
    public bool UseMockProviders { get; set; } = true;

    public int MaxParallelJobs { get; set; } = 4;

    /// <summary>Kuyruk boşken yoklama aralığı.</summary>
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>Çalışan bir işin kilit süresi; worker çökerse bu süreden sonra iş yeniden kuyruğa alınır.</summary>
    public int LeaseSeconds { get; set; } = 180;

    /// <summary>Çalışan işin iptal edilip edilmediğinin denetim aralığı.</summary>
    public int CancelPollMs { get; set; } = 1500;

    /// <summary>Geçici hatada bekleme: taban × 2^deneme saniye (2, 4, 8...).</summary>
    public double RetryBackoffBaseSeconds { get; set; } = 2;

    public Dictionary<GenerationJobType, StrategySettings> Strategies { get; set; } = [];

    public MockSettings Mock { get; set; } = new();

    public PricingSettings Pricing { get; set; } = new();

    public BudgetSettings Budget { get; set; } = new();
}

public sealed class StrategySettings
{
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;

    /// <summary>Başarısızlıkta düşülecek iş tipi (ör. Recolor → ProductSwap).</summary>
    public GenerationJobType? Fallback { get; set; }

    public Dictionary<string, string> Options { get; set; } = [];
}

public sealed class MockSettings
{
    public int DelayMs { get; set; } = 1500;

    /// <summary>0–1 arası yapay hata oranı (hata akışlarını denemek için).</summary>
    public double FailureRate { get; set; }
}

/// <param name="Provider">Kayıtlı sağlayıcı anahtarı (DI keyed service).</param>
/// <param name="ConfiguredProvider">Yapılandırmadaki asıl sağlayıcı (mock modda da izlenebilirlik için kaydedilir).</param>
public sealed record ResolvedStrategy(
    GenerationJobType JobType,
    string Provider,
    string ConfiguredProvider,
    ProviderRequestOptions Options,
    GenerationJobType? Fallback);

/// <summary>
/// Hangi iş tipinin hangi sağlayıcı/model ile çalışacağına karar veren tek yer.
/// İş kuralları ve frontend sağlayıcıyı bilmez; model değişimi yalnızca yapılandırma değişikliğidir.
/// </summary>
public sealed class GenerationStrategy(IOptionsMonitor<GenerationSettings> settings, IServiceProvider services)
{
    public const string MockProvider = "Mock";
    public const string ImageWorkerProvider = "ImageWorker";

    public ResolvedStrategy Resolve(GenerationJobType jobType)
    {
        var current = settings.CurrentValue;
        if (!current.Strategies.TryGetValue(jobType, out var strategy) || string.IsNullOrWhiteSpace(strategy.Provider))
        {
            throw new InvalidOperationException($"'{jobType}' iş tipi için Generation:Strategies yapılandırması yok.");
        }

        var provider = current.UseMockProviders && strategy.Provider != ImageWorkerProvider ? MockProvider : strategy.Provider;
        var options = new ProviderRequestOptions(strategy.Model, strategy.Options);
        return new ResolvedStrategy(jobType, provider, strategy.Provider, options, strategy.Fallback);
    }

    public ITryOnProvider GetTryOnProvider(ResolvedStrategy strategy) => Get<ITryOnProvider>(strategy);

    public IImageGenerationProvider GetGenerationProvider(ResolvedStrategy strategy) => Get<IImageGenerationProvider>(strategy);

    public IImageEditProvider GetEditProvider(ResolvedStrategy strategy) => Get<IImageEditProvider>(strategy);

    private T Get<T>(ResolvedStrategy strategy)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(strategy);
        return services.GetKeyedService<T>(strategy.Provider)
            ?? throw new InvalidOperationException(
                $"'{strategy.Provider}' sağlayıcısı {typeof(T).Name} olarak kayıtlı değil. " +
                "Gerçek sağlayıcılar Aşama 4'te eklenecek; şimdilik Generation:UseMockProviders=true kullanın.");
    }
}
