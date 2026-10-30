using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SanalProva.Api.Features.Platform;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Identity;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.IntegrationTests;

/// <summary>Tüm günlük satırlarını (mesaj, durum değerleri, istisna) toplar: sızıntı testleri için.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private sealed class Capture(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                text += " | " + string.Join(";", values.Select(v => $"{v.Key}={v.Value}"));
            }

            owner.Lines.Enqueue(exception is null ? text : text + " | " + exception);
        }
    }
}

/// <summary>
/// M5.1d: tek kullanımlık bağlantıyla şifre belirleme. Kanıtlananlar: token tüketimi + parola + denetim TEK transaction'dadır (herhangi bir adım patlarsa token tüketilmemiş
/// kalır, parola yazılmaz); kullanılmış/süresi dolmuş/iptal edilmiş/amacı yanlış/bilinmeyen token tek ve aynı yanıtla reddedilir; eşzamanlı isteklerden tam biri geçer;
/// parola politikası ihlali token'ı yakmaz; token ve parola günlüklerde/veritabanında düz metin olarak bulunmaz; sonrasında Owner yalnızca kendi firmasını görür.
/// </summary>
public sealed class SetupPasswordTests(ApiFactory factory) : ApiTestBase(factory)
{
    private const string Endpoint = "/api/v1/account/setup-password";
    private const string StrongPassword = "Gizli-Parola-98765";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(ApiClient Admin, CreateOrganizationResponse Created, string Token, string Email)> NewOrganizationAsync(WebApplicationFactory<Program>? host = null)
    {
        ApiFactory.RequireDatabase(Factory);
        host ??= Factory;
        var platform = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.Users.Where(u => u.Email == platform.Email).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsPlatformAdmin, true), Ct);
        }

        var admin = await ApiClient.LoginAsync(host, platform.Email, platform.StoreId);
        var email = $"sahip-{Guid.NewGuid():N}"[..20] + "@ornek.test";
        var created = await ApiClient.ReadAsync<CreateOrganizationResponse>(
            await PlatformOrganizationTests.CreateRawAsync(admin, PlatformOrganizationTests.Body(email: email), PlatformOrganizationTests.NewKey()), HttpStatusCode.Created);
        return (admin, created, PlatformOrganizationTests.TokenOf(created.SetupLink!), email);
    }

    private static Task<HttpResponseMessage> SetPasswordAsync(ApiClient client, string token, string password) =>
        client.SendAsync(HttpMethod.Post, Endpoint, new { token, password });

    private ApiClient Anonymous(WebApplicationFactory<Program>? host = null) => new((host ?? Factory).CreateClient());

    private async Task<SetupTokenValidity> ValidityAsync(string token)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AccountSetupTokenService>().ValidateAsync(token, Ct);
    }

    private async Task<string?> PasswordHashAsync(Guid userId)
    {
        var (scope, db) = OpenDb();
        using (scope)
        {
            return await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.PasswordHash).SingleAsync(Ct);
        }
    }

    private static async Task AssertInvalidLinkAsync(HttpResponseMessage response) =>
        await ApiClient.AssertProblemAsync(response, HttpStatusCode.BadRequest, "SETUP_LINK_INVALID");

    [Fact]
    public async Task Gecerli_baglantiyla_sifre_belirlenir_Owner_giris_yapar_yalnizca_kendi_firmasini_gorur()
    {
        var (admin, created, token, email) = await NewOrganizationAsync();
        var other = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var otherCatalog = await TestData.CreateCatalogAsync(Services, other.OrganizationId);

        var response = await SetPasswordAsync(Anonymous(), token, StrongPassword);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(SetupTokenValidity.Used, await ValidityAsync(token));
        Assert.NotNull(await PasswordHashAsync(created.OwnerUserId));

        // Yeni Owner giriş yapar; oturum yalnızca yeni firmasına bağlıdır
        var owner = await ApiClient.LoginAsync(Factory, email, created.StoreId, StrongPassword);
        var me = await owner.GetAsync<SanalProva.Api.Features.Auth.MeResponse>("/api/v1/auth/me");
        Assert.Equal(created.OrganizationId, me.OrganizationId);
        Assert.Equal(created.StoreId, Assert.Single(me.Stores).Id);
        Assert.Equal(StoreRole.Owner, me.Stores[0].Role);

        // Yeni firma sözleşmeyi henüz kabul etmedi: mağaza işlemleri kapalı, oturum bilgisi ve sözleşme açık; Owner kabul edince açılır
        Assert.True(me.AgreementAcceptanceRequired);
        Assert.True(me.CanAcceptAgreement);
        await ApiClient.AssertProblemAsync(await owner.GetAsync("/api/v1/products"), HttpStatusCode.Forbidden, "AGREEMENT_ACCEPTANCE_REQUIRED");
        var agreement = await owner.GetAsync<SanalProva.Api.Features.Agreement.AgreementDto>("/api/v1/agreement/current");
        await owner.PostAsync<SanalProva.Api.Features.Agreement.AgreementDto>("/api/v1/organization/agreement-acceptance", new { version = agreement.Version, textSha256 = agreement.TextSha256 }, HttpStatusCode.Created);
        Assert.False((await owner.GetAsync<SanalProva.Api.Features.Auth.MeResponse>("/api/v1/auth/me")).AgreementAcceptanceRequired);

        // Başka firmanın kataloğu ve şubesi görünmez/erişilemez; platform uç noktaları kapalı
        var products = await (await owner.GetAsync("/api/v1/products")).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(otherCatalog.SalvarProduct.ToString(), products, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/v1/products/{otherCatalog.SalvarProduct}")).StatusCode);
        owner.StoreId = other.StoreId;
        await ApiClient.AssertProblemAsync(await owner.GetAsync("/api/v1/sessions"), HttpStatusCode.Forbidden, "STORE_FORBIDDEN");
        owner.StoreId = created.StoreId;
        await ApiClient.AssertProblemAsync(await owner.GetAsync("/api/v1/platform/organizations"), HttpStatusCode.Forbidden, "PLATFORM_ADMIN_REQUIRED");

        // Denetim: hedef firmada, kullanıcı kimliğiyle; token/parola/e-posta yok
        var (scope, db) = OpenDb();
        using (scope)
        {
            var audit = await db.AuditLogs.AsNoTracking().Where(a => a.Action == "Account.PasswordSet" && a.OrganizationId == created.OrganizationId).SingleAsync(Ct);
            Assert.Equal((created.OwnerUserId, nameof(AppUser), created.OwnerUserId), (audit.UserId, audit.EntityType, audit.EntityId));
        }

        // Ne token ne düz parola hiçbir metin sütununda bulunur
        Assert.Equal(0, await PlatformOrganizationTests.CountOccurrencesAsync(Factory.TestConnectionString!, token, Ct));
        Assert.Equal(0, await PlatformOrganizationTests.CountOccurrencesAsync(Factory.TestConnectionString!, StrongPassword, Ct));

        // Şifre belirlendikten sonra yeniden üretim yapılamaz (şifre sıfırlama ayrı akış)
        await ApiClient.AssertProblemAsync(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/organizations/{created.OrganizationId}/owner/setup-link"), HttpStatusCode.Conflict, "OWNER_ALREADY_ACTIVATED");
    }

    [Fact]
    public async Task Kullanilmis_suresi_dolmus_iptal_edilmis_ve_bilinmeyen_baglantilar_ayni_yanitla_reddedilir()
    {
        var (_, created, used, _) = await NewOrganizationAsync();
        var anonymous = Anonymous();

        // Kullanılmış: ikinci kullanım reddedilir ve parola değişmez
        Assert.Equal(HttpStatusCode.NoContent, (await SetPasswordAsync(anonymous, used, StrongPassword)).StatusCode);
        var firstHash = await PasswordHashAsync(created.OwnerUserId);
        var usedResponse = await SetPasswordAsync(anonymous, used, "Baska-Parola-123456");
        await AssertInvalidLinkAsync(usedResponse);
        Assert.Equal(firstHash, await PasswordHashAsync(created.OwnerUserId));

        // Süresi dolmuş / iptal edilmiş: yeni firma (Owner şifresiz) üzerinden
        var second = await NewOrganizationAsync();
        var expiredToken = second.Token;
        var (scope, db) = OpenDb();
        using (scope)
        {
            var now = DateTimeOffset.UtcNow;
            await db.AccountSetupTokens.IgnoreQueryFilters().Where(t => t.UserId == second.Created.OwnerUserId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedAt, now.AddHours(-2)).SetProperty(t => t.ExpiresAt, now.AddHours(-1)), Ct);
        }

        var expiredResponse = await SetPasswordAsync(anonymous, expiredToken, StrongPassword);
        await AssertInvalidLinkAsync(expiredResponse);
        Assert.Null(await PasswordHashAsync(second.Created.OwnerUserId));

        var third = await NewOrganizationAsync();
        var reissued = await ApiClient.ReadAsync<SetupLinkResponse>(await third.Admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/organizations/{third.Created.OrganizationId}/owner/setup-link"));
        var revokedResponse = await SetPasswordAsync(anonymous, third.Token, StrongPassword); // ilk bağlantı iptal edildi
        await AssertInvalidLinkAsync(revokedResponse);
        var unknownResponse = await SetPasswordAsync(anonymous, AccountSetupTokenCredentials.NewToken(), StrongPassword);
        await AssertInvalidLinkAsync(unknownResponse);

        // Hangi nedenle reddedildiği yanıttan anlaşılmaz: gövdeler aynı
        var bodies = new[] { usedResponse, expiredResponse, revokedResponse, unknownResponse };
        var texts = await Task.WhenAll(bodies.Select(r => r.Content.ReadAsStringAsync(Ct)));
        Assert.Single(texts.Select(t => System.Text.RegularExpressions.Regex.Replace(t, "\"traceId\":\"[^\"]*\"", string.Empty)).Distinct());

        // Yeni (iptal sonrası) bağlantı çalışır
        Assert.Equal(HttpStatusCode.NoContent, (await SetPasswordAsync(anonymous, PlatformOrganizationTests.TokenOf(reissued.SetupLink), StrongPassword)).StatusCode);
    }

    [Fact]
    public async Task Parola_politikasi_ihlali_baglantiyi_tuketmez_dogru_parolayla_tekrar_denenebilir()
    {
        var (_, created, token, _) = await NewOrganizationAsync();
        var anonymous = Anonymous();

        var weak = await SetPasswordAsync(anonymous, token, "kisa");
        var text = await weak.Content.ReadAsStringAsync(Ct);
        await ApiClient.AssertProblemAsync(weak, HttpStatusCode.BadRequest, "PASSWORD_POLICY");
        Assert.Contains("PasswordTooShort", text, StringComparison.Ordinal);
        Assert.DoesNotContain("kisa", text.Replace("PasswordTooShort", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        Assert.Equal(SetupTokenValidity.Valid, await ValidityAsync(token)); // token yanmadı (transaction geri alındı)
        Assert.Null(await PasswordHashAsync(created.OwnerUserId));
        Assert.Equal(HttpStatusCode.NoContent, (await SetPasswordAsync(anonymous, token, StrongPassword)).StatusCode);
    }

    [Fact]
    public async Task Eszamanli_isteklerde_tam_olarak_biri_basarili_olur_giris_yalnizca_kazanan_parolayla_calisir()
    {
        var (_, created, token, email) = await NewOrganizationAsync();
        var passwords = Enumerable.Range(0, 8).Select(i => $"Esz-Parola-{i}-98765").ToArray();

        var results = await Task.WhenAll(passwords.Select(async password =>
        {
            var response = await SetPasswordAsync(Anonymous(), token, password);
            return (password, response.StatusCode);
        }));

        var winners = results.Where(r => r.StatusCode == HttpStatusCode.NoContent).ToList();
        Assert.Single(winners);
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.NoContent), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));

        var login = await ApiClient.LoginAsync(Factory, email, created.StoreId, winners[0].password);
        Assert.NotNull(login.AuthCookie);
        foreach (var loser in results.Where(r => r.StatusCode != HttpStatusCode.NoContent).Take(3)) // 5 hatalı denemede hesap kilitlenir
        {
            var attempt = await new ApiClient(Factory.CreateClient()).SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = loser.password, rememberMe = false });
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }
    }

    [Fact]
    public async Task Pasif_Owner_ve_yanlis_amacli_token_reddedilir_ve_token_tuketilmez()
    {
        var inactive = await NewOrganizationAsync();
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.Users.Where(u => u.Id == inactive.Created.OwnerUserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false), Ct);
        }

        await AssertInvalidLinkAsync(await SetPasswordAsync(Anonymous(), inactive.Token, StrongPassword));
        Assert.Equal(SetupTokenValidity.Valid, await ValidityAsync(inactive.Token)); // rollback: token kullanılmamış
        Assert.Null(await PasswordHashAsync(inactive.Created.OwnerUserId));

        // Şifre sıfırlama amaçlı token bu uç noktada geçmez (UPDATE amaç koşulunu içerir) ve yanmaz
        var other = await NewOrganizationAsync();
        string resetToken;
        await using (var asyncScope = Services.CreateAsyncScope())
        {
            var tokens = asyncScope.ServiceProvider.GetRequiredService<AccountSetupTokenService>();
            (resetToken, _) = await tokens.IssueAsync(other.Created.OwnerUserId, other.Created.OrganizationId, AccountSetupTokenPurpose.PasswordReset, TimeSpan.FromHours(1), null, Ct);
        }

        await AssertInvalidLinkAsync(await SetPasswordAsync(Anonymous(), resetToken, StrongPassword));
        Assert.Equal(SetupTokenValidity.Valid, await ValidityAsync(resetToken));
        Assert.Null(await PasswordHashAsync(other.Created.OwnerUserId));
    }

    [Fact]
    public async Task Gecersiz_istekler_ve_CSRF_olmadan_istek_reddedilir()
    {
        var anonymous = Anonymous();
        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(anonymous, "", StrongPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(anonymous, AccountSetupTokenCredentials.NewToken(), "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(anonymous, new string('x', 5000), StrongPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetPasswordAsync(anonymous, AccountSetupTokenCredentials.NewToken(), new string('p', 5000))).StatusCode);

        // CSRF başlığı olmadan (çerezli tarayıcı saldırısı): reddedilir
        using var raw = Factory.CreateClient();
        var response = await raw.PostAsJsonAsync(Endpoint, new { token = AccountSetupTokenCredentials.NewToken(), password = StrongPassword }, Ct);
        await ApiClient.AssertProblemAsync(response, HttpStatusCode.BadRequest, "CSRF_INVALID");
    }

    [Fact]
    public async Task IP_basina_hiz_siniri_uygulanir()
    {
        ApiFactory.RequireDatabase(Factory);
        using var limited = DerivedHost(b => b.UseSetting("Security:SetupPasswordRateLimitPerMinute", "3"));
        var client = Anonymous(limited);
        for (var i = 0; i < 3; i++)
        {
            await AssertInvalidLinkAsync(await SetPasswordAsync(client, AccountSetupTokenCredentials.NewToken(), StrongPassword));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await SetPasswordAsync(client, AccountSetupTokenCredentials.NewToken(), StrongPassword)).StatusCode);
    }

    [Fact]
    public async Task Denetim_kaydi_yazilamazsa_butun_islem_geri_alinir_token_yanmaz_parola_yazilmaz()
    {
        ApiFactory.RequireDatabase(Factory);
        var logs = new CapturingLoggerProvider();
        // Kullanıcı kimliği sunucu başlamadan bilinmez: kesinti, sonradan ayarlanan paylaşımlı bir kutudaki kimlik için çalışır
        var flag = new StrongBox<Guid>(Guid.Empty);
        using var host = DerivedHost(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<ILoggerFactory>(LoggerFactory.Create(l => l.SetMinimumLevel(LogLevel.Trace).AddProvider(logs)));
            s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(new LateBoundFailInterceptor(flag)));
        }));
        var (_, created, token, email) = await NewOrganizationAsync(host);
        flag.Value = created.OwnerUserId;

        var failed = await SetPasswordAsync(Anonymous(host), token, StrongPassword);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Null(await PasswordHashAsync(created.OwnerUserId)); // parola yazılmadı (aynı transaction geri alındı)
        Assert.Equal(SetupTokenValidity.Valid, await ValidityAsync(token)); // token tüketilmedi
        var attempt = await new ApiClient(Factory.CreateClient()).SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = StrongPassword, rememberMe = false });
        Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);

        // Hata giderilince aynı bağlantı çalışır (yanmamıştı)
        flag.Value = Guid.Empty;
        Assert.Equal(HttpStatusCode.NoContent, (await SetPasswordAsync(Anonymous(host), token, StrongPassword)).StatusCode);

        // Hata günlüğü dahil hiçbir günlük satırı token'ı veya parolayı içermez
        var all = string.Join('\n', logs.Lines);
        Assert.Contains("test: denetim yazilamadi", all, StringComparison.Ordinal); // istisna gerçekten günlüğe düştü (test geçerli)
        Assert.DoesNotContain(token, all, StringComparison.Ordinal);
        Assert.DoesNotContain(StrongPassword, all, StringComparison.Ordinal);
    }

    private sealed class LateBoundFailInterceptor(StrongBox<Guid> userId) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var target = userId.Value;
            if (target != Guid.Empty && eventData.Context!.ChangeTracker.Entries<SanalProva.Domain.Audit.AuditLog>().Any(e => e.State == EntityState.Added && e.Entity.Action == "Account.PasswordSet" && e.Entity.EntityId == target))
            {
                throw new InvalidOperationException("test: denetim yazilamadi");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Basarili_ve_basarisiz_butun_akislarda_gunluklere_token_ve_parola_sizmaz()
    {
        ApiFactory.RequireDatabase(Factory);
        var logs = new CapturingLoggerProvider();
        using var host = DerivedHost(b => b.ConfigureTestServices(s => s.AddSingleton<ILoggerFactory>(LoggerFactory.Create(l => l.SetMinimumLevel(LogLevel.Trace).AddProvider(logs)))));
        var (admin, created, token, email) = await NewOrganizationAsync(host);
        var anonymous = Anonymous(host);

        var weakPassword = "Zayif-Parola-1";
        var unknown = AccountSetupTokenCredentials.NewToken();
        await SetPasswordAsync(anonymous, unknown, weakPassword); // bilinmeyen token
        await SetPasswordAsync(anonymous, token, "kisa"); // politika ihlali
        var reissued = await ApiClient.ReadAsync<SetupLinkResponse>(await admin.SendAsync(HttpMethod.Post, $"/api/v1/platform/organizations/{created.OrganizationId}/owner/setup-link"));
        var newToken = PlatformOrganizationTests.TokenOf(reissued.SetupLink);
        await SetPasswordAsync(anonymous, token, StrongPassword); // iptal edilmiş
        Assert.Equal(HttpStatusCode.NoContent, (await SetPasswordAsync(anonymous, newToken, StrongPassword)).StatusCode);
        await SetPasswordAsync(anonymous, newToken, "Tekrar-Parola-123456"); // kullanılmış
        await ApiClient.LoginAsync(host, email, created.StoreId, StrongPassword);

        Assert.NotEmpty(logs.Lines);
        var all = string.Join('\n', logs.Lines);
        foreach (var secret in new[] { token, newToken, unknown, StrongPassword, weakPassword, "Tekrar-Parola-123456" })
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("#t=", all, StringComparison.Ordinal);
        Assert.DoesNotContain(email, all, StringComparison.OrdinalIgnoreCase); // e-posta (kişisel veri) günlüğe girmez
    }
}
