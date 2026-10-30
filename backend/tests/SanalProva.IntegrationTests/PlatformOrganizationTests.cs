using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SanalProva.Api.Features.Platform;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Identity;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.IntegrationTests;

/// <summary>
/// M5.1c: platform yöneticisinin işletme (firma + ilk şube + Owner) oluşturması. Kanıtlananlar: yalnızca platform yöneticisi; bağlantı yalnızca ilk yanıtta;
/// idempotency tekrarı yeni firma/token üretmez ve bağlantıyı göstermez; eşzamanlı aynı anahtar tek firma; hata halinde hiçbir şey yazılmaz (rollback);
/// düz token veritabanının hiçbir metin sütununda ve denetim kaydında yok; platform yöneticisi yeni firmanın oturum/katalog verisine erişemez.
/// </summary>
public sealed class PlatformOrganizationTests(ApiFactory factory) : ApiTestBase(factory)
{
    private const string Orgs = "/api/v1/platform/organizations";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TestTenant Tenant, ApiClient Client)> PlatformAdminAsync()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.Users.Where(u => u.Email == tenant.Email).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsPlatformAdmin, true), Ct);
        }

        return (tenant, await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId));
    }

    internal static object Body(string? slug = null, string? email = null, string? name = null, string? displayName = null) => new
    {
        name = name ?? "Gül Gelinlik",
        slug = slug ?? $"gul-{Guid.NewGuid():N}"[..20],
        storeName = "Merkez Şube",
        storeCode = "mrk",
        ownerEmail = email ?? $"owner-{Guid.NewGuid():N}"[..20] + "@ornek.test",
        ownerDisplayName = displayName ?? "Ayşe Sahibi",
    };

    internal static Task<HttpResponseMessage> CreateRawAsync(ApiClient client, object body, string? key = null) =>
        client.SendAsync(HttpMethod.Post, Orgs, body, configure: r =>
        {
            if (key is not null)
            {
                r.Headers.Add("Idempotency-Key", key);
            }
        });

    internal static string NewKey() => Guid.NewGuid().ToString("N");

    internal static string TokenOf(string link) => link.Split("#t=")[1];

    /// <summary>Veritabanındaki TÜM metin sütunlarında (JSON ayarlar, denetim, hash'ler dahil) verilen değeri arar; bulunan satır sayısı döner.</summary>
    internal static async Task<int> CountOccurrencesAsync(string connectionString, string needle, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var columns = new List<(string Table, string Column)>();
        await using (var list = new SqlCommand(
            "SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND DATA_TYPE IN ('nvarchar','varchar','nchar','char','ntext','text')", connection))
        await using (var reader = await list.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                columns.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var total = 0;
        foreach (var (table, column) in columns)
        {
            await using var count = new SqlCommand($"SELECT COUNT(*) FROM [{table}] WHERE CHARINDEX(@n, CAST([{column}] AS nvarchar(max))) > 0", connection);
            count.Parameters.AddWithValue("@n", needle);
            total += (int)(await count.ExecuteScalarAsync(ct))!;
        }

        return total;
    }

    [Fact]
    public async Task Yalnizca_platform_yoneticisi_firma_olusturur_diger_herkes_reddedilir_ve_hicbir_sey_yazilmaz()
    {
        ApiFactory.RequireDatabase(Factory);
        var owner = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var ownerClient = await ApiClient.LoginAsync(Factory, owner.Email, owner.StoreId);
        var slug = $"yetkisiz-{Guid.NewGuid():N}"[..20];

        await ApiClient.AssertProblemAsync(await CreateRawAsync(ownerClient, Body(slug: slug), NewKey()), HttpStatusCode.Forbidden, "PLATFORM_ADMIN_REQUIRED"); // mağaza Owner'ı bile
        await ApiClient.AssertProblemAsync(await ownerClient.GetAsync(Orgs), HttpStatusCode.Forbidden, "PLATFORM_ADMIN_REQUIRED");
        await ApiClient.AssertProblemAsync(await ownerClient.SendAsync(HttpMethod.Post, $"{Orgs}/{owner.OrganizationId}/owner/setup-link"), HttpStatusCode.Forbidden, "PLATFORM_ADMIN_REQUIRED");

        var anonymous = new ApiClient(Factory.CreateClient());
        Assert.Equal(HttpStatusCode.Unauthorized, (await CreateRawAsync(anonymous, Body(slug: slug), NewKey())).StatusCode);

        var (scope, db) = OpenDb();
        using (scope)
        {
            Assert.False(await db.Organizations.AnyAsync(o => o.Slug == slug, Ct));
        }
    }

    [Fact]
    public async Task Firma_ilk_sube_ve_parolasiz_Owner_olusur_baglanti_yalnizca_ilk_yanitta_gosterilir_ve_hicbir_yerde_saklanmaz()
    {
        var (_, client) = await PlatformAdminAsync();
        var body = Body();
        var email = (string)body.GetType().GetProperty("ownerEmail")!.GetValue(body)!;
        var slug = (string)body.GetType().GetProperty("slug")!.GetValue(body)!;
        var before = DateTimeOffset.UtcNow;

        var response = await CreateRawAsync(client, body, NewKey());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("no-cache", response.Headers.Pragma.ToString(), StringComparison.Ordinal);
        var created = await ApiClient.ReadAsync<CreateOrganizationResponse>(response, HttpStatusCode.Created);

        Assert.True(created.SetupLinkShown);
        Assert.StartsWith("/hesap/sifre-belirle#t=", created.SetupLink, StringComparison.Ordinal);
        var token = TokenOf(created.SetupLink!);
        Assert.True(token.Length >= 43);
        Assert.InRange(created.SetupLinkExpiresAt!.Value, before.AddHours(71.9), before.AddHours(72.1)); // D1: 72 saat

        var (scope, db) = OpenDb();
        using (scope)
        {
            var org = await db.Organizations.AsNoTracking().SingleAsync(o => o.Slug == slug, Ct);
            Assert.Equal(created.OrganizationId, org.Id);
            Assert.False(org.Settings.PaidAiEnabled); // yeni firma ücretli AI'ya otomatik yetkili değil
            Assert.True(org.IsActive);
            Assert.NotNull(org.CreatedByUserId);

            var store = await db.Stores.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.OrganizationId == org.Id, Ct);
            Assert.Equal(created.StoreId, store.Id);
            Assert.Equal("MRK", store.Code);

            var owner = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email, Ct);
            Assert.Equal(created.OwnerUserId, owner.Id);
            Assert.Equal(org.Id, owner.OrganizationId);
            Assert.Null(owner.PasswordHash); // şifre yok: giriş yapılamaz
            Assert.False(owner.IsPlatformAdmin);
            var role = await db.UserStoreRoles.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.UserId == owner.Id, Ct);
            Assert.Equal((store.Id, StoreRole.Owner), (role.StoreId, role.Role));

            var tokenRow = await db.AccountSetupTokens.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.UserId == owner.Id, Ct);
            Assert.Equal(AccountSetupTokenPurpose.InitialPassword, tokenRow.Purpose);
            Assert.Equal(org.CreatedByUserId, tokenRow.CreatedByUserId);

            // Denetim: hedef firmaya, platform yöneticisi adına; yalnızca olay adı/varlık türü/kimlik (kişisel veri ve token yok)
            var audit = await db.AuditLogs.AsNoTracking().Where(a => a.OrganizationId == org.Id).OrderBy(a => a.Id).ToListAsync(Ct);
            Assert.Equal(["Platform.OrganizationCreated", "Platform.SetupLinkIssued"], audit.Select(a => a.Action));
            Assert.All(audit, a => Assert.Equal(org.CreatedByUserId, a.UserId));
        }

        // Giriş yapılamaz (hangi alanın yanlış olduğu söylenmez)
        var login = new ApiClient(Factory.CreateClient());
        await ApiClient.AssertProblemAsync(await login.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = "Rastgele-Parola-123", rememberMe = false }), HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");

        // Düz token ve e-posta dışında hiçbir değer sızmaz: tüm metin sütunları (denetim, JSON ayarlar, hash'ler) düz token'ı içermez
        Assert.Equal(0, await CountOccurrencesAsync(Factory.TestConnectionString!, token, Ct));
    }

    [Fact]
    public async Task Ayni_anahtar_ayni_govde_tekrarinda_yeni_firma_ve_token_uretilmez_baglanti_tekrar_gosterilmez()
    {
        var (_, client) = await PlatformAdminAsync();
        var body = Body();
        var slug = (string)body.GetType().GetProperty("slug")!.GetValue(body)!;
        var key = NewKey();

        var first = await ApiClient.ReadAsync<CreateOrganizationResponse>(await CreateRawAsync(client, body, key), HttpStatusCode.Created);
        var replayResponse = await CreateRawAsync(client, body, key);
        var replayText = await replayResponse.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal("no-store", replayResponse.Headers.CacheControl?.ToString());
        var replay = System.Text.Json.JsonSerializer.Deserialize<CreateOrganizationResponse>(replayText, System.Text.Json.JsonSerializerOptions.Web)!;

        Assert.Equal((first.OrganizationId, first.StoreId, first.OwnerUserId), (replay.OrganizationId, replay.StoreId, replay.OwnerUserId));
        Assert.False(replay.SetupLinkShown);
        Assert.Null(replay.SetupLink);
        Assert.DoesNotContain(TokenOf(first.SetupLink!), replayText, StringComparison.Ordinal); // eski yanıt/kayıt tekrarda token sızdırmaz

        var (scope, db) = OpenDb();
        using (scope)
        {
            Assert.Equal(1, await db.Organizations.CountAsync(o => o.Slug == slug, Ct));
            Assert.Equal(1, await db.AccountSetupTokens.IgnoreQueryFilters().CountAsync(t => t.UserId == first.OwnerUserId, Ct)); // tekrar yeni token açmadı
            Assert.Equal(2, await db.AuditLogs.CountAsync(a => a.OrganizationId == first.OrganizationId, Ct)); // tekrar yeni denetim kaydı da yazmadı
        }

        // Aynı anahtar + farklı gövde: reddedilir ve hiçbir şey oluşmaz
        var otherSlug = $"diger-{Guid.NewGuid():N}"[..20];
        await ApiClient.AssertProblemAsync(await CreateRawAsync(client, Body(slug: otherSlug), key), HttpStatusCode.UnprocessableEntity, "IDEMPOTENCY_KEY_REUSED");
        (scope, db) = OpenDb();
        using (scope)
        {
            Assert.False(await db.Organizations.AnyAsync(o => o.Slug == otherSlug, Ct));
        }
    }

    [Fact]
    public async Task Eszamanli_ayni_anahtarla_tek_firma_olusur_kalan_istekler_tekrar_sayilir()
    {
        var (admin, _) = await PlatformAdminAsync();
        var body = Body();
        var slug = (string)body.GetType().GetProperty("slug")!.GetValue(body)!;
        var key = NewKey();

        var clients = new List<ApiClient>();
        for (var i = 0; i < 8; i++)
        {
            clients.Add(await ApiClient.LoginAsync(Factory, admin.Email, admin.StoreId)); // parola özeti CPU yükü sıralı; yarışan kısım yalnızca oluşturma isteğidir
        }

        var statuses = await Task.WhenAll(clients.Select(async client =>
        {
            var response = await CreateRawAsync(client, body, key);
            var text = await response.Content.ReadAsStringAsync(Ct);
            return (response.StatusCode, Text: text);
        }));

        var detail = string.Join(" | ", statuses.Select(s => $"{(int)s.StatusCode}:{(s.Text.Length > 140 ? s.Text[..140] : s.Text)}"));
        Assert.True(statuses.Count(s => s.StatusCode == HttpStatusCode.Created) == 1, detail);
        Assert.True(statuses.Count(s => s.StatusCode == HttpStatusCode.OK) == 7, detail);
        var linkCount = statuses.Count(s => s.Text.Contains("#t=", StringComparison.Ordinal));
        Assert.Equal(1, linkCount); // bağlantı yalnızca tek yanıtta

        var (scope, db) = OpenDb();
        using (scope)
        {
            var org = await db.Organizations.AsNoTracking().SingleAsync(o => o.Slug == slug, Ct);
            Assert.Equal(1, await db.Stores.IgnoreQueryFilters().CountAsync(s => s.OrganizationId == org.Id, Ct));
            Assert.Equal(1, await db.AccountSetupTokens.IgnoreQueryFilters().CountAsync(t => t.OrganizationId == org.Id, Ct));
        }
    }

    [Fact]
    public async Task Slug_ve_eposta_cakismasi_409_doner_hata_halinde_hicbir_sey_yazilmaz()
    {
        var (admin, client) = await PlatformAdminAsync();
        var first = Body();
        var firstSlug = (string)first.GetType().GetProperty("slug")!.GetValue(first)!;
        var firstEmail = (string)first.GetType().GetProperty("ownerEmail")!.GetValue(first)!;
        await ApiClient.ReadAsync<CreateOrganizationResponse>(await CreateRawAsync(client, first, NewKey()), HttpStatusCode.Created);

        // Aynı slug (farklı anahtar, farklı e-posta)
        await ApiClient.AssertProblemAsync(await CreateRawAsync(client, Body(slug: firstSlug), NewKey()), HttpStatusCode.Conflict, "SLUG_TAKEN");

        // Aynı e-posta (büyük/küçük harf fark etmez; farklı slug) — sistem genelinde benzersiz
        var freeSlug = $"bos-{Guid.NewGuid():N}"[..20];
        await ApiClient.AssertProblemAsync(await CreateRawAsync(client, Body(slug: freeSlug, email: firstEmail.ToUpperInvariant()), NewKey()), HttpStatusCode.Conflict, "EMAIL_TAKEN");

        // Mevcut başka bir firmadaki personelin e-postası da alınmış sayılır
        await ApiClient.AssertProblemAsync(await CreateRawAsync(client, Body(slug: freeSlug, email: admin.Email), NewKey()), HttpStatusCode.Conflict, "EMAIL_TAKEN");

        // Transaction ortasında patlayan durum (kullanıcı adı çakışması; e-posta ön kontrolünden geçer): firma ve şube geri alınır
        var hiddenEmail = $"gizli-{Guid.NewGuid():N}"[..20] + "@ornek.test";
        await using (var scope = Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var squatter = new AppUser { OrganizationId = admin.OrganizationId, DisplayName = "Eski Kayıt", Email = $"baska-{Guid.NewGuid():N}"[..20] + "@ornek.test", UserName = hiddenEmail };
            Assert.True((await users.CreateAsync(squatter, TestData.Password)).Succeeded);
        }

        await ApiClient.AssertProblemAsync(await CreateRawAsync(client, Body(slug: freeSlug, email: hiddenEmail), NewKey()), HttpStatusCode.Conflict, "EMAIL_TAKEN");
        var (db2Scope, db) = OpenDb();
        using (db2Scope)
        {
            Assert.False(await db.Organizations.AnyAsync(o => o.Slug == freeSlug, Ct)); // org + şube rollback
        }
    }

    [Fact]
    public async Task Gecersiz_istekler_400_doner_idempotency_anahtari_zorunludur()
    {
        var (_, client) = await PlatformAdminAsync();
        await ApiClient.AssertProblemAsync(await CreateRawAsync(client, Body()), HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_REQUIRED");
        await ApiClient.AssertProblemAsync(await CreateRawAsync(client, Body(), "kisa"), HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_REQUIRED");
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateRawAsync(client, Body(slug: "Gecersiz Slug"), NewKey())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateRawAsync(client, Body(email: "e-posta-degil"), NewKey())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateRawAsync(client, Body(name: ""), NewKey())).StatusCode);
    }

    [Fact]
    public async Task Platform_yoneticisi_yeni_firmanin_oturum_katalog_ve_yonetim_verisine_erisemez()
    {
        var (_, admin) = await PlatformAdminAsync();
        var created = await ApiClient.ReadAsync<CreateOrganizationResponse>(await CreateRawAsync(admin, Body(), NewKey()), HttpStatusCode.Created);

        // Yeni firmanın kataloğu var; platform yöneticisi şubeyi X-Store-Id ile seçse de rolü yoktur
        var catalog = await TestData.CreateCatalogAsync(Services, created.OrganizationId);
        admin.StoreId = created.StoreId;
        await ApiClient.AssertProblemAsync(await admin.GetAsync("/api/v1/sessions"), HttpStatusCode.Forbidden, "STORE_FORBIDDEN");
        await ApiClient.AssertProblemAsync(await admin.SendAsync(HttpMethod.Post, "/api/v1/sessions", new { customerLabel = "x" }), HttpStatusCode.Forbidden, "STORE_FORBIDDEN");

        // Katalog firma rolüne bağlıdır ve yöneticinin KENDİ firmasına sınırlıdır: yeni firmanın hiçbir kategori/ürünü görünmez
        var categories = await (await admin.GetAsync("/api/v1/categories")).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(catalog.Salvar.ToString(), categories, StringComparison.OrdinalIgnoreCase);
        var products = await (await admin.GetAsync("/api/v1/products")).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(catalog.SalvarProduct.ToString(), products, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/products/{catalog.SalvarProduct}")).StatusCode);
    }

    [Fact]
    public async Task Okuma_uclari_yalnizca_ozet_doner_pasif_sifre_bekleyen_Owner_gorunur_olmayan_firma_404()
    {
        var (_, admin) = await PlatformAdminAsync();
        var body = Body();
        var slug = (string)body.GetType().GetProperty("slug")!.GetValue(body)!;
        var created = await ApiClient.ReadAsync<CreateOrganizationResponse>(await CreateRawAsync(admin, body, NewKey()), HttpStatusCode.Created);

        var list = await admin.GetAsync<List<OrganizationListItem>>($"{Orgs}?take=200");
        var item = Assert.Single(list, o => o.Slug == slug);
        Assert.True(item.OwnerSetupPending);
        Assert.False(item.PaidAiEnabled);

        var detail = await admin.GetAsync<OrganizationDetail>($"{Orgs}/{created.OrganizationId}");
        Assert.Equal(created.OrganizationId, detail.Id);
        Assert.Single(detail.Stores);
        var owner = Assert.Single(detail.Owners);
        Assert.True(owner.SetupPending);
        Assert.Equal(created.OwnerUserId, owner.UserId);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Orgs}/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(HttpMethod.Post, $"{Orgs}/{Guid.NewGuid()}/owner/setup-link")).StatusCode);
    }

    [Fact]
    public async Task Yeniden_uretim_onceki_baglantiyi_iptal_eder_yeni_baglanti_gosterilir_ve_denetlenir()
    {
        var (_, admin) = await PlatformAdminAsync();
        var created = await ApiClient.ReadAsync<CreateOrganizationResponse>(await CreateRawAsync(admin, Body(), NewKey()), HttpStatusCode.Created);
        var firstToken = TokenOf(created.SetupLink!);

        var reissue = await admin.SendAsync(HttpMethod.Post, $"{Orgs}/{created.OrganizationId}/owner/setup-link");
        Assert.Equal("no-store", reissue.Headers.CacheControl?.ToString());
        var link = await ApiClient.ReadAsync<SetupLinkResponse>(reissue);
        var secondToken = TokenOf(link.SetupLink);
        Assert.NotEqual(firstToken, secondToken);
        Assert.Equal(created.OwnerUserId, link.OwnerUserId);

        await using var scope = Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<AccountSetupTokenService>();
        Assert.Equal(SetupTokenValidity.Revoked, await tokens.ValidateAsync(firstToken, Ct));
        Assert.Equal(SetupTokenValidity.Valid, await tokens.ValidateAsync(secondToken, Ct));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(3, await db.AuditLogs.CountAsync(a => a.OrganizationId == created.OrganizationId, Ct)); // oluşturma + 2 bağlantı
        Assert.Equal(0, await CountOccurrencesAsync(Factory.TestConnectionString!, secondToken, Ct));
    }
}
