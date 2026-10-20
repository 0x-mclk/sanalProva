using System.Net;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SanalProva.Api.Features.TryOn;
using SanalProva.Api.Hubs;
using SanalProva.Domain.Generation;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;
using SkiaSharp;

namespace SanalProva.IntegrationTests;

/// <summary>Mock sağlayıcı ile uçtan uca üretim hattı: kuyruk, worker, sürümleme, hata/yeniden deneme, kota, bildirim.</summary>
public sealed class GenerationTests(ApiFactory factory) : ApiTestBase(factory)
{
    private async Task<(TestTenant Tenant, ApiClient Client, CatalogIds Catalog, SessionDetail Session, PhotoDto Photo)> ReadySessionAsync()
    {
        var (tenant, client) = await StaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        var (session, photo) = await PreparedSessionAsync(client);
        return (tenant, client, catalog, session, photo);
    }

    [Fact]
    public async Task Hazir_takim_uretimi_demo_isaretli_sonuc_uretir_ve_maliyet_kaydi_birakir()
    {
        var (tenant, client, catalog, session, photo) = await ReadySessionAsync();
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);

        var started = await StartGenerationAsync(client, session.Id);
        Assert.Equal(GenerationJobType.OutfitSet, started.JobType);
        Assert.Contains(started.Status, new[] { GenerationJobStatus.Queued, GenerationJobStatus.Running, GenerationJobStatus.Succeeded });

        var job = await WaitForJobAsync(client, started.Id);

        Assert.Equal(GenerationJobStatus.Succeeded, job.Status);
        Assert.Equal("Mock", job.Provider);
        Assert.Equal("tryon-max", job.Model); // gerçek model yapılandırmadan, sağlayıcı mock
        Assert.True(job.IsDemo);
        Assert.NotNull(job.ResultImageId);

        var detail = await client.GetAsync<SessionDetail>($"/api/v1/sessions/{session.Id}");
        Assert.Equal(SessionStep.Result, detail.NextStep);
        var image = Assert.Single(detail.Images);
        Assert.True(image.IsDemo);
        Assert.False(image.IsColorPreview);
        Assert.Equal(1, image.VersionNo);
        Assert.Contains("Sultan Bindallı", image.Label, StringComparison.Ordinal);

        // Görsel gerçekten filigranlı: alt bant bordo, boyut müşteri fotoğrafıyla aynı
        var bytes = await client.DownloadAsync(image.Urls.Url);
        using var bitmap = SKBitmap.Decode(bytes);
        Assert.Equal((photo.Width, photo.Height), (bitmap.Width, bitmap.Height));
        var corner = bitmap.GetPixel(3, bitmap.Height - 3);
        Assert.True(corner.Red > corner.Green + 40, $"DEMO bandı bordo olmalı: {corner}");

        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var log = await db.AiUsageLogs.SingleAsync(l => l.JobId == job.Id, TestContext.Current.CancellationToken);
            Assert.True(log.Success);
            Assert.Equal("Mock", log.Provider);
            Assert.Equal(0m, log.EstimatedCostUsd);
            Assert.True((await db.GenerationJobs.SingleAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken)).PromptTemplateVersion is not null);
        }
    }

    [Fact]
    public async Task Serbest_kombin_uretimi_kategori_sirasiyla_etiketlenir_ve_surum_artar()
    {
        var (_, client, catalog, session, _) = await ReadySessionAsync();
        await SaveDraftAsync(client, session.Id, OutfitMode.FreeCombination, catalog.KemerProduct, catalog.SalvarProduct, catalog.GomlekProduct, catalog.YelekProduct);

        var first = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id)).Id);
        var second = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id)).Id);

        Assert.Equal(GenerationJobStatus.Succeeded, first.Status);
        Assert.Equal(GenerationJobType.FreeCombination, first.JobType);
        Assert.Equal("gemini-3-pro-image", first.Model);
        var images = await client.GetAsync<List<GeneratedImageDto>>($"/api/v1/sessions/{session.Id}/images");
        Assert.Equal([1, 2], images.Select(i => i.VersionNo));
        // Katman sırası: içten dışa (şalvar, gömlek → yelek → kemer)
        Assert.Equal("Kombin: Salvar + Gomlek + Yelek + Kemer", images[0].Label);
        Assert.Equal(first.ResultImageId, images[0].Id);
        Assert.Equal(second.ResultImageId, images[1].Id);
        Assert.NotEqual(images[0].Urls.Url, images[1].Urls.Url);
    }

    [Fact]
    public async Task Ayni_idempotency_anahtari_tek_is_olusturur()
    {
        var (tenant, client, catalog, session, _) = await ReadySessionAsync();
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => StartGenerationAsync(client, session.Id, "ayni-anahtar")));

        Assert.Single(results.Select(r => r.Id).Distinct());
        await WaitForJobAsync(client, results[0].Id);
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            Assert.Equal(1, await db.GenerationJobs.CountAsync(j => j.SessionId == session.Id, TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.GeneratedImages.CountAsync(i => i.SessionId == session.Id, TestContext.Current.CancellationToken));
        }

        var missing = await client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{session.Id}/generations", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Eksik_veya_gecersiz_secim_uretimi_baslatamaz()
    {
        var (tenant, client, catalog, session, _) = await ReadySessionAsync();
        async Task<HttpResponseMessage> Start() => await client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{session.Id}/generations", new { },
            configure: r => r.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N")));

        await ApiClient.AssertProblemAsync(await Start(), HttpStatusCode.BadRequest, "EMPTY_SELECTION");

        // Kategori çakışması: şalvar ve gömlek aynı anda seçilemez
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var salvar = await db.Categories.SingleAsync(c => c.Id == catalog.Salvar, TestContext.Current.CancellationToken);
            salvar.ConflictsWith = [catalog.Gomlek];
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var draft = await SaveDraftAsync(client, session.Id, OutfitMode.FreeCombination, catalog.SalvarProduct, catalog.GomlekProduct);
        Assert.Equal(["ConflictingCategories"], draft.Warnings);
        var response = await Start();
        await ApiClient.AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "INVALID_COMBINATION");
        Assert.Contains("ConflictingCategories", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fotografsiz_oturumda_ve_AI_referansi_silinen_urunle_uretim_reddedilir()
    {
        var (tenant, client) = await StaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        var session = await CreateSessionAsync(client);
        await ConsentAsync(client, session.Id);
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);

        await ApiClient.AssertProblemAsync(
            await client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{session.Id}/generations", new { }, configure: r => r.Headers.Add("Idempotency-Key", "a")),
            HttpStatusCode.Conflict, "NO_PHOTO");

        await UploadPhotoAsync(client, session.Id);
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            await db.ProductImages.Where(i => i.ProductId == catalog.SetProduct).ExecuteUpdateAsync(s => s.SetProperty(i => i.IsAiReference, false), TestContext.Current.CancellationToken);
        }

        await ApiClient.AssertProblemAsync(
            await client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{session.Id}/generations", new { }, configure: r => r.Headers.Add("Idempotency-Key", "b")),
            HttpStatusCode.UnprocessableEntity, "PRODUCT_NOT_READY");
    }

    [Fact]
    public async Task Gunluk_kota_dolunca_yeni_uretim_reddedilir()
    {
        var (tenant, client, catalog, session, _) = await ReadySessionAsync();
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.Stores.IgnoreQueryFilters().Where(s => s.Id == tenant.StoreId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DailyGenerationQuota, 1), TestContext.Current.CancellationToken);
        }

        await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id)).Id);

        await ApiClient.AssertProblemAsync(
            await client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{session.Id}/generations", new { }, configure: r => r.Headers.Add("Idempotency-Key", "kota")),
            HttpStatusCode.TooManyRequests, "QUOTA_EXCEEDED");
    }

    [Fact]
    public async Task Gecici_saglayici_hatasi_yeniden_denenir_kalici_olursa_basarisiz_olur_ve_tekrar_denenebilir()
    {
        var (tenant, client, catalog, session, _) = await ReadySessionAsync();
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        // Benzersiz fotoğraf: hata enjeksiyonu yalnızca bu testin fotoğrafını etkiler (özet statik tabloda tutulur)
        var photo = await UploadPhotoAsync(client, session.Id, TestImages.Portrait(seed: 1001));
        TestFaults.FailFor(await client.DownloadAsync(photo.Urls.Url), InjectedFault.Retryable);

        var job = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id)).Id);

        Assert.Equal(GenerationJobStatus.Failed, job.Status);
        Assert.Equal(3, job.Attempt); // MaxAttempts
        Assert.Equal(GenerationErrorCode.ProviderUnavailable, job.ErrorCode);
        Assert.True(job.Retryable);
        Assert.Equal("Üretim servisine şu an ulaşılamıyor. Lütfen tekrar deneyin.", job.Message);
        Assert.DoesNotContain("Enjekte", job.Message, StringComparison.Ordinal); // sağlayıcı ayrıntısı kullanıcıya sızmaz
        Assert.Null(job.ResultImageId);

        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var logs = await db.AiUsageLogs.Where(l => l.JobId == job.Id).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(3, logs.Count);
            Assert.All(logs, l => Assert.False(l.Success));
        }

        // Tekrar dene: yeni iş oluşur (aynı hata enjekte olduğu için yine başarısız olur ama yeni kayıttır)
        var retry = await client.PostAsync<JobDto>($"/api/v1/jobs/{job.Id}/retry", expected: HttpStatusCode.Accepted);
        Assert.NotEqual(job.Id, retry.Id);
        Assert.Equal(job.SessionId, retry.SessionId);
        await WaitForJobAsync(client, retry.Id);
    }

    [Fact]
    public async Task Kalici_hata_tekrar_denenmez_ve_basarili_tekrar_sonuc_uretir()
    {
        var (_, client, catalog, session, _) = await ReadySessionAsync();
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        var photo = await UploadPhotoAsync(client, session.Id, TestImages.Portrait(seed: 1002));
        TestFaults.FailFor(await client.DownloadAsync(photo.Urls.Url), InjectedFault.Permanent);

        var job = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id)).Id);

        Assert.Equal(GenerationJobStatus.Failed, job.Status);
        Assert.Equal(1, job.Attempt);
        Assert.Equal(GenerationErrorCode.ContentRejected, job.ErrorCode);
        Assert.False(job.Retryable);

        // Müşteri başka fotoğraf çekti (farklı baytlar) → tekrar üretim başarılı
        var better = await UploadPhotoAsync(client, session.Id, TestImages.Portrait(seed: 99));
        var next = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id)).Id);
        Assert.Equal(GenerationJobStatus.Succeeded, next.Status);
        Assert.NotEqual(Guid.Empty, better.Id);
        // Başarısız iş geçmişte durur
        Assert.Equal(2, (await client.GetAsync<SessionDetail>($"/api/v1/sessions/{session.Id}")).Jobs.Count);
    }

    [Fact]
    public async Task Kuyrukta_bekleyen_is_iptal_edilir_islenen_veya_biten_is_edilmez()
    {
        var (tenant, client, catalog, session, photo) = await ReadySessionAsync();
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        var done = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id)).Id);
        await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{done.Id}/cancel", new { }), HttpStatusCode.Conflict, "JOB_NOT_CANCELLABLE");

        // Worker'ın almayacağı (gelecekte çalışacak) bir iş
        Guid queuedId;
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var source = await db.GenerationJobs.AsNoTracking().SingleAsync(j => j.Id == done.Id, TestContext.Current.CancellationToken);
            var queued = new GenerationJob
            {
                OrganizationId = tenant.OrganizationId, StoreId = tenant.StoreId, SessionId = session.Id, JobType = GenerationJobType.OutfitSet,
                SelectionId = source.SelectionId, SourcePhotoId = photo.Id, IdempotencyKey = "gelecek", NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1), RequestedByUserId = source.RequestedByUserId,
            };
            db.GenerationJobs.Add(queued);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            queuedId = queued.Id;
        }

        var waiting = await client.GetAsync<JobDto>($"/api/v1/jobs/{queuedId}");
        Assert.Equal(GenerationJobStatus.Queued, waiting.Status);
        Assert.True(waiting.QueuePosition >= 1);

        var cancelled = await client.PostAsync<JobDto>($"/api/v1/jobs/{queuedId}/cancel");
        Assert.Equal(GenerationJobStatus.Cancelled, cancelled.Status);
        await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{queuedId}/cancel", new { }), HttpStatusCode.Conflict, "JOB_NOT_CANCELLABLE");
    }

    [Fact]
    public async Task Kilidi_dolan_calisan_is_yeniden_kuyruga_alinip_tamamlanir()
    {
        var (tenant, client, catalog, session, _) = await ReadySessionAsync();
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        var first = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id)).Id);

        // Çöken worker simülasyonu: iş "Running", kilit süresi geçmiş, sonuç henüz yazılmamış
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.GeneratedImages.IgnoreQueryFilters().Where(i => i.JobId == first.Id).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
            await db.GenerationJobs.IgnoreQueryFilters().Where(j => j.Id == first.Id).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, GenerationJobStatus.Running)
                .SetProperty(j => j.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(-5))
                .SetProperty(j => j.FinishedAt, (DateTimeOffset?)null), TestContext.Current.CancellationToken);
        }

        var recovered = await WaitForJobAsync(client, first.Id, j => j.Status == GenerationJobStatus.Succeeded && j.Attempt >= 2);

        Assert.Equal(GenerationJobStatus.Succeeded, recovered.Status);
        Assert.Equal(2, recovered.Attempt);
        Assert.NotNull(recovered.ResultImageId);
        Assert.Equal(tenant.StoreId, tenant.StoreId);
    }

    [Fact]
    public async Task Paralel_workerlar_her_isi_tam_bir_kez_isler()
    {
        var (tenant, client, catalog, session, _) = await ReadySessionAsync();
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);

        var started = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => StartGenerationAsync(client, session.Id)));
        var finished = await Task.WhenAll(started.Select(j => WaitForJobAsync(client, j.Id)));

        Assert.All(finished, j => Assert.Equal(GenerationJobStatus.Succeeded, j.Status));
        Assert.All(finished, j => Assert.Equal(1, j.Attempt)); // SKIP LOCKED: hiçbir iş iki kez alınmadı
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var versions = await db.GeneratedImages.Where(i => i.SessionId == session.Id).Select(i => i.VersionNo).OrderBy(v => v).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(Enumerable.Range(1, 8), versions); // benzersiz ve boşluksuz sürüm numaraları
        }
    }

    [Fact]
    public async Task Baska_subenin_personeli_isi_goremez_iptal_edemez_yeniden_deneyemez()
    {
        var (tenant, owner, catalog, session, _) = await ReadySessionAsync();
        await SaveDraftAsync(owner, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        var job = await WaitForJobAsync(owner, (await StartGenerationAsync(owner, session.Id)).Id);
        var image = (await owner.GetAsync<List<GeneratedImageDto>>($"/api/v1/sessions/{session.Id}/images")).Single();
        var storeB = await TestData.AddStoreAsync(Services, tenant.OrganizationId, "SB");
        var other = await ApiClient.LoginAsync(Factory, await TestData.AddUserAsync(Services, tenant.OrganizationId, [(storeB, StoreRole.Staff)]), storeB);

        await ApiClient.AssertProblemAsync(await other.GetAsync($"/api/v1/jobs/{job.Id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await other.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{job.Id}/cancel", new { }), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await other.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{job.Id}/retry", new { }), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await other.SendAsync(HttpMethod.Patch, $"/api/v1/images/{image.Id}/favorite", new { isFavorite = true }), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Favori_isaretlenir_ve_revizyon_motoru_acikca_henuz_etkin_degildir()
    {
        var (_, client, catalog, session, _) = await ReadySessionAsync();
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        var job = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id)).Id);

        var favorite = await client.PatchAsync<GeneratedImageDto>($"/api/v1/images/{job.ResultImageId}/favorite", new { isFavorite = true });
        Assert.True(favorite.IsFavorite);

        // Revizyon arayüzü var, motor Aşama 5'te: sahte sonuç üretilmez
        var response = await client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{session.Id}/revisions",
            new { baseImageId = job.ResultImageId, type = "Recolor", color = "kirik-beyaz" });
        await ApiClient.AssertProblemAsync(response, HttpStatusCode.NotImplemented, "NOT_IMPLEMENTED");
        Assert.Single(await client.GetAsync<List<GeneratedImageDto>>($"/api/v1/sessions/{session.Id}/images"));
    }

    [Fact]
    public async Task SignalR_yalnizca_yetkili_oturuma_katilir_ve_is_durumunu_iletir()
    {
        var (tenant, client, catalog, session, _) = await ReadySessionAsync();
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        var storeB = await TestData.AddStoreAsync(Services, tenant.OrganizationId, "SB");
        var outsider = await ApiClient.LoginAsync(Factory, await TestData.AddUserAsync(Services, tenant.OrganizationId, [(storeB, StoreRole.Staff)]), storeB);

        await using var owner = Connect(client.AuthCookie);
        await using var stranger = Connect(outsider.AuthCookie);
        var ownerMessages = new List<JobUpdatedMessage>();
        var strangerMessages = new List<JobUpdatedMessage>();
        owner.On<JobUpdatedMessage>("JobUpdated", m => { lock (ownerMessages) { ownerMessages.Add(m); } });
        stranger.On<JobUpdatedMessage>("JobUpdated", m => { lock (strangerMessages) { strangerMessages.Add(m); } });
        await owner.StartAsync(TestContext.Current.CancellationToken);
        await stranger.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(await owner.InvokeAsync<bool>("JoinSession", session.Id, TestContext.Current.CancellationToken));
        Assert.False(await stranger.InvokeAsync<bool>("JoinSession", session.Id, TestContext.Current.CancellationToken));

        var job = await StartGenerationAsync(client, session.Id);
        await WaitForJobAsync(client, job.Id);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (ownerMessages)
            {
                if (ownerMessages.Any(m => m.Status == "Succeeded"))
                {
                    break;
                }
            }

            await Task.Delay(30, TestContext.Current.CancellationToken);
        }

        lock (ownerMessages)
        {
            var seen = string.Join(" → ", ownerMessages.Select(m => $"{m.Status}/{m.Stage ?? "-"}{(m.ResultImageId is null ? string.Empty : "+img")}"));
            Assert.True(ownerMessages.Any(m => m.JobId == job.Id && m.Status == "Succeeded" && m.ResultImageId is not null), $"Başarı bildirimi gelmedi: {seen}");
            Assert.True(ownerMessages.Any(m => m.Stage == "Generating"), $"Aşama bildirimi gelmedi: {seen}");
        }

        lock (strangerMessages)
        {
            Assert.Empty(strangerMessages);
        }
    }

    /// <summary>Gerçek kullanımdaki WebSocket taşıması (TestServer WebSocket istemcisi). Long polling TestServer üzerinde yük altında bildirim kaçırabiliyor.</summary>
    private HubConnection Connect(string authCookie) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Factory.Server.BaseAddress, StaffHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.WebSockets;
                options.Headers["Cookie"] = authCookie;
                options.WebSocketFactory = async (context, ct) =>
                {
                    var socketClient = Factory.Server.CreateWebSocketClient();
                    socketClient.ConfigureRequest = request => request.Headers["Cookie"] = authCookie;
                    return await socketClient.ConnectAsync(context.Uri, ct);
                };
            })
            .Build();
}
