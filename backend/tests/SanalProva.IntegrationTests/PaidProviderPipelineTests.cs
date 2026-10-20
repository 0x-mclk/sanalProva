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

    public Task<ProviderResult> TryOnAsync(ImageInput person, ImageInput garment, ProviderRequestOptions options, CancellationToken ct)
    {
        Calls++;
        Seen.Add(options);
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

    private sealed class Harness(ServiceProvider services, ScriptedTryOn fashn, ScriptedCompose gemini) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;
        public ScriptedTryOn Fashn { get; } = fashn;
        public ScriptedCompose Gemini { get; } = gemini;

        public GenerationProcessor Processor => Services.GetRequiredService<GenerationProcessor>();

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private Harness BuildHarness(
        ScriptedTryOn? fashn = null, ScriptedCompose? gemini = null, bool allowPaid = true, decimal daily = 10m, decimal monthly = 150m, decimal maxJob = 0.5m,
        string freeModel = "gemini-3-pro-image", string setModel = "tryon-max")
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Main"] = Factory.TestConnectionString,
            ["Storage:LocalRoot"] = Factory.StorageRoot,
            ["Storage:SigningKey"] = new string('t', 48),
            ["ImageWorker:BaseUrl"] = "http://127.0.0.1:1",
            ["Generation:UseMockProviders"] = "false",
            ["Generation:RetryBackoffBaseSeconds"] = "0.01",
            ["Generation:CancelPollMs"] = "40",
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
        return new Harness(services.BuildServiceProvider(), fashn, gemini);
    }

    private async Task<(TestTenant Tenant, ApiClient Client, CatalogIds Catalog)> TenantAsync()
    {
        var (tenant, client) = await StaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        return (tenant, client, catalog);
    }

    /// <summary>Oturum + onay + fotoğraf + seçim + "Running" iş (worker'a karışmaz).</summary>
    private async Task<(Guid JobId, Guid SessionId)> RunningJobAsync(TestTenant tenant, ApiClient client, CatalogIds catalog, OutfitMode mode)
    {
        var (session, photo) = await PreparedSessionAsync(client);
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var user = await db.Users.SingleAsync(u => u.Email == tenant.Email, TestContext.Current.CancellationToken);
            var selection = new OutfitSelection { OrganizationId = tenant.OrganizationId, SessionId = session.Id, Mode = mode, CreatedByUserId = user.Id };
            if (mode == OutfitMode.OutfitSet)
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
        await using var harness = BuildHarness(fashn);
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
    public async Task Basarisiz_tahminden_sonra_yeni_tahmin_acilir()
    {
        var (tenant, client, catalog) = await TenantAsync();
        var fashn = new ScriptedTryOn(async (n, options, _) =>
        {
            if (n == 1)
            {
                await options.OnSubmitted!("pred-failed");
                throw new ProviderException(GenerationErrorCode.ProviderUnavailable, "PipelineError", retryable: true);
            }

            Assert.Null(options.ResumeRequestId); // başarısız tahmin ücretsizdir; yenisi açılır
            return Result("Fashn", "tryon-max", "pred-new", 0.225m, 3, 0.225m);
        });
        await using var harness = BuildHarness(fashn);
        var (jobId, _) = await RunningJobAsync(tenant, client, catalog, OutfitMode.OutfitSet);

        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);
        Assert.Null((await LoadAsync(jobId)).Job.ProviderRequestId);
        await ReclaimAsync(jobId);
        await harness.Processor.ProcessClaimedAsync(jobId, TestContext.Current.CancellationToken);

        Assert.Equal(GenerationJobStatus.Succeeded, (await LoadAsync(jobId)).Job.Status);
        Assert.Equal(2, harness.Fashn.Calls);
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
        await using var harness = BuildHarness();
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
        await using var harness = BuildHarness(freeModel: model);
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

    private static byte[] TestFlatJpeg()
    {
        using var bitmap = new SkiaSharp.SKBitmap(900, 1500);
        bitmap.Erase(new SkiaSharp.SKColor(0, 0, 0));
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        return image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90).ToArray();
    }
}
