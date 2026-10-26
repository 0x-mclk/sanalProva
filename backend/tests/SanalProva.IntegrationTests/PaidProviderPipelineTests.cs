using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SanalProva.Domain.Generation;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Ai.Abstractions;
using SanalProva.Infrastructure.Ai.TryOn;
using SanalProva.Infrastructure.Generation;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.IntegrationTests;

internal sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "SanalProva.IntegrationTests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>Sahte "gerçek" Try-On sağlayıcısı: çağrıları sayar ve senaryoya göre davranır. Ağ çağrısı yok, ücret yok.</summary>
internal sealed class ScriptedTryOn(Func<int, ProviderRequestOptions, CancellationToken, Task<ProviderResult>> script) : ITryOnProvider
{
    public int Calls { get; private set; }

    public List<ProviderRequestOptions> Seen { get; } = [];

    /// <summary>Sağlayıcıya giden görseller (maliyet hesabı gerçek boyutlarla doğrulanır).</summary>
    public List<(ImageInput Person, ImageInput Garment)> Inputs { get; } = [];

    public Task<ProviderResult> TryOnAsync(ImageInput person, ImageInput garment, ProviderRequestOptions options, CancellationToken ct)
    {
        Calls++;
        Seen.Add(options);
        Inputs.Add((person, garment));
        return script(Calls, options, ct);
    }
}

internal sealed class ScriptedCompose(Func<int, ProviderRequestOptions, CancellationToken, Task<ProviderResult>> script) : IImageGenerationProvider
{
    public int Calls { get; private set; }

    public string? LastPrompt { get; private set; }

    public IReadOnlyList<GarmentReference> LastGarments { get; private set; } = [];

    public Task<ProviderResult> ComposeOutfitAsync(ImageInput person, IReadOnlyList<GarmentReference> garments, string prompt, ProviderRequestOptions options, CancellationToken ct)
    {
        Calls++;
        LastPrompt = prompt;
        LastGarments = garments;
        return script(Calls, options, ct);
    }
}

/// <summary>
/// Gerçek sağlayıcı yolunun (bütçe kapısı, çift ücret koruması, maliyet kaydı, iptal) uçtan uca sınanması: gerçek veritabanı ve depo,
/// <b>sahte</b> FASHN/Gemini sağlayıcıları. Her test kendi işlemcisini kurar ve işi "Running" olarak ayırıp doğrudan çalıştırır; böylece
/// paralel çalışan ana worker'a (mock) karışmaz ve diğer testlerin ayarları değişmez.
/// </summary>
public sealed class PaidProviderPipelineTests(ApiFactory factory) : ApiTestBase(factory)
{
    private static ProviderResult Result(string provider, string model, string requestId, decimal estimated, decimal? credits = null, decimal? actual = null, int seed = 1) =>
        new([new GeneratedOutput(TestImages.Portrait(seed + 100), "image/jpeg")], provider, model, requestId,
            new ProviderUsage(1200, credits, null, null, estimated, actual));

    private sealed class Harness(ServiceProvider services, ScriptedTryOn fashn, ScriptedCompose gemini, ScriptedTryOn tryOn) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;
        public ScriptedTryOn Fashn { get; } = fashn;

        /// <summary>Yeni tek ürün modelleri (FalSeedream, FalGptImage, Pruna) için ortak sahte sağlayıcı.</summary>
        public ScriptedTryOn TryOn { get; } = tryOn;
        public ScriptedCompose Gemini { get; } = gemini;

        public GenerationProcessor Processor => Services.GetRequiredService<GenerationProcessor>();

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    /// <summary>Kuyruğa dönen işin geri çekilme süresi (sn): paylaşılan test veritabanında ana (mock) worker işi kapmasın, test sıradaki adımı kendisi sürsün.</summary>
    private const double QueuedHoldSeconds = 1800;

    private Harness BuildHarness(
        ScriptedTryOn? fashn = null, ScriptedCompose? gemini = null, bool allowPaid = true, decimal daily = 10m, decimal monthly = 150m, decimal maxJob = 0.5m,
        string freeModel = "gemini-3-pro-image", string setModel = "tryon-max", bool liveCombination = false, int? leaseSeconds = null, double retryBackoffSeconds = 0.01)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Main"] = Factory.TestConnectionString,
            ["Storage:LocalRoot"] = Factory.StorageRoot,
            ["Storage:SigningKey"] = new string('t', 48),
            ["ImageWorker:BaseUrl"] = "http://127.0.0.1:1",
            ["Generation:UseMockProviders"] = "false",
            ["Generation:RetryBackoffBaseSeconds"] = retryBackoffSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Generation:CancelPollMs"] = "40",
            ["Generation:LiveFreeCombinationEnabled"] = liveCombination.ToString(),
            ["Generation:Budget:AllowPaidCalls"] = allowPaid.ToString(),
            ["Generation:Budget:DailyLimitUsd"] = daily.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Generation:Budget:MonthlyLimitUsd"] = monthly.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Generation:Budget:MaxJobCostUsd"] = maxJob.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Generation:Strategies:OutfitSet:Provider"] = "Fashn",
            ["Generation:Strategies:OutfitSet:Model"] = setModel,
            ["Generation:Strategies:OutfitSet:Options:resolution"] = "1k",
            ["Generation:Strategies:OutfitSet:Options:generation_mode"] = "balanced",
            ["Generation:Strategies:FreeCombination:Provider"] = "Gemini",
            ["Generation:Strategies:FreeCombination:Model"] = freeModel,
            ["Generation:Strategies:FreeCombination:Options:image_size"] = freeModel.Contains("lite", StringComparison.Ordinal) ? "1K" : "2K",
        };
        if (leaseSeconds is { } lease)
        {
            values["Generation:LeaseSeconds"] = lease.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddInfrastructure(configuration);

        // Sahte FASHN, istenen çözünürlüğe göre kredi/maliyet bildirir (1k = 2 kredi = 0,15 $, 2k = 3 kredi = 0,225 $)
        fashn ??= new ScriptedTryOn((n, options, _) =>
        {
            var credits = options.Parameters.GetValueOrDefault("resolution") == "2k" ? 3m : 2m;
            return Task.FromResult(Result("Fashn", "tryon-max", $"pred-{n}", credits * 0.075m, credits, credits * 0.075m));
        });
        gemini ??= new ScriptedCompose((n, _, _) => Task.FromResult(Result("Gemini", freeModel, $"int-{n}", 0.1562m)));
        services.AddKeyedSingleton<ITryOnProvider>("Fashn", fashn);
        services.AddKeyedSingleton<IImageGenerationProvider>("Gemini", gemini);

        // Yeni modeller: gerçek adapter'lar gibi maliyeti girdi boyutları, kalite ve görsel sayısından hesaplar (fal/Pruna istek başına maliyet döndürmez)
        ScriptedTryOn tryOn = null!;
        tryOn = new ScriptedTryOn((n, options, _) =>
        {
            var model = TryOnModels.Find(options.Parameters["model_key"])!;
            var (person, garment) = tryOn.Inputs[^1];
            return Task.FromResult(Result(model.Provider, options.Model, $"req-{n}", ProviderHttp.EstimatedCost(options, new PricingSettings(), person, garment)));
        });
        foreach (var key in new[] { "FalSeedream", "FalGptImage", "Pruna" })
        {
            services.AddKeyedSingleton<ITryOnProvider>(key, tryOn);
        }

        return new Harness(services.BuildServiceProvider(), fashn, gemini, tryOn);
    }

    private async Task<(TestTenant Tenant, ApiClient Client, CatalogIds Catalog)> TenantAsync()
    {
        var (tenant, client) = await StaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        return (tenant, client, catalog);
    }

    /// <summary>Oturum + onay + fotoğraf + seçim + "Running" iş (worker'a karışmaz).</summary>
    private async Task<(Guid JobId, Guid SessionId)> RunningJobAsync(
        TestTenant tenant, ApiClient client, CatalogIds catalog, OutfitMode mode, string? modelKey = null, string? quality = null, bool kaftan = false)
    {
        var (session, photo) = await PreparedSessionAsync(client);
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var user = await db.Users.SingleAsync(u => u.Email == tenant.Email, TestContext.Current.CancellationToken);
            var selection = new OutfitSelection { OrganizationId = tenant.OrganizationId, SessionId = session.Id, Mode = mode, CreatedByUserId = user.Id };
            if (mode == OutfitMode.OutfitSet && kaftan)
            {
                selection.Items.Add(new OutfitSelectionItem { OrganizationId = tenant.OrganizationId, ProductId = catalog.KaftanProduct, CategoryId = catalog.Kaftan, LayerOrder = 30 });
            }
            else if (mode == OutfitMode.OutfitSet)
            {
                selection.Items.Add(new OutfitSelectionItem { OrganizationId = tenant.OrganizationId, ProductId = catalog.SetProduct, VariantId = catalog.SetVariant, CategoryId = catalog.Takim, LayerOrder = 50 });
            }
            else
            {
                var parts = new[] { (catalog.SalvarProduct, catalog.Salvar, 10), (catalog.GomlekProduct, catalog.Gomlek, 15), (catalog.YelekProduct, catalog.Yelek, 20), (catalog.KaftanProduct, catalog.Kaftan, 30), (catalog.KemerProduct, catalog.Kemer, 40) };
                foreach (var (product, category, layer) in parts)
                {
                    selection.Items.Add(new OutfitSelectionItem { OrganizationId = tenant.OrganizationId, ProductId = product, CategoryId = category, LayerOrder = layer });
                }
            }

            var now = DateTimeOffset.UtcNow;
            var job = new GenerationJob
            {
                OrganizationId = tenant.OrganizationId, StoreId = tenant.StoreId, SessionId = session.Id,
                JobType = mode == OutfitMode.OutfitSet ? GenerationJobType.OutfitSet : GenerationJobType.FreeCombination,
                SelectionId = selection.Id, SourcePhotoId = photo.Id, IdempotencyKey = Guid.NewGuid().ToString("N"), RequestedByUserId = user.Id,
                ModelKey = modelKey, Quality = quality,
                Status = GenerationJobStatus.Running, Attempt = 1, Stage = GenerationStage.Preparing, StartedAt = now, NextAttemptAt = now, LeaseUntil = now.AddMinutes(10),
            };
            db.OutfitSelections.Add(selection);
            db.GenerationJobs.Add(job);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return (job.Id, session.Id);
        }
    }

    /// <summary>İşi bir sonraki deneme için yeniden "Running" yapar (worker'ın claim'ini taklit eder, ana worker'a karışmadan).</summary>
    private async Task ReclaimAsync(Guid jobId)
    {
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.GenerationJobs.IgnoreQueryFilters().Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, GenerationJobStatus.Running).SetProperty(j => j.Attempt, j => j.Attempt + 1)
                .SetProperty(j => j.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(10)), TestContext.Current.CancellationToken);
        }
    }

    /// <remarks>Kuyruğa dönen iş için sıradaki deneme zamanı ileri alınır: paralel çalışan ana (mock) worker işi kapmasın, test o anı sınasın.</remarks>
    private async Task SetJobAsync(Guid jobId, GenerationJobStatus status, DateTimeOffset? leaseUntil = null)
    {
        var (scope, db) = OpenDb();
        using (scope)
        {
            var nextAttempt = DateTimeOffset.UtcNow.AddHours(1);
            await db.GenerationJobs.IgnoreQueryFilters().Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, status).SetProperty(j => j.LeaseUntil, leaseUntil).SetProperty(j => j.NextAttemptAt, nextAttempt),
                TestContext.Current.CancellationToken);
        }
    }

    private async Task<(GenerationJob Job, List<AiUsageLog> Logs, List<GeneratedImage> Images)> LoadAsync(Guid jobId)
    {
        var (scope, db) = OpenDb();
        using (scope)
        {
            var job = await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(j => j.Id == jobId, TestContext.Current.CancellationToken);
            var logs = await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().Where(l => l.JobId == jobId).OrderBy(l => l.CreatedAt).ToListAsync(TestContext.Current.CancellationToken);
            var images = await db.GeneratedImages.IgnoreQueryFilters().AsNoTracking().Where(i => i.JobId == jobId).ToListAsync(TestContext.Current.CancellationToken);
            return (job, logs, images);
        }
    }

    [Fact]
    public async Task Ucretli_cagri_bayragi_kapaliyken_gercek_saglayici_hic_cagrilmaz()
    {
        var (tenant, client, catalog) = await TenantAsync();
        await using var harness = BuildHarness(allowPaid: false);
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, logs, images) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Failed, job.Status);
        Assert.Equal(GenerationErrorCode.ProviderNotConfigured, job.ErrorCode);
        Assert.Equal(0, harness.Fashn.Calls);
        Assert.Empty(images);
        Assert.All(logs, l => Assert.Equal(0m, l.EstimatedCostUsd));
    }

    [Fact]
    public async Task Basarili_gercek_uretim_gercek_olarak_isaretlenir_ve_maliyeti_kaydedilir()
    {
        var (tenant, client, catalog) = await TenantAsync();
        await using var harness = BuildHarness();
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, logs, images) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Succeeded, job.Status);
        Assert.Equal("Fashn", job.Provider);
        Assert.Equal("tryon-max", job.Model);
        Assert.Equal("fashn-tryon.v1", job.PromptTemplateVersion);
        var image = Assert.Single(images);
        Assert.False(image.IsDemo); // DEMO yalnızca mock sağlayıcıdan gelen sonuçlarda
        var log = Assert.Single(logs);
        Assert.True(log.Success);
        Assert.Equal("Fashn", log.Provider);
        Assert.Equal(0.15m, log.EstimatedCostUsd); // varsayılan: balanced + 1k = 2 kredi
        Assert.Equal(0.15m, log.ActualCostUsd);
        Assert.Equal(2m, log.CreditsUsed);
        Assert.Equal("1k", harness.Fashn.Seen[0].Parameters["resolution"]);
        Assert.Equal("balanced", harness.Fashn.Seen[0].Parameters["generation_mode"]);
        Assert.Equal("pred-1", log.ProviderRequestId);
        Assert.Equal(1, log.Attempt);
        Assert.True(log.DurationMs > 0);
        Assert.NotNull(harness.Fashn.Seen[0].Seed);
    }

    [Fact]
    public async Task Gunluk_butce_asilacaksa_saglayici_cagrilmaz_ve_ucret_dogmaz()
    {
        var (tenant, client, catalog) = await TenantAsync();
        // Çağrı başı en kötü tahmin 0,15 USD (1k); günlük sınır 0,20 → ilk iş geçer, ikincisi engellenir (aynı firmada)
        await using var harness = BuildHarness(daily: 0.20m);
        var (first, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);
        var (second, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);

        await harness.Processor.ProcessClaimedAsync(first, TestContext.Current.CancellationToken);
        await harness.Processor.ProcessClaimedAsync(second, TestContext.Current.CancellationToken);

        Assert.Equal(GenerationJobStatus.Succeeded, (await LoadAsync(first)).Job.Status);
        var (blocked, logs, images) = await LoadAsync(second);
        Assert.Equal(GenerationJobStatus.Failed, blocked.Status);
        Assert.Equal(GenerationErrorCode.BudgetExceeded, blocked.ErrorCode);
        Assert.Equal(1, harness.Fashn.Calls); // ikinci iş için sağlayıcıya hiç gidilmedi
        Assert.Empty(images);
        Assert.All(logs, l => Assert.Equal(0m, l.EstimatedCostUsd));
    }

    [Fact]
    public async Task Is_basi_maliyet_siniri_asilacaksa_cagri_yapilmaz()
    {
        var (tenant, client, catalog) = await TenantAsync();
        await using var harness = BuildHarness(maxJob: 0.10m); // en kötü tahmin 0,15 > 0,10
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, _, _) = await LoadAsync(jobId);
        Assert.Equal(GenerationErrorCode.BudgetExceeded, job.ErrorCode);
        Assert.Equal(0, harness.Fashn.Calls);
    }

    [Fact]
    public async Task Zaman_asimindan_sonra_ayni_tahmin_sorgulanir_ikinci_istek_acilmaz()
    {
        var (tenant, client, catalog) = await TenantAsync();
        var fashn = new ScriptedTryOn(async (n, options, _) =>
        {
            if (n == 1)
            {
                await options.OnSubmitted!("pred-slow"); // sağlayıcı isteği kabul etti, sonuç gelmedi
                throw new ProviderException(GenerationErrorCode.Timeout, "zaman aşımı", retryable: true);
            }

            Assert.Equal("pred-slow", options.ResumeRequestId); // yeni (ücretli) istek yerine aynı tahmin
            return Result("Fashn", "tryon-max", "pred-slow", 0.225m, 3, 0.225m);
        });
        await using var harness = BuildHarness(fashn, retryBackoffSeconds: QueuedHoldSeconds);
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);
        var afterFirst = (await LoadAsync(jobId)).Job;
        Assert.Equal(GenerationJobStatus.Queued, afterFirst.Status);
        Assert.Equal("pred-slow", afterFirst.ProviderRequestId); // kimlik hemen kalıcı kaydedildi

        await ReclaimAsync(jobId);
        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, logs, images) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Succeeded, job.Status);
        Assert.Single(images);
        Assert.Equal(2, logs.Count);
        Assert.Equal(0m, logs[0].EstimatedCostUsd); // zaman aşımı denemesi ücret yazmaz
        Assert.Equal(0.225m, logs.Sum(l => l.EstimatedCostUsd)); // toplam tek çağrı ücreti
        Assert.Equal([1, 2], logs.Select(l => l.Attempt).ToArray()); // yeniden deneme sayısı izlenebilir
    }

    [Fact]
    public async Task Kilidi_dolup_baska_workera_gecen_isin_eski_worker_i_sonuc_ve_maliyet_yazmaz_yeni_sahip_ayni_tahmini_bitirir()
    {
        var (tenant, client, catalog) = await TenantAsync();
        var jobId = Guid.Empty;
        var fashn = new ScriptedTryOn(async (n, options, _) =>
        {
            if (n == 1)
            {
                // Eski worker: sağlayıcı isteği kabul etti (kimlik kalıcı), yanıt gelmeden kilit doldu ve iş başka worker'a geçti (deneme 1 → 2)
                await options.OnSubmitted!("pred-stale");
                await ReclaimAsync(jobId);
                return Result("Fashn", "tryon-max", "pred-stale", 0.15m, 2, 0.15m);
            }

            Assert.Equal("pred-stale", options.ResumeRequestId); // yeni sahip yeni (ücretli) istek açmaz, aynı tahmini sorgular
            return Result("Fashn", "tryon-max", "pred-stale", 0.15m, 2, 0.15m);
        });
        await using var harness = BuildHarness(fashn);
        (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);

        // Eski worker: yazmaları çit belirteciyle (deneme numarası) reddedilir; hata fırlatmaz, hiçbir şey yazmaz
        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);
        var (stale, staleLogs, staleImages) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Running, stale.Status); // yeni sahibin elinde
        Assert.Equal(2, stale.Attempt);
        Assert.Equal("pred-stale", stale.ProviderRequestId);
        Assert.Empty(staleImages);
        Assert.Empty(staleLogs);

        // Yeni sahip aynı tahminle bitirir: tek sonuç, tek maliyet kaydı, tek ücret
        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);
        var (job, logs, images) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Succeeded, job.Status);
        Assert.Single(images);
        var log = Assert.Single(logs);
        Assert.Equal((0.15m, 2), (log.EstimatedCostUsd, log.Attempt));
        Assert.Equal(2, fashn.Calls);
    }

    [Theory]
    [InlineData(GenerationJobStatus.Queued)] // kilit doldu, iş kurtarılıp yeniden kuyruğa alındı
    [InlineData(GenerationJobStatus.Cancelled)] // personel iptal etti
    public async Task Is_saglayici_cagrisindan_once_elden_cikarsa_ucretli_istek_hic_gonderilmez(GenerationJobStatus state)
    {
        var (tenant, client, catalog) = await TenantAsync();
        await using var harness = BuildHarness();
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);
        await SetJobAsync(jobId, state); // worker işi aldı ama sağlayıcıya gitmeden iş elinden çıktı

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, logs, images) = await LoadAsync(jobId);
        Assert.Equal(0, harness.Fashn.Calls); // gönderim kapısı: sahiplik koşullu kilit uzatma başarısız → çağrı yok
        Assert.Equal(state, job.Status); // eski worker durumu değiştirmedi
        Assert.Empty(logs);
        Assert.Empty(images);
    }

    [Fact]
    public async Task Kilit_suresinden_uzun_saglayici_beklemesinde_kilit_yenilenir_is_baska_workera_gecmez()
    {
        var (tenant, client, catalog) = await TenantAsync();
        var fashn = new ScriptedTryOn(async (_, options, ct) =>
        {
            await options.OnSubmitted!("pred-long");
            await Task.Delay(TimeSpan.FromSeconds(7), ct); // kilit süresinin (3 sn) iki katından uzun
            return Result("Fashn", "tryon-max", "pred-long", 0.15m, 2, 0.15m);
        });
        await using var harness = BuildHarness(fashn, leaseSeconds: 3);
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);
        // Kısa kilit: ana test host'unun worker döngüsü süresi dolan işleri sürekli kurtarıp kendisi işler (gerçek rakip worker)
        await SetJobAsync(jobId, GenerationJobStatus.Running, DateTimeOffset.UtcNow.AddSeconds(3));

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, logs, images) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Succeeded, job.Status);
        Assert.Equal(1, job.Attempt); // iş hiç başka worker'a geçmedi
        Assert.Equal(1, fashn.Calls);
        Assert.False(Assert.Single(images).IsDemo); // sonucu rakip (mock) worker değil bu worker yazdı
        Assert.Equal(0.15m, Assert.Single(logs).EstimatedCostUsd);
    }

    [Fact]
    public async Task Bekleme_sirasinda_is_baska_workera_gecerse_eski_worker_bekleyisi_birakir_yeni_sahip_ayni_tahmini_bitirir()
    {
        var (tenant, client, catalog) = await TenantAsync();
        var jobId = Guid.Empty;
        var fashn = new ScriptedTryOn(async (n, options, ct) =>
        {
            if (n == 1)
            {
                await options.OnSubmitted!("pred-moved");
                await ReclaimAsync(jobId); // iş başka worker'a geçti (deneme 1 → 2)
                await Task.Delay(TimeSpan.FromSeconds(30), ct); // kilit yenilemesi başarısız olunca iptal edilmeli
                return Result("Fashn", "tryon-max", "pred-moved", 0.15m, 2, 0.15m);
            }

            Assert.Equal("pred-moved", options.ResumeRequestId);
            return Result("Fashn", "tryon-max", "pred-moved", 0.15m, 2, 0.15m);
        });
        await using var harness = BuildHarness(fashn, leaseSeconds: 3);
        (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);

        var stale = System.Diagnostics.Stopwatch.StartNew();
        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);
        stale.Stop();

        Assert.True(stale.Elapsed < TimeSpan.FromSeconds(15), $"Eski worker bekleyişi bırakmadı: {stale.Elapsed}");
        var (moved, movedLogs, movedImages) = await LoadAsync(jobId);
        Assert.Equal((GenerationJobStatus.Running, 2), (moved.Status, moved.Attempt));
        Assert.Empty(movedLogs);
        Assert.Empty(movedImages);

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);
        var (job, logs, images) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Succeeded, job.Status);
        Assert.Single(images);
        Assert.Equal((0.15m, 2), (Assert.Single(logs).EstimatedCostUsd, logs[0].Attempt));
    }

    [Fact]
    public async Task Worker_gonderimden_sonra_coker_kurtarilan_is_ayni_tahmini_sorgular_maliyet_bir_kez_yazilir()
    {
        var (tenant, client, catalog) = await TenantAsync();
        using var crash = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var fashn = new ScriptedTryOn(async (n, options, _) =>
        {
            if (n == 1)
            {
                await options.OnSubmitted!("pred-crash"); // sağlayıcı kabul etti, kimlik kalıcı
                await crash.CancelAsync(); // süreç kapanıyor/çöküyor
                throw new OperationCanceledException(crash.Token);
            }

            Assert.Equal("pred-crash", options.ResumeRequestId); // yeni (ücretli) istek yok
            return Result("Fashn", "tryon-max", "pred-crash", 0.15m, 2, 0.15m);
        });
        await using var harness = BuildHarness(fashn);
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Processor.ProcessClaimedAsync(jobId, crash.Token));

        var (crashed, crashedLogs, _) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Running, crashed.Status); // "başarısız" yazılmadı: kilit dolunca kurtarılacak
        Assert.Equal("pred-crash", crashed.ProviderRequestId);
        Assert.Empty(crashedLogs);

        await ReclaimAsync(jobId); // kilit doldu, iş kurtarılıp yeniden alındı
        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, logs, images) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Succeeded, job.Status);
        Assert.Single(images);
        Assert.Equal(0.15m, Assert.Single(logs).EstimatedCostUsd);
        Assert.Equal(2, fashn.Calls); // ikinci çağrı yeni gönderim değil, aynı tahminin sorgusu
    }

    [Fact]
    public async Task Basarisiz_tahminden_sonra_yeni_tahmin_acilir()
    {
        var (tenant, client, catalog) = await TenantAsync();
        var fashn = new ScriptedTryOn(async (n, options, _) =>
        {
            if (n == 1)
            {
                await options.OnSubmitted!("pred-failed");
                throw new ProviderException(GenerationErrorCode.ProviderUnavailable, "PipelineError", retryable: true) { RequestEnded = true }; // sağlayıcı "failed" dedi
            }

            Assert.Null(options.ResumeRequestId); // başarısız tahmin ücretsizdir; yenisi açılır
            return Result("Fashn", "tryon-max", "pred-new", 0.225m, 3, 0.225m);
        });
        await using var harness = BuildHarness(fashn, retryBackoffSeconds: QueuedHoldSeconds);
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);
        Assert.Null((await LoadAsync(jobId)).Job.ProviderRequestId);
        await ReclaimAsync(jobId);
        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        Assert.Equal(GenerationJobStatus.Succeeded, (await LoadAsync(jobId)).Job.Status);
        Assert.Equal(2, harness.Fashn.Calls);
    }

    // ---------------------------------------------------------------- Sonucu belirsiz gönderim (exactly-once garanti edilemez; çift ücret önlenir)

    private async Task SetAttemptAsync(Guid jobId, int attempt)
    {
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.GenerationJobs.IgnoreQueryFilters().Where(j => j.Id == jobId)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Attempt, attempt), TestContext.Current.CancellationToken);
        }
    }

    private static void AssertUncertain(GenerationJob job, List<AiUsageLog> logs, List<GeneratedImage> images, decimal worstCase)
    {
        Assert.Equal(GenerationJobStatus.Failed, job.Status); // Queued değil: otomatik yeniden gönderim yok
        Assert.Equal(GenerationErrorCode.OutcomeUncertain, job.ErrorCode);
        Assert.NotNull(job.ProviderSubmitStartedAt);
        Assert.Empty(images);
        var log = Assert.Single(logs, l => l.ErrorCode == GenerationErrorCode.OutcomeUncertain);
        Assert.False(log.Success);
        Assert.Equal(($"uncertain:{job.Id}", CostReconciliationState.Open, false), (log.EventKey, log.ReconciliationState, log.IsAdjustment)); // tekil olay + mutabakat bekliyor
        Assert.Equal(worstCase, log.EstimatedCostUsd); // ücretlenmiş olabilir: bütçe en kötü durumu harcanmış sayar
    }

    [Fact]
    public async Task Gonderim_yaniti_kaybolursa_sonuc_belirsiz_olur_yeniden_gonderilmez_en_kotu_maliyet_kaydedilir()
    {
        var (tenant, client, catalog) = await TenantAsync();
        var fashn = new ScriptedTryOn(async (_, options, _) =>
        {
            await options.OnSubmitting!(); // adapter: niyet kalıcı, sonra gönderim
            throw ProviderHttp.Uncertain(new ProviderException(GenerationErrorCode.Timeout, "yanıt yok", retryable: true));
        });
        await using var harness = BuildHarness(fashn);
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, logs, images) = await LoadAsync(jobId);
        AssertUncertain(job, logs, images, 0.15m);
        Assert.Null(job.ProviderRequestId);
        Assert.Equal(1, fashn.Calls);
    }

    [Fact]
    public async Task Gonderim_niyetinden_sonra_coken_worker_kurtarilinca_saglayici_yeniden_cagrilmaz()
    {
        var (tenant, client, catalog) = await TenantAsync();
        using var crash = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var fashn = new ScriptedTryOn(async (_, options, _) =>
        {
            await options.OnSubmitting!(); // niyet yazıldı; istek gönderildi mi bilinmiyor
            await crash.CancelAsync(); // süreç çöküyor, kimlik hiç gelmedi
            throw new OperationCanceledException(crash.Token);
        });
        await using var harness = BuildHarness(fashn);
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Processor.ProcessClaimedAsync(jobId, crash.Token));
        var (crashed, crashedLogs, _) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Running, crashed.Status);
        Assert.NotNull(crashed.ProviderSubmitStartedAt);
        Assert.Null(crashed.ProviderRequestId);
        Assert.Empty(crashedLogs);

        await ReclaimAsync(jobId); // kilit doldu, iş kurtarıldı
        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, logs, images) = await LoadAsync(jobId);
        AssertUncertain(job, logs, images, 0.15m);
        Assert.Equal(1, fashn.Calls); // ikinci ücretli istek açılmadı
    }

    [Fact]
    public async Task Yoklamada_baglanti_koparsa_kimlik_korunur_ayni_istek_sorgulanir_tek_ucret()
    {
        var (tenant, client, catalog) = await TenantAsync();
        var fashn = new ScriptedTryOn(async (n, options, _) =>
        {
            if (n == 1)
            {
                await options.OnSubmitting!();
                await options.OnSubmitted!("pred-net");
                throw new ProviderException(GenerationErrorCode.ProviderUnavailable, "bağlantı koptu", retryable: true); // istek sürüyor olabilir
            }

            Assert.Equal("pred-net", options.ResumeRequestId);
            return Result("Fashn", "tryon-max", "pred-net", 0.15m, 2, 0.15m);
        });
        await using var harness = BuildHarness(fashn, retryBackoffSeconds: QueuedHoldSeconds);
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);
        var afterFirst = (await LoadAsync(jobId)).Job;
        Assert.Equal(GenerationJobStatus.Queued, afterFirst.Status);
        Assert.Equal("pred-net", afterFirst.ProviderRequestId);

        await ReclaimAsync(jobId);
        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, logs, images) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Succeeded, job.Status);
        Assert.Single(images);
        Assert.Equal(0.15m, logs.Sum(l => l.EstimatedCostUsd));
        Assert.Equal(2, fashn.Calls);
    }

    [Fact]
    public async Task Kesin_ret_gonderim_niyetini_temizler_yeniden_deneme_yeni_istek_acar()
    {
        var (tenant, client, catalog) = await TenantAsync();
        var fashn = new ScriptedTryOn(async (n, options, _) =>
        {
            Assert.Null(options.ResumeRequestId);
            await options.OnSubmitting!();
            if (n == 1)
            {
                throw new ProviderException(GenerationErrorCode.RateLimited, "429", retryable: true); // yanıtlı ret: istek kabul edilmedi
            }

            await options.OnSubmitted!("pred-ok");
            return Result("Fashn", "tryon-max", "pred-ok", 0.15m, 2, 0.15m);
        });
        await using var harness = BuildHarness(fashn, retryBackoffSeconds: QueuedHoldSeconds);
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);
        var afterFirst = (await LoadAsync(jobId)).Job;
        Assert.Equal(GenerationJobStatus.Queued, afterFirst.Status);
        Assert.Null(afterFirst.ProviderSubmitStartedAt); // ücret doğmadı: niyet temizlendi

        await ReclaimAsync(jobId);
        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, logs, _) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Succeeded, job.Status);
        Assert.Equal(0.15m, logs.Sum(l => l.EstimatedCostUsd));
        Assert.Equal(2, fashn.Calls);
    }

    [Fact]
    public async Task Son_denemede_kimlikli_istek_tamamlanmazsa_sonuc_belirsizdir_kimlik_mutabakat_icin_saklanir()
    {
        var (tenant, client, catalog) = await TenantAsync();
        var fashn = new ScriptedTryOn(async (_, options, _) =>
        {
            await options.OnSubmitting!();
            await options.OnSubmitted!("pred-last");
            throw new ProviderException(GenerationErrorCode.Timeout, "zaman aşımı", retryable: true);
        });
        await using var harness = BuildHarness(fashn);
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);
        await SetAttemptAsync(jobId, 3); // son deneme (MaxAttempts = 3)

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, logs, images) = await LoadAsync(jobId);
        AssertUncertain(job, logs, images, 0.15m); // "Timeout" değil: istek sağlayıcıda tamamlanmış olabilir
        Assert.Equal("pred-last", job.ProviderRequestId);
        Assert.Equal("pred-last", Assert.Single(logs).ProviderRequestId);
    }

    [Fact]
    public async Task Ek_cozum_gecisinde_yeni_ucretli_istek_acilmaz()
    {
        var (tenant, client, catalog) = await TenantAsync();
        var fashn = new ScriptedTryOn((_, options, _) =>
        {
            Assert.Equal("pred-extra", options.ResumeRequestId); // yalnızca sorgu
            throw new ProviderException(GenerationErrorCode.ProviderUnavailable, "failed", retryable: true) { RequestEnded = true };
        });
        await using var harness = BuildHarness(fashn);
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);
        var (scope, db) = OpenDb();
        using (scope)
        {
            // Kurtarmanın verdiği çözüm geçişi: deneme sınırı 3 → 4, işaret kodu OutcomeUncertain
            await db.GenerationJobs.IgnoreQueryFilters().Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.MaxAttempts, 4).SetProperty(j => j.Attempt, 4).SetProperty(j => j.ErrorCode, GenerationErrorCode.OutcomeUncertain)
                .SetProperty(j => j.Provider, "Fashn").SetProperty(j => j.ProviderRequestId, "pred-extra")
                .SetProperty(j => j.ProviderSubmitStartedAt, DateTimeOffset.UtcNow.AddMinutes(-5)),
                TestContext.Current.CancellationToken);
        }

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, _, _) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Failed, job.Status); // sağlayıcı başarısız dedi (ücretsiz); deneme hakkı yok, yeni istek açılmadı
        Assert.Equal(GenerationErrorCode.ProviderUnavailable, job.ErrorCode);
        Assert.Equal(1, fashn.Calls);
    }

    [Fact]
    public async Task Kilit_suresi_dolan_son_denemede_ucretli_gonderim_basladiysa_is_zaman_asimi_sayilmaz()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, client, catalog) = await TenantAsync();
        await using var harness = BuildHarness();
        var (submitted, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);
        var (notSubmitted, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);
        var (extraPassCrashed, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);
        var expired = DateTimeOffset.UtcNow.AddMinutes(-1);

        // Paylaşılan test veritabanı: kurtarma, geri alınan bir işlem içinde sınanır (ana worker'ın kuyruğuna hiçbir şey bırakılmaz)
        var (scope, db) = OpenDb();
        using (scope)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            async Task Set(Guid id, int attempt, int max, bool intent, GenerationErrorCode? code = null) => await db.GenerationJobs.IgnoreQueryFilters().Where(j => j.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.MaxAttempts, max).SetProperty(j => j.Attempt, attempt).SetProperty(j => j.ErrorCode, code).SetProperty(j => j.LeaseUntil, expired)
                    .SetProperty(j => j.ProviderSubmitStartedAt, intent ? expired : null), ct);
            await Set(submitted, 3, 3, intent: true, GenerationErrorCode.ProviderUnavailable);
            await Set(notSubmitted, 3, 3, intent: false);
            await Set(extraPassCrashed, 4, 4, intent: true, GenerationErrorCode.OutcomeUncertain); // çözüm geçişi zaten verilmişti

            await harness.Processor.RecoverExpiredLeasesAsync(db, ct);

            async Task<(GenerationJobStatus, GenerationErrorCode?, int)> State(Guid id) => await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking()
                .Where(j => j.Id == id).Select(j => ValueTuple.Create(j.Status, j.ErrorCode, j.MaxAttempts)).SingleAsync(ct);
            var resolution = await State(submitted);
            var timedOut = await State(notSubmitted);
            var crashedTwice = await State(extraPassCrashed);

            // Çözüm geçişi kuyruktan alınabilir: deneme sayısı artınca ck_generation_jobs_attempts ihlal edilmez
            var claimed = await db.GenerationJobs.IgnoreQueryFilters().Where(j => j.Id == submitted)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, GenerationJobStatus.Running).SetProperty(j => j.Attempt, j => j.Attempt + 1), ct);
            await transaction.RollbackAsync(ct);

            Assert.Equal((GenerationJobStatus.Queued, GenerationErrorCode.OutcomeUncertain, 4), resolution); // tek çözüm geçişi
            Assert.Equal(1, claimed);
            Assert.Equal((GenerationJobStatus.Failed, GenerationErrorCode.Timeout, 3), timedOut); // ücretli adıma gelinmemişti
            Assert.Equal((GenerationJobStatus.Failed, GenerationErrorCode.OutcomeUncertain, 4), crashedTwice); // ikinci geçiş verilmez
        }
    }

    // ---------------------------------------------------------------- M3.1: belirsiz maliyet kaydı (atomik, tekil, tekrar çalıştırılabilir)

    private static ScriptedTryOn UncertainThenSuccess() => new(async (n, options, _) =>
    {
        await options.OnSubmitting!();
        if (n == 1)
        {
            throw ProviderHttp.Uncertain(new ProviderException(GenerationErrorCode.Timeout, "yanıt yok", retryable: true));
        }

        await options.OnSubmitted!($"pred-{n}");
        return Result("Fashn", "tryon-max", $"pred-{n}", 0.15m, 2, 0.15m);
    });

    private async Task<AiUsageLog> UncertainRowAsync(Guid jobId)
    {
        var (scope, db) = OpenDb();
        using (scope)
        {
            return await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.EventKey == $"uncertain:{jobId}", TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Belirsiz_islem_rezerve_tutarla_tekil_olay_anahtariyla_Open_kaydedilir_tekrar_calisinca_ikinci_kayit_olmaz()
    {
        var (tenant, client, catalog) = await TenantAsync();
        var fashn = UncertainThenSuccess();
        await using var harness = BuildHarness(fashn);
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, logs, _) = await LoadAsync(jobId);
        Assert.Equal(0.15m, job.ReservedCostUsd); // tutar gönderim niyetiyle aynı kayıtta yazıldı
        var row = await UncertainRowAsync(jobId);
        Assert.Equal((0.15m, CostReconciliationState.Open, false), (row.EstimatedCostUsd, row.ReconciliationState, row.IsAdjustment));
        Assert.Single(logs);

        // Kurtarma/yeniden işleme: iş yeniden "Running" olup işlenirse aynı olay ikinci kez yazılmaz, sağlayıcı çağrılmaz
        await ReclaimAsync(jobId);
        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (again, againLogs, _) = await LoadAsync(jobId);
        Assert.Equal(GenerationErrorCode.OutcomeUncertain, again.ErrorCode);
        Assert.Single(againLogs);
        Assert.Equal(1, fashn.Calls);
    }

    [Fact]
    public async Task Kurtarma_ikinci_cokmede_isi_belirsiz_yapar_maliyet_satirini_ayni_islemde_yazar_tekrar_calisinca_cogalmaz()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, client, catalog) = await TenantAsync();
        await using var harness = BuildHarness();
        var (reserved, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);
        var (legacy, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet); // eski kayıt: tutar yazılmamış
        var (live, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet); // kilidi geçerli: dokunulmaz
        var expired = DateTimeOffset.UtcNow.AddMinutes(-1);

        // Paylaşılan veritabanı: kurtarma geri alınan bir işlemde sınanır (ana worker'ın kuyruğuna bir şey bırakılmaz)
        var (scope, db) = OpenDb();
        using (scope)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            async Task Crashed(Guid id, decimal? reservedCost) => await db.GenerationJobs.IgnoreQueryFilters().Where(j => j.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.MaxAttempts, 4).SetProperty(j => j.Attempt, 4).SetProperty(j => j.ErrorCode, GenerationErrorCode.OutcomeUncertain)
                .SetProperty(j => j.Provider, "Fashn").SetProperty(j => j.Model, "tryon-max").SetProperty(j => j.LeaseUntil, expired)
                .SetProperty(j => j.ProviderSubmitStartedAt, expired).SetProperty(j => j.ReservedCostUsd, reservedCost), ct);
            await Crashed(reserved, 0.15m);
            await Crashed(legacy, null);

            await harness.Processor.RecoverExpiredLeasesAsync(db, ct);
            await harness.Processor.RecoverExpiredLeasesAsync(db, ct); // tekrar çalıştırma

            foreach (var (id, expectedCost) in new[] { (reserved, 0.15m), (legacy, 0.5m) }) // eksik tutar: iş başı bütçe sınırı (0,5) güvenli üst sınır
            {
                var state = await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking().Where(j => j.Id == id).Select(j => new { j.Status, j.ErrorCode }).SingleAsync(ct);
                Assert.Equal((GenerationJobStatus.Failed, GenerationErrorCode.OutcomeUncertain), (state.Status, state.ErrorCode));
                var rows = await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().Where(l => l.JobId == id).ToListAsync(ct);
                var row = Assert.Single(rows); // iş durumu ile aynı transaction: "belirsiz ama bütçe görmüyor" durumu yok; ikinci çalıştırma ikinci satır yazmadı
                Assert.Equal(($"uncertain:{id}", expectedCost, CostReconciliationState.Open, "Fashn"), (row.EventKey, row.EstimatedCostUsd, row.ReconciliationState, row.Provider));
                Assert.Equal(GenerationErrorCode.OutcomeUncertain, row.ErrorCode);
            }

            var untouched = await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking().Where(j => j.Id == live).Select(j => j.Status).SingleAsync(ct);
            Assert.Equal(GenerationJobStatus.Running, untouched);
            Assert.Empty(await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().Where(l => l.JobId == live).ToListAsync(ct));
            await transaction.RollbackAsync(ct);
        }
    }

    [Fact]
    public async Task Suprucu_eksik_maliyet_satirini_ekler_eski_kaydi_duzeltir_ikinci_calistirmada_hicbir_sey_degismez()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, client, catalog) = await TenantAsync();
        var (missing, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);
        var (legacy, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);
        var sweep = Services.GetRequiredService<CostReconciliationSweep>();

        var (scope, db) = OpenDb();
        using (scope)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            foreach (var id in new[] { missing, legacy })
            {
                await db.GenerationJobs.IgnoreQueryFilters().Where(j => j.Id == id).ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, GenerationJobStatus.Failed).SetProperty(j => j.ErrorCode, GenerationErrorCode.OutcomeUncertain)
                    .SetProperty(j => j.Provider, "Fashn").SetProperty(j => j.Model, "tryon-max").SetProperty(j => j.ReservedCostUsd, 0.15m).SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null), ct);
            }

            // R4 döneminde yazılmış kayıt: olay anahtarı ve mutabakat durumu yok
            db.AiUsageLogs.Add(new AiUsageLog
            {
                OrganizationId = tenant.OrganizationId, StoreId = tenant.StoreId, JobId = legacy, Provider = "Fashn", Model = "tryon-max", Operation = "OutfitSet",
                Attempt = 1, Success = false, ErrorCode = GenerationErrorCode.OutcomeUncertain, EstimatedCostUsd = 0.15m,
            });
            await db.SaveChangesAsync(ct);

            async Task<List<AiUsageLog>> RowsAsync(Guid id) => await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().Where(l => l.JobId == id).ToListAsync(ct);
            var first = await sweep.RunOnceAsync(db, ct);
            Assert.True(first.MissingCostRows >= 1 && first.LegacyKeyed >= 1);

            var missingRow = Assert.Single(await RowsAsync(missing));
            Assert.Equal(($"uncertain:{missing}", 0.15m, CostReconciliationState.Open), (missingRow.EventKey, missingRow.EstimatedCostUsd, missingRow.ReconciliationState));
            var legacyRow = Assert.Single(await RowsAsync(legacy)); // ikinci satır eklenmedi: eski satır düzeltildi
            Assert.Equal(($"uncertain:{legacy}", CostReconciliationState.Open), (legacyRow.EventKey, legacyRow.ReconciliationState));

            await sweep.RunOnceAsync(db, ct); // tekrar çalıştırma
            Assert.Equal(missingRow.Id, Assert.Single(await RowsAsync(missing)).Id);
            Assert.Equal(legacyRow.Id, Assert.Single(await RowsAsync(legacy)).Id);
            await transaction.RollbackAsync(ct);
        }
    }

    [Fact]
    public async Task Belirsiz_maliyet_butceyi_tutar_ucretlenmedi_karari_butceyi_serbest_birakir_yeni_is_ancak_o_zaman_gecer()
    {
        var (tenant, client, catalog) = await TenantAsync();
        var fashn = UncertainThenSuccess();
        await using var harness = BuildHarness(fashn, daily: 0.20m); // tek çağrının en kötü tutarı 0,15
        var (first, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);
        var (blocked, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);
        var (after, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);
        var budget = harness.Services.GetRequiredService<BudgetGuard>();

        await harness.Processor.ProcessClaimedAsync(first, TestContext.Current.CancellationToken);
        await harness.Processor.ProcessClaimedAsync(blocked, TestContext.Current.CancellationToken);

        Assert.Equal(GenerationErrorCode.BudgetExceeded, (await LoadAsync(blocked)).Job.ErrorCode); // belirsiz 0,15 bütçede tutuluyor
        Assert.Equal(1, fashn.Calls);

        var caseRow = await UncertainRowAsync(first);
        var (scope, db) = OpenDb();
        using (scope)
        {
            Assert.Equal(0.15m, (await budget.GetSpendAsync(db, tenant.OrganizationId, null, TestContext.Current.CancellationToken)).Today);
            var decided = await Services.GetRequiredService<CostReconciliationService>().DecideAsync(
                db, caseRow.Id, Guid.NewGuid(), Guid.NewGuid().ToString("N"), new DecideCommand(CostDecision.ConfirmedNotCharged, null, "Panelde yok", 0), null, TestContext.Current.CancellationToken);
            Assert.Equal(DecisionOutcome.Recorded, decided.Outcome);
        }

        (scope, db) = OpenDb();
        using (scope)
        {
            Assert.Equal(0m, (await budget.GetSpendAsync(db, tenant.OrganizationId, null, TestContext.Current.CancellationToken)).Today); // düzeltme asıl günün bütçesine yazıldı
        }

        await harness.Processor.ProcessClaimedAsync(after, TestContext.Current.CancellationToken);
        Assert.Equal(GenerationJobStatus.Succeeded, (await LoadAsync(after)).Job.Status);
    }

    [Fact]
    public async Task Belirsiz_maliyet_kaydi_oturum_silinse_de_kalir_mutabakat_yapilabilir()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, client, catalog) = await TenantAsync();
        await using var harness = BuildHarness(UncertainThenSuccess());
        var (jobId, sessionId) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);
        await harness.Processor.ProcessClaimedAsync(jobId, ct);
        var caseRow = await UncertainRowAsync(jobId);

        // KVKK silmesi: oturum ve bağlı işler gider; maliyet/mutabakat kaydı kişisel veri içermediği için kalır
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.TryOnSessions.IgnoreQueryFilters().Where(s => s.Id == sessionId).ExecuteDeleteAsync(ct);
        }

        (scope, db) = OpenDb();
        using (scope)
        {
            Assert.False(await db.GenerationJobs.IgnoreQueryFilters().AnyAsync(j => j.Id == jobId, ct));
            var survived = await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == caseRow.Id, ct);
            Assert.Equal((null, CostReconciliationState.Open, 0.15m), (survived.JobId, survived.ReconciliationState, survived.EstimatedCostUsd));

            var decided = await Services.GetRequiredService<CostReconciliationService>().DecideAsync(
                db, caseRow.Id, Guid.NewGuid(), Guid.NewGuid().ToString("N"), new DecideCommand(CostDecision.ConfirmedCharged, 0.12m, null, 0), null, ct);
            Assert.Equal(DecisionOutcome.Recorded, decided.Outcome);
            Assert.Null(decided.Decision!.JobId);
        }
    }

    [Fact]
    public async Task Gecersiz_cikti_ucreti_kaydedilir_ve_tekrar_denenmez()
    {
        var (tenant, client, catalog) = await TenantAsync();
        var flat = new ProviderResult([new GeneratedOutput(TestFlatJpeg(), "image/jpeg")], "Fashn", "tryon-max", "pred-flat", new ProviderUsage(900, 3, null, null, 0.225m, 0.225m));
        await using var harness = BuildHarness(new ScriptedTryOn((_, _, _) => Task.FromResult(flat)));
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, logs, images) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Failed, job.Status); // Queued değil: otomatik yeniden deneme çift ücret olurdu
        Assert.Equal(GenerationErrorCode.InvalidOutput, job.ErrorCode);
        Assert.Empty(images);
        var log = Assert.Single(logs);
        Assert.False(log.Success);
        Assert.Equal(0.225m, log.EstimatedCostUsd); // ücret doğdu ve kayıtlı
        Assert.Equal(1, harness.Fashn.Calls);
    }

    [Fact]
    public async Task Bes_parcali_kombin_gemini_pro_ile_katman_sirasinda_ve_v2_prompt_ile_gonderilir()
    {
        var (tenant, client, catalog) = await TenantAsync();
        await using var harness = BuildHarness(liveCombination: true); // eski Gemini yolu yalnızca açıkça etkinleştirilince
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.FreeCombination);

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, logs, images) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Succeeded, job.Status);
        Assert.Equal("gemini-3-pro-image", job.Model);
        Assert.Equal("free-combination.v2", job.PromptTemplateVersion);
        Assert.Equal(5, harness.Gemini.LastGarments.Count);
        Assert.Equal(harness.Gemini.LastGarments.Select(g => g.LayerOrder).Order().ToList(), harness.Gemini.LastGarments.OrderBy(g => g.LayerOrder).Select(g => g.LayerOrder).ToList());
        Assert.Contains("Images 2 to 6", harness.Gemini.LastPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("customer", harness.Gemini.LastPrompt!, StringComparison.OrdinalIgnoreCase); // müşteri bilgisi prompt'a girmez
        Assert.False(Assert.Single(images).IsDemo);
        Assert.Equal(0.1562m, Assert.Single(logs).EstimatedCostUsd);
    }

    [Theory]
    [InlineData("gemini-3.1-flash-lite-image")]
    [InlineData("gemini-uydurma-model")]
    public async Task Kombin_icin_uygun_olmayan_veya_dogrulanmamis_model_cagrilmaz(string model)
    {
        var (tenant, client, catalog) = await TenantAsync();
        await using var harness = BuildHarness(freeModel: model, liveCombination: true);
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.FreeCombination);

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, _, images) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Failed, job.Status);
        Assert.Equal(GenerationErrorCode.ProviderNotConfigured, job.ErrorCode);
        Assert.Equal(0, harness.Gemini.Calls);
        Assert.Empty(images);
    }

    [Fact]
    public async Task Calisan_is_iptal_edilince_bekleyis_birakilir_sonuc_atilir_en_kotu_maliyet_kaydedilir()
    {
        var (tenant, client, catalog) = await TenantAsync();
        var started = new TaskCompletionSource();
        var fashn = new ScriptedTryOn(async (_, options, ct) =>
        {
            await options.OnSubmitted!("pred-cancel");
            started.SetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), ct); // iptal jetonu olmadan 30 sn beklerdi
            return Result("Fashn", "tryon-max", "pred-cancel", 0.225m);
        });
        await using var harness = BuildHarness(fashn);
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);

        var run = harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var cancel = await client.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{jobId}/cancel", new { });
        Assert.Equal(System.Net.HttpStatusCode.OK, cancel.StatusCode);
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var (job, logs, images) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Cancelled, job.Status); // iş hâlâ iptal; başarı veya hata ile ezilmedi
        Assert.Empty(images);
        var log = Assert.Single(logs);
        Assert.False(log.Success);
        Assert.Equal(0.15m, log.EstimatedCostUsd); // çağrı başlamıştı: en kötü durum (1k = 2 kredi) bütçeye işlenir
        Assert.Equal("pred-cancel", log.ProviderRequestId);
    }

    [Fact]
    public async Task Calisan_is_iptal_edilebilir_bitmis_is_edilemez()
    {
        var (tenant, client, catalog) = await TenantAsync();
        await using var harness = BuildHarness();
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);
        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{jobId}/cancel", new { }), System.Net.HttpStatusCode.Conflict, "JOB_NOT_CANCELLABLE");
    }

    [Fact]
    public async Task Maliyet_raporu_yalnizca_yoneticiye_acik_ve_kisisel_veri_icermez()
    {
        var (tenant, manager, staff) = await ManagerAndStaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        await using var harness = BuildHarness();
        var (jobId, _) = await RunningJobAsync(tenant, manager, catalog, OutfitMode.OutfitSet);
        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        await ApiClient.AssertProblemAsync(await staff.GetAsync("/api/v1/admin/usage"), System.Net.HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");

        var response = await manager.GetAsync("/api/v1/admin/usage");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"estimatedCostUsd\":0.15", body, StringComparison.Ordinal);
        Assert.Contains("\"provider\":\"Fashn\"", body, StringComparison.Ordinal);
        Assert.Contains("\"liveVerified\":false", body, StringComparison.Ordinal); // hiçbir model canlı doğrulanmış gösterilmez
        Assert.DoesNotContain(tenant.Email, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Yonetici_kalite_ayari_fashn_parametrelerini_ve_maliyeti_belirler()
    {
        var (tenant, manager, _) = await ManagerAndStaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        await using var harness = BuildHarness();

        // Varsayılan: balanced + 1k
        var defaults = await manager.GetAsync<SanalProva.Api.Features.Admin.AiQualityDto>("/api/v1/admin/ai-quality");
        Assert.Equal(("1k", "balanced", false), (defaults.Resolution, defaults.Mode, defaults.IsOverride));
        Assert.Equal(2m, defaults.Options.Single(o => o is { Resolution: "1k", Mode: "balanced" }).Credits);
        Assert.Equal(0.225m, defaults.Options.Single(o => o is { Resolution: "2k", Mode: "balanced" }).EstimatedUsdPerImage);
        Assert.DoesNotContain(defaults.Options, o => o.Resolution == "4k");

        // 2k seçilince sonraki üretim 2k ile gider ve 3 kredi / 0,225 $ olarak kaydedilir
        var changed = await manager.PutAsync<SanalProva.Api.Features.Admin.AiQualityDto>("/api/v1/admin/ai-quality", new { resolution = "2k", mode = "balanced" });
        Assert.True(changed.IsOverride);
        var (jobId, _) = await RunningJobAsync(tenant, manager, catalog, OutfitMode.OutfitSet);
        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        Assert.Equal("2k", harness.Fashn.Seen[0].Parameters["resolution"]);
        var (job, logs, _) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Succeeded, job.Status);
        Assert.Equal(0.225m, Assert.Single(logs).EstimatedCostUsd);
    }

    [Fact]
    public async Task Butce_kapisi_secilen_kaliteye_gore_en_kotu_maliyeti_kullanir()
    {
        var (tenant, manager, _) = await ManagerAndStaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        await using var harness = BuildHarness(maxJob: 0.20m); // 1k (0,15) sığar, 2k (0,225) sığmaz

        var (cheap, _) = await RunningJobAsync(tenant, manager, catalog, OutfitMode.OutfitSet);
        await harness.Processor.ProcessClaimedAsync(cheap, TestContext.Current.CancellationToken);
        Assert.Equal(GenerationJobStatus.Succeeded, (await LoadAsync(cheap)).Job.Status);

        await manager.PutAsync<SanalProva.Api.Features.Admin.AiQualityDto>("/api/v1/admin/ai-quality", new { resolution = "2k", mode = "balanced" });
        var (expensive, _) = await RunningJobAsync(tenant, manager, catalog, OutfitMode.OutfitSet);
        await harness.Processor.ProcessClaimedAsync(expensive, TestContext.Current.CancellationToken);

        var (job, _, _) = await LoadAsync(expensive);
        Assert.Equal(GenerationErrorCode.BudgetExceeded, job.ErrorCode);
        Assert.Equal(1, harness.Fashn.Calls); // ikinci iş için sağlayıcıya gidilmedi
    }

    [Fact]
    public async Task Kalite_ayari_yalnizca_yoneticiye_acik_ve_gecersiz_degerleri_reddeder()
    {
        var (_, manager, staff) = await ManagerAndStaffAsync();

        await ApiClient.AssertProblemAsync(await staff.GetAsync("/api/v1/admin/ai-quality"), System.Net.HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        await ApiClient.AssertProblemAsync(await staff.SendAsync(HttpMethod.Put, "/api/v1/admin/ai-quality", new { resolution = "1k", mode = "balanced" }), System.Net.HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        foreach (var bad in new[] { ("4k", "balanced"), ("3k", "balanced"), ("1k", "ultra"), ("", "") })
        {
            await ApiClient.AssertProblemAsync(
                await manager.SendAsync(HttpMethod.Put, "/api/v1/admin/ai-quality", new { resolution = bad.Item1, mode = bad.Item2 }), System.Net.HttpStatusCode.BadRequest, "INVALID_QUALITY");
        }

        var unchanged = await manager.GetAsync<SanalProva.Api.Features.Admin.AiQualityDto>("/api/v1/admin/ai-quality");
        Assert.False(unchanged.IsOverride);
    }

    [Fact]
    public async Task Tek_kaftan_seedream_ile_uretilir_model_kalite_ve_maliyet_kaydedilir()
    {
        var (tenant, client, catalog) = await TenantAsync();
        await using var harness = BuildHarness();
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet, TryOnModels.Seedream, "standard", kaftan: true);

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, logs, images) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Succeeded, job.Status);
        Assert.Equal("FalSeedream", job.Provider);
        Assert.Equal("bytedance/seedream/v5/flash/edit", job.Model);
        Assert.Equal("single-garment.v1", job.PromptTemplateVersion);
        Assert.Equal(0, harness.Fashn.Calls); // eski FASHN çağrılmaz

        var sent = Assert.Single(harness.TryOn.Seen);
        Assert.Equal("auto_2K", sent.Parameters["image_size"]);
        Assert.Contains("Image 2", sent.Parameters["prompt"], StringComparison.Ordinal);
        Assert.Contains("Zümrüt Kaftan", sent.Parameters["prompt"], StringComparison.Ordinal);
        Assert.DoesNotContain("customer", sent.Parameters["prompt"], StringComparison.OrdinalIgnoreCase);

        Assert.False(Assert.Single(images).IsDemo);
        var log = Assert.Single(logs);
        Assert.Equal("standard", log.Quality);
        Assert.Equal(0.027m, log.EstimatedCostUsd);
        Assert.Null(log.ActualCostUsd); // fal istek başına maliyet bildirmez: tahmin
    }

    [Fact]
    public async Task Gpt_image_kalitesi_parametreyi_maliyeti_ve_butce_kapisini_belirler()
    {
        var (tenant, client, catalog) = await TenantAsync();
        await using var harness = BuildHarness(maxJob: 0.05m); // medium en kötü ≈ 0,03–0,04 sığar; high en kötü (çıktı ≥ 0,036 + iki referans + prompt) sığmaz
        var (medium, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet, TryOnModels.GptImage, "medium", kaftan: true);
        var (high, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet, TryOnModels.GptImage, "high", kaftan: true);

        await harness.Processor.ProcessClaimedAsync(medium, TestContext.Current.CancellationToken);
        await harness.Processor.ProcessClaimedAsync(high, TestContext.Current.CancellationToken);

        var (mediumJob, mediumLogs, _) = await LoadAsync(medium);
        Assert.Equal(GenerationJobStatus.Succeeded, mediumJob.Status);
        Assert.Equal("openai/gpt-image-2.5/sunburst/edit", mediumJob.Model);
        Assert.Equal("medium", Assert.Single(harness.TryOn.Seen).Parameters["quality"]);
        var log = Assert.Single(mediumLogs);
        Assert.Equal("medium", log.Quality);
        var (person, garment) = Assert.Single(harness.TryOn.Inputs);
        var model = TryOnModels.Find(TryOnModels.GptImage)!;
        var expected = TryOnModels.Price(model, model.Quality("medium")!, new PricingSettings(), ProviderHttp.CostInput(harness.TryOn.Seen[0], person, garment)).Estimated;
        Assert.Equal(expected, log.EstimatedCostUsd); // kalite × çözünürlük + iki referans görsel + prompt
        Assert.InRange(log.EstimatedCostUsd, 0.02m, 0.05m);

        var (highJob, _, _) = await LoadAsync(high);
        Assert.Equal(GenerationErrorCode.BudgetExceeded, highJob.ErrorCode);
        Assert.Equal(1, harness.TryOn.Calls); // high için sağlayıcıya gidilmedi
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public async Task Gpt_image_pahali_kaliteler_saglayiciya_acikca_gider_ve_maliyeti_kaydedilir(string quality)
    {
        var (tenant, client, catalog) = await TenantAsync();
        await using var harness = BuildHarness(maxJob: 0.5m);
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet, TryOnModels.GptImage, quality, kaftan: true);

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, logs, _) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Succeeded, job.Status);
        Assert.Equal(quality, job.Quality);
        var sent = Assert.Single(harness.TryOn.Seen);
        Assert.Equal(quality, sent.Parameters["quality"]); // şema varsayılanına (high) bırakılmaz
        var log = Assert.Single(logs);
        Assert.Equal(quality, log.Quality);
        var (person, garment) = Assert.Single(harness.TryOn.Inputs);
        var model = TryOnModels.Find(TryOnModels.GptImage)!;
        var input = ProviderHttp.CostInput(sent, person, garment);
        Assert.Equal(TryOnModels.Price(model, model.Quality(quality)!, new PricingSettings(), input).Estimated, log.EstimatedCostUsd);
        var medium = TryOnModels.Price(model, model.Quality("medium")!, new PricingSettings(), input).Estimated;
        Assert.True(log.EstimatedCostUsd > medium, $"{quality} tahmini medium'dan yüksek olmalı");
    }

    [Fact]
    public async Task Gpt_image_max_is_basi_siniri_asarsa_saglayici_cagrilmaz()
    {
        var (tenant, client, catalog) = await TenantAsync();
        await using var harness = BuildHarness(maxJob: 0.10m); // max'ın en kötü çıktısı tek başına ≥ 0,144 $
        var (maxJob, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet, TryOnModels.GptImage, "max", kaftan: true);
        var (autoJob, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet, TryOnModels.GptImage, "auto", kaftan: true);

        await harness.Processor.ProcessClaimedAsync(maxJob, TestContext.Current.CancellationToken);
        await harness.Processor.ProcessClaimedAsync(autoJob, TestContext.Current.CancellationToken);

        Assert.Equal(GenerationErrorCode.BudgetExceeded, (await LoadAsync(maxJob)).Job.ErrorCode);
        Assert.Equal(GenerationErrorCode.BudgetExceeded, (await LoadAsync(autoJob)).Job.ErrorCode); // auto en kötü durumu max sütunudur
        Assert.Equal(0, harness.TryOn.Calls);
    }

    [Fact]
    public async Task Pruna_turbo_parametresi_gider_serbest_metin_gonderilmez()
    {
        var (tenant, client, catalog) = await TenantAsync();
        await using var harness = BuildHarness();
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet, TryOnModels.Pruna, "turbo");

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, logs, _) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Succeeded, job.Status);
        Assert.Equal("pruna-tryon.v1", job.PromptTemplateVersion);
        var sent = Assert.Single(harness.TryOn.Seen);
        Assert.Equal("true", sent.Parameters["turbo"]);
        Assert.False(sent.Parameters.ContainsKey("prompt"));
        Assert.Equal(0.008m, Assert.Single(logs).EstimatedCostUsd);
    }

    [Fact]
    public async Task Bes_parcali_kombin_gercek_modda_varsayilan_olarak_calismaz()
    {
        var (tenant, client, catalog) = await TenantAsync();
        await using var harness = BuildHarness(); // LiveFreeCombinationEnabled = false (varsayılan)
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.FreeCombination);

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        var (job, _, images) = await LoadAsync(jobId);
        Assert.Equal(GenerationJobStatus.Failed, job.Status);
        Assert.Equal(GenerationErrorCode.ProviderNotConfigured, job.ErrorCode);
        Assert.Equal(0, harness.Gemini.Calls);
        Assert.Empty(images);
    }

    private static byte[] TestFlatJpeg()
    {
        using var bitmap = new SkiaSharp.SKBitmap(900, 1500);
        bitmap.Erase(new SkiaSharp.SKColor(0, 0, 0));
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        return image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90).ToArray();
    }
}
