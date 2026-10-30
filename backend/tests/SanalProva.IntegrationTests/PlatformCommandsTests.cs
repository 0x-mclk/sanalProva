using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SanalProva.Api.Features.Platform;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Identity;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.IntegrationTests;

/// <summary>İzole laboratuvar veritabanına bağlı API ana makinesi (üretim işçisi yok; boş, yöneticisiz taze durum).</summary>
internal sealed class LabApiFactory(string appConnectionString, string storageRoot, ILoggerProvider? logs = null, SetupTokenOptions? tokenOverride = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Main", appConnectionString);
        builder.UseSetting("Seed:Enabled", "false");
        builder.UseSetting("Storage:LocalRoot", storageRoot);
        builder.UseSetting("Storage:SigningKey", new string('t', 48));
        builder.UseSetting("Security:SetupTokenKey", new string('s', 48));
        builder.UseSetting("Security:LoginRateLimitPerMinute", "100000");
        builder.UseSetting("Security:SetupPasswordRateLimitPerMinute", "100000");
        builder.UseSetting("ImageWorker:BaseUrl", "http://127.0.0.1:1");
        builder.UseSetting("Retention:SweepIntervalSeconds", "0");
        builder.UseSetting("Display:SweepIntervalSeconds", "0");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
        builder.ConfigureTestServices(services =>
        {
            foreach (var worker in services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType == typeof(SanalProva.Infrastructure.Generation.GenerationWorker)).ToList())
            {
                services.Remove(worker);
            }

            if (logs is not null)
            {
                services.AddSingleton<ILoggerFactory>(LoggerFactory.Create(l => l.SetMinimumLevel(LogLevel.Trace).AddProvider(logs)));
            }

            if (tokenOverride is not null)
            {
                services.AddSingleton<IOptions<SetupTokenOptions>>(Options.Create(tokenOverride)); // yalnızca komut yolunu sınamak için (başlangıç doğrulaması gerçek ayarı görür)
            }
        });
    }
}

/// <summary>
/// M5.1g: platform operatör komutları. Kanıtlananlar: --confirm olmadan hiçbir yazma yok; ilk yönetici + 72 saatlik tek kullanımlık bağlantı (yalnızca standart çıktıda, bir kez);
/// aktif yönetici varken ikincisi reddedilir (--additional bilinçli); bağlantı yeniden üretme/iptal/erişim kurtarma; PaidAiEnabled yalnızca bu komutla değişir ve denetlenir;
/// komutlar HTTP'den erişilemez (ayrı süreç web sunucusu başlatmadan biter); yönetici müşteri verisine erişemez; token/parola günlük ve veritabanında düz yok.
/// Gerçek durum izole <c>sanalprova_lab_*</c> veritabanında (yöneticisiz taze şema); <c>sanalprova_dev</c>'e dokunulmaz.
/// </summary>
public sealed class PlatformCommandsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string StrongPassword = "Gizli-Parola-98765";

    private sealed class Env : IAsyncDisposable
    {
        private readonly MigrationLab _lab;
        private readonly string _storage;

        private Env(MigrationLab lab, LabApiFactory host, string storage, CapturingLoggerProvider logs)
        {
            _lab = lab;
            Host = host;
            _storage = storage;
            Logs = logs;
        }

        public LabApiFactory Host { get; }

        public CapturingLoggerProvider Logs { get; }

        public string AppCs => _lab.AppCs;

        public static async Task<Env> CreateAsync(SetupTokenOptions? tokenOverride = null)
        {
            var lab = await MigrationLab.CreateAsync(Ct);
            await lab.MigrateAsync(Ct);
            var storage = Path.Combine(Path.GetTempPath(), "sanalprova-lab-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(storage);
            var logs = new CapturingLoggerProvider();
            var host = new LabApiFactory(lab.AppCs, storage, logs, tokenOverride);
            _ = host.Services; // ana makineyi tek seferde kur
            return new Env(lab, host, storage, logs);
        }

        public async Task<(int Exit, string Output)> RunAsync(params string[] args)
        {
            var writer = new StringWriter();
            var exit = await PlatformCommands.RunAsync(Host.Services, ["platform", .. args], writer, Ct);
            return (exit, writer.ToString());
        }

        public async Task<T> QueryAsync<T>(Func<AppDbContext, Task<T>> query)
        {
            await using var scope = Host.Services.CreateAsyncScope();
            return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await _lab.DisposeAsync();
            try
            {
                Directory.Delete(_storage, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string LinkOf(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim()).Single(l => l.Contains("#t=", StringComparison.Ordinal));
        return line;
    }

    private static string TokenOf(string output) => LinkOf(output).Split("#t=")[1];

    private static async Task<HttpResponseMessage> SetPasswordAsync(LabApiFactory host, string token, string password) =>
        await new ApiClient(host.CreateClient()).SendAsync(HttpMethod.Post, "/api/v1/account/setup-password", new { token, password });

    private static async Task<(int Users, int Orgs, int Tokens, int Audits)> CountsAsync(Env env) =>
        await env.QueryAsync(async db => (await db.Users.CountAsync(Ct), await db.Organizations.CountAsync(Ct),
            await db.AccountSetupTokens.IgnoreQueryFilters().CountAsync(Ct), await db.AuditLogs.CountAsync(Ct)));

    [Fact]
    public async Task Confirm_olmadan_hicbir_veritabani_degisikligi_yapilmaz_plan_yazilir()
    {
        await using var env = await Env.CreateAsync();
        var before = await CountsAsync(env);

        var (exit, output) = await env.RunAsync("bootstrap", "--email", "yonetici@ornek.test", "--name", "Platform Yöneticisi");

        Assert.Equal(PlatformCommands.ExitOk, exit);
        Assert.Contains("KURU ÇALIŞMA", output, StringComparison.Ordinal);
        Assert.Contains("PLAN", output, StringComparison.Ordinal);
        Assert.DoesNotContain("#t=", output, StringComparison.Ordinal); // bağlantı üretilmedi
        Assert.Equal(before, await CountsAsync(env));
        Assert.Equal((0, 0, 0, 0), before); // taze şema
    }

    [Fact]
    public async Task Ilk_yonetici_olusur_72_saatlik_baglanti_yalnizca_ciktida_bir_kez_gorunur_sifre_belirlenince_calisir()
    {
        await using var env = await Env.CreateAsync();
        const string email = "yonetici@ornek.test";
        var before = DateTimeOffset.UtcNow;

        var (exit, output) = await env.RunAsync("bootstrap", "--email", email, "--name", "Platform Yöneticisi", "--confirm");

        Assert.Equal(PlatformCommands.ExitOk, exit);
        var link = LinkOf(output);
        Assert.StartsWith("/hesap/sifre-belirle#t=", link, StringComparison.Ordinal); // --base-url yok: göreli
        var token = TokenOf(output);
        Assert.Equal(1, output.Split(token).Length - 1); // çıktıda yalnızca bir kez

        await env.QueryAsync(async db =>
        {
            var org = await db.Organizations.AsNoTracking().SingleAsync(o => o.Slug == PlatformCommands.PlatformSlug, Ct);
            var admin = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email, Ct);
            Assert.True(admin.IsPlatformAdmin);
            Assert.True(admin.IsActive);
            Assert.Null(admin.PasswordHash); // giriş yapılamaz
            Assert.Equal(org.Id, admin.OrganizationId);
            Assert.Empty(await db.UserStoreRoles.IgnoreQueryFilters().Where(r => r.UserId == admin.Id).ToListAsync(Ct)); // hiçbir firmada mağaza rolü yok
            var tokenRow = await db.AccountSetupTokens.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.UserId == admin.Id, Ct);
            Assert.InRange(tokenRow.ExpiresAt, before.AddHours(71.9), before.AddHours(72.1)); // D1: 72 saat
            Assert.Null(tokenRow.CreatedByUserId);
            var audit = await db.AuditLogs.AsNoTracking().OrderBy(a => a.Id).ToListAsync(Ct);
            Assert.Equal(["Platform.AdminBootstrapped", "Platform.SetupLinkIssued"], audit.Select(a => a.Action));
            Assert.All(audit, a => Assert.Null(a.UserId));
            return 0;
        });

        // Token yalnızca çıktıda: veritabanının hiçbir metin sütununda ve hiçbir günlük satırında yok
        Assert.Equal(0, await PlatformOrganizationTests.CountOccurrencesAsync(env.AppCs, token, Ct));
        Assert.DoesNotContain(token, string.Join('\n', env.Logs.Lines), StringComparison.Ordinal);

        // Şifre henüz yok: giriş yapılamaz; bağlantıyla belirlenir ve yönetici platform uçlarını kullanır
        var anonymous = new ApiClient(env.Host.CreateClient());
        await ApiClient.AssertProblemAsync(await anonymous.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = StrongPassword, rememberMe = false }), HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(HttpStatusCode.NoContent, (await SetPasswordAsync(env.Host, token, StrongPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(env.Host, token, StrongPassword)).StatusCode); // tek kullanım
        var admin = await ApiClient.LoginAsync(env.Host, email, null, StrongPassword);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/platform/organizations")).StatusCode);
    }

    [Fact]
    public async Task Base_url_verilirse_baglanti_mutlak_olur()
    {
        await using var env = await Env.CreateAsync();
        var (_, output) = await env.RunAsync("bootstrap", "--email", "yonetici@ornek.test", "--name", "Yönetici Kişi", "--base-url", "https://panel.ornek.test/", "--confirm");
        Assert.StartsWith("https://panel.ornek.test/hesap/sifre-belirle#t=", LinkOf(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Aktif_yonetici_varken_ikinci_yonetici_reddedilir_additional_bilincli_karardir_ayni_eposta_olmaz()
    {
        await using var env = await Env.CreateAsync();
        Assert.Equal(PlatformCommands.ExitOk, (await env.RunAsync("bootstrap", "--email", "bir@ornek.test", "--name", "Birinci Yönetici", "--confirm")).Exit);
        var before = await CountsAsync(env);

        var (refused, message) = await env.RunAsync("bootstrap", "--email", "iki@ornek.test", "--name", "İkinci Yönetici", "--confirm");
        Assert.Equal(PlatformCommands.ExitRefused, refused);
        Assert.Contains("REDDEDİLDİ", message, StringComparison.Ordinal);
        Assert.DoesNotContain("#t=", message, StringComparison.Ordinal);
        Assert.Equal(before, await CountsAsync(env)); // hiçbir şey yazılmadı

        var (okExit, okOutput) = await env.RunAsync("bootstrap", "--email", "iki@ornek.test", "--name", "İkinci Yönetici", "--additional", "--confirm");
        Assert.Equal(PlatformCommands.ExitOk, okExit);
        Assert.Contains("#t=", okOutput, StringComparison.Ordinal);
        Assert.Equal(1, await env.QueryAsync(db => db.Organizations.CountAsync(Ct))); // aynı platform firması

        var (duplicate, _) = await env.RunAsync("bootstrap", "--email", "BIR@ornek.test", "--name", "Aynı Kişi", "--additional", "--confirm"); // e-posta sistem genelinde benzersiz
        Assert.Equal(PlatformCommands.ExitRefused, duplicate);
    }

    [Fact]
    public async Task Baglanti_yeniden_uretilir_onceki_iptal_olur_sifre_belirlenmisse_reddedilir_iptal_ve_bilinmeyen_hesap()
    {
        await using var env = await Env.CreateAsync();
        const string email = "yonetici@ornek.test";
        var first = TokenOf((await env.RunAsync("bootstrap", "--email", email, "--name", "Platform Yöneticisi", "--confirm")).Output);

        // Kuru çalışma: yeni bağlantı yok, eski geçerli
        var (dryExit, dryOutput) = await env.RunAsync("reissue-link", "--email", email);
        Assert.Equal(PlatformCommands.ExitOk, dryExit);
        Assert.DoesNotContain("#t=", dryOutput, StringComparison.Ordinal);

        var (exit, output) = await env.RunAsync("reissue-link", "--email", email, "--confirm");
        Assert.Equal(PlatformCommands.ExitOk, exit);
        var second = TokenOf(output);
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(env.Host, first, StrongPassword)).StatusCode); // eski iptal

        // İptal komutu: açık bağlantı kalmaz
        var (revokeExit, revokeOutput) = await env.RunAsync("revoke", "--email", email, "--confirm");
        Assert.Equal(PlatformCommands.ExitOk, revokeExit);
        Assert.Contains("1 açık bağlantı iptal", revokeOutput, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(env.Host, second, StrongPassword)).StatusCode);

        // Yeniden üretip şifreyi belirle; sonrasında yeniden üretim reddedilir
        var third = TokenOf((await env.RunAsync("reissue-link", "--email", email, "--confirm")).Output);
        Assert.Equal(HttpStatusCode.NoContent, (await SetPasswordAsync(env.Host, third, StrongPassword)).StatusCode);
        Assert.Equal(PlatformCommands.ExitRefused, (await env.RunAsync("reissue-link", "--email", email, "--confirm")).Exit);

        // Bilinmeyen e-posta ve platform yöneticisi olmayan hesap bulunamadı sayılır
        Assert.Equal(PlatformCommands.ExitNotFound, (await env.RunAsync("reissue-link", "--email", "yok@ornek.test", "--confirm")).Exit);
        Assert.Equal(PlatformCommands.ExitNotFound, (await env.RunAsync("revoke", "--email", "yok@ornek.test", "--confirm")).Exit);

        // Denetim: yalnızca olay adları (token/e-posta yok)
        var actions = await env.QueryAsync(async db => await db.AuditLogs.AsNoTracking().OrderBy(a => a.Id).Select(a => a.Action).ToListAsync(Ct));
        Assert.Contains("Platform.SetupLinkRevoked", actions);
        Assert.Equal(0, await PlatformOrganizationTests.CountOccurrencesAsync(env.AppCs, third, Ct));
    }

    [Fact]
    public async Task Erisim_kurtarma_parolayi_siler_guvenlik_damgasini_yeniler_yeni_baglanti_uretir()
    {
        await using var env = await Env.CreateAsync();
        const string email = "yonetici@ornek.test";
        var token = TokenOf((await env.RunAsync("bootstrap", "--email", email, "--name", "Platform Yöneticisi", "--confirm")).Output);
        Assert.Equal(HttpStatusCode.NoContent, (await SetPasswordAsync(env.Host, token, StrongPassword)).StatusCode);
        var stampBefore = await env.QueryAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.SecurityStamp).SingleAsync(Ct));
        var oldSession = await ApiClient.LoginAsync(env.Host, email, null, StrongPassword);
        Assert.Equal(HttpStatusCode.OK, (await oldSession.GetAsync("/api/v1/auth/me")).StatusCode);

        var (dryExit, _) = await env.RunAsync("reset-access", "--email", email); // kuru çalışma: değişiklik yok
        Assert.Equal(PlatformCommands.ExitOk, dryExit);
        Assert.NotNull(await env.QueryAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.PasswordHash).SingleAsync(Ct)));

        var (exit, output) = await env.RunAsync("reset-access", "--email", email, "--confirm");
        Assert.Equal(PlatformCommands.ExitOk, exit);
        var recoveryToken = TokenOf(output);

        Assert.Null(await env.QueryAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.PasswordHash).SingleAsync(Ct)));
        Assert.NotEqual(stampBefore, await env.QueryAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.SecurityStamp).SingleAsync(Ct)));
        var anonymous = new ApiClient(env.Host.CreateClient());
        await ApiClient.AssertProblemAsync(await anonymous.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = StrongPassword, rememberMe = false }), HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");

        const string newPassword = "Yeni-Gizli-Parola-4321";
        Assert.Equal(HttpStatusCode.NoContent, (await SetPasswordAsync(env.Host, recoveryToken, newPassword)).StatusCode);
        Assert.NotNull(await ApiClient.LoginAsync(env.Host, email, null, newPassword));
        var actions = await env.QueryAsync(async db => await db.AuditLogs.AsNoTracking().Select(a => a.Action).ToListAsync(Ct));
        Assert.Contains("Platform.AccessReset", actions);
        Assert.Equal(0, await PlatformOrganizationTests.CountOccurrencesAsync(env.AppCs, recoveryToken, Ct));
        Assert.Equal(0, await PlatformOrganizationTests.CountOccurrencesAsync(env.AppCs, newPassword, Ct));
        Assert.DoesNotContain(newPassword, string.Join('\n', env.Logs.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ucretli_AI_yetkisi_yalnizca_komutla_degisir_denetlenir_kuru_calismada_degismez()
    {
        await using var env = await Env.CreateAsync();
        const string email = "yonetici@ornek.test";
        var token = TokenOf((await env.RunAsync("bootstrap", "--email", email, "--name", "Platform Yöneticisi", "--confirm")).Output);
        await SetPasswordAsync(env.Host, token, StrongPassword);
        var admin = await ApiClient.LoginAsync(env.Host, email, null, StrongPassword);
        var created = await ApiClient.ReadAsync<CreateOrganizationResponse>(await PlatformOrganizationTests.CreateRawAsync(admin, PlatformOrganizationTests.Body(slug: "gul-gelinlik"), PlatformOrganizationTests.NewKey()), HttpStatusCode.Created);

        async Task<bool> EnabledAsync() => await env.QueryAsync(async db => (await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == created.OrganizationId, Ct)).Settings.PaidAiEnabled);
        async Task<int> AuditCountAsync(string action) => await env.QueryAsync(db => db.AuditLogs.CountAsync(a => a.OrganizationId == created.OrganizationId && a.Action == action, Ct));
        Assert.False(await EnabledAsync()); // yeni firma varsayılan kapalı

        // Kuru çalışma: değişmez
        var (dryExit, dryOutput) = await env.RunAsync("paid-ai", "--org", "gul-gelinlik", "--enable");
        Assert.Equal(PlatformCommands.ExitOk, dryExit);
        Assert.Contains("KURU ÇALIŞMA", dryOutput, StringComparison.Ordinal);
        Assert.False(await EnabledAsync());

        // Açma: değişir, denetim yazılır (kişisel veri yok, kullanıcı yok)
        Assert.Equal(PlatformCommands.ExitOk, (await env.RunAsync("paid-ai", "--org", "gul-gelinlik", "--enable", "--confirm")).Exit);
        Assert.True(await EnabledAsync());
        Assert.Equal(1, await AuditCountAsync("Platform.PaidAiEnabled"));
        Assert.Null(await env.QueryAsync(db => db.AuditLogs.Where(a => a.Action == "Platform.PaidAiEnabled").Select(a => a.UserId).SingleAsync(Ct)));

        // Zaten açık: değişiklik yok, ikinci denetim kaydı yok
        var (sameExit, sameOutput) = await env.RunAsync("paid-ai", "--org", "gul-gelinlik", "--enable", "--confirm");
        Assert.Equal(PlatformCommands.ExitOk, sameExit);
        Assert.Contains("DEĞİŞİKLİK YOK", sameOutput, StringComparison.Ordinal);
        Assert.Equal(1, await AuditCountAsync("Platform.PaidAiEnabled"));

        // Kapatma
        Assert.Equal(PlatformCommands.ExitOk, (await env.RunAsync("paid-ai", "--org", "gul-gelinlik", "--disable", "--confirm")).Exit);
        Assert.False(await EnabledAsync());
        Assert.Equal(1, await AuditCountAsync("Platform.PaidAiDisabled"));

        // Hatalı kullanım
        Assert.Equal(PlatformCommands.ExitNotFound, (await env.RunAsync("paid-ai", "--org", "yok-firma", "--enable", "--confirm")).Exit);
        Assert.Equal(PlatformCommands.ExitUsage, (await env.RunAsync("paid-ai", "--org", "gul-gelinlik", "--confirm")).Exit);
        Assert.Equal(PlatformCommands.ExitUsage, (await env.RunAsync("paid-ai", "--org", "gul-gelinlik", "--enable", "--disable", "--confirm")).Exit);
        Assert.False(await EnabledAsync());
    }

    [Fact]
    public async Task Platform_yoneticisi_musteri_katalog_fotograf_ve_oturum_verisine_erisemez()
    {
        await using var env = await Env.CreateAsync();
        const string email = "yonetici@ornek.test";
        var token = TokenOf((await env.RunAsync("bootstrap", "--email", email, "--name", "Platform Yöneticisi", "--confirm")).Output);
        await SetPasswordAsync(env.Host, token, StrongPassword);
        var admin = await ApiClient.LoginAsync(env.Host, email, null, StrongPassword);

        // Müşteri firması: oturum + onay + fotoğraf + katalog (platform yöneticisi oluşturur; verisine erişemez)
        var created = await ApiClient.ReadAsync<CreateOrganizationResponse>(await PlatformOrganizationTests.CreateRawAsync(admin, PlatformOrganizationTests.Body(), PlatformOrganizationTests.NewKey()), HttpStatusCode.Created);
        var customerEmail = await env.QueryAsync(db => db.Users.Where(u => u.Id == created.OwnerUserId).Select(u => u.Email!).SingleAsync(Ct));
        Assert.Equal(HttpStatusCode.NoContent, (await SetPasswordAsync(env.Host, PlatformOrganizationTests.TokenOf(created.SetupLink!), StrongPassword)).StatusCode);
        await TestData.AcceptAgreementAsync(env.Host.Services, created.OrganizationId, customerEmail);
        var catalog = await TestData.CreateCatalogAsync(env.Host.Services, created.OrganizationId);
        var owner = await ApiClient.LoginAsync(env.Host, customerEmail, created.StoreId, StrongPassword);
        var session = await owner.PostAsync<SanalProva.Api.Features.TryOn.SessionDetail>("/api/v1/sessions", new { customerLabel = "Müşteri" }, HttpStatusCode.Created);
        await owner.PostAsync<SanalProva.Api.Features.TryOn.SessionDetail>($"/api/v1/sessions/{session.Id}/consent", ApiTestBase.FullConsent());
        var photoResponse = await owner.UploadAsync($"/api/v1/sessions/{session.Id}/photos", TestImages.Portrait());
        var photo = await ApiClient.ReadAsync<SanalProva.Api.Features.TryOn.PhotoDto>(photoResponse, HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/v1/sessions/{session.Id}")).StatusCode); // veri gerçekten var ve sahibi erişebiliyor

        // Platform yöneticisi: şubeyi X-Store-Id ile seçse de rolü yok → oturum/fotoğraf/katalog/yönetim kapalı
        admin.StoreId = created.StoreId;
        // 1) Platform firması sözleşmeyi kabul etmedikçe mağaza uçları zaten kapalı (sözleşme kapısı)
        await ApiClient.AssertProblemAsync(await admin.GetAsync("/api/v1/sessions"), HttpStatusCode.Forbidden, "AGREEMENT_ACCEPTANCE_REQUIRED");
        // 2) Platform firması kabul etse bile (store filtresinin kendisini sınamak için) müşteri şubesine rolü yoktur
        await TestData.AcceptAgreementAsync(env.Host.Services, await env.QueryAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.OrganizationId).SingleAsync(Ct)), email);
        await ApiClient.AssertProblemAsync(await admin.GetAsync("/api/v1/sessions"), HttpStatusCode.Forbidden, "STORE_FORBIDDEN");
        await ApiClient.AssertProblemAsync(await admin.GetAsync($"/api/v1/sessions/{session.Id}"), HttpStatusCode.Forbidden, "STORE_FORBIDDEN");
        await ApiClient.AssertProblemAsync(await admin.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{session.Id}"), HttpStatusCode.Forbidden, "STORE_FORBIDDEN");
        await ApiClient.AssertProblemAsync(await admin.UploadAsync($"/api/v1/sessions/{session.Id}/photos", TestImages.Portrait(3)), HttpStatusCode.Forbidden, "STORE_FORBIDDEN");
        // Platform yöneticisinin hiçbir firmada (kendi platform firmasında bile) mağaza rolü yok: katalog uçları rol denetiminde kapanır
        await ApiClient.AssertProblemAsync(await admin.GetAsync("/api/v1/products"), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        var products = await (await admin.GetAsync("/api/v1/products")).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(catalog.SalvarProduct.ToString(), products, StringComparison.OrdinalIgnoreCase);
        await ApiClient.AssertProblemAsync(await admin.GetAsync($"/api/v1/products/{catalog.SalvarProduct}"), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/displays")).StatusCode);

        // İmzalı fotoğraf adresi yöneticiye hiç verilmez; müşterinin görsel anahtarı tahmin edilse bile imzasız istek reddedilir
        var unsigned = await new ApiClient(env.Host.CreateClient()).GetAsync($"/files/{created.OrganizationId:N}/{created.StoreId:N}/sessions/{session.Id:N}/{photo.Id:N}.jpg");
        Assert.False(unsigned.IsSuccessStatusCode, $"imzasız dosya isteği başarılı döndü: {(int)unsigned.StatusCode}");
    }

    [Fact]
    public async Task Anahtar_eksik_veya_zayifsa_komut_token_uretmez()
    {
        await using var env = await Env.CreateAsync(new SetupTokenOptions { SetupTokenKey = "kisa" });
        var before = await CountsAsync(env);

        var (exit, output) = await env.RunAsync("bootstrap", "--email", "yonetici@ornek.test", "--name", "Platform Yöneticisi", "--confirm");

        Assert.Equal(PlatformCommands.ExitFailed, exit);
        Assert.Contains("SetupTokenKey", output, StringComparison.Ordinal);
        Assert.DoesNotContain("#t=", output, StringComparison.Ordinal);
        Assert.Equal(before, await CountsAsync(env));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bilinmeyen")]
    [InlineData("bootstrap --email gecersiz --name Yönetici")]
    [InlineData("bootstrap --email a@b.test")]
    [InlineData("bootstrap --email a@b.test --name Yönetici --base-url ftp://x")]
    [InlineData("bootstrap --bilinmeyen-secenek")]
    public async Task Hatali_kullanimda_kullanim_yazilir_hicbir_sey_degismez(string arguments)
    {
        await using var env = await Env.CreateAsync();
        var before = await CountsAsync(env);
        var args = arguments.Length == 0 ? Array.Empty<string>() : arguments.Split(' ');

        var (exit, output) = await env.RunAsync(args);

        Assert.True(exit == PlatformCommands.ExitUsage, $"çıkış {exit}: {output}");
        Assert.Contains("Kullanım", output, StringComparison.Ordinal);
        Assert.Equal(before, await CountsAsync(env));
    }

    // ---------------------------------------------------------------- HTTP üzerinden erişilemezlik

    [Fact]
    public void Komutlar_hicbir_uc_noktadan_hub_dan_veya_baska_koddan_cagrilmaz()
    {
        var src = Directory.GetParent(Path.GetDirectoryName(MigrationLab.RepoFile("backend/src/SanalProva.Api/SanalProva.Api.csproj"))!)!.FullName;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            if (File.ReadAllText(file).Contains("PlatformCommands", StringComparison.Ordinal)
                && relative is not ("SanalProva.Api/Program.cs" or "SanalProva.Api/Features/Platform/PlatformCommands.cs"))
            {
                offenders.Add(relative);
            }
        }

        Assert.True(offenders.Count == 0, "PlatformCommands'ı anan izinsiz dosya(lar): " + string.Join(", ", offenders));
    }

    [Fact]
    public void Platform_yoneticisi_bayragini_yazan_tek_yer_operator_komutudur()
    {
        var src = Directory.GetParent(Path.GetDirectoryName(MigrationLab.RepoFile("backend/src/SanalProva.Api/SanalProva.Api.csproj"))!)!.FullName;
        var writes = new System.Text.RegularExpressions.Regex(@"IsPlatformAdmin\s*=\s*(true|[^=>\s])|SetProperty\([^;]*IsPlatformAdmin", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/Migrations/", StringComparison.Ordinal))
            {
                continue;
            }

            if (writes.IsMatch(File.ReadAllText(file)) && relative != "SanalProva.Api/Features/Platform/PlatformCommands.cs")
            {
                offenders.Add(relative);
            }
        }

        Assert.True(offenders.Count == 0, "IsPlatformAdmin yazan izinsiz dosya(lar): " + string.Join(", ", offenders));
    }

    [Fact]
    public async Task Ayri_surec_olarak_calisir_web_sunucusu_baslatmaz_kuru_calismada_veritabanina_yazmaz()
    {
        ApiFactory.RequireDatabase(Factory);
        var connection = Factory.TestConnectionString!;
        var apiDll = MigrationLab.RepoFile("backend/src/SanalProva.Api/bin/Debug/net10.0/SanalProva.Api.dll");
        Assert.True(File.Exists(apiDll), "Önce çözüm derlenmeli: " + apiDll);

        int UsersWithEmail(string address)
        {
            // Paylaşılan test veritabanında başka testler kullanıcı ekler: toplam sayı değil, bu sürecin benzersiz e-postası denetlenir
            using var c = new Microsoft.Data.SqlClient.SqlConnection(connection);
            c.Open();
            using var cmd = new Microsoft.Data.SqlClient.SqlCommand("SELECT COUNT(*) FROM users WHERE email = @e", c);
            cmd.Parameters.AddWithValue("@e", address);
            return (int)cmd.ExecuteScalar()!;
        }

        var email = $"surec-{Guid.NewGuid():N}"[..16] + "@ornek.test";
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(apiDll)! };
        foreach (var argument in new[] { apiDll, "platform", "bootstrap", "--email", email, "--name", "Süreç Testi", "--additional" }) // test veritabanında yönetici zaten olabilir; kuru çalışma (--confirm yok)
        {
            start.ArgumentList.Add(argument);
        }

        // Yapılandırma yalnızca alt sürecin ortam değişkenlerindedir (kaynağa/dosyaya yazılmaz); Production: user-secrets/tohum yok
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ConnectionStrings__Main"] = connection;
        start.Environment["Security__SetupTokenKey"] = new string('s', 48);
        start.Environment["Storage__SigningKey"] = new string('t', 48);
        start.Environment["Storage__LocalRoot"] = Path.Combine(Path.GetTempPath(), "sanalprova-surec-" + Guid.NewGuid().ToString("N"));

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
            Assert.Fail("Komut süreci 90 sn içinde bitmedi: web sunucusu başlamış olabilir.");
        }

        var output = await stdout;
        Assert.True(process.ExitCode == 0, $"çıkış {process.ExitCode}: {output}{await stderr}");
        Assert.Contains("KURU ÇALIŞMA", output, StringComparison.Ordinal); // --confirm yok: yazma yok
        Assert.DoesNotContain("Now listening", output, StringComparison.Ordinal); // Kestrel hiç başlamadı
        Assert.Equal(0, UsersWithEmail(email)); // kuru çalışma: hesap yazılmadı
    }

    private ApiFactory Factory { get; }

    public PlatformCommandsTests(ApiFactory factory) => Factory = factory;
}
