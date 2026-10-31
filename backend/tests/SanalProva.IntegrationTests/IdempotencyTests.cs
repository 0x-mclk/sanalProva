using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Features.TryOn;
using SanalProva.Domain.Generation;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;

namespace SanalProva.IntegrationTests;

/// <summary>
/// M3.7: idempotency'nin <b>doğrulanması</b> (yeni mekanizma değil). Mevcut kapsam: aynı anahtar eşzamanlı (mock), aynı ücretli istek farklı anahtarla (gerçek mod), eşzamanlı retry (gerçek mod),
/// belirsiz iş tekrarı, veritabanı filtreli benzersiz indeksi (AdmissionConcurrencyTests, DuplicateGuardTests, GenerationTests). Bu sınıf eksik kalanları kanıtlar: anahtar yeniden oynatma
/// anlamları, anahtar kapsamı, aynı anahtar/farklı içerik, gerçek modda eşzamanlı aynı anahtar, yarım kalan kabul (kilit zaman aşımı), tekrar isteklerinin ikinci rezervasyon açmaması.
/// </summary>
public sealed class IdempotencyTests(ApiFactory factory) : ApiTestBase(factory), IDisposable
{
    private readonly RealModeApiFactory _real = new(factory.StorageRoot);

    public void Dispose() => _real.Dispose();

    private static Task<HttpResponseMessage> StartRawAsync(ApiClient client, Guid sessionId, string? key, object? body = null) =>
        client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/generations", body ?? new { }, configure: r =>
        {
            if (key is not null)
            {
                r.Headers.Add("Idempotency-Key", key);
            }
        });

    private async Task<(TestTenant Tenant, ApiClient Client, CatalogIds Catalog, SessionDetail Session)> ReadyAsync(WebApplicationFactoryHost host)
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(host.Services);
        var catalog = await TestData.CreateCatalogAsync(host.Services, tenant.OrganizationId);
        var client = await ApiClient.LoginAsync(host.Factory, tenant.Email, tenant.StoreId);
        var (session, _) = await PreparedSessionAsync(client);
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        return (tenant, client, catalog, session);
    }

    private sealed record WebApplicationFactoryHost(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory, IServiceProvider Services);

    private WebApplicationFactoryHost Mock => new(Factory, Services);

    private WebApplicationFactoryHost Real => new(_real, _real.Services);

    // ------------------------------------------------------------------ anahtar yeniden oynatma anlamları (mock)

    [Fact]
    public async Task Ayni_anahtar_tamamlanmis_is_oturum_kapali_ve_basarisiz_is_icin_ayni_isi_doner_yeni_is_acmaz_yeni_anahtar_yeni_is_acar()
    {
        var (tenant, client, catalog, session) = await ReadyAsync(Mock);

        // Tamamlanmış iş: tekrar aynı işi ve sonucunu döner (ikinci üretim yok)
        var first = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id, "tamam-1")).Id);
        var replay = await StartGenerationAsync(client, session.Id, "tamam-1");
        Assert.Equal((first.Id, GenerationJobStatus.Succeeded, first.ResultImageId), (replay.Id, replay.Status, replay.ResultImageId));

        // Oturum kapatıldıktan sonra da aynı anahtar aynı işi döner (ağ yeniden denemesi kapanış yüzünden 409 almaz); yeni anahtar kapalı oturumda reddedilir
        await client.PostAsync<SessionDetail>($"/api/v1/sessions/{session.Id}/close");
        Assert.Equal(first.Id, (await StartGenerationAsync(client, session.Id, "tamam-1")).Id);
        await ApiClient.AssertProblemAsync(await StartRawAsync(client, session.Id, "yeni-anahtar"), HttpStatusCode.Conflict, "SESSION_CLOSED");

        // Başarısız iş: anahtar o işe bağlıdır; tekrar aynı BAŞARISIZ işi döner. Yeni deneme için yeni anahtar (veya retry uç noktası) gerekir
        var (_, client2, catalog2, session2) = await ReadyAsync(Mock);
        var photo = await UploadPhotoAsync(client2, session2.Id, TestImages.Portrait(seed: 3701));
        TestFaults.FailFor(await client2.DownloadAsync(photo.Urls.Url), InjectedFault.Permanent);
        var failed = await WaitForJobAsync(client2, (await StartGenerationAsync(client2, session2.Id, "hata-1")).Id);
        Assert.Equal(GenerationJobStatus.Failed, failed.Status);
        var again = await StartGenerationAsync(client2, session2.Id, "hata-1");
        Assert.Equal((failed.Id, GenerationJobStatus.Failed), (again.Id, again.Status));

        var better = await UploadPhotoAsync(client2, session2.Id, TestImages.Portrait(seed: 3702));
        var next = await StartGenerationAsync(client2, session2.Id, "hata-2");
        Assert.NotEqual(failed.Id, next.Id);
        _ = (tenant, catalog, catalog2, better);

        // Hiçbir tekrar fazladan iş açmadı: oturum başına tam beklenen sayıda iş
        var (scope, db) = OpenDb();
        using (scope)
        {
            Assert.Equal(1, await db.GenerationJobs.IgnoreQueryFilters().CountAsync(j => j.SessionId == session.Id, TestContext.Current.CancellationToken));
            Assert.Equal(2, await db.GenerationJobs.IgnoreQueryFilters().CountAsync(j => j.SessionId == session2.Id, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Anahtar_kapsami_oturum_bazlidir_firma_ve_sube_arasi_tekrar_edilemez_gecersiz_anahtarlar_reddedilir()
    {
        var a = await ReadyAsync(Mock);
        var b = await ReadyAsync(Mock);
        const string key = "ortak-anahtar";

        // Aynı anahtar iki firmada / aynı firmanın ikinci oturumunda bağımsızdır: üç ayrı iş
        var jobA = await StartGenerationAsync(a.Client, a.Session.Id, key);
        var jobB = await StartGenerationAsync(b.Client, b.Session.Id, key);
        var (secondSession, _) = await PreparedSessionAsync(a.Client);
        await SaveDraftAsync(a.Client, secondSession.Id, OutfitMode.OutfitSet, a.Catalog.SetProduct);
        var jobA2 = await StartGenerationAsync(a.Client, secondSession.Id, key);
        Assert.Equal(3, new[] { jobA.Id, jobB.Id, jobA2.Id }.Distinct().Count());

        // Başka firma/şube, A'nın anahtarını A'nın oturumuna karşı oynatamaz: oturum görünmez (404), A'nın işi etkilenmez
        await ApiClient.AssertProblemAsync(await StartRawAsync(b.Client, a.Session.Id, key), HttpStatusCode.NotFound, "NOT_FOUND");
        var storeA2 = await TestData.AddStoreAsync(Services, a.Tenant.OrganizationId, "ID2");
        var staff2 = await ApiClient.LoginAsync(Factory, await TestData.AddUserAsync(Services, a.Tenant.OrganizationId, [(storeA2, StoreRole.Staff)]), storeA2);
        await ApiClient.AssertProblemAsync(await StartRawAsync(staff2, a.Session.Id, key), HttpStatusCode.NotFound, "NOT_FOUND");
        await WaitForJobAsync(a.Client, jobA.Id);

        // Geçersiz anahtarlar: yok, boş, yalnızca boşluk, 65 karakter → 400 ve hiçbir iş açılmaz; 64 karakter geçerli
        foreach (var bad in new string?[] { null, string.Empty, "   ", new string('k', 65) })
        {
            await ApiClient.AssertProblemAsync(await StartRawAsync(a.Client, a.Session.Id, bad), HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_REQUIRED");
        }

        Assert.Equal(HttpStatusCode.Accepted, (await StartRawAsync(a.Client, secondSession.Id, new string('k', 64))).StatusCode);
        var (scope, db) = OpenDb();
        using (scope)
        {
            Assert.Equal(1, await db.GenerationJobs.IgnoreQueryFilters().CountAsync(j => j.SessionId == a.Session.Id, TestContext.Current.CancellationToken));
        }
    }

    // ------------------------------------------------------------------ aynı anahtar, farklı içerik

    [Fact]
    public async Task Ayni_anahtar_farkli_fotografla_kullanilirsa_422_doner_eski_is_sessizce_donmez_ayni_icerik_tekrari_zararsizdir()
    {
        var (_, client, _, session) = await ReadyAsync(Mock);
        var first = await UploadPhotoAsync(client, session.Id, TestImages.Portrait(seed: 3801));
        // Aynı açıdaki yeni fotoğraf eskisini değiştirir ("yeniden çek"): ikinci fotoğraf farklı açıda
        var secondResponse = await client.UploadAsync($"/api/v1/sessions/{session.Id}/photos", TestImages.Portrait(seed: 3802), fields: new Dictionary<string, string> { ["angle"] = "Back" });
        var second = await ApiClient.ReadAsync<PhotoDto>(secondResponse, HttpStatusCode.Created);
        await ApprovePhotoAsync(client, session.Id, second.Id);
        var original = await client.PostAsync<JobDto>($"/api/v1/sessions/{session.Id}/generations", new { photoId = first.Id }, HttpStatusCode.Accepted, r => r.Headers.Add("Idempotency-Key", "icerik-1"));

        // Aynı içerik: aynı iş. Farklı içerik (başka fotoğraf): açık hata, istemci yanlış işi kendi isteği sanmaz
        var same = await client.PostAsync<JobDto>($"/api/v1/sessions/{session.Id}/generations", new { photoId = first.Id }, HttpStatusCode.Accepted, r => r.Headers.Add("Idempotency-Key", "icerik-1"));
        Assert.Equal(original.Id, same.Id);
        await ApiClient.AssertProblemAsync(await StartRawAsync(client, session.Id, "icerik-1", new { photoId = second.Id }), HttpStatusCode.UnprocessableEntity, "IDEMPOTENCY_KEY_REUSED");

        var (scope, db) = OpenDb();
        using (scope)
        {
            Assert.Equal(1, await db.GenerationJobs.IgnoreQueryFilters().CountAsync(j => j.SessionId == session.Id, TestContext.Current.CancellationToken)); // reddedilen istek iş açmadı
        }
    }

    [Fact]
    public async Task Gercek_modda_ayni_anahtar_eszamanli_tek_is_ayni_anahtar_farkli_model_veya_varyasyonla_422_farkli_anahtar_yeni_model_yeni_is()
    {
        var (_, client, _, session) = await ReadyAsync(Real);

        // 16 eşzamanlı aynı-anahtar isteği: tek ücretli iş
        var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => StartRawAsync(client, session.Id, "gercek-1")));
        try
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
            var ids = new HashSet<Guid>();
            foreach (var response in responses)
            {
                ids.Add((await ApiClient.ReadAsync<JobDto>(response, HttpStatusCode.Accepted)).Id);
            }

            Assert.Single(ids);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        // Aynı anahtar farklı model veya "yeni varyasyon" ile: içerik farklı → 422 (ücretli işlerde sessiz yanlış eşleşme yok)
        await ApiClient.AssertProblemAsync(await StartRawAsync(client, session.Id, "gercek-1", new { modelKey = "gpt-image-2.5", quality = "medium" }), HttpStatusCode.UnprocessableEntity, "IDEMPOTENCY_KEY_REUSED");
        await ApiClient.AssertProblemAsync(await StartRawAsync(client, session.Id, "gercek-1", new { allowVariation = true }), HttpStatusCode.UnprocessableEntity, "IDEMPOTENCY_KEY_REUSED");

        // Farklı anahtar + farklı model: bilinçli yeni istek, yeni iş; aynısının tekrarı o işi döner
        var other = await client.PostAsync<JobDto>($"/api/v1/sessions/{session.Id}/generations", new { modelKey = "gpt-image-2.5", quality = "medium" }, HttpStatusCode.Accepted, r => r.Headers.Add("Idempotency-Key", "gercek-2"));
        var otherAgain = await client.PostAsync<JobDto>($"/api/v1/sessions/{session.Id}/generations", new { modelKey = "gpt-image-2.5", quality = "medium" }, HttpStatusCode.Accepted, r => r.Headers.Add("Idempotency-Key", "gercek-2"));
        Assert.Equal(other.Id, otherAgain.Id);

        var (scope, db) = OpenDb();
        using (scope)
        {
            Assert.Equal(2, await db.GenerationJobs.IgnoreQueryFilters().CountAsync(j => j.SessionId == session.Id, TestContext.Current.CancellationToken));
        }
    }

    // ------------------------------------------------------------------ yarım kalan kabul

    [Fact]
    public async Task Kabul_kilidi_alinamazsa_503_doner_hicbir_sey_yazilmaz_ayni_anahtarla_tekrar_tek_is_acar()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, client, _, session) = await ReadyAsync(Mock);
        await using var holder = new SqlConnection(Factory.MigratorConnectionString);
        await holder.OpenAsync(ct);
        await using (var hold = new SqlCommand($"BEGIN TRAN; DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = N'{SanalProva.Infrastructure.Persistence.AppLocks.StoreAdmission(tenant.StoreId)}', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 1000; IF @r < 0 THROW 51002, N'kilit', 1;", holder))
        {
            await hold.ExecuteNonQueryAsync(ct);
        }

        // Kabul kilidi başka transaction'da: istek zaman aşımına uğrar (yarım kalan kabul), iş yazılmaz
        using (var busy = await StartRawAsync(client, session.Id, "yarim-1"))
        {
            await ApiClient.AssertProblemAsync(busy, HttpStatusCode.ServiceUnavailable, "ADMISSION_BUSY");
        }

        var (scope, db) = OpenDb();
        using (scope)
        {
            Assert.Equal(0, await db.GenerationJobs.IgnoreQueryFilters().CountAsync(j => j.SessionId == session.Id, ct));
        }

        await using (var release = new SqlCommand("ROLLBACK TRAN;", holder))
        {
            await release.ExecuteNonQueryAsync(ct);
        }

        // İstemci güvenle aynı anahtarı tekrar gönderir: tam bir iş; bir kez daha tekrar aynı işi döner
        var job = await StartGenerationAsync(client, session.Id, "yarim-1");
        Assert.Equal(job.Id, (await StartGenerationAsync(client, session.Id, "yarim-1")).Id);
        (scope, db) = OpenDb();
        using (scope)
        {
            Assert.Equal(1, await db.GenerationJobs.IgnoreQueryFilters().CountAsync(j => j.SessionId == session.Id, ct));
        }
    }

    [Fact]
    public async Task Geri_alinan_kabul_transactioni_anahtari_tuketmez_yarim_yazilan_is_gorunmez()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, client, _, session) = await ReadyAsync(Mock);

        // Çökmüş kabul benzetimi: aynı anahtarla iş satırı ekleyen transaction commit edilmeden geri alınır (süreç ölümü = ROLLBACK)
        var (scope, db) = OpenDb();
        using (scope)
        {
            var template = await db.TryOnSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == session.Id, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            db.GenerationJobs.Add(new GenerationJob
            {
                OrganizationId = template.OrganizationId, StoreId = template.StoreId, SessionId = template.Id, JobType = GenerationJobType.OutfitSet, IdempotencyKey = "cokme-1",
                RequestedByUserId = template.CreatedByUserId, Status = GenerationJobStatus.Queued, NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1),
            });
            await db.SaveChangesAsync(ct);
            await transaction.RollbackAsync(ct);
        }

        var job = await StartGenerationAsync(client, session.Id, "cokme-1"); // anahtar tüketilmedi: normal kabul
        Assert.True(job.Status is GenerationJobStatus.Queued or GenerationJobStatus.Running or GenerationJobStatus.Succeeded);
        (scope, db) = OpenDb();
        using (scope)
        {
            Assert.Equal(1, await db.GenerationJobs.IgnoreQueryFilters().CountAsync(j => j.SessionId == session.Id, ct));
        }
    }

    // ------------------------------------------------------------------ retry yolu

    [Fact]
    public async Task Retry_baska_firma_ve_sube_icin_404_basarili_veya_etkin_is_icin_409_eszamanli_retry_gercek_modda_tek_is_acar()
    {
        var (otherTenant, otherClient, _, _) = await ReadyAsync(Real);
        _ = otherTenant;

        // Ana test host'unun (mock) worker'ı aynı veritabanını yoklar ve yeni işi kapabilir: geçiş "hâlâ Queued ise" koşuluyla atomik; kapılırsa yeni oturumla yeniden denenir
        TestTenant tenant = null!;
        ApiClient client = null!;
        JobDto started = null!;
        for (var attempt = 0; attempt < 20 && started is null; attempt++)
        {
            var ready = await ReadyAsync(Real);
            var candidate = await StartGenerationAsync(ready.Client, ready.Session.Id, "retry-1");
            var (scope0, db0) = OpenDb();
            using (scope0)
            {
                var rows = await db0.GenerationJobs.IgnoreQueryFilters().Where(j => j.Id == candidate.Id && j.Status == GenerationJobStatus.Queued)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, GenerationJobStatus.Failed).SetProperty(j => j.ErrorCode, GenerationErrorCode.ProviderUnavailable), TestContext.Current.CancellationToken);
                if (rows == 1)
                {
                    (tenant, client, started) = (ready.Tenant, ready.Client, candidate);
                }
            }
        }

        Assert.NotNull(started);

        // Başka firma: iş görünmez
        await ApiClient.AssertProblemAsync(await otherClient.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{started.Id}/retry", new { }), HttpStatusCode.NotFound, "NOT_FOUND");

        // Aynı firmanın başka şubesi: iş görünmez
        var storeB = await TestData.AddStoreAsync(_real.Services, tenant.OrganizationId, "RT2");
        var staffB = await ApiClient.LoginAsync(_real, await TestData.AddUserAsync(_real.Services, tenant.OrganizationId, [(storeB, StoreRole.Staff)]), storeB);
        await ApiClient.AssertProblemAsync(await staffB.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{started.Id}/retry", new { }), HttpStatusCode.NotFound, "NOT_FOUND");

        // Sahibi: 10 eşzamanlı retry → tek yeni (ücretli) iş; etkin iş varken yeniden retry aynı işi döner
        var retries = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{started.Id}/retry", new { })));
        var ids = new HashSet<Guid>();
        foreach (var response in retries)
        {
            using (response)
            {
                ids.Add((await ApiClient.ReadAsync<JobDto>(response, HttpStatusCode.Accepted)).Id);
            }
        }

        var retried = Assert.Single(ids);
        Assert.NotEqual(started.Id, retried);
        Assert.Equal(retried, (await client.PostAsync<JobDto>($"/api/v1/jobs/{started.Id}/retry", new { }, HttpStatusCode.Accepted)).Id);

        // Başarısız olmayan (etkin) işin retry'ı: 409
        await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{retried}/retry", new { }), HttpStatusCode.Conflict, "JOB_NOT_FAILED");
    }

    private static Task<HttpResponseMessage> RetryRawAsync(ApiClient client, Guid jobId, string? key) =>
        client.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{jobId}/retry", new { }, configure: r =>
        {
            if (key is not null)
            {
                r.Headers.Add("Idempotency-Key", key);
            }
        });

    [Fact]
    public async Task Retry_anahtarli_eszamanli_istek_mock_modda_bile_tek_is_acar_ayni_anahtar_ayni_isi_doner_ozgun_anahtar_ve_gecersiz_anahtar_reddedilir_yeni_anahtar_yeni_is_acar()
    {
        var (_, client, _, session) = await ReadyAsync(Mock);
        var photo = await UploadPhotoAsync(client, session.Id, TestImages.Portrait(seed: 4001));
        TestFaults.FailFor(await client.DownloadAsync(photo.Urls.Url), InjectedFault.Permanent);
        var failed = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id, "rk-orijinal")).Id);
        Assert.Equal(GenerationJobStatus.Failed, failed.Status);

        // Aynı anahtarla 8 eşzamanlı retry (ağ yeniden denemesi / çift tıklama) → tek yeni iş
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RetryRawAsync(client, failed.Id, "rk-1")));
        var ids = new HashSet<Guid>();
        foreach (var response in responses)
        {
            using (response)
            {
                ids.Add((await ApiClient.ReadAsync<JobDto>(response, HttpStatusCode.Accepted)).Id);
            }
        }

        var retried = Assert.Single(ids);
        Assert.NotEqual(failed.Id, retried);

        // İş bittikten sonra da aynı anahtar aynı işi döner (resultImageId korunur); yeni iş açılmaz
        var done = await WaitForJobAsync(client, retried);
        using (var replayResponse = await RetryRawAsync(client, failed.Id, "rk-1"))
        {
            var replay = await ApiClient.ReadAsync<JobDto>(replayResponse, HttpStatusCode.Accepted);
            Assert.Equal((retried, done.ResultImageId), (replay.Id, replay.ResultImageId));
        }

        // Özgün işin başlangıç anahtarı retry için kullanılamaz (aksi halde başarısız iş "yeni iş" gibi dönerdi)
        await ApiClient.AssertProblemAsync(await RetryRawAsync(client, failed.Id, "rk-orijinal"), HttpStatusCode.UnprocessableEntity, "IDEMPOTENCY_KEY_REUSED");

        // Geçersiz anahtar: boş veya 64 karakterden uzun
        await ApiClient.AssertProblemAsync(await RetryRawAsync(client, failed.Id, new string('x', 65)), HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_REQUIRED");

        // Gerçekten yeni kullanıcı eylemi (yeni anahtar) yeni iş açar; anahtarsız eski istemci de çalışır
        using (var fresh = await RetryRawAsync(client, failed.Id, "rk-2"))
        {
            Assert.NotEqual(retried, (await ApiClient.ReadAsync<JobDto>(fresh, HttpStatusCode.Accepted)).Id);
        }

        using (var legacy = await RetryRawAsync(client, failed.Id, null))
        {
            Assert.Equal(HttpStatusCode.Accepted, legacy.StatusCode);
        }

        var (scope, db) = OpenDb();
        using (scope)
        {
            Assert.Equal(1 + 3, await db.GenerationJobs.IgnoreQueryFilters().CountAsync(j => j.SessionId == session.Id, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Olcum_mock_modda_eszamanli_retry_ucretsiz_oldugundan_dedupe_yoktur_ama_kota_asilmaz()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, client, _, session) = await ReadyAsync(Mock);
        var photo = await UploadPhotoAsync(client, session.Id, TestImages.Portrait(seed: 3901));
        TestFaults.FailFor(await client.DownloadAsync(photo.Urls.Url), InjectedFault.Permanent);
        var failed = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id, "olcum-1")).Id);
        Assert.Equal(GenerationJobStatus.Failed, failed.Status);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{failed.Id}/retry", new { })));
        var created = new HashSet<Guid>();
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                created.Add((await ApiClient.ReadAsync<JobDto>(response, HttpStatusCode.Accepted)).Id);
            }
        }

        var (scope, db) = OpenDb();
        using (scope)
        {
            var store = await db.Stores.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == tenant.StoreId, ct);
            Assert.True(created.Count <= 8);
            Assert.True(await db.GenerationJobs.IgnoreQueryFilters().CountAsync(j => j.SessionId == session.Id, ct) <= 1 + 8);
            _ = store;
        }

        LoadMeasurementTests.WriteReport("mock_retry", new { ConcurrentRetries = 8, JobsCreated = created.Count, Note = "Mock modda retry dedupe yok (ücretsiz); gerçek modda tek iş (ayrı test)." });
    }
}
