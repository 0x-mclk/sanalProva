using System.Net;
using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Features.TryOn;
using SanalProva.Domain.Generation;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.IntegrationTests;

/// <summary>
/// R3 kabul kontrolü: şube kabul kilidi (sp_getapplock) + benzersiz indeks altında kota ve "aynı ücretli istek için tek iş" kuralları
/// eşzamanlı isteklerde aşılmaz. M2'de ölçülen değerler (kota 3 iken 11/5/10 kabul, aynı istek için 2/9/10 ücretli iş) burada ideal değerle
/// doğrulanır; her senaryo birden çok turda tekrarlanır.
/// </summary>
public sealed class AdmissionConcurrencyTests(ApiFactory factory) : ApiTestBase(factory), IDisposable
{
    private const int Rounds = 5;
    private const int ConcurrentRequests = 12;
    private readonly RealModeApiFactory _real = new(factory.StorageRoot);

    public void Dispose() => _real.Dispose();

    private static Task<HttpResponseMessage[]> FireAsync(ApiClient client, string url, object body) =>
        Task.WhenAll(Enumerable.Range(0, ConcurrentRequests).Select(_ =>
            client.SendAsync(HttpMethod.Post, url, body, configure: r => r.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N")))));

    private async Task<(TestTenant Tenant, ApiClient Client, SessionDetail Session)> ReadyAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, IServiceProvider services)
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(services);
        var catalog = await TestData.CreateCatalogAsync(services, tenant.OrganizationId);
        var client = await ApiClient.LoginAsync(host, tenant.Email, tenant.StoreId);
        var (session, _) = await PreparedSessionAsync(client);
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        return (tenant, client, session);
    }

    private static async Task AssertOnlyAsync(HttpResponseMessage[] responses, int accepted, HttpStatusCode rejectedStatus, string rejectedCode)
    {
        Assert.Equal(accepted, responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        foreach (var rejected in responses.Where(r => r.StatusCode != HttpStatusCode.Accepted))
        {
            await ApiClient.AssertProblemAsync(rejected, rejectedStatus, rejectedCode);
        }
    }

    /// <summary>
    /// Gerçek modda yeni bir iş açıp onu "Failed" yapar. Ana test host'unun (mock) worker'ı aynı veritabanını yoklar ve yeni açılan işi geçiş yapılmadan kapabilir; bu yüzden geçiş
    /// "yalnızca iş hâlâ Queued ise" koşuluyla atomik yapılır. İş kapılmışsa (koşul 0 satır) yeni oturumla yeniden denenir: yarış kaybı testi değil, düzeneği etkiler.
    /// </summary>
    private async Task<(ApiClient Client, SessionDetail Session, JobDto Job)> FailedJobAsync(GenerationErrorCode code, bool withSubmitIntent = false)
    {
        var ct = TestContext.Current.CancellationToken;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var (_, client, session) = await ReadyAsync(_real, _real.Services);
            var job = await StartGenerationAsync(client, session.Id);
            var (scope, db) = OpenDb();
            using (scope)
            {
                var intent = withSubmitIntent ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
                var rows = await db.GenerationJobs.IgnoreQueryFilters().Where(j => j.Id == job.Id && j.Status == GenerationJobStatus.Queued).ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, GenerationJobStatus.Failed).SetProperty(j => j.ErrorCode, code).SetProperty(j => j.ProviderSubmitStartedAt, intent), ct);
                if (rows == 1)
                {
                    return (client, session, job);
                }
            }
        }

        throw new TimeoutException("Ana worker her seferinde işi kapmadan önce başarısız yapılamadı.");
    }

    [Fact]
    public async Task Gunluk_sube_kotasi_eszamanli_isteklerde_asilmaz()
    {
        const int quota = 3;
        for (var round = 0; round < Rounds; round++)
        {
            var (tenant, client, session) = await ReadyAsync(Factory, Services);
            var (scope, db) = OpenDb(tenant.OrganizationId);
            using (scope)
            {
                await db.Stores.Where(s => s.Id == tenant.StoreId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DailyGenerationQuota, quota), TestContext.Current.CancellationToken);
            }

            var responses = await FireAsync(client, $"/api/v1/sessions/{session.Id}/generations", new { });
            try
            {
                await AssertOnlyAsync(responses, quota, HttpStatusCode.TooManyRequests, "QUOTA_EXCEEDED");
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            (scope, db) = OpenDb(tenant.OrganizationId);
            using (scope)
            {
                Assert.Equal(quota, await db.GenerationJobs.CountAsync(j => j.StoreId == tenant.StoreId, TestContext.Current.CancellationToken));
            }
        }
    }

    [Fact]
    public async Task Oturum_basina_uretim_siniri_eszamanli_isteklerde_asilmaz()
    {
        const int limit = 2;
        for (var round = 0; round < Rounds; round++)
        {
            var (tenant, client, session) = await ReadyAsync(Factory, Services);
            var (scope, db) = OpenDb(tenant.OrganizationId);
            using (scope)
            {
                var organization = await db.Organizations.SingleAsync(o => o.Id == tenant.OrganizationId, TestContext.Current.CancellationToken);
                organization.Settings.MaxGenerationsPerSession = limit;
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var responses = await FireAsync(client, $"/api/v1/sessions/{session.Id}/generations", new { });
            try
            {
                await AssertOnlyAsync(responses, limit, HttpStatusCode.TooManyRequests, "SESSION_QUOTA_EXCEEDED");
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }
    }

    [Fact]
    public async Task Gercek_modda_ayni_istek_eszamanli_gelince_tek_ucretli_is_acilir()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var (_, client, session) = await ReadyAsync(_real, _real.Services);

            // Farklı Idempotency-Key, aynı fotoğraf + seçim + model: hepsi aynı (tek) işi alır
            var started = await Task.WhenAll(Enumerable.Range(0, ConcurrentRequests).Select(_ => StartGenerationAsync(client, session.Id)));

            Assert.Single(started.Select(j => j.Id).Distinct());
            var (scope, db) = OpenDb();
            using (scope)
            {
                Assert.Equal(1, await db.GenerationJobs.IgnoreQueryFilters().CountAsync(j => j.SessionId == session.Id, TestContext.Current.CancellationToken));
            }
        }
    }

    [Fact]
    public async Task Gercek_modda_ayni_basarisiz_ise_eszamanli_yeniden_dene_tek_ucretli_is_acar()
    {
        for (var round = 0; round < 3; round++)
        {
            var (client, session, failed) = await FailedJobAsync(GenerationErrorCode.ProviderUnavailable);

            var retries = await Task.WhenAll(Enumerable.Range(0, ConcurrentRequests).Select(_ =>
                client.PostAsync<JobDto>($"/api/v1/jobs/{failed.Id}/retry", new { }, HttpStatusCode.Accepted)));

            var retried = Assert.Single(retries.Select(j => j.Id).Distinct());
            Assert.NotEqual(failed.Id, retried);
            var (scope, db) = OpenDb();
            using (scope)
            {
                Assert.Equal(2, await db.GenerationJobs.IgnoreQueryFilters().CountAsync(j => j.SessionId == session.Id, TestContext.Current.CancellationToken)); // başarısız + tek yeni
            }
        }
    }

    [Fact]
    public async Task Sonucu_belirsiz_is_ayni_istekte_doner_yeniden_dene_ve_varyasyon_bilincli_yeni_is_acar()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, session, uncertain) = await FailedJobAsync(GenerationErrorCode.OutcomeUncertain, withSubmitIntent: true);

        // Aynı istek (farklı Idempotency-Key, eşzamanlı): sağlayıcıda gerçekleşmiş olabilecek işlem ikinci kez ücretlendirilmez, aynı iş döner
        var repeated = await Task.WhenAll(Enumerable.Range(0, ConcurrentRequests).Select(_ => StartGenerationAsync(client, session.Id)));
        Assert.All(repeated, j => Assert.Equal(uncertain.Id, j.Id));
        var shown = repeated[0];
        Assert.Equal((GenerationJobStatus.Failed, GenerationErrorCode.OutcomeUncertain, true), (shown.Status, shown.ErrorCode, shown.Retryable));
        Assert.Contains("ikinci bir ücret", shown.Message, StringComparison.Ordinal); // personel riski görerek karar verir

        // Bilinçli yeniden deneme: tek yeni iş (eşzamanlı basışlarda da)
        var retries = await Task.WhenAll(Enumerable.Range(0, ConcurrentRequests).Select(_ =>
            client.PostAsync<JobDto>($"/api/v1/jobs/{uncertain.Id}/retry", new { }, HttpStatusCode.Accepted)));
        var retried = Assert.Single(retries.Select(j => j.Id).Distinct());
        Assert.NotEqual(uncertain.Id, retried);

        // Bilinçli varyasyon: ayrı (ücretli) iş
        var variation = await client.PostAsync<JobDto>($"/api/v1/sessions/{session.Id}/generations", new { allowVariation = true }, HttpStatusCode.Accepted,
            r => r.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N")));
        Assert.NotEqual(uncertain.Id, variation.Id);
        Assert.NotEqual(retried, variation.Id);

        var (scope, db) = OpenDb();
        using (scope)
        {
            Assert.Equal(3, await db.GenerationJobs.IgnoreQueryFilters().CountAsync(j => j.SessionId == session.Id, ct));
        }
    }

    [Fact]
    public async Task Kilit_atlanirsa_veritabani_ayni_ucretli_istek_icin_ikinci_etkin_isi_reddeder()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, client, session) = await ReadyAsync(_real, _real.Services);
        var first = await StartGenerationAsync(client, session.Id);

        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var original = await db.GenerationJobs.AsNoTracking().SingleAsync(j => j.Id == first.Id, ct);
            Assert.NotNull(original.DedupeKey); // gerçek mod + varyasyon istenmedi

            GenerationJob Copy(GenerationJobStatus status) => new()
            {
                OrganizationId = original.OrganizationId, StoreId = original.StoreId, SessionId = original.SessionId, JobType = original.JobType,
                SelectionId = original.SelectionId, SourcePhotoId = original.SourcePhotoId, RequestFingerprint = original.RequestFingerprint,
                DedupeKey = original.DedupeKey, ModelKey = original.ModelKey, Quality = original.Quality, IdempotencyKey = Guid.NewGuid().ToString("N"),
                RequestedByUserId = original.RequestedByUserId, NextAttemptAt = DateTimeOffset.UtcNow, Status = status,
            };

            // Başarısız iş filtreye girmez: aynı anahtarla eklenebilir (yeniden deneme bu yüzden mümkün)
            db.GenerationJobs.Add(Copy(GenerationJobStatus.Failed));
            await db.SaveChangesAsync(ct);

            // Etkin ikinci iş: kabul kilidi atlansa bile filtreli benzersiz indeks reddeder
            db.GenerationJobs.Add(Copy(GenerationJobStatus.Queued));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
            Assert.True(DbErrors.IsUniqueViolation(ex));
            Assert.Contains("ux_generation_jobs_active_dedupe", ex.InnerException?.Message, StringComparison.Ordinal);
        }
    }
}
