using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SanalProva.Api.Hubs;
using SanalProva.Infrastructure.Storage;

namespace SanalProva.IntegrationTests;

public sealed class HealthAndSecurityTests(ApiFactory factory)
{
    [Fact]
    public async Task Live_her_zaman_200_doner()
    {
        var response = await factory.CreateClient().GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Ready_veritabanini_saglikli_gorsel_servisi_degraded_raporlar()
    {
        ApiFactory.RequireDatabase(factory);

        var response = await factory.CreateClient().GetAsync("/health/ready", TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var checks = json.RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", checks["database"]);
        Assert.Equal("Healthy", checks["storage"]);
        Assert.Equal("Degraded", checks["image-worker"]); // test ortamında servis yok; uygulama yine çalışır
    }

    [Fact]
    public async Task Kimliksiz_istek_yonlendirme_yerine_401_alir()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/v1/auth/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Dev_status_mock_modu_ve_stratejileri_gosterir()
    {
        ApiFactory.RequireDatabase(factory);

        var status = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/dev/status", TestContext.Current.CancellationToken);

        Assert.True(status.GetProperty("useMockProviders").GetBoolean());
        Assert.Empty(status.GetProperty("database").GetProperty("pendingMigrations").EnumerateArray());
        var outfitSet = status.GetProperty("strategies").EnumerateArray().Single(s => s.GetProperty("jobType").GetString() == "OutfitSet");
        Assert.Equal("Mock", outfitSet.GetProperty("provider").GetString());
        Assert.Equal("Fashn", outfitSet.GetProperty("configuredProvider").GetString());
    }
}

public sealed class AuthTests(ApiFactory factory)
{
    private static async Task<string> GetCsrfAsync(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf", TestContext.Current.CancellationToken);
        return csrf.GetProperty("token").GetString()!;
    }

    private static HttpRequestMessage Post(string url, object body, string? csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (csrf is not null)
        {
            request.Headers.Add("X-CSRF-TOKEN", csrf);
        }

        return request;
    }

    [Fact]
    public async Task Csrf_tokeni_olmayan_giris_reddedilir()
    {
        ApiFactory.RequireDatabase(factory);
        var tenant = await TestData.CreateTenantAsync(factory.Services);
        var client = factory.CreateClient();

        using var response = await client.SendAsync(
            Post("/api/v1/auth/login", new { email = tenant.Email, password = tenant.Password, rememberMe = false }, csrf: null),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("CSRF_INVALID", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gecerli_giris_cerez_verir_ve_me_firma_ile_magazalari_doner()
    {
        ApiFactory.RequireDatabase(factory);
        var tenant = await TestData.CreateTenantAsync(factory.Services);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var login = await client.SendAsync(
            Post("/api/v1/auth/login", new { email = tenant.Email, password = tenant.Password, rememberMe = false }, await GetCsrfAsync(client)), ct);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("sp_auth=", StringComparison.Ordinal) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));

        var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me", ct);
        Assert.Equal(tenant.OrganizationId, me.GetProperty("organizationId").GetGuid());
        Assert.False(me.GetProperty("demoCatalog").GetBoolean(), "Gerçek firmalarda \"Örnek\" rozeti gösterilmez");
        var store = me.GetProperty("stores").EnumerateArray().Single();
        Assert.Equal(tenant.StoreId, store.GetProperty("id").GetGuid());
        Assert.Equal("Staff", store.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Yanlis_parola_401_ve_genel_mesaj_doner()
    {
        ApiFactory.RequireDatabase(factory);
        var tenant = await TestData.CreateTenantAsync(factory.Services);
        var client = factory.CreateClient();

        using var response = await client.SendAsync(
            Post("/api/v1/auth/login", new { email = tenant.Email, password = "yanlis-parola-1", rememberMe = false }, await GetCsrfAsync(client)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("INVALID_CREDENTIALS", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}

public sealed class FileEndpointTests(ApiFactory factory)
{
    [Fact]
    public async Task Imzali_url_dosyayi_verir_imzasiz_veya_bozuk_url_403_doner()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = factory.Services.GetRequiredService<IImageStorageService>();
        var key = StorageKeys.CatalogFile(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "png");
        await storage.SaveAsync(key, new MemoryStream([1, 2, 3, 4]), "image/png", ct);
        var client = factory.CreateClient();

        var url = storage.GetReadUrl(key, TimeSpan.FromMinutes(1));
        var ok = await client.GetAsync(url, ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("image/png", ok.Content.Headers.ContentType?.MediaType);
        Assert.Equal([1, 2, 3, 4], await ok.Content.ReadAsByteArrayAsync(ct));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/files/{key}?exp=9999999999&sig=sahte", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(storage.GetReadUrl(key, TimeSpan.FromMinutes(-1)), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url.Replace(".png", ".jpg", StringComparison.Ordinal), ct)).StatusCode);
    }
}

public sealed class SignalRTests(ApiFactory factory)
{
    private HubConnection Connect(string path) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, path), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

    [Fact]
    public async Task Tanilama_hubi_ping_yanitlar()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = Connect(DiagnosticsHub.Path);
        await connection.StartAsync(ct);

        var pong = await connection.InvokeAsync<DiagnosticsPong>("Ping", "merhaba", ct);

        Assert.Equal("merhaba", pong.Message);
        Assert.Equal(connection.ConnectionId, pong.ConnectionId);
    }

    [Theory]
    [InlineData(StaffHub.Path)]
    [InlineData(DisplayHub.Path)]
    public async Task Kimliksiz_istemci_personel_ve_tv_hublarina_baglanamaz(string path)
    {
        await using var connection = Connect(path);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }
}
