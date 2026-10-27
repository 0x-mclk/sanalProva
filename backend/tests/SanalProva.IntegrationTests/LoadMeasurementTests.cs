using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SanalProva.Api.Features.TryOn;
using SanalProva.Domain.Generation;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Generation;

namespace SanalProva.IntegrationTests;

/// <summary>
/// M3.6: yük altında ölçüm. Doğruluk değişmezleri (çift iş, çift maliyet, sürüm boşluğu, kota aşımı = sıfır) her çalıştırmada zorunlu kılınır; hız ve kilitlenme sayıları ise
/// <c>%TEMP%\sanalprova_m36_*.json</c> dosyalarına yazılır (tekrarlanabilir ölçüm raporu). Kilitlenme sayısı sunucu sayacından (<c>sys.dm_os_performance_counters</c>) okunur;
/// bunun için yönetici bağlantısı (<c>SANALPROVA_LAB_ADMIN</c>) gerekir, yoksa sayaç raporda boş kalır.
/// </summary>
public sealed class LoadMeasurementTests(ApiFactory factory) : ApiTestBase(factory)
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    internal static async Task<long?> DeadlockCounterAsync()
    {
        var admin = Environment.GetEnvironmentVariable("SANALPROVA_LAB_ADMIN");
        if (string.IsNullOrWhiteSpace(admin))
        {
            return null;
        }

        await using var connection = new SqlConnection(admin);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name = N'Number of Deadlocks/sec' AND instance_name = N'_Total'", connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static void WriteReport(string name, object report) =>
        File.WriteAllText(Path.Combine(Path.GetTempPath(), $"sanalprova_m36_{name}.json"), JsonSerializer.Serialize(report, Pretty));

    private async Task<(TestTenant Tenant, ApiClient Client, List<SessionDetail> Sessions)> FirmAsync(int sessions)
    {
        var (tenant, client) = await StaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        var list = new List<SessionDetail>();
        for (var i = 0; i < sessions; i++)
        {
            var (session, _) = await PreparedSessionAsync(client);
            await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
            list.Add(session);
        }

        return (tenant, client, list);
    }

    [Fact]
    public async Task Yuk_altinda_cok_firmali_isler_tam_bir_kez_islenir_surum_ve_maliyet_degismezleri_korunur_kilitlenme_ve_hiz_olculur()
    {
        const int firms = 6, sessionsPerFirm = 3, jobsPerSession = 5, extraWorkers = 12;
        var ct = TestContext.Current.CancellationToken;
        var world = new List<(TestTenant Tenant, ApiClient Client, List<SessionDetail> Sessions)>();
        for (var i = 0; i < firms; i++)
        {
            world.Add(await FirmAsync(sessionsPerFirm));
        }

        var stop = 0;
        var processor = Services.GetRequiredService<GenerationProcessor>();
        var workers = Enumerable.Range(0, extraWorkers).Select(_ => Task.Run(async () =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                if (!await processor.ProcessNextAsync(ct))
                {
                    await Task.Delay(5, ct);
                }
            }
        }, ct)).ToList();

        var deadlocksBefore = await DeadlockCounterAsync();
        var watch = Stopwatch.StartNew();
        var jobs = new List<(TestTenant Tenant, SessionDetail Session, JobDto Job)>();
        try
        {
            var started = await Task.WhenAll(world.SelectMany(w => w.Sessions.SelectMany(s => Enumerable.Range(0, jobsPerSession).Select(async _ => (w, s, Job: await StartGenerationAsync(w.Client, s.Id))))));
            var finished = await Task.WhenAll(started.Select(async x => (x.w.Tenant, x.s, Job: await WaitForJobAsync(x.w.Client, x.Job.Id, timeoutSeconds: 120))));
            jobs.AddRange(finished);
        }
        finally
        {
            Interlocked.Exchange(ref stop, 1);
            await Task.WhenAll(workers);
        }

        watch.Stop();
        var deadlocks = await DeadlockCounterAsync() is { } after && deadlocksBefore is { } before ? after - before : (long?)null;

        // Doğruluk değişmezleri: sıfır tolerans
        Assert.Equal(firms * sessionsPerFirm * jobsPerSession, jobs.Count);
        Assert.All(jobs, j => Assert.Equal(GenerationJobStatus.Succeeded, j.Job.Status));
        Assert.All(jobs, j => Assert.Equal(1, j.Job.Attempt)); // hiçbir iş iki worker'a verilmedi, hiçbiri yeniden denenmedi
        var maxAttempts = 0;
        foreach (var group in jobs.GroupBy(j => j.Session.Id))
        {
            var tenant = group.First().Tenant;
            var (scope, db) = OpenDb(tenant.OrganizationId);
            using (scope)
            {
                var versions = (await db.GeneratedImages.Where(i => i.SessionId == group.Key).Select(i => i.VersionNo).ToListAsync(ct)).Order().ToList();
                Assert.Equal(Enumerable.Range(1, jobsPerSession), versions); // benzersiz, boşluksuz sürüm
                var ids = group.Select(j => j.Job.Id).ToList();
                Assert.Equal(jobsPerSession, await db.AiUsageLogs.CountAsync(l => l.JobId != null && ids.Contains(l.JobId.Value), ct)); // iş başına tek maliyet kaydı
                maxAttempts = Math.Max(maxAttempts, group.Max(j => j.Job.Attempt));
            }
        }

        WriteReport("worker_load", new
        {
            Firms = firms, Sessions = firms * sessionsPerFirm, Jobs = jobs.Count, ExtraWorkers = extraWorkers, HostWorkers = 4,
            ElapsedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 2), JobsPerSecond = Math.Round(jobs.Count / watch.Elapsed.TotalSeconds, 1),
            ServerDeadlocksDuringRun = deadlocks, MaxAttemptObserved = maxAttempts, DuplicateJobs = 0, DuplicateCosts = 0, VersionGaps = 0,
        });
    }

    [Fact]
    public async Task Yuk_altinda_gunluk_sube_kotasi_ve_oturum_siniri_asilmaz_kabul_sayisi_tam_olarak_sinira_esittir()
    {
        var ct = TestContext.Current.CancellationToken;

        // A) Şube kotası 20; 5 oturum × 10 eşzamanlı istek = 50: tam 20 kabul (oturum sınırı 15, aşılmaz)
        var firm = await FirmAsync(5);
        var (scope, db) = OpenDb(firm.Tenant.OrganizationId);
        using (scope)
        {
            await db.Stores.Where(s => s.Id == firm.Tenant.StoreId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DailyGenerationQuota, 20), ct);
        }

        var deadlocksBefore = await DeadlockCounterAsync();
        var watch = Stopwatch.StartNew();
        var responses = await Task.WhenAll(firm.Sessions.SelectMany(s => Enumerable.Range(0, 10).Select(_ =>
            firm.Client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{s.Id}/generations", new { }, configure: r => r.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"))))));
        watch.Stop();
        try
        {
            Assert.Equal(20, responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
            Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Accepted), r => Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode));
            Assert.Equal(0, responses.Count(r => (int)r.StatusCode >= 500)); // kilit bekleme/zaman aşımı yok
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        (scope, db) = OpenDb(firm.Tenant.OrganizationId);
        using (scope)
        {
            Assert.Equal(20, await db.GenerationJobs.CountAsync(j => j.StoreId == firm.Tenant.StoreId, ct));
        }

        // B) Oturum sınırı 15; tek oturuma 40 eşzamanlı istek: tam 15 kabul
        var single = await FirmAsync(1);
        var burst = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ =>
            single.Client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{single.Sessions[0].Id}/generations", new { }, configure: r => r.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N")))));
        try
        {
            Assert.Equal(15, burst.Count(r => r.StatusCode == HttpStatusCode.Accepted));
            Assert.Equal(0, burst.Count(r => (int)r.StatusCode >= 500));
            Assert.All(burst.Where(r => r.StatusCode != HttpStatusCode.Accepted), r => Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode));
        }
        finally
        {
            foreach (var response in burst)
            {
                response.Dispose();
            }
        }

        var deadlocks = await DeadlockCounterAsync() is { } after && deadlocksBefore is { } before ? after - before : (long?)null;
        WriteReport("admission_load", new
        {
            StoreQuotaScenario = new { Requests = 50, Quota = 20, Accepted = 20, ElapsedMs = watch.ElapsedMilliseconds }, SessionLimitScenario = new { Requests = 40, Limit = 15, Accepted = 15 },
            ServerDeadlocksDuringRun = deadlocks, ServerErrors = 0, QuotaOvershoot = 0,
        });
    }
}
