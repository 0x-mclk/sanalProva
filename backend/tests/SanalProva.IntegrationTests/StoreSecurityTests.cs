using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SanalProva.Api.Features.Catalog;
using SanalProva.Api.Features.TryOn;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Tenancy;

namespace SanalProva.IntegrationTests;

/// <summary>Firma ve şube izolasyonu ile rol yetkileri. Yetkisiz erişimlerin hepsi gerçek HTTP katmanında sınanır.</summary>
public sealed class StoreSecurityTests(ApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Sube_basligi_olmayan_istek_reddedilir()
    {
        var (_, client) = await StaffAsync();
        client.StoreId = null;

        await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/sessions"), HttpStatusCode.BadRequest, "STORE_REQUIRED");
    }

    [Fact]
    public async Task Rolu_olmayan_sube_ayni_firmada_olsa_da_reddedilir()
    {
        var (tenant, client) = await StaffAsync();
        client.StoreId = await TestData.AddStoreAsync(Services, tenant.OrganizationId, "OTH");

        await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/sessions"), HttpStatusCode.Forbidden, "STORE_FORBIDDEN");
    }

    [Fact]
    public async Task Baska_firmanin_subesi_ve_var_olmayan_sube_ayni_sekilde_reddedilir()
    {
        var (_, client) = await StaffAsync();
        var other = await TestData.CreateTenantAsync(Services);

        client.StoreId = other.StoreId;
        await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/sessions"), HttpStatusCode.Forbidden, "STORE_FORBIDDEN");

        client.StoreId = Guid.NewGuid();
        await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/sessions"), HttpStatusCode.Forbidden, "STORE_FORBIDDEN");
    }

    [Fact]
    public async Task Pasif_sube_reddedilir()
    {
        var (tenant, client) = await StaffAsync();
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.Stores.IgnoreQueryFilters().Where(s => s.Id == tenant.StoreId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), TestContext.Current.CancellationToken);
        }

        await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/sessions"), HttpStatusCode.Forbidden, "STORE_FORBIDDEN");
    }

    [Fact]
    public async Task Iki_subesi_olan_kullanici_her_subede_yalnizca_o_subenin_oturumlarini_gorur()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services);
        var storeB = await TestData.AddStoreAsync(Services, tenant.OrganizationId, "SB");
        var email = await TestData.AddUserAsync(Services, tenant.OrganizationId, [(tenant.StoreId, StoreRole.Staff), (storeB, StoreRole.Staff)]);
        var client = await ApiClient.LoginAsync(Factory, email, tenant.StoreId);

        var inA = await CreateSessionAsync(client, "A müşterisi");
        client.StoreId = storeB;
        var inB = await CreateSessionAsync(client, "B müşterisi");

        var listB = await client.GetAsync<Page<SessionSummary>>("/api/v1/sessions");
        Assert.Equal([inB.Id], listB.Items.Select(s => s.Id));
        await ApiClient.AssertProblemAsync(await client.GetAsync($"/api/v1/sessions/{inA.Id}"), HttpStatusCode.NotFound, "NOT_FOUND");

        client.StoreId = tenant.StoreId;
        var listA = await client.GetAsync<Page<SessionSummary>>("/api/v1/sessions");
        Assert.Equal([inA.Id], listA.Items.Select(s => s.Id));
        Assert.Equal(inA.Id, (await client.GetAsync<SessionDetail>($"/api/v1/sessions/{inA.Id}")).Id);
    }

    [Fact]
    public async Task Baska_subenin_personeli_oturumu_goremez_degistiremez_silemez()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services);
        var storeB = await TestData.AddStoreAsync(Services, tenant.OrganizationId, "SB");
        var ownerA = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        var (session, photo) = await PreparedSessionAsync(ownerA);
        var staffB = await ApiClient.LoginAsync(Factory, await TestData.AddUserAsync(Services, tenant.OrganizationId, [(storeB, StoreRole.Staff)]), storeB);
        var id = session.Id;

        // Başka şubenin başlığı ile: hiçbir uç nokta oturumun varlığını bile belli etmez
        await ApiClient.AssertProblemAsync(await staffB.GetAsync($"/api/v1/sessions/{id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await staffB.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await staffB.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/close", new { }), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await staffB.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{id}/photos/{photo.Id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await staffB.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{id}/consent"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await staffB.UploadAsync($"/api/v1/sessions/{id}/photos", TestImages.Portrait()), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(
            await staffB.SendAsync(HttpMethod.Put, $"/api/v1/sessions/{id}/draft", new { mode = "OutfitSet", items = Array.Empty<object>() }), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(
            await staffB.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{id}/generations", new { }, configure: r => r.Headers.Add("Idempotency-Key", "x")), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await staffB.GetAsync($"/api/v1/sessions/{id}/images"), HttpStatusCode.NotFound, "NOT_FOUND");

        // Sahibi etkilenmedi
        Assert.Equal(SessionStatus.Open, (await ownerA.GetAsync<SessionDetail>($"/api/v1/sessions/{id}")).Status);
    }

    [Fact]
    public async Task Baska_firmanin_personeli_oturumu_goremez()
    {
        var (_, owner) = await StaffAsync();
        var session = await CreateSessionAsync(owner);
        var (_, intruder) = await StaffAsync();

        await ApiClient.AssertProblemAsync(await intruder.GetAsync($"/api/v1/sessions/{session.Id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Empty((await intruder.GetAsync<Page<SessionSummary>>("/api/v1/sessions")).Items);
    }

    [Fact]
    public async Task Staff_katalog_yazamaz_manager_yazar()
    {
        var (_, manager, staff) = await ManagerAndStaffAsync();
        var category = new { name = "Şalvar", slug = "salvar", sortOrder = 1, bodyZone = "Lower", layerOrder = 10, maxSelection = 1, isActive = true };

        await ApiClient.AssertProblemAsync(await staff.SendAsync(HttpMethod.Post, "/api/v1/categories", category), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        var created = await manager.PostAsync<CategoryDto>("/api/v1/categories", category, HttpStatusCode.Created);

        Assert.Equal("Şalvar", created.Name);
        Assert.Contains((await staff.GetAsync<List<CategoryDto>>("/api/v1/categories")), c => c.Id == created.Id);
    }

    [Fact]
    public async Task Katalog_yonetimi_icin_herhangi_bir_subede_manager_rolu_yeter()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services);
        var storeB = await TestData.AddStoreAsync(Services, tenant.OrganizationId, "SB");
        var email = await TestData.AddUserAsync(Services, tenant.OrganizationId, [(tenant.StoreId, StoreRole.Staff), (storeB, StoreRole.Manager)]);
        var client = await ApiClient.LoginAsync(Factory, email, tenant.StoreId);

        var created = await client.PostAsync<CategoryDto>(
            "/api/v1/categories", new { name = "Kemer", slug = "kemer", sortOrder = 1, bodyZone = "Waist", layerOrder = 40, maxSelection = 1, isActive = true }, HttpStatusCode.Created);

        Assert.True(created.IsActive);
    }

    [Fact]
    public async Task Kimliksiz_istekler_401_alir()
    {
        ApiFactory.RequireDatabase(Factory);
        var anonymous = new ApiClient(Factory.CreateClient()) { StoreId = Guid.NewGuid() };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/categories")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/consent-notice/current")).StatusCode);
    }

    [Fact]
    public async Task Csrf_basligi_olmayan_degistirici_istek_reddedilir()
    {
        var (_, client) = await StaffAsync();

        var response = await client.SendAsync(HttpMethod.Post, "/api/v1/sessions", new { customerLabel = "x" }, configure: r => r.Headers.Remove("X-CSRF-TOKEN"));

        await ApiClient.AssertProblemAsync(response, HttpStatusCode.BadRequest, "CSRF_INVALID");
    }

    [Fact]
    public async Task Sube_filtresi_veritabani_seviyesinde_de_uygulanir()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services);
        var storeB = await TestData.AddStoreAsync(Services, tenant.OrganizationId, "SB");
        var client = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        var session = await CreateSessionAsync(client);

        using var scopeA = Services.CreateScope();
        scopeA.ServiceProvider.GetRequiredService<TenantContext>().OrganizationId = tenant.OrganizationId;
        scopeA.ServiceProvider.GetRequiredService<TenantContext>().StoreId = storeB;
        var db = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Empty(await db.TryOnSessions.ToListAsync(TestContext.Current.CancellationToken));
        scopeA.ServiceProvider.GetRequiredService<TenantContext>().StoreId = tenant.StoreId;
        Assert.Single(await db.TryOnSessions.ToListAsync(TestContext.Current.CancellationToken), s => s.Id == session.Id);
    }
}
