using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SanalProva.Api.Features.TryOn;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Retention;
using SkiaSharp;

namespace SanalProva.IntegrationTests;

public sealed class SessionFlowTests(ApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Oturum_adimlari_sirayla_ilerler_ve_sayfa_yenilenince_ayni_yerden_devam_edilir()
    {
        var (tenant, client) = await StaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);

        var session = await CreateSessionAsync(client, "Zeynep");
        Assert.Equal(SessionStep.Consent, session.NextStep);
        Assert.Matches("^PR-\\d{3}$", session.Code);
        Assert.Equal("Zeynep", session.CustomerLabel);

        session = await ConsentAsync(client, session.Id);
        Assert.Equal(SessionStep.Photo, session.NextStep);

        await UploadPhotoAsync(client, session.Id);
        Assert.Equal(SessionStep.Mode, (await client.GetAsync<SessionDetail>($"/api/v1/sessions/{session.Id}")).NextStep);

        await SaveDraftAsync(client, session.Id, OutfitMode.FreeCombination);
        Assert.Equal(SessionStep.Outfit, (await client.GetAsync<SessionDetail>($"/api/v1/sessions/{session.Id}")).NextStep);

        // "Sayfa yenileme": yeni bir istemci (yeni oturum çerezi) aynı prova ve taslağı görür
        var reloaded = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        var again = await reloaded.GetAsync<SessionDetail>($"/api/v1/sessions/{session.Id}");
        Assert.Equal(OutfitMode.FreeCombination, again.Draft!.Mode);
        Assert.Single(again.Photos);
        Assert.NotNull(catalog);

        var list = await reloaded.GetAsync<Page<SessionSummary>>("/api/v1/sessions?status=open");
        var summary = Assert.Single(list.Items);
        Assert.Equal(SessionStep.Outfit, summary.NextStep);
        Assert.NotNull(summary.Photo);
        Assert.Null(summary.Result);
    }

    [Fact]
    public async Task Onay_olmadan_fotograf_ve_uretim_kabul_edilmez()
    {
        var (_, client) = await StaffAsync();
        var session = await CreateSessionAsync(client);

        await ApiClient.AssertProblemAsync(await client.UploadAsync($"/api/v1/sessions/{session.Id}/photos", TestImages.Portrait()), HttpStatusCode.Conflict, "CONSENT_REQUIRED");
        await ApiClient.AssertProblemAsync(
            await client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{session.Id}/generations", new { }, configure: r => r.Headers.Add("Idempotency-Key", "k1")),
            HttpStatusCode.Conflict, "CONSENT_REQUIRED");
    }

    [Fact]
    public async Task Eksik_veya_eski_onaylar_reddedilir_cocuk_icin_veli_onayi_gerekir()
    {
        var (_, client) = await StaffAsync();
        var session = await CreateSessionAsync(client);
        var url = $"/api/v1/sessions/{session.Id}/consent";
        object Body(bool processing = true, bool transfer = true, bool minor = false, bool? guardian = null, string version = ConsentNotice.Version) => new
        {
            noticeVersion = version, processingConsent = processing, foreignTransferConsent = transfer, extendedRetentionConsent = false,
            isMinor = minor, guardianConsent = guardian, method = "StaffConfirmedVerbal",
        };

        await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Post, url, Body(processing: false)), HttpStatusCode.BadRequest, "CONSENT_INCOMPLETE");
        await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Post, url, Body(transfer: false)), HttpStatusCode.BadRequest, "CONSENT_INCOMPLETE");
        await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Post, url, Body(minor: true)), HttpStatusCode.BadRequest, "GUARDIAN_CONSENT_REQUIRED");
        await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Post, url, Body(version: "eski")), HttpStatusCode.BadRequest, "NOTICE_OUTDATED");

        var ok = await client.PostAsync<SessionDetail>(url, Body(minor: true, guardian: true));
        Assert.Equal(true, ok.Consent!.GuardianConsent);
        await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Post, url, Body()), HttpStatusCode.Conflict, "CONSENT_EXISTS");
    }

    [Fact]
    public async Task Aydinlatma_metni_surumlu_doner()
    {
        var (_, client) = await StaffAsync();

        var notice = await client.GetAsync<ConsentNoticeDto>("/api/v1/consent-notice/current");

        Assert.Equal(ConsentNotice.Version, notice.Version);
        Assert.True(notice.Paragraphs.Count >= 3);
        Assert.Contains(notice.Paragraphs, p => p.Contains("yurt dışı", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fotograf_kalite_uyarilari_ve_yukleme_guvenligi()
    {
        var (_, client) = await StaffAsync();
        var session = await CreateSessionAsync(client);
        await ConsentAsync(client, session.Id);
        var url = $"/api/v1/sessions/{session.Id}/photos";

        var good = await UploadPhotoAsync(client, session.Id, TestImages.Portrait());
        Assert.False(good.Quality.HasWarnings, string.Join(" | ", good.Quality.Warnings));
        Assert.True(good.IsPrimary);

        var landscape = await UploadPhotoAsync(client, session.Id, TestImages.Jpeg(1600, 1000, seed: 2));
        Assert.True(landscape.Quality.NotPortrait);
        Assert.Contains(landscape.Quality.Warnings, w => w.Contains("dikey", StringComparison.Ordinal));

        var lowRes = await UploadPhotoAsync(client, session.Id, TestImages.Jpeg(450, 700, seed: 3));
        Assert.True(lowRes.Quality.LowResolution);

        await ApiClient.AssertProblemAsync(await client.UploadAsync(url, "GIF89a...."u8.ToArray(), "a.gif", "image/gif"), HttpStatusCode.UnsupportedMediaType, "UNSUPPORTED_IMAGE_TYPE");
        await ApiClient.AssertProblemAsync(await client.UploadAsync(url, [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[100]]), HttpStatusCode.UnprocessableEntity, "IMAGE_CORRUPT");
        await ApiClient.AssertProblemAsync(await client.UploadAsync(url, TestImages.Jpeg(200, 300)), HttpStatusCode.UnprocessableEntity, "IMAGE_TOO_SMALL");
    }

    [Fact]
    public async Task Yuklenen_fotograf_yeniden_kodlanir_metadata_tasimaz_ve_imzali_url_ile_acilir()
    {
        var (_, client) = await StaffAsync();
        var session = await CreateSessionAsync(client);
        await ConsentAsync(client, session.Id);
        byte[] polyglot = [.. TestImages.Portrait(), .. "<script>alert(1)</script>"u8.ToArray()];

        var photo = await UploadPhotoAsync(client, session.Id, polyglot);

        var stored = await client.DownloadAsync(photo.Urls.Url);
        Assert.True(stored.AsSpan().IndexOf("<script>"u8) < 0, "Eklenen veri depoya yazılmamalı");
        using var decoded = SKBitmap.Decode(stored);
        Assert.Equal((photo.Width, photo.Height), (decoded.Width, decoded.Height));
        var thumb = await client.DownloadAsync(photo.Urls.ThumbUrl);
        using var thumbBitmap = SKBitmap.Decode(thumb);
        Assert.Equal(480, Math.Max(thumbBitmap.Width, thumbBitmap.Height));
        Assert.Contains("/files/", photo.Urls.Url, StringComparison.Ordinal);
        Assert.Contains("sig=", photo.Urls.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Yeniden_cek_oncekini_siler_ve_yeni_fotograf_birincil_olur()
    {
        var (_, client) = await StaffAsync();
        var session = await CreateSessionAsync(client);
        await ConsentAsync(client, session.Id);
        var first = await UploadPhotoAsync(client, session.Id, TestImages.Portrait(1));

        var second = await UploadPhotoAsync(client, session.Id, TestImages.Portrait(2));

        var detail = await client.GetAsync<SessionDetail>($"/api/v1/sessions/{session.Id}");
        Assert.Equal(second.Id, Assert.Single(detail.Photos).Id);
        Assert.True(detail.Photos[0].IsPrimary);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(first.Urls.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(first.Urls.ThumbUrl)).StatusCode);
    }

    [Fact]
    public async Task Fotograf_silme_dosyayi_kaldirir_ve_adimi_geri_alir()
    {
        var (_, client) = await StaffAsync();
        var (session, photo) = await PreparedSessionAsync(client);

        var after = await ApiClient.ReadAsync<SessionDetail>(await client.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{session.Id}/photos/{photo.Id}"));

        Assert.Empty(after.Photos);
        Assert.Equal(SessionStep.Photo, after.NextStep);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(photo.Urls.Url)).StatusCode);
    }

    [Fact]
    public async Task Taslak_secim_dogrulanir_kaydedilir_ve_uyari_uretir()
    {
        var (tenant, client) = await StaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        var (session, _) = await PreparedSessionAsync(client);

        // Hazır takım modunda yalnızca takım, serbest kombinde yalnızca bağımsız ürün
        await ApiClient.AssertProblemAsync(
            await client.SendAsync(HttpMethod.Put, $"/api/v1/sessions/{session.Id}/draft", new { mode = "OutfitSet", items = new[] { new { productId = catalog.SalvarProduct } } }),
            HttpStatusCode.BadRequest, "WRONG_PRODUCT_TYPE");
        await ApiClient.AssertProblemAsync(
            await client.SendAsync(HttpMethod.Put, $"/api/v1/sessions/{session.Id}/draft", new { mode = "FreeCombination", items = new[] { new { productId = catalog.SetProduct } } }),
            HttpStatusCode.BadRequest, "WRONG_PRODUCT_TYPE");
        await ApiClient.AssertProblemAsync(
            await client.SendAsync(HttpMethod.Put, $"/api/v1/sessions/{session.Id}/draft", new { mode = "FreeCombination", items = new[] { new { productId = Guid.NewGuid() } } }),
            HttpStatusCode.BadRequest, "UNKNOWN_PRODUCT");
        await ApiClient.AssertProblemAsync(
            await client.SendAsync(HttpMethod.Put, $"/api/v1/sessions/{session.Id}/draft", new { mode = "FreeCombination", items = new[] { new { productId = catalog.SalvarProduct, variantId = Guid.NewGuid() } } }),
            HttpStatusCode.BadRequest, "UNKNOWN_VARIANT");

        var draft = await SaveDraftAsync(client, session.Id, OutfitMode.FreeCombination, catalog.SalvarProduct, catalog.GomlekProduct, catalog.YelekProduct, catalog.KemerProduct);
        Assert.Equal(4, draft.Items.Count);
        Assert.Empty(draft.Warnings);
        Assert.Contains(draft.Items, i => i.CategoryId == catalog.Kemer && i.ProductId == catalog.KemerProduct);

        // Seçimler kategori bazlı saklanır ve yeniden okunur
        var detail = await client.GetAsync<SessionDetail>($"/api/v1/sessions/{session.Id}");
        Assert.Equal(4, detail.Draft!.Items.Count);

        // Mod değişince taslak yeniden yazılır; kategori çakışması uyarısı GenerationTests'te sınanır
        var set = await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        Assert.Single(set.Items);
    }

    [Fact]
    public async Task Kapatilan_oturuma_fotograf_taslak_veya_onay_eklenemez()
    {
        var (_, client) = await StaffAsync();
        var (session, _) = await PreparedSessionAsync(client);

        var closed = await client.PostAsync<SessionDetail>($"/api/v1/sessions/{session.Id}/close");

        Assert.Equal(SessionStatus.Closed, closed.Status);
        Assert.Equal(SessionStep.Closed, closed.NextStep);
        await ApiClient.AssertProblemAsync(await client.UploadAsync($"/api/v1/sessions/{session.Id}/photos", TestImages.Portrait()), HttpStatusCode.Conflict, "SESSION_CLOSED");
        await ApiClient.AssertProblemAsync(
            await client.SendAsync(HttpMethod.Put, $"/api/v1/sessions/{session.Id}/draft", new { mode = "OutfitSet", items = Array.Empty<object>() }), HttpStatusCode.Conflict, "SESSION_CLOSED");
        Assert.Single((await client.GetAsync<Page<SessionSummary>>("/api/v1/sessions?status=closed")).Items);
        Assert.Empty((await client.GetAsync<Page<SessionSummary>>("/api/v1/sessions?status=open")).Items);
    }

    [Fact]
    public async Task Saklama_suresi_varsayilan_24_saat_ek_onayla_30_gun_olur()
    {
        var (_, client) = await StaffAsync();

        var normal = await CreateSessionAsync(client);
        var extended = await CreateSessionAsync(client);
        await ConsentAsync(client, extended.Id, extended: true);
        extended = await client.GetAsync<SessionDetail>($"/api/v1/sessions/{extended.Id}");

        Assert.InRange((normal.RetentionUntil - DateTimeOffset.UtcNow).TotalHours, 23.9, 24.1);
        Assert.InRange((extended.RetentionUntil - DateTimeOffset.UtcNow).TotalDays, 29.9, 30.1);
        Assert.True(extended.ExtendedRetention);
    }

    [Fact]
    public async Task Onayin_geri_alinmasi_oturumu_ve_dosyalari_aninda_siler()
    {
        var (tenant, client) = await StaffAsync();
        var (session, photo) = await PreparedSessionAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(photo.Urls.Url)).StatusCode);

        var response = await client.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{session.Id}/consent");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await ApiClient.AssertProblemAsync(await client.GetAsync($"/api/v1/sessions/{session.Id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(photo.Urls.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(photo.Urls.ThumbUrl)).StatusCode);
        await AssertNoTraceAsync(tenant.OrganizationId, session.Id, "Session.PurgedByConsentWithdrawal");
    }

    [Fact]
    public async Task Personel_oturumu_silince_butun_kisisel_veri_kalkar()
    {
        var (tenant, client) = await StaffAsync();
        var (session, photo) = await PreparedSessionAsync(client);

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{session.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(photo.Urls.Url)).StatusCode);
        await AssertNoTraceAsync(tenant.OrganizationId, session.Id, "Session.PurgedByStaff");
    }

    [Fact]
    public async Task Saklama_suresi_dolan_oturum_otomatik_silinir_digerleri_kalir()
    {
        var (tenant, client) = await StaffAsync();
        var (expired, expiredPhoto) = await PreparedSessionAsync(client);
        var (alive, alivePhoto) = await PreparedSessionAsync(client);
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.TryOnSessions.IgnoreQueryFilters().Where(s => s.Id == expired.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RetentionUntil, DateTimeOffset.UtcNow.AddMinutes(-1)), TestContext.Current.CancellationToken);
        }

        var purged = await Services.GetRequiredService<RetentionSweeper>().SweepAsync(TestContext.Current.CancellationToken);

        Assert.True(purged >= 1);
        await ApiClient.AssertProblemAsync(await client.GetAsync($"/api/v1/sessions/{expired.Id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(expiredPhoto.Urls.Url)).StatusCode);
        Assert.Equal(alive.Id, (await client.GetAsync<SessionDetail>($"/api/v1/sessions/{alive.Id}")).Id);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(alivePhoto.Urls.Url)).StatusCode);
        await AssertNoTraceAsync(tenant.OrganizationId, expired.Id, "Session.PurgedByRetention");
    }

    /// <summary>Oturuma ait hiçbir kayıt, dosya metadata'sı ve depo dosyası kalmamalı; yalnızca kişisel veri içermeyen denetim kaydı kalır.</summary>
    private async Task AssertNoTraceAsync(Guid organizationId, Guid sessionId, string auditAction)
    {
        var (scope, db) = OpenDb();
        using (scope)
        {
            var ct = TestContext.Current.CancellationToken;
            Assert.False(await db.TryOnSessions.IgnoreQueryFilters().AnyAsync(s => s.Id == sessionId, ct));
            Assert.False(await db.SessionPhotos.IgnoreQueryFilters().AnyAsync(p => p.SessionId == sessionId, ct));
            Assert.False(await db.ConsentRecords.IgnoreQueryFilters().AnyAsync(c => c.SessionId == sessionId, ct));
            Assert.False(await db.StoredFiles.IgnoreQueryFilters().AnyAsync(f => f.OrganizationId == organizationId && f.StorageKey.Contains(sessionId.ToString("N")), ct));
            Assert.True(await db.AuditLogs.AnyAsync(a => a.EntityId == sessionId && a.Action == auditAction, ct));
        }
    }
}
