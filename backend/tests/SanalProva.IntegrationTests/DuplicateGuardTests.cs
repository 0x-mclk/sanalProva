using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using SanalProva.Api.Features.TryOn;
using SanalProva.Domain.TryOn;

namespace SanalProva.IntegrationTests;

/// <summary>
/// "Gerçek sağlayıcı" modunda (UseMockProviders=false) çalışan ikinci API ana makinesi. Worker yoklaması çok uzun aralığa alındığı için bu
/// ana makine hiçbir işi kendisi işlemez (ücretli çağrı yapılamaz: ücretli çağrı bayrağı da kapalı, anahtar da yok); yalnızca istek doğrulaması sınanır.
/// Aynı veritabanını paylaşan ana test makinesinin worker'ı (mock) kuyruktaki işleri alabilir; testler iş durumundan bağımsızdır.
/// </summary>
public sealed class RealModeApiFactory : WebApplicationFactory<Program>
{
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), "sanalprova-it", Guid.NewGuid().ToString("N"));

    public RealModeApiFactory()
    {
        var secrets = new ConfigurationBuilder().AddUserSecrets(typeof(Program).Assembly, optional: true).Build();
        ConnectionString = Environment.GetEnvironmentVariable("SANALPROVA_TEST_DB") ?? secrets.GetConnectionString("Test");
    }

    public string? ConnectionString { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Main", ConnectionString ?? "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none");
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
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }
}

public sealed class DuplicateGuardTests(ApiFactory mainFactory) : IDisposable
{
    private readonly RealModeApiFactory _real = new();

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

        // Seçim değişirse yeni iş açılır
        await ApiTestBase.SaveDraftAsync(client, session.Id, OutfitMode.FreeCombination, catalog.SalvarProduct, catalog.GomlekProduct);
        var different = await ApiTestBase.StartGenerationAsync(client, session.Id, "k4");
        Assert.NotEqual(first.Id, different.Id);
        Assert.NotEqual(variation.Id, different.Id);
    }
}
