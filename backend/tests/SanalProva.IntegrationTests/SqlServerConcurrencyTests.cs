using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SanalProva.Api.Features.TryOn;
using SanalProva.Domain.Generation;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Generation;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.IntegrationTests;

/// <summary>
/// SQL Server'a uyarlanan iş kuyruğu (UPDLOCK + READPAST), oturum sürüm kilidi (sp_getapplock) ve kilitlenme (1205) tanımasının
/// eşzamanlılık altında doğrulanması.
/// </summary>
public sealed class SqlServerConcurrencyTests(ApiFactory factory) : ApiTestBase(factory)
{
    private const int JobsPerTenant = 10;
    private const int ExtraWorkers = 6;

    private async Task<(TestTenant Tenant, ApiClient Client, SessionDetail Session)> ReadyAsync()
    {
        var (tenant, client) = await StaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        var (session, _) = await PreparedSessionAsync(client);
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        return (tenant, client, session);
    }

    [Fact]
    public async Task Paralel_workerlar_iki_firmada_her_isi_bir_kez_isler_surum_ve_izolasyon_korunur()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await ReadyAsync();
        var b = await ReadyAsync();

        // Ana host'un 4 worker döngüsüne ek 6 eşzamanlı işleyici: 10 worker aynı kuyruktan iş çeker
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var processor = Services.GetRequiredService<GenerationProcessor>();
        var workers = Enumerable.Range(0, ExtraWorkers).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (!await processor.ProcessNextAsync(stop.Token))
                    {
                        await Task.Delay(10, stop.Token);
                    }
                }
                catch (Exception) when (stop.IsCancellationRequested)
                {
                    return; // durdurma: SqlClient iptali SqlException olarak da gelir (GenerationWorker ile aynı kural)
                }
            }
        }, ct)).ToList();

        JobDto[] jobsA, jobsB;
        try
        {
            var started = await Task.WhenAll(
                Task.WhenAll(Enumerable.Range(0, JobsPerTenant).Select(_ => StartGenerationAsync(a.Client, a.Session.Id))),
                Task.WhenAll(Enumerable.Range(0, JobsPerTenant).Select(_ => StartGenerationAsync(b.Client, b.Session.Id))));
            jobsA = await Task.WhenAll(started[0].Select(j => WaitForJobAsync(a.Client, j.Id, timeoutSeconds: 60)));
            jobsB = await Task.WhenAll(started[1].Select(j => WaitForJobAsync(b.Client, j.Id, timeoutSeconds: 60)));
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(workers);
        }

        foreach (var (tenant, session, jobs) in new[] { (a.Tenant, a.Session, jobsA), (b.Tenant, b.Session, jobsB) })
        {
            Assert.All(jobs, j => Assert.Equal(GenerationJobStatus.Succeeded, j.Status));
            Assert.All(jobs, j => Assert.Equal(1, j.Attempt)); // hiçbir iş iki worker'a verilmedi
            var (scope, db) = OpenDb(tenant.OrganizationId);
            using (scope)
            {
                var images = await db.GeneratedImages.Where(i => i.SessionId == session.Id).ToListAsync(ct);
                Assert.Equal(Enumerable.Range(1, JobsPerTenant), images.Select(i => i.VersionNo).Order()); // benzersiz, boşluksuz sürüm
                Assert.All(images, i => Assert.Equal(tenant.OrganizationId, i.OrganizationId));

                var ids = jobs.Select(j => j.Id).ToList();
                var logs = await db.AiUsageLogs.Where(l => l.JobId != null && ids.Contains(l.JobId.Value)).ToListAsync(ct);
                Assert.Equal(JobsPerTenant, logs.Count); // iş başına tek maliyet kaydı: çift sayım yok
                Assert.All(logs, l => Assert.Equal((tenant.OrganizationId, tenant.StoreId), (l.OrganizationId, l.StoreId)));
            }
        }

        // Worker işi firmadan bağımsız işler; API yine firma sınırını korur
        await ApiClient.AssertProblemAsync(await b.Client.GetAsync($"/api/v1/jobs/{jobsA[0].Id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await a.Client.GetAsync($"/api/v1/jobs/{jobsB[0].Id}"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Oturum_surum_kilidi_ayni_oturumu_bekletir_digerini_bekletmez_ve_transaction_bitince_birakilir()
    {
        ApiFactory.RequireDatabase(Factory);
        var ct = TestContext.Current.CancellationToken;
        var session = AppLocks.SessionVersion(Guid.NewGuid());
        var otherSession = AppLocks.SessionVersion(Guid.NewGuid());
        var (holderScope, holder) = OpenDb();
        var (waiterScope, waiter) = OpenDb();
        using (holderScope)
        using (waiterScope)
        {
            // Kilit transaction'a aittir: transaction yoksa çağrı reddedilir (asılı kalan oturum kilidi olmaz)
            await Assert.ThrowsAsync<InvalidOperationException>(() => AppLocks.AcquireExclusiveAsync(waiter, session, TimeSpan.FromSeconds(1), ct));

            await using var held = await holder.Database.BeginTransactionAsync(ct);
            await AppLocks.AcquireExclusiveAsync(holder, session, TimeSpan.FromSeconds(1), ct);

            await using (await waiter.Database.BeginTransactionAsync(ct))
            {
                var ex = await Assert.ThrowsAsync<SqlException>(() => AppLocks.AcquireExclusiveAsync(waiter, session, TimeSpan.FromMilliseconds(300), ct));
                Assert.Equal(AppLocks.NotGrantedError, ex.Number);
                Assert.Contains("sonucu -1", ex.Message, StringComparison.Ordinal); // -1 = zaman aşımı
                Assert.True(DbErrors.IsTransientLockFailure(ex));
            }

            await using (await waiter.Database.BeginTransactionAsync(ct))
            {
                await AppLocks.AcquireExclusiveAsync(waiter, otherSession, TimeSpan.FromMilliseconds(300), ct); // başka oturum beklemez
            }

            await held.RollbackAsync(ct); // geri alma da kilidi bırakır
            await using (await waiter.Database.BeginTransactionAsync(ct))
            {
                await AppLocks.AcquireExclusiveAsync(waiter, session, TimeSpan.FromMilliseconds(300), ct);
            }
        }
    }

    [Fact]
    public async Task Gercek_kilitlenme_1205_gecici_kilit_hatasi_olarak_taninir()
    {
        ApiFactory.RequireDatabase(Factory);
        var ct = TestContext.Current.CancellationToken;
        var table = $"##deadlock_probe_{Guid.NewGuid():N}"; // genel geçici tablo: iki bağlantı aynı satırları görür
        await using var a = new SqlConnection(Factory.TestConnectionString);
        await using var b = new SqlConnection(Factory.TestConnectionString);
        await a.OpenAsync(ct);
        await b.OpenAsync(ct);
        await ExecAsync(a, $"CREATE TABLE {table} (id int PRIMARY KEY, v int NOT NULL); INSERT {table} VALUES (1, 0), (2, 0);", ct);
        try
        {
            await ExecAsync(a, $"BEGIN TRAN; UPDATE {table} SET v = 1 WHERE id = 1;", ct);
            await ExecAsync(b, $"SET DEADLOCK_PRIORITY LOW; BEGIN TRAN; UPDATE {table} SET v = 2 WHERE id = 2;", ct);

            // Çapraz istek döngü oluşturur; düşük öncelikli b kurban seçilir, a devam eder
            var aSecond = ExecAsync(a, $"UPDATE {table} SET v = 1 WHERE id = 2;", ct);
            var bSecond = ExecAsync(b, $"UPDATE {table} SET v = 2 WHERE id = 1;", ct);
            var ex = await Assert.ThrowsAsync<SqlException>(() => bSecond);
            await aSecond;
            await ExecAsync(a, "COMMIT;", ct);

            Assert.Equal(DbErrors.DeadlockVictim, ex.Number);
            Assert.True(DbErrors.IsDeadlock(ex));
            Assert.True(DbErrors.IsTransientLockFailure(ex));
            Assert.True(DbErrors.IsDeadlock(new DbUpdateException("SaveChanges sarmalı", ex))); // EF yolu
        }
        finally
        {
            await ExecAsync(a, $"IF OBJECT_ID(N'tempdb..{table}') IS NOT NULL DROP TABLE {table};", CancellationToken.None);
        }
    }

    private static async Task ExecAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Tablo adı testte üretilen GUID'dir, kullanıcı girdisi değil
        command.CommandText = sql;
#pragma warning restore CA2100
        command.CommandTimeout = 30;
        await command.ExecuteNonQueryAsync(ct);
    }
}
