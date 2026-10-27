using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Ai.Abstractions;
using SanalProva.Infrastructure.Ai.Fal;
using SanalProva.Infrastructure.Ai.Fashn;
using SanalProva.Infrastructure.Ai.Gemini;
using SanalProva.Infrastructure.Ai.Mock;
using SanalProva.Infrastructure.Ai.Pruna;
using SanalProva.Infrastructure.ImageWorker;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Storage;
using SanalProva.Infrastructure.Tenancy;

namespace SanalProva.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        // Veritabanı
        var connectionString = configuration.GetConnectionString("Main")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Main tanımlı değil (SQL Server, uygulama hesabı). Değer dotnet user-secrets'tan gelir; kaynak koda yazılmaz.");
        services.AddSingleton<TimestampInterceptor>();
        services.AddDbContext<AppDbContext>((sp, options) => options
            .UseSqlServer(connectionString, AppDbContext.ConfigureSqlServer)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TimestampInterceptor>()));

        // Depolama
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<FileUrlSigner>();
        services.AddSingleton<IImageStorageService, LocalFileStorage>();
        services.AddSingleton<Images.ImageStore>();

        // AI sağlayıcıları — her biri DI anahtarıyla kaydedilir; GenerationStrategy yapılandırmaya göre seçer
        services.AddOptions<GenerationSettings>().Bind(configuration.GetSection(GenerationSettings.Section));
        services.AddSingleton<GenerationStrategy>();
        services.AddSingleton<MockImageProvider>();
        services.AddKeyedSingleton<ITryOnProvider>(GenerationStrategy.MockProvider, (sp, _) => sp.GetRequiredService<MockImageProvider>());
        services.AddKeyedSingleton<IImageGenerationProvider>(GenerationStrategy.MockProvider, (sp, _) => sp.GetRequiredService<MockImageProvider>());
        services.AddKeyedSingleton<IImageEditProvider>(GenerationStrategy.MockProvider, (sp, _) => sp.GetRequiredService<MockImageProvider>());

        // Gerçek sağlayıcılar (FASHN, Gemini). Anahtarlar yalnızca ortam değişkeni/user-secrets'tan gelir; mock modda hiçbir ağ çağrısı yapılmaz.
        // RemoveAllLoggers: giden istek günlüğü (URL/başlık) kapatılır; gövde, anahtar ve görsel hiçbir yerde günlüğe girmez.
        services.AddOptions<FashnOptions>().Bind(configuration.GetSection(FashnOptions.Section));
        services.AddOptions<GeminiOptions>().Bind(configuration.GetSection(GeminiOptions.Section));
        services.AddSingleton<CostEstimator>();
        services.AddHttpClient<FashnTryOnProvider>((sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<FashnOptions>>().Value;
            http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = Timeout.InfiniteTimeSpan; // istek başına zaman aşımı sağlayıcı içinde (bağlı iptal jetonuyla)
        }).RemoveAllLoggers();
        services.AddHttpClient<GeminiProvider>((sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<GeminiOptions>>().Value;
            http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = Timeout.InfiniteTimeSpan;
        }).RemoveAllLoggers();
        services.AddKeyedTransient<ITryOnProvider>(FashnTryOnProvider.ProviderName, (sp, _) => sp.GetRequiredService<FashnTryOnProvider>());
        services.AddKeyedTransient<IImageGenerationProvider>(GeminiProvider.ProviderName, (sp, _) => sp.GetRequiredService<GeminiProvider>());
        services.AddKeyedTransient<IImageEditProvider>(GeminiProvider.ProviderName, (sp, _) => sp.GetRequiredService<GeminiProvider>());
        services.AddSingleton<Generation.BudgetGuard>();

        // Tek ürün giydirme modelleri (Aşama 4b): fal.ai (Seedream 5 Flash Edit, GPT Image 2.5 Sunburst Edit) ve Pruna (P-Image Try-On).
        // FASHN ve Gemini adapter'ları eski (pasif) olarak kayıtlı kalır.
        services.AddOptions<FalOptions>().Bind(configuration.GetSection(FalOptions.Section));
        services.AddOptions<PrunaOptions>().Bind(configuration.GetSection(PrunaOptions.Section));
        services.AddHttpClient<FalQueueClient>((sp, http) =>
        {
            http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<FalOptions>>().Value.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = Timeout.InfiniteTimeSpan;
        }).RemoveAllLoggers();
        services.AddHttpClient<PrunaTryOnProvider>((sp, http) =>
        {
            http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<PrunaOptions>>().Value.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = Timeout.InfiniteTimeSpan;
        }).RemoveAllLoggers();
        services.AddTransient<FalSeedreamTryOnProvider>();
        services.AddTransient<FalGptImageTryOnProvider>();
        services.AddKeyedTransient<ITryOnProvider>(FalSeedreamTryOnProvider.Name, (sp, _) => sp.GetRequiredService<FalSeedreamTryOnProvider>());
        services.AddKeyedTransient<ITryOnProvider>(FalGptImageTryOnProvider.Name, (sp, _) => sp.GetRequiredService<FalGptImageTryOnProvider>());
        services.AddKeyedTransient<ITryOnProvider>(PrunaTryOnProvider.Name, (sp, _) => sp.GetRequiredService<PrunaTryOnProvider>());

        // Üretim hattı ve KVKK saklama süresi temizliği
        services.AddSingleton<Generation.IJobNotifier, Generation.NullJobNotifier>();
        services.AddSingleton<Generation.GenerationProcessor>();
        services.AddSingleton<Generation.CostReconciliationSweep>();
        services.AddSingleton<Generation.CostReconciliationService>();
        services.AddHostedService<Generation.GenerationWorker>();
        services.AddOptions<Retention.RetentionSettings>().Bind(configuration.GetSection(Retention.RetentionSettings.Section));
        services.AddScoped<Retention.SessionPurger>();
        services.AddSingleton<Retention.RetentionSweeper>();
        services.AddSingleton<Retention.OrphanSweeper>();
        services.AddHostedService<Retention.RetentionJob>();

        // Smart TV: eşleştirme, durum sürümü, çevrimiçi durumu ve bakım (boşta temizleme)
        services.AddOptions<Displays.DisplaySettings>().Bind(configuration.GetSection(Displays.DisplaySettings.Section));
        services.AddSingleton<Displays.IDisplayNotifier, Displays.NullDisplayNotifier>();
        services.AddSingleton<Displays.DisplayPresence>();
        services.AddSingleton<Displays.ClaimAttemptLimiter>();
        services.AddScoped<Displays.DisplayStateService>();
        services.AddHostedService<Displays.DisplayMaintenanceJob>();

        // Python görsel işleme servisi (OpenCV, ileride SAM 2)
        services.AddOptions<ImageWorkerOptions>()
            .Bind(configuration.GetSection(ImageWorkerOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddHttpClient<ImageWorkerClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<ImageWorkerOptions>>().Value;
            http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            if (!string.IsNullOrEmpty(options.InternalKey))
            {
                http.DefaultRequestHeaders.Add(ImageWorkerClient.InternalKeyHeader, options.InternalKey);
            }
        });
        services.AddTransient<IImageSegmentationService>(sp => sp.GetRequiredService<ImageWorkerClient>());
        services.AddTransient<IRecolorService>(sp => sp.GetRequiredService<ImageWorkerClient>());

        return services;
    }
}
