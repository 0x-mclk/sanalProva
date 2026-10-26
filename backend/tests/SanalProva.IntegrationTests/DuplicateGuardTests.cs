using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SanalProva.Api.Features.TryOn;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Generation;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.IntegrationTests;

/// <summary>
/// "Gerçek sağlayıcı" modunda (UseMockProviders=false) çalışan ikinci API ana makinesi. Üretim worker'ı kaydedilmez: bu ana makine hiçbir işi
/// kendisi işlemez (ücretli çağrı da yapılamaz: ücretli çağrı bayrağı kapalı, anahtar yok); yalnızca istek doğrulaması sınanır.
/// Aynı veritabanını paylaşan ana test makinesinin worker'ı (mock) kuyruktaki işleri alabilir; testler iş durumundan bağımsızdır.
/// </summary>
public sealed class RealModeApiFactory : WebApplicationFactory<Program>
{
    private readonly string _storageRoot;
    private readonly decimal? _maxJobCostUsd;

    /// <param name="storageRoot">Ana test sunucusunun depo kökü: aynı veritabanındaki işleri ana worker işleyebildiğinde dosyaları da bulabilsin.</param>
    /// <param name="maxJobCostUsd">İş başı bütçe sınırı (verilmezse yapılandırma varsayılanı).</param>
    public RealModeApiFactory(string storageRoot, decimal? maxJobCostUsd = null)
    {
        _storageRoot = storageRoot;
        _maxJobCostUsd = maxJobCostUsd;
        var secrets = new ConfigurationBuilder().AddUserSecrets(typeof(Program).Assembly, optional: true).Build();
        ConnectionString = Environment.GetEnvironmentVariable("SANALPROVA_TEST_DB") ?? secrets.GetConnectionString("Test");
    }

    public string? ConnectionString { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Main", ConnectionString ?? ApiFactory.NoDatabase);
        builder.UseSetting("Seed:Enabled", "false");
        builder.UseSetting("Security:LoginRateLimitPerMinute", "100000");
        builder.UseSetting("Storage:LocalRoot", _storageRoot);
        builder.UseSetting("Storage:SigningKey", new string('t', 48));
        builder.UseSetting("ImageWorker:BaseUrl", "http://127.0.0.1:1");
        builder.UseSetting("Generation:UseMockProviders", "false");
        builder.UseSetting("Generation:Budget:AllowPaidCalls", "false");
        builder.UseSetting("Generation:PollIntervalMs", "3600000");
        builder.UseSetting("Retention:SweepIntervalSeconds", "0");
        builder.UseSetting("Display:SweepIntervalSeconds", "0");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        if (_maxJobCostUsd is { } max)
        {
            builder.UseSetting("Generation:Budget:MaxJobCostUsd", max.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Bu host yalnızca istek doğrulaması içindir: üretim worker'ı hiç çalışmaz. Uzun yoklama aralığı yetmez, çünkü ilk yoklama host
        // başlar başlamaz yapılır; paylaşılan veritabanında başka testlerin kuyruktaki işlerini gerçek modda alıp başarısız ediyordu
        // (bildirimler de bu host'un hub'ına gidiyordu).
        builder.ConfigureTestServices(services =>
        {
            var worker = services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(GenerationWorker));
            services.Remove(worker);
        });
    }

}

public sealed class DuplicateGuardTests(ApiFactory mainFactory) : IDisposable
{
    private readonly RealModeApiFactory _real = new(mainFactory.StorageRoot);

    public void Dispose() => _real.Dispose();

    [Fact]
    public async Task Gercek_saglayicida_ayni_fotograf_ve_secimle_ikinci_istek_yeni_ucretli_is_acmaz()
    {
        ApiFactory.RequireDatabase(mainFactory);
        var tenant = await TestData.CreateTenantAsync(_real.Services);
        var catalog = await TestData.CreateCatalogAsync(_real.Services, tenant.OrganizationId);
        var client = await ApiClient.LoginAsync(_real, tenant.Email, tenant.StoreId);
        var (session, _) = await ApiTestBase.PreparedSessionAsync(client);
        await ApiTestBase.SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);

        var first = await ApiTestBase.StartGenerationAsync(client, session.Id, "k1");
        var duplicate = await ApiTestBase.StartGenerationAsync(client, session.Id, "k2");

        Assert.Equal(first.Id, duplicate.Id); // aynı istek: mevcut iş döner, ikinci ücret yok

        // Bilerek yeni varyasyon istenirse yeni iş açılır
        var variation = await client.PostAsync<JobDto>($"/api/v1/sessions/{session.Id}/generations", new { allowVariation = true }, HttpStatusCode.Accepted,
            r => r.Headers.Add("Idempotency-Key", "k3"));
        Assert.NotEqual(first.Id, variation.Id);

        // Aynı seçim farklı model/kaliteyle bilinçli yeni istektir: yeni iş açılır; aynı model+kalite tekrarı yine mevcut işi döner
        var otherModel = await client.PostAsync<JobDto>($"/api/v1/sessions/{session.Id}/generations", new { modelKey = "gpt-image-2.5", quality = "medium" }, HttpStatusCode.Accepted,
            r => r.Headers.Add("Idempotency-Key", "k4"));
        var otherModelAgain = await client.PostAsync<JobDto>($"/api/v1/sessions/{session.Id}/generations", new { modelKey = "gpt-image-2.5", quality = "medium" }, HttpStatusCode.Accepted,
            r => r.Headers.Add("Idempotency-Key", "k5"));
        Assert.NotEqual(first.Id, otherModel.Id);
        Assert.Equal(otherModel.Id, otherModelAgain.Id);
    }

    [Fact]
    public async Task Gercek_modda_cok_parcali_kombin_reddedilir()
    {
        ApiFactory.RequireDatabase(mainFactory);
        var tenant = await TestData.CreateTenantAsync(_real.Services);
        var catalog = await TestData.CreateCatalogAsync(_real.Services, tenant.OrganizationId);
        var client = await ApiClient.LoginAsync(_real, tenant.Email, tenant.StoreId);
        var (session, _) = await ApiTestBase.PreparedSessionAsync(client);
        await ApiTestBase.SaveDraftAsync(client, session.Id, OutfitMode.FreeCombination, catalog.SalvarProduct, catalog.GomlekProduct);

        var response = await client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{session.Id}/generations", new { }, configure: r => r.Headers.Add("Idempotency-Key", "kombin"));

        await ApiClient.AssertProblemAsync(response, HttpStatusCode.Conflict, "FREE_COMBINATION_DISABLED");
        var options = await client.GetAsync<SanalProva.Api.Features.Admin.TryOnOptionsDto>("/api/v1/ai/try-on-options");
        Assert.True(options.Live);
        Assert.False(options.FreeCombinationEnabled);
    }

    [Fact]
    public async Task Is_basi_butceyi_asan_kalite_isaretlenir_ve_kuyruga_alinmaz()
    {
        ApiFactory.RequireDatabase(mainFactory);
        using var strict = new RealModeApiFactory(mainFactory.StorageRoot, maxJobCostUsd: 0.10m); // max en kötü ≈ 0,42 $; medium ≈ 0,04 $
        var tenant = await TestData.CreateTenantAsync(strict.Services);
        var catalog = await TestData.CreateCatalogAsync(strict.Services, tenant.OrganizationId);
        using (var scope = strict.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var organization = await db.Organizations.IgnoreQueryFilters().SingleAsync(o => o.Id == tenant.OrganizationId, TestContext.Current.CancellationToken);
            organization.Settings.AllowedTryOnQualities = ["gpt-image-2.5:medium", "gpt-image-2.5:max"];
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var client = await ApiClient.LoginAsync(strict, tenant.Email, tenant.StoreId);
        var options = await client.GetAsync<SanalProva.Api.Features.Admin.TryOnOptionsDto>("/api/v1/ai/try-on-options");
        var gpt = options.Models.Single(m => m.Key == "gpt-image-2.5");
        Assert.True(gpt.Qualities.Single(q => q.Key == "max").ExceedsJobLimit);
        Assert.True(gpt.Qualities.Single(q => q.Key == "max").HighCost);
        Assert.False(gpt.Qualities.Single(q => q.Key == "medium").ExceedsJobLimit);

        var (session, _) = await ApiTestBase.PreparedSessionAsync(client);
        await ApiTestBase.SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.KaftanProduct);
        var max = await client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{session.Id}/generations", new { modelKey = "gpt-image-2.5", quality = "max" },
            configure: r => r.Headers.Add("Idempotency-Key", "max"));
        await ApiClient.AssertProblemAsync(max, HttpStatusCode.Conflict, "QUALITY_OVER_BUDGET");

        var medium = await client.PostAsync<JobDto>($"/api/v1/sessions/{session.Id}/generations", new { modelKey = "gpt-image-2.5", quality = "medium" }, HttpStatusCode.Accepted,
            r => r.Headers.Add("Idempotency-Key", "medium"));
        Assert.Equal(("gpt-image-2.5", "medium", "GPT Image 2.5 · Orta"), (medium.ModelKey, medium.Quality, medium.ModelLabel));
    }
}
