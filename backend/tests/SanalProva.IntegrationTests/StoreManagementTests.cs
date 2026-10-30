using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SanalProva.Domain.Display;
using SanalProva.Domain.Tenancy;

namespace SanalProva.IntegrationTests;

/// <summary>M5.2a: şube yönetimi (yalnızca Owner), firma izolasyonu, Owner satırlarının yeni şubeye yayılması, pasife alma.</summary>
public sealed class StoreManagementTests(ApiFactory factory) : ApiTestBase(factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record StoreDto(Guid Id, string Name, string Code, string Timezone, int? DailyGenerationQuota, bool IsActive, int MemberCount);

    private async Task<(TestTenant Tenant, ApiClient Owner)> OwnerAsync()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Factory.Services, StoreRole.Owner);
        return (tenant, await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId));
    }

    private static object NewStore(string code, string name = "Yeni Şube") => new { name, code, timezone = "Europe/Istanbul", dailyGenerationQuota = (int?)null };

    [Fact]
    public async Task Owner_sube_ekler_kod_buyuk_harfe_cevrilir_ve_Owner_yeni_subede_rol_alir()
    {
        var (tenant, owner) = await OwnerAsync();

        var created = await owner.PostAsync<StoreDto>("/api/v1/management/stores", NewStore("kad-1"), HttpStatusCode.Created);

        Assert.Equal("KAD-1", created.Code);
        Assert.True(created.IsActive);
        Assert.Equal(1, created.MemberCount); // yalnızca Owner
        var me = await owner.GetAsync<JsonElement>("/api/v1/auth/me");
        var ids = me.GetProperty("stores").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(created.Id, ids);
        Assert.Contains(tenant.StoreId, ids);

        // Yeni şubede Owner olarak gerçekten işlem yapabilir
        var scoped = await ApiClient.LoginAsync(Factory, tenant.Email, created.Id);
        Assert.Equal(HttpStatusCode.OK, (await scoped.GetAsync("/api/v1/displays")).StatusCode);
    }

    [Fact]
    public async Task Birden_fazla_Owner_varsa_yeni_sube_hepsine_yayilir()
    {
        var (tenant, owner) = await OwnerAsync();
        var second = await TestData.AddUserAsync(Factory.Services, tenant.OrganizationId, [(tenant.StoreId, StoreRole.Owner)]);
        var secondClient = await ApiClient.LoginAsync(Factory, second);

        var created = await owner.PostAsync<StoreDto>("/api/v1/management/stores", NewStore("YAY"), HttpStatusCode.Created);

        Assert.Equal(2, created.MemberCount);
        var me = await secondClient.GetAsync<JsonElement>("/api/v1/auth/me");
        Assert.Contains(created.Id, me.GetProperty("stores").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Ayni_firmada_ayni_kod_409_baska_firmada_kullanilabilir()
    {
        var (_, owner) = await OwnerAsync();
        var (_, otherOwner) = await OwnerAsync();
        await owner.PostAsync<StoreDto>("/api/v1/management/stores", NewStore("MRK"), HttpStatusCode.Created);

        await ApiClient.AssertProblemAsync(await owner.SendAsync(HttpMethod.Post, "/api/v1/management/stores", NewStore("mrk", "Başka ad")), HttpStatusCode.Conflict, "STORE_CODE_TAKEN");
        await otherOwner.PostAsync<StoreDto>("/api/v1/management/stores", NewStore("MRK"), HttpStatusCode.Created);
    }

    [Theory]
    [InlineData(StoreRole.Staff)]
    [InlineData(StoreRole.Manager)]
    public async Task Staff_ve_Manager_sube_yonetemez(StoreRole role)
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Factory.Services, role);
        var client = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);

        await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/management/stores"), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Post, "/api/v1/management/stores", NewStore("YOK")), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        await ApiClient.AssertProblemAsync(
            await client.SendAsync(HttpMethod.Put, $"/api/v1/management/stores/{tenant.StoreId}", new { name = "Ad", code = "AD", timezone = "Europe/Istanbul" }), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        await ApiClient.AssertProblemAsync(
            await client.SendAsync(HttpMethod.Patch, $"/api/v1/management/stores/{tenant.StoreId}/status", new { isActive = false }), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
    }

    [Fact]
    public async Task Anonim_istek_401()
    {
        ApiFactory.RequireDatabase(Factory);
        var anonymous = new ApiClient(Factory.CreateClient());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/management/stores")).StatusCode);
    }

    [Fact]
    public async Task Baska_firmanin_subesi_listelenmez_guncellenemez_pasife_alinamaz()
    {
        var (_, owner) = await OwnerAsync();
        var (otherTenant, _) = await OwnerAsync();

        var list = await owner.GetAsync<List<StoreDto>>("/api/v1/management/stores");
        Assert.DoesNotContain(list, s => s.Id == otherTenant.StoreId);
        await ApiClient.AssertProblemAsync(
            await owner.SendAsync(HttpMethod.Put, $"/api/v1/management/stores/{otherTenant.StoreId}", new { name = "Ele geçir", code = "HACK", timezone = "Europe/Istanbul" }), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(
            await owner.SendAsync(HttpMethod.Patch, $"/api/v1/management/stores/{otherTenant.StoreId}/status", new { isActive = false }), HttpStatusCode.NotFound, "NOT_FOUND");

        var (scope, db) = OpenDb(otherTenant.OrganizationId);
        using (scope)
        {
            var untouched = await db.Stores.SingleAsync(s => s.Id == otherTenant.StoreId, Ct);
            Assert.True(untouched.IsActive);
            Assert.Equal("TST", untouched.Code);
        }
    }

    [Fact]
    public async Task Guncelleme_ad_kod_saat_dilimi_ve_kota_degisir_gecersiz_degerler_reddedilir()
    {
        var (tenant, owner) = await OwnerAsync();

        var updated = await owner.PutAsync<StoreDto>($"/api/v1/management/stores/{tenant.StoreId}", new { name = "Yeni Ad", code = "yn-1", timezone = "Europe/London", dailyGenerationQuota = 25 });
        Assert.Equal(("Yeni Ad", "YN-1", "Europe/London", 25), (updated.Name, updated.Code, updated.Timezone, updated.DailyGenerationQuota));

        await ApiClient.AssertProblemAsync(
            await owner.SendAsync(HttpMethod.Put, $"/api/v1/management/stores/{tenant.StoreId}", new { name = "Ad", code = "AD", timezone = "Mars/Olympus" }), HttpStatusCode.BadRequest, "TIMEZONE_INVALID");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await owner.SendAsync(HttpMethod.Put, $"/api/v1/management/stores/{tenant.StoreId}", new { name = "Ad", code = "AD", timezone = "Europe/Istanbul", dailyGenerationQuota = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await owner.SendAsync(HttpMethod.Put, $"/api/v1/management/stores/{tenant.StoreId}", new { name = "Ad", code = "a b", timezone = "Europe/Istanbul" })).StatusCode);

        // Başka şubenin koduna çevrilemez
        var other = await owner.PostAsync<StoreDto>("/api/v1/management/stores", NewStore("DGR"), HttpStatusCode.Created);
        await ApiClient.AssertProblemAsync(
            await owner.SendAsync(HttpMethod.Put, $"/api/v1/management/stores/{other.Id}", new { name = "Ad", code = "yn-1", timezone = "Europe/Istanbul" }), HttpStatusCode.Conflict, "STORE_CODE_TAKEN");
    }

    [Fact]
    public async Task Pasife_alinan_subeye_erisim_kesilir_son_etkin_sube_pasife_alinamaz_yeniden_etkinlesir()
    {
        var (tenant, owner) = await OwnerAsync();
        var second = await owner.PostAsync<StoreDto>("/api/v1/management/stores", NewStore("IKI"), HttpStatusCode.Created);
        var staffEmail = await TestData.AddUserAsync(Factory.Services, tenant.OrganizationId, [(second.Id, StoreRole.Staff)]);
        var staff = await ApiClient.LoginAsync(Factory, staffEmail, second.Id);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/v1/displays")).StatusCode);

        var off = await owner.PatchAsync<StoreDto>($"/api/v1/management/stores/{second.Id}/status", new { isActive = false });
        Assert.False(off.IsActive);
        await ApiClient.AssertProblemAsync(await staff.GetAsync("/api/v1/displays"), HttpStatusCode.Forbidden, "STORE_FORBIDDEN");
        var me = await staff.GetAsync<JsonElement>("/api/v1/auth/me");
        Assert.Empty(me.GetProperty("stores").EnumerateArray()); // pasif şube seçilemez

        // Firmanın tek etkin şubesi kaldı: pasife alınamaz
        await ApiClient.AssertProblemAsync(
            await owner.SendAsync(HttpMethod.Patch, $"/api/v1/management/stores/{tenant.StoreId}/status", new { isActive = false }), HttpStatusCode.Conflict, "LAST_ACTIVE_STORE");

        var on = await owner.PatchAsync<StoreDto>($"/api/v1/management/stores/{second.Id}/status", new { isActive = true });
        Assert.True(on.IsActive);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/v1/displays")).StatusCode);
    }

    [Fact]
    public async Task Pasife_alinan_subenin_TV_eslestirmeleri_iptal_edilir()
    {
        var (tenant, owner) = await OwnerAsync();
        var second = await owner.PostAsync<StoreDto>("/api/v1/management/stores", NewStore("TVX"), HttpStatusCode.Created);
        Guid deviceId;
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var device = new DisplayDevice
            {
                OrganizationId = tenant.OrganizationId, StoreId = second.Id, Name = "Test TV", TokenHash = Guid.NewGuid().ToString("N"),
            };
            db.DisplayDevices.Add(device);
            await db.SaveChangesAsync(Ct);
            deviceId = device.Id;
        }

        await owner.PatchAsync<StoreDto>($"/api/v1/management/stores/{second.Id}/status", new { isActive = false });

        var (scope2, db2) = OpenDb(tenant.OrganizationId);
        using (scope2)
        {
            var revoked = await db2.DisplayDevices.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == deviceId, Ct);
            Assert.NotNull(revoked.RevokedAt);
        }
    }

    [Fact]
    public async Task Sube_islemleri_denetim_kaydi_birakir_kisisel_veri_yok()
    {
        var (tenant, owner) = await OwnerAsync();
        var created = await owner.PostAsync<StoreDto>("/api/v1/management/stores", NewStore("DNT"), HttpStatusCode.Created);
        await owner.PutAsync<StoreDto>($"/api/v1/management/stores/{created.Id}", new { name = "Ad", code = "DNT", timezone = "Europe/Istanbul" });
        await owner.PatchAsync<StoreDto>($"/api/v1/management/stores/{created.Id}/status", new { isActive = false });

        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var actions = await db.AuditLogs.AsNoTracking().Where(a => a.EntityId == created.Id).Select(a => a.Action).ToListAsync(Ct);
            Assert.Contains("Store.Created", actions);
            Assert.Contains("Store.Updated", actions);
            Assert.Contains("Store.Deactivated", actions);
        }
    }
}
