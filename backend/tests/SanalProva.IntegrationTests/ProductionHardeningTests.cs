using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SanalProva.Api.Infrastructure;

namespace SanalProva.IntegrationTests;

/// <summary>
/// Pilot Faz A: üretim başlangıç denetimi (eksik/güvensiz yapılandırma → başlamaz) ve güvenilen vekil başlıkları. Veritabanı gerektirmez.
/// Not: Docker/Caddy/TLS ile gerçek uçtan uca doğrulama bu makinede yapılamadı (Docker yok); burada yalnızca uygulama tarafı sınanır.
/// </summary>
public sealed class ProductionHardeningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string SigningKey = "Kq7!vZ2#mR9$tW4&yB6*nE1@cH8^uJ3%";
    private const string SetupKey = "Pw5@dL8!sF2#gQ7$xN4&bV9*kT1^zM6%";

    private static Dictionary<string, string?> Valid() => new()
    {
        ["ConnectionStrings:Main"] = "Server=sqlserver,1433;Database=sanalprova;User ID=sanalprova_app;Password=Zr4-hN8-vK2-qT6;Encrypt=True;TrustServerCertificate=False;HostNameInCertificate=sqlserver",
        ["Storage:SigningKey"] = SigningKey,
        ["Security:SetupTokenKey"] = SetupKey,
        ["Storage:LocalRoot"] = "/data/storage",
        ["DataProtection:KeysPath"] = "/data/dpkeys",
        ["Retention:DeletionJournalPath"] = "/data/journal/deleted-sessions.log",
        ["AllowedHosts"] = "pilot.mağaza.test",
        ["ForwardedHeaders:KnownNetworks:0"] = "172.31.250.0/24",
        ["Generation:UseMockProviders"] = "true",
    };

    private static IReadOnlyList<string> Validate(Action<Dictionary<string, string?>>? change = null)
    {
        var values = Valid();
        change?.Invoke(values);
        return ProductionStartupValidator.Validate(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "t";

        public string ContentRootPath { get; set; } = ".";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    // ---------------------------------------------------------------- üretim başlangıç denetimi

    [Fact]
    public void Gecerli_uretim_yapilandirmasi_hata_vermez()
    {
        Assert.Empty(Validate());
    }

    [Fact]
    public void Encrypt_belirtilmemisse_SqlClient_varsayilani_zorunlu_sifreleme_oldugu_icin_kabul_edilir()
    {
        Assert.Empty(Validate(v => v["ConnectionStrings:Main"] = "Server=sqlserver;Database=sanalprova;User ID=sanalprova_app;Password=Zr4-hN8-vK2-qT6;HostNameInCertificate=sqlserver"));
    }

    [Fact]
    public void Gelistirme_ortami_ve_bos_yapilandirma_denetimden_muaftir_uretimde_bos_yapilandirma_reddedilir()
    {
        var empty = new ConfigurationBuilder().Build();
        ProductionStartupValidator.EnsureValid(empty, new FakeEnvironment("Development")); // hata yok
        ProductionStartupValidator.EnsureValid(empty, new FakeEnvironment("Testing"));
        var ex = Assert.Throws<InvalidOperationException>(() => ProductionStartupValidator.EnsureValid(empty, new FakeEnvironment("Production")));
        Assert.Contains("ConnectionStrings:Main tanımlı değil", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Storage:SigningKey", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ConnectionStrings:Main", "Server=sqlserver;Database=sanalprova;User ID=sanalprova_app;Password=Zr4-hN8-vK2-qT6;Encrypt=False", "Encrypt=True")]
    [InlineData("ConnectionStrings:Main", "Server=sqlserver;Database=sanalprova;User ID=sanalprova_app;Password=Zr4-hN8-vK2-qT6;Encrypt=True;TrustServerCertificate=True", "TrustServerCertificate")]
    [InlineData("ConnectionStrings:Main", "Server=sqlserver;Database=sanalprova;User ID=sa;Password=Zr4-hN8-vK2-qT6;Encrypt=True", "'sa'")]
    [InlineData("ConnectionStrings:Main", "Server=.\\SANALPROVA;Database=sanalprova;Integrated Security=True;Encrypt=True", "'sa'")]
    [InlineData("ConnectionStrings:Main", "Server=sqlserver;Database=sanalprova_dev;User ID=sanalprova_app;Password=Zr4-hN8-vK2-qT6;Encrypt=True", "veritabanı adı")]
    [InlineData("ConnectionStrings:Main", "Server=sqlserver;Database=sanalprova_e2e;User ID=sanalprova_app;Password=Zr4-hN8-vK2-qT6;Encrypt=True", "veritabanı adı")]
    [InlineData("ConnectionStrings:Main", "Server=sqlserver;Database=sanalprova;User ID=sanalprova_app;Password=;Encrypt=True", "parola boş")]
    [InlineData("ConnectionStrings:Main", "bu bir bağlantı dizesi değil", "geçerli bir bağlantı dizesi")]
    [InlineData("Storage:SigningKey", "kisa", "32 karakterden kısa")]
    [InlineData("Storage:SigningKey", "changeme-changeme-changeme-changeme-0", "yer tutucu")]
    [InlineData("Storage:SigningKey", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "yer tutucu")]
    [InlineData("Security:SetupTokenKey", Valid_SigningKey, "farklı değerler")]
    [InlineData("Storage:LocalRoot", "storage/relative", "mutlak yol")]
    [InlineData("DataProtection:KeysPath", "", "DataProtection:KeysPath")]
    [InlineData("Retention:DeletionJournalPath", "", "DeletionJournalPath")]
    [InlineData("Retention:DeletionJournalPath", "/data/storage/journal.log", "Storage:LocalRoot dışında")]
    [InlineData("AllowedHosts", "*", "AllowedHosts")]
    [InlineData("Seed:Enabled", "true", "Seed:Enabled")]
    [InlineData("Storage:Provider", "S3", "yalnızca 'Local'")]
    public void Guvensiz_veya_eksik_deger_uretimde_acik_hata_verir(string key, string value, string expectedFragment)
    {
        var problems = Validate(v => v[key] = value);

        Assert.Contains(problems, p => p.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(problems, p => p.Contains("Zr4-hN8", StringComparison.Ordinal) || p.Contains(SigningKey, StringComparison.Ordinal) || p.Contains(SetupKey, StringComparison.Ordinal)); // gizli değer asla yazılmaz
    }

    private const string Valid_SigningKey = SigningKey; // [InlineData] sabit ister: SetupTokenKey, SigningKey ile aynı verilir

    [Fact]
    public void Vekil_yapilandirmasi_yoksa_veya_gecersizse_uretim_baslamaz()
    {
        Assert.Contains(Validate(v => v.Remove("ForwardedHeaders:KnownNetworks:0")), p => p.Contains("KnownProxies veya KnownNetworks", StringComparison.Ordinal));
        Assert.Contains(Validate(v => v["ForwardedHeaders:KnownNetworks:0"] = "172.31.250.0/99"), p => p.Contains("geçersiz CIDR", StringComparison.Ordinal));
        Assert.Contains(Validate(v => v["ForwardedHeaders:KnownProxies:0"] = "bu-ip-degil"), p => p.Contains("geçersiz IP", StringComparison.Ordinal));
        Assert.Empty(Validate(v =>
        {
            v.Remove("ForwardedHeaders:KnownNetworks:0");
            v["ForwardedHeaders:KnownNetworks"] = "10.0.0.0/24,172.31.250.0/24"; // ortam değişkeni biçimi (virgüllü)
        }));
    }

    [Fact]
    public void Ucretli_AI_kapilari_tutarsizsa_veya_anahtar_eksikse_uretim_baslamaz()
    {
        // Gerçek mod ama ücretli çağrılar kapalı: tüm üretimler reddedilirdi
        Assert.Contains(Validate(v => v["Generation:UseMockProviders"] = "false"), p => p.Contains("AllowPaidCalls=true olmalı", StringComparison.Ordinal));
        // Ücretli çağrı açık ama mock modunda
        Assert.Contains(Validate(v => v["Generation:Budget:AllowPaidCalls"] = "true"), p => p.Contains("yalnızca UseMockProviders=false", StringComparison.Ordinal));

        void Live(Dictionary<string, string?> v)
        {
            v["Generation:UseMockProviders"] = "false";
            v["Generation:Budget:AllowPaidCalls"] = "true";
            v["Generation:Strategies:OutfitSet:Provider"] = "FalSeedream";
        }

        // Anahtar yok
        Assert.Contains(Validate(Live), p => p.Contains("Ai:Fal:ApiKey tanımlı değil", StringComparison.Ordinal));
        // Anahtar var: geçerli
        Assert.Empty(Validate(v =>
        {
            Live(v);
            v["Ai:Fal:ApiKey"] = "fal-anahtari-gercek-degil-ama-dolu";
        }));
        // Bütçe kapısı kapatılamaz, sınırlar tutarlı olmalı
        Assert.Contains(Validate(v =>
        {
            Live(v);
            v["Ai:Fal:ApiKey"] = "x-anahtar";
            v["Generation:Budget:Enforce"] = "false";
        }), p => p.Contains("Enforce=false olamaz", StringComparison.Ordinal));
        Assert.Contains(Validate(v =>
        {
            Live(v);
            v["Ai:Fal:ApiKey"] = "x-anahtar";
            v["Generation:Budget:DailyLimitUsd"] = "1";
            v["Generation:Budget:MaxJobCostUsd"] = "2";
        }), p => p.Contains("tutarsız", StringComparison.Ordinal));
        // Serbest kombin gerçek modda kapalıyken Gemini anahtarı istenmez; açıksa istenir
        Assert.Empty(Validate(v =>
        {
            Live(v);
            v["Ai:Fal:ApiKey"] = "x-anahtar";
            v["Generation:Strategies:FreeCombination:Provider"] = "Gemini";
        }));
        Assert.Contains(Validate(v =>
        {
            Live(v);
            v["Ai:Fal:ApiKey"] = "x-anahtar";
            v["Generation:LiveFreeCombinationEnabled"] = "true";
            v["Generation:Strategies:FreeCombination:Provider"] = "Gemini";
        }), p => p.Contains("Ai:Gemini:ApiKey", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Uretim_sureci_guvensiz_yapilandirmayla_baslamayi_reddeder_sunucu_dinlemez()
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
        var apiDll = MigrationLab.RepoFile($"backend/src/SanalProva.Api/bin/{configuration}/net10.0/SanalProva.Api.dll");
        Assert.True(File.Exists(apiDll), "Önce çözüm derlenmeli: " + apiDll);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(apiDll)! };
        start.ArgumentList.Add(apiDll);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        // Kasıtlı güvensiz/eksik: TLS yok, sa hesabı, zayıf anahtar, vekil yok, AllowedHosts yıldız. Başka yapılandırma kaynağı yok (user-secrets yalnızca Development'ta okunur).
        start.Environment["ConnectionStrings__Main"] = "Server=127.0.0.1,1;Database=sanalprova_dev;User ID=sa;Password=zayif;Encrypt=False";
        start.Environment["Storage__SigningKey"] = "kisa";
        start.Environment["Security__SetupTokenKey"] = new string('s', 48);

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Süreç 90 sn içinde bitmedi: güvensiz yapılandırmayla başlamış olabilir.");
        }

        var all = await stdout + await stderr;
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("Üretim yapılandırması güvensiz veya eksik", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Now listening", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=zayif", all, StringComparison.Ordinal); // bağlantı dizesi/parola metni yazılmadı
    }

    // ---------------------------------------------------------------- güvenilen vekil başlıkları

    private static async Task<string> WhoAsync(Dictionary<string, string?> config, string remoteIp, Action<HttpRequestMessage>? headers = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddTrustedForwardedHeaders(builder.Configuration);
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp); // TestServer'da gerçek ağ yok: vekil adresi burada belirlenir
            return next();
        });
        app.UseTrustedForwardedHeaders(app.Configuration);
        app.MapGet("/who", (HttpContext c) => $"{c.Connection.RemoteIpAddress}|{c.Request.Scheme}|{c.Request.Host}");
        await app.StartAsync(Ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/who");
        headers?.Invoke(request);
        using var response = await app.GetTestClient().SendAsync(request, Ct);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static Dictionary<string, string?> Network(string cidr) => new() { ["ForwardedHeaders:KnownNetworks:0"] = cidr };

    [Fact]
    public async Task Guvenilen_vekilden_gelen_istemci_IP_ve_sema_kabul_edilir()
    {
        var result = await WhoAsync(Network("10.0.0.0/24"), "10.0.0.5", r =>
        {
            r.Headers.Add("X-Forwarded-For", "203.0.113.9");
            r.Headers.Add("X-Forwarded-Proto", "https");
        });

        Assert.StartsWith("203.0.113.9|https|", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Guvenilmeyen_kaynaktan_gelen_sahte_basliklar_yok_sayilir()
    {
        var result = await WhoAsync(Network("10.0.0.0/24"), "192.0.2.77", r =>
        {
            r.Headers.Add("X-Forwarded-For", "203.0.113.9");
            r.Headers.Add("X-Forwarded-Proto", "https");
        });

        Assert.StartsWith("192.0.2.77|http|", result, StringComparison.Ordinal); // gerçek bağlantı IP'si ve şeması
    }

    [Fact]
    public async Task Istemcinin_eklediği_zincirdeki_sahte_ilk_deger_yok_sayilir_yalniz_en_yakin_vekilin_degeri_alinir()
    {
        var result = await WhoAsync(Network("10.0.0.0/24"), "10.0.0.5", r => r.Headers.Add("X-Forwarded-For", "198.51.100.7, 203.0.113.9"));

        Assert.StartsWith("203.0.113.9|", result, StringComparison.Ordinal); // ForwardLimit=1: "198.51.100.7" istemci uydurması
    }

    [Fact]
    public async Task Tek_IP_ile_tanimli_vekil_kabul_edilir_Host_basligi_vekil_degeriyle_degistirilmez()
    {
        var result = await WhoAsync(new Dictionary<string, string?> { ["ForwardedHeaders:KnownProxies:0"] = "172.31.250.2" }, "172.31.250.2", r =>
        {
            r.Headers.Add("X-Forwarded-For", "203.0.113.9");
            r.Headers.Add("X-Forwarded-Host", "saldirgan.example");
        });

        Assert.StartsWith("203.0.113.9|", result, StringComparison.Ordinal);
        Assert.DoesNotContain("saldirgan.example", result, StringComparison.Ordinal); // yalnızca For ve Proto işlenir
    }

    [Fact]
    public async Task Vekil_yapilandirilmamissa_loopback_dahil_hicbir_kaynaktan_baslik_kabul_edilmez()
    {
        var result = await WhoAsync([], "127.0.0.1", r =>
        {
            r.Headers.Add("X-Forwarded-For", "203.0.113.9");
            r.Headers.Add("X-Forwarded-Proto", "https");
        });

        Assert.StartsWith("127.0.0.1|http|", result, StringComparison.Ordinal);
    }
}
