using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SanalProva.Api.Features.Account;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Monitoring;

namespace SanalProva.IntegrationTests;

/// <summary>
/// Pilot Faz C: ekran kilidi PIN'i (sunucu doğrulaması, sayaç, oturum kapatma, pasife alma), personel kredi görünümü, platform finans/izleme yetkisi.
/// Kanıtlananlar: PIN düz metin saklanmaz; hatalı denemelerde kalan hak döner, sınırda TÜM oturumlar düşer ve PIN korunur; PIN'li hesap parolayla kilit açamaz;
/// pasife alma PIN'i siler; Staff/Manager defteri ve tarifeyi göremez; platform finans/izleme yalnızca platform yöneticisine açık.
/// </summary>
public sealed class PilotFazCTests(ApiFactory factory) : ApiTestBase(factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string?> PinHashAsync(string email)
    {
        var (scope, db) = OpenDb();
        using (scope)
        {
            return await db.Users.AsNoTracking().Where(u => u.Email == email).Select(u => u.PinHash).SingleAsync(Ct);
        }
    }

    private static async Task SetPinAsync(ApiClient client, string pin = "4829") =>
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Put, "/api/v1/account/pin", new { currentPassword = TestData.Password, pin })).StatusCode);

    [Theory]
    [InlineData("1234")]
    [InlineData("4321")]
    [InlineData("1111")]
    [InlineData("123")]
    [InlineData("123456789")]
    [InlineData("12a4")]
    public void Zayif_pinler_politikayi_gecemez(string pin) => Assert.False(PinPolicy.IsAcceptable(pin));

    [Theory]
    [InlineData("4829")]
    [InlineData("135790")]
    public void Makul_pinler_kabul_edilir(string pin) => Assert.True(PinPolicy.IsAcceptable(pin));

    [Fact]
    public async Task Pin_belirleme_parola_ister_politikayi_uygular_ve_duz_metin_saklanmaz()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Staff);
        var client = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);

        var status = await client.GetAsync<LockStatusDto>("/api/v1/account/lock");
        Assert.False(status.HasPin);
        Assert.Equal(300, status.IdleLockSeconds);

        await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Put, "/api/v1/account/pin", new { currentPassword = TestData.Password, pin = "1234" }), HttpStatusCode.BadRequest, "PIN_POLICY");
        await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Put, "/api/v1/account/pin", new { currentPassword = "yanlis-parola", pin = "4829" }), HttpStatusCode.Unauthorized, "INVALID_PASSWORD");
        Assert.Null(await PinHashAsync(tenant.Email));

        await SetPinAsync(client);
        var hash = await PinHashAsync(tenant.Email);
        Assert.NotNull(hash);
        Assert.DoesNotContain("4829", hash, StringComparison.Ordinal);
        Assert.True((await client.GetAsync<LockStatusDto>("/api/v1/account/lock")).HasPin);
    }

    [Fact]
    public async Task Kilit_dogru_pinle_acilir_hatali_pinde_kalan_hak_doner_basari_sayaci_sifirlar()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Staff);
        var client = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        await SetPinAsync(client);

        var wrong = await client.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { pin = "9999" });
        await ApiClient.AssertProblemAsync(wrong, HttpStatusCode.Unauthorized, "INVALID_PIN");
        using (var doc = JsonDocument.Parse(await wrong.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Equal(PinPolicy.MaxAttempts - 1, doc.RootElement.GetProperty("remainingAttempts").GetInt32());
        }

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { pin = "4829" })).StatusCode);

        // Başarı sayacı sıfırladı: yeniden 5 hak var
        var again = await client.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { pin = "9999" });
        using var doc2 = JsonDocument.Parse(await again.Content.ReadAsStringAsync(Ct));
        Assert.Equal(PinPolicy.MaxAttempts - 1, doc2.RootElement.GetProperty("remainingAttempts").GetInt32());
    }

    [Fact]
    public async Task Ardisik_hatali_denemede_tum_oturumlar_kapanir_pin_korunur_yeniden_giris_calisir()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Staff);
        var client = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        var otherDevice = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        await SetPinAsync(client);

        for (var i = 1; i < PinPolicy.MaxAttempts; i++)
        {
            await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { pin = "0000" }), HttpStatusCode.Unauthorized, "INVALID_PIN");
        }

        await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { pin = "0000" }), HttpStatusCode.Unauthorized, "PIN_LOCKED_OUT");

        // Aynı hesabın diğer cihazındaki oturum da düşer; doğru PIN artık oturum açmaz (oturum yok)
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherDevice.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { pin = "4829" })).StatusCode);

        var fresh = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        Assert.Equal(HttpStatusCode.NoContent, (await fresh.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { pin = "4829" })).StatusCode);
    }

    [Fact]
    public async Task Pinli_hesap_parolayla_kilit_acamaz_pinsiz_hesap_parolayla_acar_ikisi_birden_gecersiz()
    {
        ApiFactory.RequireDatabase(Factory);
        var withPin = await TestData.CreateTenantAsync(Services, StoreRole.Staff);
        var withPinClient = await ApiClient.LoginAsync(Factory, withPin.Email, withPin.StoreId);
        await SetPinAsync(withPinClient);
        await ApiClient.AssertProblemAsync(await withPinClient.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { password = TestData.Password }), HttpStatusCode.Unauthorized, "INVALID_PASSWORD");

        var noPin = await TestData.CreateTenantAsync(Services, StoreRole.Staff);
        var noPinClient = await ApiClient.LoginAsync(Factory, noPin.Email, noPin.StoreId);
        Assert.Equal(HttpStatusCode.NoContent, (await noPinClient.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { password = TestData.Password })).StatusCode);
        await ApiClient.AssertProblemAsync(await noPinClient.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { password = "yanlis" }), HttpStatusCode.Unauthorized, "INVALID_PASSWORD");
        await ApiClient.AssertProblemAsync(await noPinClient.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { pin = "4829", password = TestData.Password }), HttpStatusCode.BadRequest, "UNLOCK_CREDENTIAL_REQUIRED");
        await ApiClient.AssertProblemAsync(await noPinClient.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { }), HttpStatusCode.BadRequest, "UNLOCK_CREDENTIAL_REQUIRED");
    }

    [Fact]
    public async Task Oturumsuz_kilit_ve_pin_uclari_401_doner()
    {
        ApiFactory.RequireDatabase(Factory);
        var anonymous = new ApiClient(Factory.CreateClient());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/account/lock")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { pin = "4829" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(HttpMethod.Put, "/api/v1/account/pin", new { currentPassword = "x", pin = "4829" })).StatusCode);
    }

    [Fact]
    public async Task Kilit_acma_kullanici_basina_hiz_sinirlidir()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Staff);
        var client = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        await SetPinAsync(client); // 1 istek (pencere payı)

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            codes.Add((await client.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { pin = "4829" })).StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        Assert.Equal(HttpStatusCode.NoContent, codes[0]);
    }

    [Fact]
    public async Task Pasife_alma_pini_siler_etkinlestirilince_eski_pin_gecmez_oturum_hemen_duser()
    {
        ApiFactory.RequireDatabase(Factory);
        var owner = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var ownerClient = await ApiClient.LoginAsync(Factory, owner.Email, owner.StoreId);
        var staffEmail = await TestData.AddUserAsync(Services, owner.OrganizationId, [(owner.StoreId, StoreRole.Staff)]);
        var staff = await ApiClient.LoginAsync(Factory, staffEmail, owner.StoreId);
        await SetPinAsync(staff);
        Guid staffId;
        var (scope, db) = OpenDb();
        using (scope)
        {
            staffId = await db.Users.Where(u => u.Email == staffEmail).Select(u => u.Id).SingleAsync(Ct);
        }

        await ownerClient.PatchAsync<JsonElement>($"/api/v1/management/users/{staffId}/status", new { isActive = false });

        Assert.Equal(HttpStatusCode.Unauthorized, (await staff.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { pin = "4829" })).StatusCode);
        Assert.Null(await PinHashAsync(staffEmail));

        await ownerClient.PatchAsync<JsonElement>($"/api/v1/management/users/{staffId}/status", new { isActive = true });
        var again = await ApiClient.LoginAsync(Factory, staffEmail, owner.StoreId);
        Assert.False((await again.GetAsync<LockStatusDto>("/api/v1/account/lock")).HasPin);
    }

    [Fact]
    public async Task Rol_dusurulunce_kilit_acik_olsa_bile_yonetim_uclari_sunucuda_reddedilir()
    {
        ApiFactory.RequireDatabase(Factory);
        var owner = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var managerEmail = await TestData.AddUserAsync(Services, owner.OrganizationId, [(owner.StoreId, StoreRole.Manager)]);
        var manager = await ApiClient.LoginAsync(Factory, managerEmail, owner.StoreId);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/v1/admin/usage")).StatusCode);

        var (scope, db) = OpenDb();
        using (scope)
        {
            var id = await db.Users.Where(u => u.Email == managerEmail).Select(u => u.Id).SingleAsync(Ct);
            await db.UserStoreRoles.IgnoreQueryFilters().Where(r => r.UserId == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Role, StoreRole.Staff), Ct);
        }

        // Kilit arayüz kalkanıdır: yetki her istekte veritabanındaki güncel rolden gelir
        await ApiClient.AssertProblemAsync(await manager.GetAsync("/api/v1/admin/usage"), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
    }

    // ---- Kredi görünürlüğü ----

    [Fact]
    public async Task Staff_ve_manager_yalnizca_bakiye_gorur_defter_ve_tarifeyi_goremez()
    {
        ApiFactory.RequireDatabase(Factory);
        var owner = await TestData.CreateTenantAsync(Services, StoreRole.Owner, credits: 7);
        foreach (var role in new[] { StoreRole.Staff, StoreRole.Manager })
        {
            var email = await TestData.AddUserAsync(Services, owner.OrganizationId, [(owner.StoreId, role)]);
            var client = await ApiClient.LoginAsync(Factory, email, owner.StoreId);
            var balance = await client.GetAsync<JsonElement>("/api/v1/credits/balance");
            Assert.Equal(7, balance.GetProperty("available").GetInt32());
            Assert.True(balance.GetProperty("isLow").GetBoolean()); // 7 ≤ varsayılan eşik 10
            Assert.False(balance.GetProperty("isEmpty").GetBoolean());
            Assert.False(balance.GetProperty("live").GetBoolean()); // mock: arayüz uyarı göstermez
            Assert.False(balance.TryGetProperty("reserved", out _));
            Assert.False(balance.TryGetProperty("tariff", out _));
            await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/credits"), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
            await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/credits/ledger"), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        }

        var ownerClient = await ApiClient.LoginAsync(Factory, owner.Email, owner.StoreId);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync("/api/v1/credits")).StatusCode);
    }

    [Fact]
    public async Task Baska_firmanin_bakiyesi_gorunmez_ve_oturumsuz_erisim_yoktur()
    {
        ApiFactory.RequireDatabase(Factory);
        var rich = await TestData.CreateTenantAsync(Services, StoreRole.Staff, credits: 500);
        var poor = await TestData.CreateTenantAsync(Services, StoreRole.Staff, credits: 0);
        var poorClient = await ApiClient.LoginAsync(Factory, poor.Email, poor.StoreId);
        var balance = await poorClient.GetAsync<JsonElement>("/api/v1/credits/balance");
        Assert.Equal(0, balance.GetProperty("available").GetInt32());
        Assert.True(balance.GetProperty("isEmpty").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await new ApiClient(Factory.CreateClient()) { StoreId = rich.StoreId }.GetAsync("/api/v1/credits/balance")).StatusCode);
    }

    [Fact]
    public async Task Bedel_bilgisi_mock_modda_sifirdir_ve_kredi_harcanmaz_der()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Staff, credits: 5);
        var client = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        var quote = await client.GetAsync<JsonElement>("/api/v1/credits/quote?jobType=OutfitSet");
        Assert.False(quote.GetProperty("live").GetBoolean()); // ana test sunucusu mock
        Assert.Equal(0, quote.GetProperty("cost").GetInt32());
        Assert.True(quote.GetProperty("sufficient").GetBoolean());
        Assert.Equal(5, quote.GetProperty("available").GetInt32());
    }

    // ---- Platform finans / izleme ----

    private async Task<ApiClient> PlatformAdminClientAsync()
    {
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.Users.Where(u => u.Email == tenant.Email).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsPlatformAdmin, true), Ct);
        }

        return await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
    }

    [Theory]
    [InlineData("/api/v1/platform/finance")]
    [InlineData("/api/v1/platform/monitoring")]
    [InlineData("/api/v1/platform/cost-cases")]
    public async Task Platform_finans_ve_izleme_yalnizca_platform_yoneticisine_aciktir(string url)
    {
        ApiFactory.RequireDatabase(Factory);
        foreach (var role in new[] { StoreRole.Staff, StoreRole.Manager, StoreRole.Owner })
        {
            var tenant = await TestData.CreateTenantAsync(Services, role);
            var client = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await new ApiClient(Factory.CreateClient()).GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Platform_finans_ozeti_bakiye_butce_ve_acik_rezervasyonu_gosterir()
    {
        ApiFactory.RequireDatabase(Factory);
        var org = await TestData.CreateTenantAsync(Services, StoreRole.Owner, credits: 4);
        var (scope, db) = OpenDb();
        using (scope)
        {
            var organization = await db.Organizations.SingleAsync(o => o.Id == org.OrganizationId, Ct);
            organization.Settings.DailyBudgetUsd = 1.5m;
            await db.SaveChangesAsync(Ct);
        }

        var admin = await PlatformAdminClientAsync();
        var list = await admin.GetAsync<List<OrganizationFinance>>("/api/v1/platform/finance");
        var row = Assert.Single(list, f => f.OrganizationId == org.OrganizationId);
        Assert.Equal(4, row.CreditBalance);
        Assert.Equal(0, row.CreditReserved);
        Assert.Equal(4, row.CreditAvailable);
        Assert.True(row.LowCredit);
        Assert.Equal(1.5m, row.DailyLimitUsd);
        Assert.Equal(0m, row.OpenReservedUsd);
        Assert.Equal(0, row.OpenUncertainCases);
        Assert.Equal(MonitorLevel.Ok, row.BudgetLevel);
    }

    [Fact]
    public async Task Izleme_kontrolleri_veritabani_disk_ve_ai_modunu_raporlar()
    {
        ApiFactory.RequireDatabase(Factory);
        var admin = await PlatformAdminClientAsync();
        var checks = await admin.GetAsync<List<MonitorCheck>>("/api/v1/platform/monitoring");
        Assert.Contains(checks, c => c.Name == "database" && c.Level != MonitorLevel.Critical);
        Assert.Contains(checks, c => c.Name == "disk");
        Assert.Contains(checks, c => c.Name == "ai-mode" && c.Message.Contains("Mock", StringComparison.Ordinal));
        Assert.Contains(checks, c => c.Name == "jobs");
        Assert.Contains(checks, c => c.Name == "uncertain-cases");
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0, MonitorLevel.Ok)]
    [InlineData(0.79, 0.0, 0.0, MonitorLevel.Ok)]
    [InlineData(0.8, 0.0, 0.0, MonitorLevel.Warning)]
    [InlineData(0.5, 0.0, 0.3, MonitorLevel.Warning)]
    [InlineData(1.0, 0.0, 0.0, MonitorLevel.Critical)]
    [InlineData(0.0, 29.0, 0.0, MonitorLevel.Warning)]
    [InlineData(0.0, 30.0, 0.0, MonitorLevel.Critical)]
    public void Butce_seviyesi_esikleri(double today, double month, double open, MonitorLevel expected)
    {
        var limits = new SanalProva.Infrastructure.Generation.BudgetLimits(1m, 30m, 0.25m, false, false, false);
        Assert.Equal(expected, PilotMonitoring.BudgetLevel((decimal)today, (decimal)month, (decimal)open, limits));
    }

    [Fact]
    public void Sifir_sinirli_butcede_harcama_kritiktir()
    {
        var closed = new SanalProva.Infrastructure.Generation.BudgetLimits(0m, 0m, 0m, true, true, true);
        Assert.Equal(MonitorLevel.Ok, PilotMonitoring.BudgetLevel(0m, 0m, 0m, closed));
        Assert.Equal(MonitorLevel.Critical, PilotMonitoring.BudgetLevel(0.01m, 0m, 0m, closed));
    }

    private async Task SeedUncertainAsync(TestTenant tenant, DateTimeOffset occurredAt, decimal cost = 0.1m)
    {
        var (scope, db) = OpenDb();
        using (scope)
        {
            db.AiUsageLogs.Add(new SanalProva.Domain.Generation.AiUsageLog
            {
                OrganizationId = tenant.OrganizationId, StoreId = tenant.StoreId, Provider = "Fashn", Model = "tryon-max", Operation = "OutfitSet", Attempt = 1,
                Success = false, ErrorCode = SanalProva.Domain.Generation.GenerationErrorCode.OutcomeUncertain, EstimatedCostUsd = cost, EventKey = $"uncertain:{Guid.NewGuid()}",
                ReconciliationState = SanalProva.Domain.Generation.CostReconciliationState.Open, OccurredAt = occurredAt,
            });
            await db.SaveChangesAsync(Ct);
        }
    }

    [Fact]
    public async Task Belirsiz_vaka_ve_asilan_butce_finans_ozetinde_ve_izlemede_gorunur()
    {
        ApiFactory.RequireDatabase(Factory);
        var org = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var (scope, db) = OpenDb();
        using (scope)
        {
            var organization = await db.Organizations.SingleAsync(o => o.Id == org.OrganizationId, Ct);
            organization.Settings.DailyBudgetUsd = 0.4m;
            await db.SaveChangesAsync(Ct);
        }

        await SeedUncertainAsync(org, DateTimeOffset.UtcNow.AddDays(-4));
        var (spendScope, spendDb) = OpenDb();
        using (spendScope)
        {
            spendDb.AiUsageLogs.Add(new SanalProva.Domain.Generation.AiUsageLog
            {
                OrganizationId = org.OrganizationId, StoreId = org.StoreId, Provider = "Fashn", Model = "tryon-max", Operation = "OutfitSet", Attempt = 1,
                Success = true, EstimatedCostUsd = 0.5m, EventKey = $"spend:{Guid.NewGuid()}", OccurredAt = DateTimeOffset.UtcNow,
            });
            await spendDb.SaveChangesAsync(Ct);
        }

        var admin = await PlatformAdminClientAsync();
        var row = Assert.Single(await admin.GetAsync<List<OrganizationFinance>>("/api/v1/platform/finance"), f => f.OrganizationId == org.OrganizationId);
        Assert.Equal(1, row.OpenUncertainCases);
        Assert.NotNull(row.OldestUncertainAt);
        Assert.Equal(MonitorLevel.Critical, row.BudgetLevel); // bugünkü 0,50 $ firma günlük sınırını (0,40 $) aştı
        Assert.Equal(0.4m, row.DailyLimitUsd);
    }

    [Fact]
    public async Task Uc_gunu_asan_belirsiz_vaka_izlemede_kritiktir()
    {
        ApiFactory.RequireDatabase(Factory);
        var org = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        await SeedUncertainAsync(org, DateTimeOffset.UtcNow.AddDays(-4));
        var admin = await PlatformAdminClientAsync();
        var checks = await admin.GetAsync<List<MonitorCheck>>("/api/v1/platform/monitoring");
        var uncertain = Assert.Single(checks, c => c.Name == "uncertain-cases");
        Assert.Equal(MonitorLevel.Critical, uncertain.Level);
        Assert.Contains("mutabakat", uncertain.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Operator_status_komutu_salt_okunur_calisir_ve_cikis_kodu_en_kotu_seviyeyi_yansitir()
    {
        ApiFactory.RequireDatabase(Factory);
        var org = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        await SeedUncertainAsync(org, DateTimeOffset.UtcNow.AddDays(-4));
        var writer = new StringWriter();
        var exit = await SanalProva.Api.Features.Platform.PlatformCommands.RunAsync(Services, ["platform", "status"], writer, Ct);
        var output = writer.ToString();
        Assert.Equal(SanalProva.Api.Features.Platform.PlatformCommands.ExitMonitorCritical, exit);
        Assert.Contains("[TAMAM ] database", output, StringComparison.Ordinal);
        Assert.Contains("[KRİTİK] uncertain-cases", output, StringComparison.Ordinal);
        Assert.DoesNotContain(TestData.Password, output, StringComparison.Ordinal);
    }

    private readonly List<Guid> _corruptOrganizations = [];

    /// <summary>Kasıtlı bozulan firmalar paylaşılan test veritabanında kalmasın (CI #16): test sonunda geçerli JSON'a onarılır.</summary>
    private async Task RepairCorruptOrganizationsAsync()
    {
        var (scope, db) = OpenDb();
        using (scope)
        {
            const string valid = "{}";
            foreach (var id in _corruptOrganizations)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE organizations SET settings = {valid} WHERE id = {id}", CancellationToken.None);
            }
        }
    }

    private async Task<Guid> CorruptOrganizationAsync(string settingsJson)
    {
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Owner, credits: 3);
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE organizations SET settings = {settingsJson} WHERE id = {tenant.OrganizationId}", Ct);
        }

        _corruptOrganizations.Add(tenant.OrganizationId);
        return tenant.OrganizationId;
    }

    /// <summary>
    /// CI #16 kök nedeni: PaidProviderPipelineTests fail-closed senaryoları için firma ayarını KASITLI bozar ("{bozuk json") ve satırı paylaşılan test veritabanında bırakır.
    /// Finans/izleme TÜM firmaların ayarını tek sorguda okuyordu: tek bozuk satır JsonException ile 500 veriyordu (platform status ve /platform/finance|monitoring kör kalıyordu).
    /// Veri bozulması uygulama hatası değil, ama izleme aracı tek bozuk firma yüzünden kör kalmamalı: bozuk firma fail-closed (ücretli AI kapalı, sınırlar 0) gösterilir ve
    /// KRİTİK uyarı üretir; diğer firmalar etkilenmez. Linux/CI'ya özgü bir EF davranışı DEĞİLDİR (Windows'ta da aynı veriyle aynı istisna).
    /// </summary>
    [Theory]
    [InlineData("{bozuk json")]
    [InlineData("{\"PaidAiEnabled\":\"evet\"}")]
    public async Task Bozuk_firma_ayari_finans_ve_izlemeyi_dusurmez_fail_closed_gosterilir_digerleri_etkilenmez(string settingsJson)
    {
        ApiFactory.RequireDatabase(Factory);
        var good = await TestData.CreateTenantAsync(Services, StoreRole.Owner, credits: 9);
        var corruptId = await CorruptOrganizationAsync(settingsJson);
        try
        {
        var admin = await PlatformAdminClientAsync();

        var finance = await admin.GetAsync<List<OrganizationFinance>>("/api/v1/platform/finance"); // eskiden 500
        var bad = Assert.Single(finance, f => f.OrganizationId == corruptId);
        Assert.True(bad.SettingsCorrupt);
        Assert.False(bad.PaidAiEnabled); // fail-closed
        Assert.Equal((0m, 0m, 0m), (bad.DailyLimitUsd, bad.MonthlyLimitUsd, bad.MaxJobCostUsd)); // sınırlar kapalı
        Assert.Equal(3, bad.CreditBalance); // kredi hesabı ayrı tablodan okunur, etkilenmez
        var fine = Assert.Single(finance, f => f.OrganizationId == good.OrganizationId);
        Assert.False(fine.SettingsCorrupt);
        Assert.Equal(9, fine.CreditBalance);

        var checks = await admin.GetAsync<List<MonitorCheck>>("/api/v1/platform/monitoring"); // eskiden 500
        Assert.Contains(checks, c => c.Name.StartsWith("settings:", StringComparison.Ordinal) && c.Level == MonitorLevel.Critical);

        var writer = new StringWriter();
        var exit = await SanalProva.Api.Features.Platform.PlatformCommands.RunAsync(Services, ["platform", "status"], writer, Ct); // eskiden JsonException
        Assert.Equal(SanalProva.Api.Features.Platform.PlatformCommands.ExitMonitorCritical, exit);
        Assert.Contains("[KRİTİK] settings:", writer.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await RepairCorruptOrganizationsAsync();
        }
    }

    /// <summary>GET /platform/organizations ve ayrıntı: bozuk firma ayarı listeyi düşürmez (eskiden 500); fail-closed işaretlenir, diğer firmalar ve sözleşme aynı kalır.</summary>
    [Theory]
    [InlineData("{bozuk json")]
    [InlineData("{\"PaidAiEnabled\":\"evet\"}")]
    public async Task Platform_firma_listesi_ve_ayrintisi_bozuk_ayarda_500_vermez_firma_isaretlenir(string settingsJson)
    {
        ApiFactory.RequireDatabase(Factory);
        var good = await TestData.CreateTenantAsync(Services, StoreRole.Owner, credits: 1);
        var (scope, db) = OpenDb();
        using (scope)
        {
            var organization = await db.Organizations.SingleAsync(o => o.Id == good.OrganizationId, Ct);
            organization.Settings.PaidAiEnabled = true;
            organization.Settings.DailyBudgetUsd = 1.25m;
            await db.SaveChangesAsync(Ct);
        }

        var corruptId = await CorruptOrganizationAsync(settingsJson); // en yeni firma: listenin başında
        try
        {
            var admin = await PlatformAdminClientAsync();
            var list = await admin.GetAsync<List<JsonElement>>("/api/v1/platform/organizations?take=200"); // eskiden 500
            var bad = Assert.Single(list, o => o.GetProperty("id").GetGuid() == corruptId);
            Assert.True(bad.GetProperty("settingsCorrupt").GetBoolean());
            Assert.False(bad.GetProperty("paidAiEnabled").GetBoolean()); // fail-closed
            var fine = Assert.Single(list, o => o.GetProperty("id").GetGuid() == good.OrganizationId);
            Assert.False(fine.GetProperty("settingsCorrupt").GetBoolean());
            Assert.True(fine.GetProperty("paidAiEnabled").GetBoolean()); // geçerli ayar değişmedi

            var detailBad = await admin.GetAsync<JsonElement>($"/api/v1/platform/organizations/{corruptId}");
            Assert.True(detailBad.GetProperty("settingsCorrupt").GetBoolean());
            Assert.Equal(JsonValueKind.Null, detailBad.GetProperty("dailyBudgetUsd").ValueKind);
            Assert.Equal(3, detailBad.GetProperty("creditBalance").GetInt32());
            var detailGood = await admin.GetAsync<JsonElement>($"/api/v1/platform/organizations/{good.OrganizationId}");
            Assert.False(detailGood.GetProperty("settingsCorrupt").GetBoolean());
            Assert.Equal(1.25m, detailGood.GetProperty("dailyBudgetUsd").GetDecimal());

            // Yetkilendirme aynı: platform yöneticisi olmayan 403
            var staff = await ApiClient.LoginAsync(Factory, good.Email, good.StoreId);
            Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/v1/platform/organizations")).StatusCode);
        }
        finally
        {
            await RepairCorruptOrganizationsAsync();
        }
    }

    /// <summary>EF "decimal precision" uyarısı (OrganizationSettings): alanlar JSON sütunundadır; sayı JSON metni olarak saklanır, SQL decimal kırpması YOKTUR. Kanıt: çok basamaklı değer birebir döner.</summary>
    [Fact]
    public async Task Firma_ayarindaki_decimal_alanlar_json_sutununda_kirpilmadan_saklanir()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var (scope, db) = OpenDb();
        using (scope)
        {
            var organization = await db.Organizations.SingleAsync(o => o.Id == tenant.OrganizationId, Ct);
            organization.Settings.DailyBudgetUsd = 0.123456789m;
            organization.Settings.MonthlyBudgetUsd = 12345.678901234m;
            await db.SaveChangesAsync(Ct);
        }

        var (scope2, db2) = OpenDb();
        using (scope2)
        {
            var settings = await db2.Organizations.AsNoTracking().Where(o => o.Id == tenant.OrganizationId).Select(o => o.Settings).SingleAsync(Ct);
            Assert.Equal(0.123456789m, settings.DailyBudgetUsd);
            Assert.Equal(12345.678901234m, settings.MonthlyBudgetUsd);
        }
    }
}
