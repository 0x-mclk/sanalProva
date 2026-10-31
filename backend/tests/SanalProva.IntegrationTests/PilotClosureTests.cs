using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SanalProva.Api.Features.Account;
using SanalProva.Api.Features.TryOn;
using SanalProva.Api.Hubs;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Credits;
using SanalProva.Domain.Generation;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;
using Microsoft.Extensions.Configuration;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.IntegrationTests;

/// <summary>
/// Pilot öncesi kapanış: sunucu tarafı ekran kilidi, fotoğraf onayının sunucuda zorunluluğu, SignalR yetki davranışı, kurulum bağlantısı idempotency'si,
/// operatör denetim izi ve işçi/mod yalıtımı (CreditSystemTests aralıklı başarısızlığının kök nedeni). Hepsi NEGATİF/atlatma senaryolarıdır.
/// </summary>
public sealed class PilotClosureTests(ApiFactory factory) : ApiTestBase(factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(TestTenant Tenant, ApiClient Client)> StaffAsync(StoreRole role = StoreRole.Staff, int credits = 1000)
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, role, credits: credits);
        return (tenant, await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId));
    }

    private static Task<HttpResponseMessage> LockAsync(ApiClient c) => c.SendAsync(HttpMethod.Post, "/api/v1/account/lock");

    private static async Task<string> SetPinAsync(ApiClient client, string pin = "4829")
    {
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Put, "/api/v1/account/pin", new { currentPassword = TestData.Password, pin })).StatusCode);
        return pin;
    }

    // ------------------------------------------------------------------ 1) sunucu tarafı ekran kilidi

    [Fact]
    public async Task Kilitli_oturum_hassas_uclara_423_alir_muaf_uclar_calisir()
    {
        var (_, owner) = await StaffAsync(StoreRole.Owner);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await LockAsync(owner)).StatusCode);

        foreach (var url in new[] { "/api/v1/sessions", "/api/v1/products", "/api/v1/categories", "/api/v1/displays", "/api/v1/credits", "/api/v1/credits/balance", "/api/v1/admin/usage", "/api/v1/management/users", "/api/v1/management/stores", "/api/v1/consent-notice/current" })
        {
            await ApiClient.AssertProblemAsync(await owner.GetAsync(url), HttpStatusCode.Locked, "SCREEN_LOCKED");
        }

        // Yazma uçları da (CSRF'li, geçerli çerezle): kilit bağlama/filtrelerden önce
        await ApiClient.AssertProblemAsync(await owner.SendAsync(HttpMethod.Post, "/api/v1/sessions", new { customerLabel = "x" }), HttpStatusCode.Locked, "SCREEN_LOCKED");
        await ApiClient.AssertProblemAsync(await owner.SendAsync(HttpMethod.Put, "/api/v1/account/pin", new { currentPassword = TestData.Password, pin = "7391" }), HttpStatusCode.Locked, "SCREEN_LOCKED");
        await ApiClient.AssertProblemAsync(await owner.SendAsync(HttpMethod.Post, "/api/v1/account/activity"), HttpStatusCode.Locked, "SCREEN_LOCKED");

        // Muaf uçlar: kimlik, kilit durumu, kilitleme (tekrar güvenli)
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/auth/me")).StatusCode);
        var status = await owner.GetAsync<LockStatusDto>("/api/v1/account/lock");
        Assert.True(status.Locked);
        Assert.Equal(HttpStatusCode.NoContent, (await LockAsync(owner)).StatusCode);
    }

    [Fact]
    public async Task Kilit_ayni_cerezle_ikinci_sekmede_ve_yenilemede_atlatilamaz_baska_cihaz_etkilenmez()
    {
        var (tenant, tab1) = await StaffAsync();
        var tab2 = tab1; // aynı çerez (ikinci sekme/doğrudan API çağrısı) = aynı oturum satırı
        var otherDevice = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        await LockAsync(tab1);

        await ApiClient.AssertProblemAsync(await tab2.GetAsync("/api/v1/sessions"), HttpStatusCode.Locked, "SCREEN_LOCKED");
        Assert.True((await tab2.GetAsync<LockStatusDto>("/api/v1/account/lock")).Locked); // yenileme sonrası arayüz bu değeri okur
        Assert.Equal(HttpStatusCode.OK, (await otherDevice.GetAsync("/api/v1/sessions")).StatusCode); // başka cihazın oturumu ayrı satır
        Assert.False((await otherDevice.GetAsync<LockStatusDto>("/api/v1/account/lock")).Locked);
    }

    [Fact]
    public async Task Kilit_yalnizca_dogru_pinle_acilir_yanlis_pin_kilitli_birakir()
    {
        var (_, client) = await StaffAsync();
        await SetPinAsync(client);
        await LockAsync(client);

        await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { pin = "0000" }), HttpStatusCode.Unauthorized, "INVALID_PIN");
        await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/sessions"), HttpStatusCode.Locked, "SCREEN_LOCKED");

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { pin = "4829" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/sessions")).StatusCode);
        Assert.False((await client.GetAsync<LockStatusDto>("/api/v1/account/lock")).Locked);
    }

    [Fact]
    public async Task Etkilesim_sinyali_sunucuda_bosta_suresini_sifirlar_ve_istemci_susarsa_sunucu_kendisi_kilitler()
    {
        var (tenant, client) = await StaffAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Post, "/api/v1/account/activity")).StatusCode);

        // İstemci durdu (JS çalışmıyor/ağ koptu): son etkileşim süre+toleranstan eski → sunucu kilitli sayar ve kilidi KALICI yazar
        var idle = PinEndpoints.IdleSeconds(Services.GetRequiredService<IConfiguration>());
        var (scope, db) = OpenDb();
        using (scope)
        {
            var userId = await db.Users.Where(u => u.Email == tenant.Email).Select(u => u.Id).SingleAsync(Ct);
            var stale = DateTimeOffset.UtcNow.AddSeconds(-(idle + ScreenLockState.IdleGraceSeconds + 5));
            await db.AuthSessions.Where(s => s.UserId == userId).ExecuteUpdateAsync(u => u.SetProperty(s => s.LastInteractionAt, stale), Ct);
        }

        await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/sessions"), HttpStatusCode.Locked, "SCREEN_LOCKED");
        (scope, db) = OpenDb();
        using (scope)
        {
            Assert.True(await db.AuthSessions.AnyAsync(s => s.LockedAt != null && db.Users.Any(u => u.Id == s.UserId && u.Email == tenant.Email), Ct));
        }
    }

    [Fact]
    public async Task Cikis_yapilan_cerez_bir_daha_kullanilamaz_oturum_satiri_silinir()
    {
        var (tenant, client) = await StaffAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Post, "/api/v1/auth/logout")).StatusCode);
        // Çıkıştan sonra aynı çerezle (çalınmış çerez senaryosu) hiçbir uç çalışmaz
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/sessions")).StatusCode);
        var (scope, db) = OpenDb();
        using (scope)
        {
            Assert.False(await db.AuthSessions.AnyAsync(s => db.Users.Any(u => u.Id == s.UserId && u.Email == tenant.Email) && s.Id.ToString() == client.AuthCookie, Ct));
        }
    }

    [Fact]
    public async Task Kilitliyken_cikis_yapilabilir_ve_kilit_baska_kullanici_oturumunu_etkilemez()
    {
        var (_, a) = await StaffAsync();
        var (_, b) = await StaffAsync();
        await LockAsync(a);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/api/v1/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.SendAsync(HttpMethod.Post, "/api/v1/auth/logout")).StatusCode);
    }

    [Fact]
    public async Task Kilit_ve_giris_oncesi_uclar_anonim_calismaya_devam_eder()
    {
        var anonymous = new ApiClient(Factory.CreateClient());
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/v1/auth/csrf")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/account/lock")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(HttpMethod.Post, "/api/v1/account/lock")).StatusCode);
    }

    /// <summary>Kimlik isteyen HER uç nokta kilitliyken reddedilir; muaf liste sabittir (yeni uç nokta unutulsa bile açık kalamaz).</summary>
    [Fact]
    public void Her_kimlikli_uc_nokta_kilide_tabidir_ya_da_gerekceli_sabit_listededir()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "POST /api/v1/auth/logout", "GET /api/v1/auth/me", "GET /api/v1/account/lock", "POST /api/v1/account/lock", "POST /api/v1/account/unlock",
        };
        var actual = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var endpoint in Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Where(e => e.RoutePattern.RawText is not null))
        {
            var authorized = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0;
            var exempt = endpoint.Metadata.GetMetadata<ScreenLockExemptMetadata>();
            if (!authorized)
            {
                if (exempt is not null) problems.Add($"{endpoint.RoutePattern.RawText}: anonim uçta gereksiz muaf işareti.");
                continue;
            }

            if (exempt is null) continue;
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            actual.Add($"{(methods is { Count: > 0 } ? string.Join(",", methods.Order(StringComparer.Ordinal)) : "ANY")} {endpoint.RoutePattern.RawText}");
            if (string.IsNullOrWhiteSpace(exempt.Reason)) problems.Add($"{endpoint.RoutePattern.RawText}: muaf gerekçesi yok.");
        }

        problems.AddRange(actual.Except(expected).Select(k => $"YENİ kilit muafı (listede yok): {k}"));
        problems.AddRange(expected.Except(actual).Select(k => $"Listede ama muaf değil/yok: {k}"));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task Platform_yoneticisi_de_kilitliyken_finans_uclarina_ulasamaz()
    {
        var (tenant, client) = await StaffAsync(StoreRole.Owner);
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.Users.Where(u => u.Email == tenant.Email).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsPlatformAdmin, true), Ct);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/platform/finance")).StatusCode);
        await LockAsync(client);
        await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/platform/finance"), HttpStatusCode.Locked, "SCREEN_LOCKED");
        await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/platform/cost-cases"), HttpStatusCode.Locked, "SCREEN_LOCKED");
    }

    // ------------------------------------------------------------------ 1b) kilit + SignalR

    private static async Task<HubConnection> ConnectedHubAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, ApiClient client, params Guid[] stores)
    {
        var connection = StaffHubRevocationTests.ConnectHub(host, client.AuthCookie);
        await connection.StartAsync(Ct);
        foreach (var store in stores) Assert.True(await connection.InvokeAsync<bool>("JoinStore", store, Ct));
        return connection;
    }

    [Fact]
    public async Task Kilitlenince_o_oturumun_hub_baglantisi_kesilir_kilitliyken_yeniden_baglanilamaz_kilit_acilinca_baglanilir()
    {
        var (tenant, client) = await StaffAsync();
        using var host = DerivedHost();
        var otherDevice = await ApiClient.LoginAsync(host, tenant.Email, tenant.StoreId);
        await using var mine = await ConnectedHubAsync(host, client, tenant.StoreId);
        await using var other = await ConnectedHubAsync(host, otherDevice, tenant.StoreId);

        // Kilit sunucuya ulaşır (aynı veritabanı; host ayrı olsa da oturum satırı ortak) → bu sunucudaki bağlantı süpürücüyle/doğrudan kesilir
        var lockClient = await ApiClient.LoginAsync(host, tenant.Email, tenant.StoreId); // aynı kullanıcı: ayrı satır; kilitlenecek olan 'client' ana host çerezi
        Assert.Equal(HttpStatusCode.NoContent, (await LockAsync(client)).StatusCode);
        var sweeper = host.Services.GetRequiredService<StaffConnectionSweeper>();
        await sweeper.SweepAsync(Ct);
        await StaffHubRevocationTests.WaitDisconnectedAsync(mine);
        Assert.NotEqual(HubConnectionState.Connected, mine.State);
        Assert.Equal(HubConnectionState.Connected, other.State); // başka cihaz etkilenmez
        _ = lockClient;

        // Kilitliyken yeni bağlantı: negotiate/WebSocket 423 ile reddedilir
        await using var blocked = StaffHubRevocationTests.ConnectHub(host, client.AuthCookie);
        await Assert.ThrowsAnyAsync<Exception>(() => blocked.StartAsync(Ct));

        // PIN'i olmayan hesap kilidi parolayla açar
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { password = TestData.Password })).StatusCode);
        await using var again = await ConnectedHubAsync(host, client, tenant.StoreId);
        Assert.Equal(HubConnectionState.Connected, again.State);
    }

    [Fact]
    public async Task Rol_veya_sube_atamasi_degisince_acik_hub_baglantisi_kesilir_yeniden_baglanan_yetkisiz_gruba_giremez()
    {
        ApiFactory.RequireDatabase(Factory);
        var owner = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var second = await TestData.AddStoreAsync(Services, owner.OrganizationId, "RL2");
        var ownerClient = await ApiClient.LoginAsync(Factory, owner.Email, owner.StoreId);
        var staffEmail = await TestData.AddUserAsync(Services, owner.OrganizationId, [(owner.StoreId, StoreRole.Staff), (second, StoreRole.Staff)]);
        using var host = DerivedHost();
        var staff = await ApiClient.LoginAsync(host, staffEmail, owner.StoreId);
        await using var connection = await ConnectedHubAsync(host, staff, owner.StoreId, second);
        Guid staffId;
        var (scope, db) = OpenDb();
        using (scope)
        {
            staffId = await db.Users.Where(u => u.Email == staffEmail).Select(u => u.Id).SingleAsync(Ct);
        }

        // İkinci şubedeki erişim kaldırılır (yalnızca ilk şube kalır)
        var response = await ownerClient.SendAsync(HttpMethod.Put, $"/api/v1/management/users/{staffId}/assignments", new { assignments = new[] { new { storeId = owner.StoreId, role = StoreRole.Staff } } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Çağrı ana host'ta yapıldı (kendi kaydı): derived host'taki bağlantı süpürücüyle (ortak veritabanı) kesilir — çok süreçli senaryonun karşılığı
        await host.Services.GetRequiredService<StaffConnectionSweeper>().SweepAsync(Ct);
        await StaffHubRevocationTests.WaitDisconnectedAsync(connection);
        Assert.NotEqual(HubConnectionState.Connected, connection.State);

        await using var again = StaffHubRevocationTests.ConnectHub(host, staff.AuthCookie);
        await again.StartAsync(Ct);
        Assert.True(await again.InvokeAsync<bool>("JoinStore", owner.StoreId, Ct));
        Assert.False(await again.InvokeAsync<bool>("JoinStore", second, Ct));
    }

    [Fact]
    public async Task Supurucu_pasife_alinan_guvenlik_damgasi_degisen_ve_gecerli_olmayan_baglantiyi_keser_gecerliye_dokunmaz()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var staffA = await TestData.AddUserAsync(Services, tenant.OrganizationId, [(tenant.StoreId, StoreRole.Staff)]);
        var staffB = await TestData.AddUserAsync(Services, tenant.OrganizationId, [(tenant.StoreId, StoreRole.Staff)]);
        var staffC = await TestData.AddUserAsync(Services, tenant.OrganizationId, [(tenant.StoreId, StoreRole.Staff)]);
        using var host = DerivedHost();
        var a = await ApiClient.LoginAsync(host, staffA, tenant.StoreId);
        var b = await ApiClient.LoginAsync(host, staffB, tenant.StoreId);
        var c = await ApiClient.LoginAsync(host, staffC, tenant.StoreId);
        await using var ca = await ConnectedHubAsync(host, a, tenant.StoreId);
        await using var cb = await ConnectedHubAsync(host, b, tenant.StoreId);
        await using var cc = await ConnectedHubAsync(host, c, tenant.StoreId);

        // Operatör komutu/başka süreç simülasyonu: veritabanı doğrudan değişir (hub kaydı bunu bilmez)
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.Users.Where(u => u.Email == staffA).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false), Ct);
            await db.Users.Where(u => u.Email == staffB).ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString()), Ct);
        }

        var aborted = await host.Services.GetRequiredService<StaffConnectionSweeper>().SweepAsync(Ct);
        Assert.True(aborted >= 2);
        await StaffHubRevocationTests.WaitDisconnectedAsync(ca);
        await StaffHubRevocationTests.WaitDisconnectedAsync(cb);
        Assert.NotEqual(HubConnectionState.Connected, ca.State);
        Assert.NotEqual(HubConnectionState.Connected, cb.State);
        Assert.Equal(HubConnectionState.Connected, cc.State);
    }

    [Fact]
    public async Task Cok_hatali_pin_oturumlari_kapatir_ve_acik_hub_baglantilarini_keser()
    {
        var (tenant, client) = await StaffAsync();
        await SetPinAsync(client);
        using var host = DerivedHost();
        var viaHost = await ApiClient.LoginAsync(host, tenant.Email, tenant.StoreId);
        await using var connection = await ConnectedHubAsync(host, viaHost, tenant.StoreId);
        for (var i = 0; i < PinPolicy.MaxAttempts; i++)
        {
            await client.SendAsync(HttpMethod.Post, "/api/v1/account/unlock", new { pin = "0000" });
        }

        // Damga yenilendi: ana host bağlantıyı doğrudan keser; derived host'taki bağlantıyı süpürücü yakalar
        await host.Services.GetRequiredService<StaffConnectionSweeper>().SweepAsync(Ct);
        await StaffHubRevocationTests.WaitDisconnectedAsync(connection);
        Assert.NotEqual(HubConnectionState.Connected, connection.State);
    }

    // ------------------------------------------------------------------ 2) fotoğraf uygunluk onayı (sunucu)

    private async Task<(TestTenant Tenant, ApiClient Client, SessionDetail Session, PhotoDto Photo)> UnapprovedAsync(Guid? existing = null)
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Staff);
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        var client = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        var session = await CreateSessionAsync(client);
        await ConsentAsync(client, session.Id);
        var photo = await UploadPhotoAsync(client, session.Id, approve: false);
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        return (tenant, client, session, photo);
    }

    private static Task<HttpResponseMessage> StartAsync(ApiClient client, Guid sessionId, object? body = null) =>
        client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/generations", body ?? new { }, configure: r => r.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N")));

    private async Task<int> JobCountAsync(Guid sessionId)
    {
        var (scope, db) = OpenDb();
        using (scope)
        {
            return await db.GenerationJobs.IgnoreQueryFilters().CountAsync(j => j.SessionId == sessionId, Ct);
        }
    }

    [Fact]
    public async Task Onaysiz_fotografla_uretim_reddedilir_is_olusmaz_ve_onaydan_sonra_kabul_edilir()
    {
        var (_, client, session, photo) = await UnapprovedAsync();
        Assert.Null(photo.StaffApprovedAt);

        // Arayüzü atlayan doğrudan istek: fotoğraf kimliği verilse de verilmese de
        await ApiClient.AssertProblemAsync(await StartAsync(client, session.Id), HttpStatusCode.Conflict, "PHOTO_NOT_APPROVED");
        await ApiClient.AssertProblemAsync(await StartAsync(client, session.Id, new { photoId = photo.Id }), HttpStatusCode.Conflict, "PHOTO_NOT_APPROVED");
        Assert.Equal(0, await JobCountAsync(session.Id));

        var approved = await ApprovePhotoAsync(client, session.Id, photo.Id);
        Assert.NotNull(approved.StaffApprovedAt);
        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(client, session.Id)).StatusCode);
        Assert.Equal(1, await JobCountAsync(session.Id));
    }

    [Fact]
    public async Task Onay_uc_beyanin_hepsini_ister_eksik_beyan_onay_vermez()
    {
        var (_, client, session, photo) = await UnapprovedAsync();
        foreach (var body in new object[]
                 {
                     new { singlePersonFullBody = true, frontalUprightPose = true, sharpAndWellLit = false },
                     new { singlePersonFullBody = false, frontalUprightPose = true, sharpAndWellLit = true },
                     new { singlePersonFullBody = true, frontalUprightPose = false, sharpAndWellLit = true },
                     new { },
                 })
        {
            var response = await client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{session.Id}/photos/{photo.Id}/approval", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        await ApiClient.AssertProblemAsync(await StartAsync(client, session.Id), HttpStatusCode.Conflict, "PHOTO_NOT_APPROVED");
    }

    [Fact]
    public async Task Yeniden_cekimde_onay_yeni_fotografa_gecmez_eski_fotograf_ve_onayi_gider()
    {
        var (_, client, session, first) = await UnapprovedAsync();
        await ApprovePhotoAsync(client, session.Id, first.Id);
        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(client, session.Id)).StatusCode);

        // Aynı açıda yeniden çekim: yeni fotoğraf satırı, onaysız
        var second = await UploadPhotoAsync(client, session.Id, TestImages.Portrait(seed: 4242), approve: false);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(second.StaffApprovedAt);
        await ApiClient.AssertProblemAsync(await StartAsync(client, session.Id, new { photoId = second.Id, allowVariation = true }), HttpStatusCode.Conflict, "PHOTO_NOT_APPROVED");
        // Eski (onaylı) fotoğraf silindi: kimliğiyle üretim denenemez
        await ApiClient.AssertProblemAsync(await StartAsync(client, session.Id, new { photoId = first.Id }), HttpStatusCode.Conflict, "NO_PHOTO");

        await ApprovePhotoAsync(client, session.Id, second.Id);
        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(client, session.Id, new { photoId = second.Id, allowVariation = true })).StatusCode);
    }

    [Fact]
    public async Task Onay_geri_alinabilir_geri_alinca_uretim_yeniden_engellenir()
    {
        var (_, client, session, photo) = await UnapprovedAsync();
        await ApprovePhotoAsync(client, session.Id, photo.Id);
        var revoked = await ApiClient.ReadAsync<PhotoDto>(await client.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{session.Id}/photos/{photo.Id}/approval"));
        Assert.Null(revoked.StaffApprovedAt);
        await ApiClient.AssertProblemAsync(await StartAsync(client, session.Id), HttpStatusCode.Conflict, "PHOTO_NOT_APPROVED");
    }

    [Fact]
    public async Task Onay_baska_oturum_sube_ve_firma_fotografina_verilemez_kapali_oturumda_verilemez()
    {
        var (_, owner, session, photo) = await UnapprovedAsync();
        var (otherTenant, other) = await StaffAsync();
        _ = otherTenant;
        // Başka firmanın personeli: foto/oturum bulunamaz (varlık sızdırılmaz)
        await ApiClient.AssertProblemAsync(await other.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{session.Id}/photos/{photo.Id}/approval", new { singlePersonFullBody = true, frontalUprightPose = true, sharpAndWellLit = true }), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await other.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{session.Id}/photos/{photo.Id}/approval"), HttpStatusCode.NotFound, "NOT_FOUND");
        // Başka oturumun fotoğraf kimliği kendi oturumunda geçersizdir
        var mine = await CreateSessionAsync(owner);
        await ConsentAsync(owner, mine.Id);
        await ApiClient.AssertProblemAsync(await owner.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{mine.Id}/photos/{photo.Id}/approval", new { singlePersonFullBody = true, frontalUprightPose = true, sharpAndWellLit = true }), HttpStatusCode.NotFound, "NOT_FOUND");

        Assert.Equal(HttpStatusCode.OK, (await owner.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{session.Id}/close")).StatusCode);
        await ApiClient.AssertProblemAsync(await owner.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{session.Id}/photos/{photo.Id}/approval", new { singlePersonFullBody = true, frontalUprightPose = true, sharpAndWellLit = true }), HttpStatusCode.Conflict, "SESSION_CLOSED");
    }

    [Fact]
    public async Task Onay_denetim_kaydi_birakir_kisisel_veri_icermez_ve_kvkk_onayi_yokken_fotograf_yuklenemez()
    {
        var (tenant, client, session, photo) = await UnapprovedAsync();
        await ApprovePhotoAsync(client, session.Id, photo.Id);
        var (scope, db) = OpenDb();
        using (scope)
        {
            var entry = await db.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.EntityId == photo.Id && a.Action == "Photo.StaffApproved", Ct);
            Assert.Equal(tenant.OrganizationId, entry.OrganizationId);
        }

        // Mevcut KVKK kapısı bozulmadı: onaysız oturuma fotoğraf yüklenemez
        var fresh = await CreateSessionAsync(client);
        await ApiClient.AssertProblemAsync(await client.UploadAsync($"/api/v1/sessions/{fresh.Id}/photos", TestImages.Portrait()), HttpStatusCode.Conflict, "CONSENT_REQUIRED");
    }

    [Fact]
    public async Task Gercek_modda_onaysiz_foto_kredi_rezervasyonundan_ve_isten_ONCE_reddedilir()
    {
        ApiFactory.RequireDatabase(Factory);
        using var real = new RealModeApiFactory(Factory.StorageRoot);
        var tenant = await TestData.CreateTenantAsync(real.Services, credits: 5);
        var catalog = await TestData.CreateCatalogAsync(real.Services, tenant.OrganizationId);
        var client = await ApiClient.LoginAsync(real, tenant.Email, tenant.StoreId);
        var session = await CreateSessionAsync(client);
        await ConsentAsync(client, session.Id);
        var photo = await UploadPhotoAsync(client, session.Id, approve: false);
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);

        await ApiClient.AssertProblemAsync(await StartAsync(client, session.Id), HttpStatusCode.Conflict, "PHOTO_NOT_APPROVED");
        Assert.Equal((5, 0), await CreditAssert.AccountAsync(real.Services, tenant.OrganizationId)); // rezervasyon yok
        Assert.DoesNotContain(await CreditAssert.LedgerAsync(real.Services, tenant.OrganizationId), e => e.EntryType == CreditEntryType.Reserve);

        await ApprovePhotoAsync(client, session.Id, photo.Id);
        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(client, session.Id)).StatusCode);
        Assert.Equal((5, 1), await CreditAssert.AccountAsync(real.Services, tenant.OrganizationId));
    }

    // ------------------------------------------------------------------ 4) işçi / mod yalıtımı (CreditSystemTests aralıklı hatası)

    /// <summary>
    /// KÖK NEDEN: ana test sunucusunun (mock) işçisi, aynı veritabanını paylaşan RealModeApiFactory'nin kabul ettiği GERÇEK mod işini kuyruktan kapıp mock sağlayıcıyla
    /// tamamlayabiliyordu; kredi rezervasyonu o an tüketildiği için "(bakiye, rezerve) = (1, 1)" doğrulaması yük altında aralıklı düşüyordu. Düzeltme üretim koduna da faydalı:
    /// iş kabul modunu taşır (<c>generation_jobs.admitted_live</c>) ve işçi yalnızca kendi modundaki işi alır.
    /// </summary>
    [Fact]
    public async Task Mock_isci_gercek_modda_kabul_edilmis_isi_almaz_rezervasyon_bozulmaz()
    {
        ApiFactory.RequireDatabase(Factory);
        using var real = new RealModeApiFactory(Factory.StorageRoot);
        var tenant = await TestData.CreateTenantAsync(real.Services, credits: 3);
        var catalog = await TestData.CreateCatalogAsync(real.Services, tenant.OrganizationId);
        var client = await ApiClient.LoginAsync(real, tenant.Email, tenant.StoreId);
        var (session, _) = await PreparedSessionAsync(client);
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(client, session.Id)).StatusCode);

        // Ana host'un mock işçisi 30 ms'de yoklar: yarışı kaçırmasın diye uzun bekle
        await Task.Delay(2500, Ct);
        await using var scope = real.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(j => j.SessionId == session.Id, Ct);
        Assert.True(job.AdmittedLive);
        Assert.Equal(GenerationJobStatus.Queued, job.Status);
        Assert.Equal(0, job.Attempt);
        Assert.Equal((3, 1), await CreditAssert.AccountAsync(real.Services, tenant.OrganizationId));
    }

    [Fact]
    public async Task Mock_modda_kabul_edilen_is_mock_isci_tarafindan_tamamlanir_ve_modu_kayitlidir()
    {
        var (tenant, client) = await StaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        var (session, _) = await PreparedSessionAsync(client);
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        var job = await StartGenerationAsync(client, session.Id);
        var done = await WaitForJobAsync(client, job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded, done.Status);
        var (scope, db) = OpenDb();
        using (scope)
        {
            Assert.False((await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct)).AdmittedLive);
        }
    }

    // ------------------------------------------------------------------ 5) kurulum bağlantısı idempotency + operatör denetim izi

    private sealed record MemberDto(Guid Id);
    private sealed record InviteDto(MemberDto Member, string SetupLink);
    private sealed record LinkDto(string SetupLink);

    private async Task<int> OpenTokensAsync(Guid userId)
    {
        var (scope, db) = OpenDb();
        using (scope)
        {
            return await db.AccountSetupTokens.IgnoreQueryFilters().CountAsync(t => t.UserId == userId && t.UsedAt == null && t.RevokedAt == null, Ct);
        }
    }

    [Fact]
    public async Task Ayni_anahtarla_baglanti_yeniden_uretilemez_ilk_baglanti_iptal_edilmez_farkli_anahtar_ve_anahtarsiz_calisir()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var owner = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        var invited = await owner.PostAsync<InviteDto>("/api/v1/management/users", new { email = $"idem-{Guid.NewGuid().ToString("N")[..10]}@test.local", displayName = "Idem Kisi", assignments = new[] { new { storeId = tenant.StoreId, role = StoreRole.Staff } } }, HttpStatusCode.Created);
        var id = invited.Member.Id;
        var url = $"/api/v1/management/users/{id}/setup-link";
        Task<HttpResponseMessage> Issue(string? key) => owner.SendAsync(HttpMethod.Post, url, new { }, configure: r => { if (key is not null) r.Headers.Add("Idempotency-Key", key); });

        Assert.Equal(HttpStatusCode.OK, (await Issue("anahtar-bir-0001")).StatusCode);
        Assert.Equal(1, await OpenTokensAsync(id)); // davet bağlantısı iptal edilip yenisi yazıldı
        // Aynı anahtar: ikinci bağlantı ÜRETİLMEZ, ilk bağlantı iptal EDİLMEZ
        await ApiClient.AssertProblemAsync(await Issue("anahtar-bir-0001"), HttpStatusCode.Conflict, "SETUP_LINK_ALREADY_ISSUED");
        Assert.Equal(1, await OpenTokensAsync(id));
        // Farklı anahtar ve anahtarsız çağrı (eski istemci) çalışır
        Assert.Equal(HttpStatusCode.OK, (await Issue("anahtar-iki-0002")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Issue(null)).StatusCode);
        Assert.Equal(1, await OpenTokensAsync(id));
        // Geçersiz uzunluk reddedilir
        await ApiClient.AssertProblemAsync(await Issue("kisa"), HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_INVALID");

        // Anahtar düz saklanmaz
        var (scope, db) = OpenDb();
        using (scope)
        {
            var hashes = await db.AccountSetupTokens.IgnoreQueryFilters().Where(t => t.UserId == id && t.RequestKeyHash != null).Select(t => t.RequestKeyHash!).ToListAsync(Ct);
            Assert.NotEmpty(hashes);
            Assert.All(hashes, h => Assert.Equal(32, h.Length));
        }
    }

    [Fact]
    public async Task Platform_sahip_baglantisi_ayni_anahtarla_ikinci_kez_uretilmez()
    {
        ApiFactory.RequireDatabase(Factory);
        var adminTenant = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.Users.Where(u => u.Email == adminTenant.Email).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsPlatformAdmin, true), Ct);
        }

        var admin = await ApiClient.LoginAsync(Factory, adminTenant.Email, adminTenant.StoreId);
        var created = await admin.SendAsync(HttpMethod.Post, "/api/v1/platform/organizations", PlatformOrganizationTests.Body(), configure: r => r.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N")));
        var organizationId = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("organizationId").GetGuid();
        var url = $"/api/v1/platform/organizations/{organizationId}/owner/setup-link";
        Task<HttpResponseMessage> Reissue(string key) => admin.SendAsync(HttpMethod.Post, url, new { }, configure: r => r.Headers.Add("Idempotency-Key", key));

        Assert.Equal(HttpStatusCode.OK, (await Reissue("platform-anahtar-1")).StatusCode);
        await ApiClient.AssertProblemAsync(await Reissue("platform-anahtar-1"), HttpStatusCode.Conflict, "SETUP_LINK_ALREADY_ISSUED");
        Assert.Equal(HttpStatusCode.OK, (await Reissue("platform-anahtar-2")).StatusCode);
    }

    [Fact]
    public async Task Operator_komutu_denetim_kaydina_kim_hangi_hedef_miktar_referansi_yazar_sir_ve_not_yazmaz()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Owner, credits: 0);
        string slug;
        var (scope, db) = OpenDb();
        using (scope)
        {
            slug = await db.Organizations.Where(o => o.Id == tenant.OrganizationId).Select(o => o.Slug).SingleAsync(Ct);
        }

        var reference = $"audit-ref-{Guid.NewGuid():N}"[..24];
        var output = new StringWriter();
        var exit = await SanalProva.Api.Features.Platform.PlatformCommands.RunAsync(
            Services, ["platform", "credits", "--org", slug, "--grant", "7", "--ref", reference, "--note", "GIZLI-SERBEST-NOT", "--confirm"], output, Ct);
        Assert.Equal(SanalProva.Api.Features.Platform.PlatformCommands.ExitOk, exit);

        (scope, db) = OpenDb();
        using (scope)
        {
            var entry = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.OrganizationId == tenant.OrganizationId && a.Action == "Platform.CreditGranted", Ct);
            Assert.NotNull(entry.Detail);
            Assert.Contains("cli op=", entry.Detail, StringComparison.Ordinal);
            Assert.Contains(Environment.MachineName, entry.Detail, StringComparison.Ordinal);
            Assert.Contains($"org={slug}", entry.Detail, StringComparison.Ordinal);
            Assert.Contains($"ref={reference}", entry.Detail, StringComparison.Ordinal);
            Assert.Contains("grant=7", entry.Detail, StringComparison.Ordinal);
            // Serbest metin notu, parola, e-posta, token, bağlantı denetim detayına girmez
            Assert.DoesNotContain("GIZLI-SERBEST-NOT", entry.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("@", entry.Detail.Replace($"{Environment.UserName}@{Environment.MachineName}", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.DoesNotContain("#t=", entry.Detail, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("GIZLI-SERBEST-NOT", output.ToString(), StringComparison.Ordinal);
    }
}
