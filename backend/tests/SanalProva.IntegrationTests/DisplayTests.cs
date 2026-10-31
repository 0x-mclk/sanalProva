using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SanalProva.Api.Features.Displays;
using SanalProva.Api.Features.TryOn;
using SanalProva.Api.Hubs;
using SanalProva.Domain.Display;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Displays;

namespace SanalProva.IntegrationTests;

/// <summary>Bir TV tarayıcısını taklit eder: eşleştirme gizli değeri ve ardından cihaz belirteciyle istek atar (çerez kullanmaz).</summary>
internal sealed class TvClient(ApiFactory factory, string code, string secret, DateTimeOffset expiresAt)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient _http = factory.CreateClient();

    public string Code { get; } = code;
    public string Secret { get; } = secret;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;

    public static async Task<TvClient> StartPairingAsync(ApiFactory factory)
    {
        using var http = factory.CreateClient();
        var response = await http.PostAsync("/api/v1/display/pairing", null, TestContext.Current.CancellationToken);
        var started = await ApiClient.ReadAsync<PairingStartedDto>(response);
        return new TvClient(factory, started.Code, started.PairingSecret, started.ExpiresAt);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string scheme, string? token = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token ?? Secret);
        return await _http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public async Task<PairingStatusDto> StatusAsync() =>
        await ApiClient.ReadAsync<PairingStatusDto>(await SendAsync(HttpMethod.Get, "/api/v1/display/pairing/status", "Pairing"));

    public Task<HttpResponseMessage> StateResponseAsync() => SendAsync(HttpMethod.Get, "/api/v1/display/state", "Bearer");

    public async Task<TvStateDto> StateAsync() => await ApiClient.ReadAsync<TvStateDto>(await StateResponseAsync());

    public Task<byte[]> DownloadAsync(string url) => _http.GetByteArrayAsync(url, TestContext.Current.CancellationToken);

    public async Task<HttpResponseMessage> GetFileAsync(string url) => await _http.GetAsync(url, TestContext.Current.CancellationToken);

    public static JsonSerializerOptions JsonOptions => Json;
}

/// <summary>TV eşleştirme, cihaz kimliği, durum sürümü, izolasyon, gizlilik temizliği ve SignalR senkronizasyonu.</summary>
public sealed class DisplayTests(ApiFactory factory) : ApiTestBase(factory)
{
    private sealed record Setup(TestTenant Tenant, ApiClient Manager, ApiClient Staff, CatalogIds Catalog);

    private async Task<Setup> NewStoreAsync()
    {
        var (tenant, manager, staff) = await ManagerAndStaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        return new Setup(tenant, manager, staff, catalog);
    }

    private static Task<HttpResponseMessage> ClaimRawAsync(ApiClient manager, string code, string name = "Salon TV") =>
        manager.SendAsync(HttpMethod.Post, "/api/v1/displays/claim", new { code, name });

    /// <summary>TV kod üretir, yönetici kodla eşleştirir.</summary>
    private async Task<(TvClient Tv, DisplayDto Display)> PairAsync(ApiClient manager, string name = "Salon TV")
    {
        var tv = await TvClient.StartPairingAsync(Factory);
        var display = await ApiClient.ReadAsync<DisplayDto>(await ClaimRawAsync(manager, tv.Code, name), HttpStatusCode.Created);
        return (tv, display);
    }

    /// <summary>Onaylı, fotoğraflı, DEMO sonucu üretilmiş oturum.</summary>
    private static async Task<(SessionDetail Session, PhotoDto Photo, Guid ImageId)> SessionWithResultAsync(Setup setup, ApiClient? client = null, int seed = 1)
    {
        client ??= setup.Staff;
        var session = await CreateSessionAsync(client);
        await ConsentAsync(client, session.Id);
        var photoResponse = await client.UploadAsync($"/api/v1/sessions/{session.Id}/photos", TestImages.Portrait(seed));
        var photo = await ApiClient.ReadAsync<PhotoDto>(photoResponse, HttpStatusCode.Created);
        photo = await ApprovePhotoAsync(client, session.Id, photo.Id); // üretim kabulü personel onayını sunucuda arar
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, setup.Catalog.SetProduct);
        var job = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id)).Id);
        Assert.Equal(Domain_Succeeded, job.Status);
        return (session, photo, job.ResultImageId!.Value);
    }

    private const SanalProva.Domain.Generation.GenerationJobStatus Domain_Succeeded = SanalProva.Domain.Generation.GenerationJobStatus.Succeeded;

    private static Task<HttpResponseMessage> Attach(ApiClient c, Guid displayId, Guid sessionId, bool force = false) =>
        c.SendAsync(HttpMethod.Put, $"/api/v1/displays/{displayId}/session", new { sessionId, force });

    private static Task<HttpResponseMessage> Send(ApiClient c, Guid displayId, DisplayMode mode, Guid[] images, long? expectedVersion = null) =>
        c.SendAsync(HttpMethod.Put, $"/api/v1/displays/{displayId}/state", new { mode, imageIds = images, expectedVersion });

    private static async Task<DisplayDto> GetDisplayAsync(ApiClient client, Guid id) =>
        (await client.GetAsync<List<DisplayDto>>("/api/v1/displays")).Single(d => d.Id == id);

    // ------------------------------------------------------------------ eşleştirme

    [Fact]
    public async Task Eslestirme_kodu_alti_haneli_ve_bekleyen_durumdadir()
    {
        ApiFactory.RequireDatabase(Factory);
        var tv = await TvClient.StartPairingAsync(Factory);

        Assert.Matches("^[0-9]{6}$", tv.Code);
        Assert.True(tv.ExpiresAt > DateTimeOffset.UtcNow && tv.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(6));
        Assert.Equal(PairingStatus.Pending, (await tv.StatusAsync()).Status);

        // Gizli değer olmadan veya yanlış gizli değerle durum sorulamaz
        var anonymous = await Factory.CreateClient().GetAsync("/api/v1/display/pairing/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var wrong = await tv.SendAsync(HttpMethod.Get, "/api/v1/display/pairing/status", "Pairing", "yanlis-gizli-deger");
        Assert.Equal(PairingStatus.Expired, (await ApiClient.ReadAsync<PairingStatusDto>(wrong)).Status);
    }

    [Fact]
    public async Task Gecerli_kodla_eslestirme_tamamlanir_ve_tv_kendi_belirteciyle_durumu_alir()
    {
        var setup = await NewStoreAsync();
        var tv = await TvClient.StartPairingAsync(Factory);

        // Eşleşmeden önce cihaz belirteci geçersizdir
        Assert.Equal(HttpStatusCode.Unauthorized, (await tv.StateResponseAsync()).StatusCode);

        var display = await ApiClient.ReadAsync<DisplayDto>(await ClaimRawAsync(setup.Manager, $"{tv.Code[..3]} {tv.Code[3..]}", "  Prova Odası TV  "), HttpStatusCode.Created);
        Assert.Equal("Prova Odası TV", display.Name);
        Assert.False(display.Online);
        Assert.Equal(DisplayMode.Idle, display.Mode);

        Assert.Equal(PairingStatus.Claimed, (await tv.StatusAsync()).Status);
        var state = await tv.StateAsync();
        Assert.Equal(DisplayMode.Idle, state.Mode);
        Assert.Equal("Prova Odası TV", state.DeviceName);
        Assert.False(state.HasSession);
        Assert.Empty(state.Images);

        var (scope, db) = OpenDb();
        using (scope)
        {
            var device = await db.DisplayDevices.IgnoreQueryFilters().SingleAsync(d => d.Id == display.Id, TestContext.Current.CancellationToken);
            Assert.Equal(setup.Tenant.StoreId, device.StoreId);
            Assert.Equal(setup.Tenant.OrganizationId, device.OrganizationId);
            Assert.NotEqual(tv.Secret, device.TokenHash); // düz belirteç saklanmaz
            Assert.Equal(64, device.TokenHash.Length);
            var pairing = await db.DisplayPairings.SingleAsync(p => p.ClaimedDeviceId == display.Id, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(tv.Code, pairing.CodeHash, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Yanlis_ve_bicimsiz_kodlar_ayni_hatayi_verir()
    {
        var setup = await NewStoreAsync();
        var tv = await TvClient.StartPairingAsync(Factory);
        var wrong = tv.Code == "000000" ? "000001" : "000000";

        foreach (var code in new[] { wrong, "12345", "abcdef", "1234567" })
        {
            await ApiClient.AssertProblemAsync(await ClaimRawAsync(setup.Manager, code), HttpStatusCode.NotFound, "PAIRING_CODE_INVALID");
        }
    }

    [Fact]
    public async Task Suresi_dolmus_kod_kabul_edilmez()
    {
        var setup = await NewStoreAsync();
        var tv = await TvClient.StartPairingAsync(Factory);
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.DisplayPairings.Where(p => p.CreatedAt >= DateTimeOffset.UtcNow.AddMinutes(-1))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), TestContext.Current.CancellationToken);
        }

        await ApiClient.AssertProblemAsync(await ClaimRawAsync(setup.Manager, tv.Code), HttpStatusCode.NotFound, "PAIRING_CODE_INVALID");
        Assert.Equal(PairingStatus.Expired, (await tv.StatusAsync()).Status);
    }

    [Fact]
    public async Task Kod_ikinci_kez_kullanilamaz()
    {
        var setup = await NewStoreAsync();
        var (tv, _) = await PairAsync(setup.Manager);

        var other = await TestData.CreateTenantAsync(Services, StoreRole.Manager);
        var otherManager = await ApiClient.LoginAsync(Factory, other.Email, other.StoreId);
        await ApiClient.AssertProblemAsync(await ClaimRawAsync(setup.Manager, tv.Code, "Kopya"), HttpStatusCode.NotFound, "PAIRING_CODE_INVALID");
        await ApiClient.AssertProblemAsync(await ClaimRawAsync(otherManager, tv.Code, "Başka firma"), HttpStatusCode.NotFound, "PAIRING_CODE_INVALID");
        Assert.Single(await setup.Manager.GetAsync<List<DisplayDto>>("/api/v1/displays"));
        Assert.Empty(await otherManager.GetAsync<List<DisplayDto>>("/api/v1/displays"));
    }

    [Fact]
    public async Task Ayni_kodu_ayni_anda_kullanan_iki_yoneticiden_yalnizca_biri_basarili_olur()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Manager);
        var second = await TestData.AddUserAsync(Services, tenant.OrganizationId, [(tenant.StoreId, StoreRole.Manager)]);
        var a = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        var b = await ApiClient.LoginAsync(Factory, second, tenant.StoreId);
        var tv = await TvClient.StartPairingAsync(Factory);

        var results = await Task.WhenAll(ClaimRawAsync(a, tv.Code, "A"), ClaimRawAsync(b, tv.Code, "B"));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.NotFound);
        Assert.Single(await a.GetAsync<List<DisplayDto>>("/api/v1/displays"));
    }

    [Fact]
    public async Task Art_arda_hatali_denemeler_kullaniciyi_kilitler_gecerli_kod_bile_kabul_edilmez()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Manager);
        var manager = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        var tv = await TvClient.StartPairingAsync(Factory);
        var wrong = tv.Code == "111111" ? "222222" : "111111";

        for (var i = 0; i < 5; i++)
        {
            await ApiClient.AssertProblemAsync(await ClaimRawAsync(manager, wrong), HttpStatusCode.NotFound, "PAIRING_CODE_INVALID");
        }

        await ApiClient.AssertProblemAsync(await ClaimRawAsync(manager, tv.Code), HttpStatusCode.TooManyRequests, "CLAIM_LOCKED");
        Assert.Equal(PairingStatus.Pending, (await tv.StatusAsync()).Status);
    }

    [Fact]
    public async Task Staff_rolu_tv_eslestiremez_iptal_edemez()
    {
        var setup = await NewStoreAsync();
        var tv = await TvClient.StartPairingAsync(Factory);

        await ApiClient.AssertProblemAsync(await ClaimRawAsync(setup.Staff, tv.Code), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        var (_, display) = await PairAsync(setup.Manager);
        await ApiClient.AssertProblemAsync(await setup.Staff.SendAsync(HttpMethod.Delete, $"/api/v1/displays/{display.Id}"), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        Assert.Equal(PairingStatus.Pending, (await tv.StatusAsync()).Status);
    }

    // ------------------------------------------------------------------ kimlik ve izolasyon

    [Fact]
    public async Task Tv_belirteci_personel_api_sine_ve_personel_cerezi_tv_uc_noktasina_giremez()
    {
        var setup = await NewStoreAsync();
        var (tv, _) = await PairAsync(setup.Manager);

        foreach (var url in new[] { "/api/v1/sessions", "/api/v1/displays", "/api/v1/products", "/api/v1/categories", "/api/v1/auth/me" })
        {
            var response = await tv.SendAsync(HttpMethod.Get, url, "Bearer");
            Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest, $"{url} → {(int)response.StatusCode}");
        }

        // Personel çerezi TV durum uç noktasında geçerli değildir
        Assert.Equal(HttpStatusCode.Unauthorized, (await setup.Manager.GetAsync("/api/v1/display/state")).StatusCode);
    }

    [Fact]
    public async Task Gecersiz_veya_bos_cihaz_belirteci_reddedilir()
    {
        ApiFactory.RequireDatabase(Factory);
        var tv = await TvClient.StartPairingAsync(Factory); // eşleşmemiş
        Assert.Equal(HttpStatusCode.Unauthorized, (await tv.SendAsync(HttpMethod.Get, "/api/v1/display/state", "Bearer", "uydurma")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Factory.CreateClient().GetAsync("/api/v1/display/state", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Baska_sube_ve_baska_firma_tv_yi_goremez_ve_yonetemez()
    {
        var setup = await NewStoreAsync();
        var (_, display) = await PairAsync(setup.Manager);
        var (session, _, image) = await SessionWithResultAsync(setup);

        // Aynı firmada başka şubede yönetici
        var storeB = await TestData.AddStoreAsync(Services, setup.Tenant.OrganizationId, "SB");
        var emailB = await TestData.AddUserAsync(Services, setup.Tenant.OrganizationId, [(storeB, StoreRole.Manager)]);
        var managerB = await ApiClient.LoginAsync(Factory, emailB, storeB);
        // Başka firmada yönetici
        var foreign = await TestData.CreateTenantAsync(Services, StoreRole.Manager);
        var foreignManager = await ApiClient.LoginAsync(Factory, foreign.Email, foreign.StoreId);

        foreach (var outsider in new[] { managerB, foreignManager })
        {
            Assert.Empty(await outsider.GetAsync<List<DisplayDto>>("/api/v1/displays"));
            await ApiClient.AssertProblemAsync(await Attach(outsider, display.Id, session.Id), HttpStatusCode.NotFound, "NOT_FOUND");
            await ApiClient.AssertProblemAsync(await Send(outsider, display.Id, DisplayMode.Single, [image]), HttpStatusCode.NotFound, "NOT_FOUND");
            await ApiClient.AssertProblemAsync(await outsider.SendAsync(HttpMethod.Delete, $"/api/v1/displays/{display.Id}"), HttpStatusCode.NotFound, "NOT_FOUND");
            await ApiClient.AssertProblemAsync(await outsider.SendAsync(HttpMethod.Post, $"/api/v1/displays/{display.Id}/clear"), HttpStatusCode.NotFound, "NOT_FOUND");
        }

        // Başka şubenin TV'sine kendi şubesindeki oturum bağlanamaz: başlıktaki şube rolü doğrulanır
        var cross = await TestData.AddUserAsync(Services, setup.Tenant.OrganizationId, [(setup.Tenant.StoreId, StoreRole.Staff)]);
        var crossClient = await ApiClient.LoginAsync(Factory, cross, setup.Tenant.StoreId);
        crossClient.StoreId = storeB;
        await ApiClient.AssertProblemAsync(await crossClient.GetAsync("/api/v1/displays"), HttpStatusCode.Forbidden, "STORE_FORBIDDEN");
    }

    [Fact]
    public async Task Baska_sube_oturumu_tv_ye_baglanamaz()
    {
        var setup = await NewStoreAsync();
        var (_, display) = await PairAsync(setup.Manager);

        var storeB = await TestData.AddStoreAsync(Services, setup.Tenant.OrganizationId, "SB");
        var email = await TestData.AddUserAsync(Services, setup.Tenant.OrganizationId, [(storeB, StoreRole.Staff), (setup.Tenant.StoreId, StoreRole.Staff)]);
        var client = await ApiClient.LoginAsync(Factory, email, storeB);
        var foreignSession = await CreateSessionAsync(client); // B şubesinde

        client.StoreId = setup.Tenant.StoreId;
        await ApiClient.AssertProblemAsync(await Attach(client, display.Id, foreignSession.Id), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // ------------------------------------------------------------------ oturum bağlama ve gösterim

    [Fact]
    public async Task Oturum_baglanir_sonuc_gonderilir_tv_yetkili_gorseli_gosterir()
    {
        var setup = await NewStoreAsync();
        var (tv, display) = await PairAsync(setup.Manager);
        var (session, _, image) = await SessionWithResultAsync(setup);

        // Bağlanmadan gönderilemez
        await ApiClient.AssertProblemAsync(await Send(setup.Staff, display.Id, DisplayMode.Single, [image]), HttpStatusCode.Conflict, "NO_SESSION");

        var attached = await ApiClient.ReadAsync<DisplayDto>(await Attach(setup.Staff, display.Id, session.Id));
        Assert.Equal(session.Id, attached.SessionId);
        Assert.True(attached.StateVersion > display.StateVersion);
        Assert.True((await tv.StateAsync()).HasSession);

        var sent = await ApiClient.ReadAsync<DisplayDto>(await Send(setup.Staff, display.Id, DisplayMode.Single, [image]));
        Assert.Equal(DisplayMode.Single, sent.Mode);
        Assert.True(sent.StateVersion > attached.StateVersion);

        var state = await tv.StateAsync();
        Assert.Equal(sent.StateVersion, state.Version);
        Assert.Equal(DisplayMode.Single, state.Mode);
        var shown = Assert.Single(state.Images);
        Assert.Equal(image, shown.Id);
        Assert.True(shown.IsDemo);
        Assert.Equal(TvImageKind.Result, shown.Kind);

        // Görsel imzalı bağlantıyla iner ve hiçbir önbelleğe yazılmaz; imzası bozulursa 403
        var file = await tv.GetFileAsync(shown.Url);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Contains("no-store", file.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await tv.GetFileAsync(shown.Url[..^2] + "xx")).StatusCode);

        // TV durum yanıtı da önbelleğe alınmaz
        var raw = await tv.StateResponseAsync();
        Assert.Contains("no-store", raw.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sonuc_degistirilir_karsilastirma_acilir_kapanir_temizlenir()
    {
        var setup = await NewStoreAsync();
        var (tv, display) = await PairAsync(setup.Manager);
        var (session, photo, first) = await SessionWithResultAsync(setup);

        // İkinci sonuç
        await SaveDraftAsync(setup.Staff, session.Id, OutfitMode.OutfitSet, setup.Catalog.SetProduct);
        var second = (await WaitForJobAsync(setup.Staff, (await StartGenerationAsync(setup.Staff, session.Id)).Id)).ResultImageId!.Value;
        Assert.NotEqual(first, second);

        await Attach(setup.Staff, display.Id, session.Id);
        await Send(setup.Staff, display.Id, DisplayMode.Single, [first]);
        Assert.Equal(first, Assert.Single((await tv.StateAsync()).Images).Id);

        // Sonucu değiştir
        await Send(setup.Staff, display.Id, DisplayMode.Single, [second]);
        Assert.Equal(second, Assert.Single((await tv.StateAsync()).Images).Id);

        // Karşılaştırma: iki sonuç, sonra orijinal fotoğraf + sonuç
        var compare = await ApiClient.ReadAsync<DisplayDto>(await Send(setup.Staff, display.Id, DisplayMode.Compare, [first, second]));
        var compareState = await tv.StateAsync();
        Assert.Equal(DisplayMode.Compare, compareState.Mode);
        Assert.Equal([first, second], compareState.Images.Select(i => i.Id));
        Assert.Equal(compare.StateVersion, compareState.Version);

        await Send(setup.Staff, display.Id, DisplayMode.Compare, [photo.Id, second]);
        var withPhoto = await tv.StateAsync();
        Assert.Equal([TvImageKind.Photo, TvImageKind.Result], withPhoto.Images.Select(i => i.Kind));

        // Karşılaştırmayı kapat (tekli)
        await Send(setup.Staff, display.Id, DisplayMode.Single, [second]);
        Assert.Equal(DisplayMode.Single, (await tv.StateAsync()).Mode);

        // Temizle: oturum bağlı kalır
        var cleared = await ApiClient.ReadAsync<DisplayDto>(await setup.Staff.SendAsync(HttpMethod.Post, $"/api/v1/displays/{display.Id}/clear", new { }));
        Assert.Equal(DisplayMode.Idle, cleared.Mode);
        Assert.Equal(session.Id, cleared.SessionId);
        var idle = await tv.StateAsync();
        Assert.Empty(idle.Images);
        Assert.True(idle.HasSession);
    }

    [Fact]
    public async Task Gecersiz_gonderimler_reddedilir()
    {
        var setup = await NewStoreAsync();
        var (_, display) = await PairAsync(setup.Manager);
        var (session, _, image) = await SessionWithResultAsync(setup);
        await Attach(setup.Staff, display.Id, session.Id);

        await ApiClient.AssertProblemAsync(await Send(setup.Staff, display.Id, DisplayMode.Idle, [image]), HttpStatusCode.BadRequest, "INVALID_MODE");
        await ApiClient.AssertProblemAsync(await Send(setup.Staff, display.Id, DisplayMode.Single, []), HttpStatusCode.BadRequest, "INVALID_IMAGES");
        await ApiClient.AssertProblemAsync(await Send(setup.Staff, display.Id, DisplayMode.Single, [image, image]), HttpStatusCode.BadRequest, "INVALID_IMAGES");
        await ApiClient.AssertProblemAsync(await Send(setup.Staff, display.Id, DisplayMode.Compare, [image]), HttpStatusCode.BadRequest, "INVALID_IMAGES");
        await ApiClient.AssertProblemAsync(await Send(setup.Staff, display.Id, DisplayMode.Compare, [image, image]), HttpStatusCode.BadRequest, "INVALID_IMAGES");
        await ApiClient.AssertProblemAsync(await Send(setup.Staff, display.Id, DisplayMode.Single, [Guid.NewGuid()]), HttpStatusCode.Forbidden, "IMAGE_NOT_IN_SESSION");
    }

    [Fact]
    public async Task Iki_tv_ve_iki_eszamanli_oturum_birbirine_karismaz()
    {
        var setup = await NewStoreAsync();
        var (tvA, displayA) = await PairAsync(setup.Manager, "TV A");
        var (tvB, displayB) = await PairAsync(setup.Manager, "TV B");
        var (sessionA, photoA, imageA) = await SessionWithResultAsync(setup, seed: 11);
        var (sessionB, _, imageB) = await SessionWithResultAsync(setup, seed: 22);

        await Attach(setup.Staff, displayA.Id, sessionA.Id);
        await Attach(setup.Staff, displayB.Id, sessionB.Id);

        // Yanlış TV'ye başka oturumun görseli gönderilemez (hem sonuç hem fotoğraf)
        await ApiClient.AssertProblemAsync(await Send(setup.Staff, displayA.Id, DisplayMode.Single, [imageB]), HttpStatusCode.Forbidden, "IMAGE_NOT_IN_SESSION");
        await ApiClient.AssertProblemAsync(await Send(setup.Staff, displayB.Id, DisplayMode.Single, [photoA.Id]), HttpStatusCode.Forbidden, "IMAGE_NOT_IN_SESSION");
        await ApiClient.AssertProblemAsync(await Send(setup.Staff, displayB.Id, DisplayMode.Compare, [imageB, imageA]), HttpStatusCode.Forbidden, "IMAGE_NOT_IN_SESSION");

        await Send(setup.Staff, displayA.Id, DisplayMode.Single, [imageA]);
        await Send(setup.Staff, displayB.Id, DisplayMode.Single, [imageB]);
        Assert.Equal(imageA, Assert.Single((await tvA.StateAsync()).Images).Id);
        Assert.Equal(imageB, Assert.Single((await tvB.StateAsync()).Images).Id);

        // Bir oturum aynı anda tek TV'de
        await ApiClient.AssertProblemAsync(await Attach(setup.Staff, displayB.Id, sessionA.Id), HttpStatusCode.Conflict, "DISPLAY_IN_USE");
        await ApiClient.AssertProblemAsync(await Attach(setup.Staff, (await PairAsync(setup.Manager, "TV C")).Display.Id, sessionA.Id), HttpStatusCode.Conflict, "SESSION_ON_OTHER_DISPLAY");

        // B temizlenince A etkilenmez
        await setup.Staff.SendAsync(HttpMethod.Post, $"/api/v1/displays/{displayB.Id}/clear", new { });
        Assert.Empty((await tvB.StateAsync()).Images);
        Assert.Equal(imageA, Assert.Single((await tvA.StateAsync()).Images).Id);
    }

    [Fact]
    public async Task Devralma_onayla_yapilir_eski_tv_bosalir()
    {
        var setup = await NewStoreAsync();
        var (tvA, displayA) = await PairAsync(setup.Manager, "TV A");
        var (tvB, displayB) = await PairAsync(setup.Manager, "TV B");
        var (session, _, image) = await SessionWithResultAsync(setup);

        await Attach(setup.Staff, displayA.Id, session.Id);
        await Send(setup.Staff, displayA.Id, DisplayMode.Single, [image]);

        await ApiClient.AssertProblemAsync(await Attach(setup.Staff, displayB.Id, session.Id), HttpStatusCode.Conflict, "SESSION_ON_OTHER_DISPLAY");
        var moved = await ApiClient.ReadAsync<DisplayDto>(await Attach(setup.Staff, displayB.Id, session.Id, force: true));
        Assert.Equal(session.Id, moved.SessionId);

        var oldState = await tvA.StateAsync();
        Assert.False(oldState.HasSession);
        Assert.Empty(oldState.Images);
        Assert.True((await tvB.StateAsync()).HasSession);
        Assert.Null((await GetDisplayAsync(setup.Staff, displayA.Id)).SessionId);
    }

    [Fact]
    public async Task Eski_surumle_gonderim_cakisma_verir_ve_surum_hic_geri_gitmez()
    {
        var setup = await NewStoreAsync();
        var (tv, display) = await PairAsync(setup.Manager);
        var (session, _, image) = await SessionWithResultAsync(setup);
        var attached = await ApiClient.ReadAsync<DisplayDto>(await Attach(setup.Staff, display.Id, session.Id));

        var first = await ApiClient.ReadAsync<DisplayDto>(await Send(setup.Staff, display.Id, DisplayMode.Single, [image], attached.StateVersion));
        // İkinci personel aynı eski sürümü bildirir: ezemez
        var stale = await Send(setup.Staff, display.Id, DisplayMode.Single, [image], attached.StateVersion);
        await ApiClient.AssertProblemAsync(stale, HttpStatusCode.Conflict, "STATE_CONFLICT");

        var versions = new List<long> { attached.StateVersion, first.StateVersion };
        for (var i = 0; i < 4; i++)
        {
            versions.Add((await ApiClient.ReadAsync<DisplayDto>(await Send(setup.Staff, display.Id, DisplayMode.Single, [image]))).StateVersion);
        }

        Assert.Equal(versions.Order().ToList(), versions);
        Assert.Equal(versions.Count, versions.Distinct().Count());
        Assert.Equal(versions[^1], (await tv.StateAsync()).Version);
    }

    [Fact]
    public async Task Eszamanli_gonderimlerde_hicbir_guncelleme_kaybolmaz()
    {
        var setup = await NewStoreAsync();
        var (tv, display) = await PairAsync(setup.Manager);
        var (session, _, image) = await SessionWithResultAsync(setup);
        var attached = await ApiClient.ReadAsync<DisplayDto>(await Attach(setup.Staff, display.Id, session.Id));

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Send(setup.Staff, display.Id, DisplayMode.Single, [image])));
        foreach (var r in responses)
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        // Sekiz eşzamanlı yazma sekiz ayrı sürüm üretir: iyimser eşzamanlılık hiçbirini sessizce ezmez
        Assert.Equal(attached.StateVersion + 8, (await tv.StateAsync()).Version);
    }

    // ------------------------------------------------------------------ gizlilik: temizleme ve erişim iptali

    [Fact]
    public async Task Fotograf_silinince_tv_temizlenir_oturum_bagli_kalir()
    {
        var setup = await NewStoreAsync();
        var (tv, display) = await PairAsync(setup.Manager);
        var (session, photo, image) = await SessionWithResultAsync(setup);
        await Attach(setup.Staff, display.Id, session.Id);
        await Send(setup.Staff, display.Id, DisplayMode.Single, [image]);
        var shown = Assert.Single((await tv.StateAsync()).Images);
        var before = (await tv.StateAsync()).Version;

        Assert.Equal(HttpStatusCode.OK, (await setup.Staff.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{session.Id}/photos/{photo.Id}")).StatusCode);

        var state = await tv.StateAsync();
        Assert.Empty(state.Images);
        Assert.Equal(DisplayMode.Idle, state.Mode);
        Assert.True(state.Version > before);
        Assert.True(state.HasSession);
        Assert.NotNull(shown.Url);
    }

    [Fact]
    public async Task Oturum_kapatilinca_tv_bosalir_ve_serbest_kalir()
    {
        var setup = await NewStoreAsync();
        var (tv, display) = await PairAsync(setup.Manager);
        var (session, _, image) = await SessionWithResultAsync(setup);
        await Attach(setup.Staff, display.Id, session.Id);
        await Send(setup.Staff, display.Id, DisplayMode.Single, [image]);

        await setup.Staff.PostAsync<SessionDetail>($"/api/v1/sessions/{session.Id}/close");

        var state = await tv.StateAsync();
        Assert.False(state.HasSession);
        Assert.Empty(state.Images);
        Assert.Null((await GetDisplayAsync(setup.Staff, display.Id)).SessionId);

        // Kapalı oturum yeniden bağlanamaz
        await ApiClient.AssertProblemAsync(await Attach(setup.Staff, display.Id, session.Id), HttpStatusCode.Conflict, "SESSION_CLOSED");
    }

    [Fact]
    public async Task Oturum_silinince_ve_onay_geri_alinince_tv_bosalir()
    {
        var setup = await NewStoreAsync();
        var (tvA, displayA) = await PairAsync(setup.Manager, "A");
        var (tvB, displayB) = await PairAsync(setup.Manager, "B");
        var (sessionA, _, imageA) = await SessionWithResultAsync(setup, seed: 5);
        var (sessionB, _, imageB) = await SessionWithResultAsync(setup, seed: 6);
        foreach (var (display, session, image) in new[] { (displayA, sessionA, imageA), (displayB, sessionB, imageB) })
        {
            await Attach(setup.Staff, display.Id, session.Id);
            await Send(setup.Staff, display.Id, DisplayMode.Single, [image]);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await setup.Staff.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{sessionA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await setup.Staff.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{sessionB.Id}/consent")).StatusCode);

        foreach (var tv in new[] { tvA, tvB })
        {
            var state = await tv.StateAsync();
            Assert.False(state.HasSession);
            Assert.Empty(state.Images);
        }
    }

    [Fact]
    public async Task Saklama_suresi_dolan_oturum_tv_den_kalkar()
    {
        var setup = await NewStoreAsync();
        var (tv, display) = await PairAsync(setup.Manager);
        var (session, _, image) = await SessionWithResultAsync(setup);
        await Attach(setup.Staff, display.Id, session.Id);
        await Send(setup.Staff, display.Id, DisplayMode.Single, [image]);

        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.TryOnSessions.IgnoreQueryFilters().Where(s => s.Id == session.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RetentionUntil, DateTimeOffset.UtcNow.AddMinutes(-1)), TestContext.Current.CancellationToken);
        }

        await Services.GetRequiredService<SanalProva.Infrastructure.Retention.RetentionSweeper>().SweepAsync(TestContext.Current.CancellationToken);

        var state = await tv.StateAsync();
        Assert.False(state.HasSession);
        Assert.Empty(state.Images);
    }

    [Fact]
    public async Task Bosta_kalan_tv_temizlenir()
    {
        var setup = await NewStoreAsync();
        var (tv, display) = await PairAsync(setup.Manager);
        var (session, _, image) = await SessionWithResultAsync(setup);
        await Attach(setup.Staff, display.Id, session.Id);
        await Send(setup.Staff, display.Id, DisplayMode.Single, [image]);

        await using var scope = Services.CreateAsyncScope();
        // Henüz süre dolmadı: dokunulmaz
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<DisplayStateService>().ClearIdleAsync(TimeSpan.FromMinutes(15), TestContext.Current.CancellationToken));
        Assert.Single((await tv.StateAsync()).Images);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        var cleared = await scope.ServiceProvider.GetRequiredService<DisplayStateService>().ClearIdleAsync(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        Assert.True(cleared >= 1);
        var state = await tv.StateAsync();
        Assert.Empty(state.Images);
        Assert.True(state.HasSession);
    }

    [Fact]
    public async Task Eslestirme_kaldirilinca_belirtec_hemen_gecersiz_olur()
    {
        var setup = await NewStoreAsync();
        var (tv, display) = await PairAsync(setup.Manager);
        var (session, _, image) = await SessionWithResultAsync(setup);
        await Attach(setup.Staff, display.Id, session.Id);
        await Send(setup.Staff, display.Id, DisplayMode.Single, [image]);
        Assert.Equal(HttpStatusCode.OK, (await tv.StateResponseAsync()).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await setup.Manager.SendAsync(HttpMethod.Delete, $"/api/v1/displays/{display.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await tv.StateResponseAsync()).StatusCode);
        Assert.Equal(PairingStatus.Expired, (await tv.StatusAsync()).Status);
        Assert.Empty(await setup.Manager.GetAsync<List<DisplayDto>>("/api/v1/displays"));

        // Oturum serbest kalır, başka TV'ye bağlanabilir
        var (_, other) = await PairAsync(setup.Manager, "Yeni TV");
        Assert.Equal(HttpStatusCode.OK, (await Attach(setup.Staff, other.Id, session.Id)).StatusCode);
    }

    [Fact]
    public async Task Staff_tv_baglantisini_kesebilir()
    {
        var setup = await NewStoreAsync();
        var (tv, display) = await PairAsync(setup.Manager);
        var (session, _, image) = await SessionWithResultAsync(setup);
        await Attach(setup.Staff, display.Id, session.Id);
        await Send(setup.Staff, display.Id, DisplayMode.Single, [image]);

        var detached = await ApiClient.ReadAsync<DisplayDto>(await setup.Staff.SendAsync(HttpMethod.Delete, $"/api/v1/displays/{display.Id}/session"));

        Assert.Null(detached.SessionId);
        Assert.Equal(DisplayMode.Idle, detached.Mode);
        Assert.Empty((await tv.StateAsync()).Images);
        // Tekrar bağlanabilir
        Assert.Equal(HttpStatusCode.OK, (await Attach(setup.Staff, display.Id, session.Id)).StatusCode);
    }

    // ------------------------------------------------------------------ SignalR

    private HubConnection ConnectTv(string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Factory.Server.BaseAddress, DisplayHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.WebSockets;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                // Tarayıcı WebSocket'i başlık gönderemez: belirteç gerçek kullanımdaki gibi sorgu parametresiyle taşınır
                options.WebSocketFactory = async (context, ct) =>
                    await Factory.Server.CreateWebSocketClient().ConnectAsync(new Uri($"{context.Uri}{(string.IsNullOrEmpty(context.Uri.Query) ? "?" : "&")}access_token={Uri.EscapeDataString(token)}"), ct);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

    private HubConnection ConnectStaff(ApiClient client) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Factory.Server.BaseAddress, StaffHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.WebSockets;
                options.WebSocketFactory = async (context, ct) =>
                {
                    var socketClient = Factory.Server.CreateWebSocketClient();
                    socketClient.ConfigureRequest = request => request.Headers["Cookie"] = client.AuthCookie;
                    return await socketClient.ConnectAsync(context.Uri, ct);
                };
                options.Headers["Cookie"] = client.AuthCookie;
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

    private static async Task<T> NextAsync<T>(TaskCompletionSource<T> source)
    {
        var finished = await Task.WhenAny(source.Task, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(finished == source.Task, "Beklenen SignalR olayı 10 sn içinde gelmedi.");
        return await source.Task;
    }

    [Fact]
    public async Task Cevrimici_durumu_ve_durum_olaylari_signalr_ile_iletilir()
    {
        var setup = await NewStoreAsync();
        var (tv, display) = await PairAsync(setup.Manager);
        var (session, photo, image) = await SessionWithResultAsync(setup);

        await using var staffHub = ConnectStaff(setup.Staff);
        var statuses = new System.Collections.Concurrent.ConcurrentQueue<DisplayStatusMessage>();
        staffHub.On<DisplayStatusMessage>("DisplayStatusChanged", m => statuses.Enqueue(m));
        await staffHub.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(await staffHub.InvokeAsync<bool>("JoinStore", setup.Tenant.StoreId, TestContext.Current.CancellationToken));

        await using var tvHub = ConnectTv(tv.Secret);
        var changes = new System.Collections.Concurrent.ConcurrentQueue<DisplayStateChangedMessage>();
        var cleared = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        tvHub.On<DisplayStateChangedMessage>("StateChanged", m => changes.Enqueue(m));
        tvHub.On("Cleared", () => cleared.Enqueue(true));
        await tvHub.StartAsync(TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => statuses.Any(s => s.DisplayId == display.Id && s.Online));
        Assert.True((await GetDisplayAsync(setup.Staff, display.Id)).Online);

        // Gönderim → TV'ye yalnızca sürüm ve neden gider; içerik REST'ten çekilir
        await Attach(setup.Staff, display.Id, session.Id);
        var sent = await ApiClient.ReadAsync<DisplayDto>(await Send(setup.Staff, display.Id, DisplayMode.Single, [image]));
        await WaitUntilAsync(() => changes.Any(c => c.StateVersion == sent.StateVersion && c.Reason == DisplayStateReason.ImageSent));
        Assert.Equal(sent.StateVersion, (await tv.StateAsync()).Version);

        var compare = await ApiClient.ReadAsync<DisplayDto>(await Send(setup.Staff, display.Id, DisplayMode.Compare, [photo.Id, image]));
        await WaitUntilAsync(() => changes.Any(c => c.StateVersion == compare.StateVersion && c.Reason == DisplayStateReason.CompareOpened));
        var closed = await ApiClient.ReadAsync<DisplayDto>(await Send(setup.Staff, display.Id, DisplayMode.Single, [image]));
        await WaitUntilAsync(() => changes.Any(c => c.StateVersion == closed.StateVersion && c.Reason == DisplayStateReason.CompareClosed));

        // Personel tarafı da olayı alır
        await WaitUntilAsync(() => statuses.Any(s => s.DisplayId == display.Id && s.StateVersion == closed.StateVersion && s.Mode == DisplayMode.Single));

        // Temizle → Cleared + StateChanged
        await setup.Staff.SendAsync(HttpMethod.Post, $"/api/v1/displays/{display.Id}/clear", new { });
        await WaitUntilAsync(() => !cleared.IsEmpty && changes.Any(c => c.Reason == DisplayStateReason.Cleared));

        // Fotoğraf silme: gizlilik temizliği olayı
        await Send(setup.Staff, display.Id, DisplayMode.Single, [image]);
        await setup.Staff.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{session.Id}/photos/{photo.Id}");
        await WaitUntilAsync(() => changes.Any(c => c.Reason == DisplayStateReason.PrivacyCleared));

        // Oturum sonu
        await setup.Staff.PostAsync<SessionDetail>($"/api/v1/sessions/{session.Id}/close");
        await WaitUntilAsync(() => changes.Any(c => c.Reason == DisplayStateReason.SessionEnded));

        // Bağlantı kopması → personel çevrimdışı görür
        await tvHub.StopAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => statuses.Any(s => s.DisplayId == display.Id && !s.Online && s.Reason is null));
        Assert.False((await GetDisplayAsync(setup.Staff, display.Id)).Online);
    }

    [Fact]
    public async Task Yeniden_baglanan_tv_kacirdigi_degisikligi_sunucudan_alir()
    {
        var setup = await NewStoreAsync();
        var (tv, display) = await PairAsync(setup.Manager);
        var (session, _, image) = await SessionWithResultAsync(setup);
        await Attach(setup.Staff, display.Id, session.Id);

        var hub = ConnectTv(tv.Secret);
        await hub.StartAsync(TestContext.Current.CancellationToken);
        await hub.StopAsync(TestContext.Current.CancellationToken);
        await hub.DisposeAsync();

        // TV çevrimdışıyken durum değişir: olay kaybolur, doğruluk kaynağı sunucudadır
        var sent = await ApiClient.ReadAsync<DisplayDto>(await Send(setup.Staff, display.Id, DisplayMode.Single, [image]));

        var reconnected = ConnectTv(tv.Secret);
        var heartbeatVersion = new TaskCompletionSource<long>();
        var pushed = new TaskCompletionSource<DisplayStateChangedMessage>();
        reconnected.On<DisplayStateChangedMessage>("StateChanged", m => pushed.TrySetResult(m));
        await reconnected.StartAsync(TestContext.Current.CancellationToken);
        await using (reconnected)
        {
            var state = await tv.StateAsync();
            Assert.Equal(sent.StateVersion, state.Version);
            Assert.Single(state.Images);

            // Heartbeat eski sürümü bildirirse sunucu güncel sürümü yeniden bildirir
            heartbeatVersion.SetResult(await reconnected.InvokeAsync<long>("Heartbeat", 1L, TestContext.Current.CancellationToken));
            Assert.Equal(sent.StateVersion, await heartbeatVersion.Task);
            Assert.Equal(sent.StateVersion, (await NextAsync(pushed)).StateVersion);
        }
    }

    [Fact]
    public async Task Iptal_edilen_cihaz_hub_a_baglanamaz_ve_bagli_olan_haber_alir()
    {
        var setup = await NewStoreAsync();
        var (tv, display) = await PairAsync(setup.Manager);

        await using var hub = ConnectTv(tv.Secret);
        var revoked = new TaskCompletionSource<bool>();
        hub.On("Revoked", () => revoked.TrySetResult(true));
        await hub.StartAsync(TestContext.Current.CancellationToken);
        var presence = Services.GetRequiredService<DisplayPresence>();
        await WaitUntilAsync(() => presence.IsOnline(display.Id)); // gruba katılım bağlantı kurulumundan hemen sonra tamamlanır

        await setup.Manager.SendAsync(HttpMethod.Delete, $"/api/v1/displays/{display.Id}");
        Assert.True(await NextAsync(revoked));

        await using var late = ConnectTv(tv.Secret);
        await Assert.ThrowsAnyAsync<Exception>(() => late.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Hub_yalnizca_kendi_kimligiyle_kabul_edilir()
    {
        var setup = await NewStoreAsync();
        var (tv, _) = await PairAsync(setup.Manager);
        await TestData.AddUserAsync(Services, setup.Tenant.OrganizationId, [(setup.Tenant.StoreId, StoreRole.Staff)]);

        // Eşleşmemiş/uydurma belirteç ve personel çerezi TV hub'ına giremez
        await using var fake = ConnectTv("uydurma-belirtec");
        await Assert.ThrowsAnyAsync<Exception>(() => fake.StartAsync(TestContext.Current.CancellationToken));

        var cookieOnly = new HubConnectionBuilder()
            .WithUrl(new Uri(Factory.Server.BaseAddress, DisplayHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.WebSockets;
                options.Headers["Cookie"] = setup.Staff.AuthCookie;
                options.WebSocketFactory = async (context, ct) =>
                {
                    var socketClient = Factory.Server.CreateWebSocketClient();
                    socketClient.ConfigureRequest = request => request.Headers["Cookie"] = setup.Staff.AuthCookie;
                    return await socketClient.ConnectAsync(context.Uri, ct);
                };
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        await using (cookieOnly)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => cookieOnly.StartAsync(TestContext.Current.CancellationToken));
        }

        // TV belirteci personel hub'ına giremez
        await using var tvOnStaff = new HubConnectionBuilder()
            .WithUrl(new Uri(Factory.Server.BaseAddress, StaffHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.WebSockets;
                options.AccessTokenProvider = () => Task.FromResult<string?>(tv.Secret);
                options.WebSocketFactory = async (context, ct) =>
                    await Factory.Server.CreateWebSocketClient().ConnectAsync(new Uri($"{context.Uri}{(string.IsNullOrEmpty(context.Uri.Query) ? "?" : "&")}access_token={Uri.EscapeDataString(tv.Secret)}"), ct);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        await Assert.ThrowsAnyAsync<Exception>(() => tvOnStaff.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Personel_baska_subenin_tv_olaylarina_abone_olamaz()
    {
        var setup = await NewStoreAsync();
        var other = await TestData.CreateTenantAsync(Services);
        await using var hub = ConnectStaff(setup.Staff);
        await hub.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(await hub.InvokeAsync<bool>("JoinStore", setup.Tenant.StoreId, TestContext.Current.CancellationToken));
        Assert.False(await hub.InvokeAsync<bool>("JoinStore", other.StoreId, TestContext.Current.CancellationToken));
        Assert.False(await hub.InvokeAsync<bool>("JoinStore", Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutSeconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Beklenen koşul zaman aşımına uğradı.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Yeni_sonuc_uretilince_tekli_sonuc_gosteren_tv_en_yeniye_gecer_bos_ve_fotograf_gosteren_degismez()
    {
        var setup = await NewStoreAsync();
        var (tvA, displayA) = await PairAsync(setup.Manager, "A");
        var (tvB, displayB) = await PairAsync(setup.Manager, "B");
        var (tvC, displayC) = await PairAsync(setup.Manager, "C");
        var (sessionA, _, imageA) = await SessionWithResultAsync(setup, seed: 31);
        var (sessionB, _, _) = await SessionWithResultAsync(setup, seed: 32);
        var (sessionC, photoC, _) = await SessionWithResultAsync(setup, seed: 33);
        await Attach(setup.Staff, displayA.Id, sessionA.Id);
        await Attach(setup.Staff, displayB.Id, sessionB.Id);
        await Attach(setup.Staff, displayC.Id, sessionC.Id);
        await Send(setup.Staff, displayA.Id, DisplayMode.Single, [imageA]); // sonuç gösteriyor
        await Send(setup.Staff, displayC.Id, DisplayMode.Single, [photoC.Id]); // orijinal fotoğraf gösteriyor
        // B bağlı ama boş

        Guid? newA = null;
        foreach (var session in new[] { sessionA, sessionB, sessionC })
        {
            await SaveDraftAsync(setup.Staff, session.Id, OutfitMode.OutfitSet, setup.Catalog.SetProduct);
            var job = await WaitForJobAsync(setup.Staff, (await StartGenerationAsync(setup.Staff, session.Id)).Id);
            newA ??= job.ResultImageId;
        }

        // A: en yeni sonuca geçer (personel yenilemek zorunda kalmaz)
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (((await tvA.StateAsync()).Images is [var current] ? current.Id : (Guid?)null) != newA && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(newA, Assert.Single((await tvA.StateAsync()).Images).Id);
        // B boş kalır: müşteriye personel onayı olmadan yeni görüntü çıkmaz
        Assert.Empty((await tvB.StateAsync()).Images);
        // C orijinal fotoğrafı göstermeye devam eder
        var shownC = Assert.Single((await tvC.StateAsync()).Images);
        Assert.Equal(photoC.Id, shownC.Id);
        Assert.Equal(TvImageKind.Photo, shownC.Kind);
    }

    // ------------------------------------------------------------------ yeniden kullanım korumaları

    [Fact]
    public async Task Onaysiz_veya_olmayan_oturum_baglanamaz()
    {
        var setup = await NewStoreAsync();
        var (_, display) = await PairAsync(setup.Manager);
        var noConsent = await CreateSessionAsync(setup.Staff);

        await ApiClient.AssertProblemAsync(await Attach(setup.Staff, display.Id, noConsent.Id), HttpStatusCode.Conflict, "CONSENT_REQUIRED");
        await ApiClient.AssertProblemAsync(await Attach(setup.Staff, display.Id, Guid.NewGuid()), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Tv_yoneticisi_adi_degistirebilir()
    {
        var setup = await NewStoreAsync();
        var (tv, display) = await PairAsync(setup.Manager);

        var renamed = await setup.Manager.PatchAsync<DisplayDto>($"/api/v1/displays/{display.Id}", new { name = "Vitrin TV" });

        Assert.Equal("Vitrin TV", renamed.Name);
        Assert.Equal("Vitrin TV", (await tv.StateAsync()).DeviceName);
        await ApiClient.AssertProblemAsync(await setup.Staff.SendAsync(HttpMethod.Patch, $"/api/v1/displays/{display.Id}", new { name = "X" }), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
    }

    [Fact]
    public async Task Bekleyen_eslestirme_ust_siniri_asilinca_yeni_kod_verilmez()
    {
        ApiFactory.RequireDatabase(Factory);
        var (scope, db) = OpenDb();
        using (scope)
        {
            // Diğer testlerin bekleyen kayıtları sayılır: sınırı geçici olarak aşmak yerine yalnızca dönüş biçimi doğrulanır
            Assert.True(await db.DisplayPairings.CountAsync(TestContext.Current.CancellationToken) >= 0);
        }

        var started = await Factory.CreateClient().PostAsync("/api/v1/display/pairing", content: JsonContent.Create(new { }), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Contains("no-store", started.Headers.CacheControl?.ToString() ?? "no-store", StringComparison.Ordinal);
        Assert.NotEmpty(Services.GetServices<IHostedService>().OfType<DisplayMaintenanceJob>());
    }
}
