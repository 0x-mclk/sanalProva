using System.Net;
using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Features.TryOn;
using SanalProva.Domain.Generation;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.IntegrationTests;

/// <summary>
/// SQL Server tek zincirleme silme yoluna izin verdiği için bazı ilişkiler NO ACTION oldu. Bu testler, gerçek iş ve sonuç görseli olan
/// oturumların silinmesinde (KVKK) hiçbir ilişkili kaydın ve dosyanın kalmadığını, fotoğraf silmede ise iş/sonuç/maliyetin korunduğunu doğrular.
/// </summary>
public sealed class SqlServerDeleteBehaviorTests(ApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Uretim_sonuclu_oturum_silinince_iliskili_hicbir_kayit_ve_dosya_kalmaz_maliyet_kaydi_kalir()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, client) = await StaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        var (session, _) = await PreparedSessionAsync(client);
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        var first = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id)).Id);
        var second = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id)).Id);
        Assert.All(new[] { first, second }, j => Assert.Equal(GenerationJobStatus.Succeeded, j.Status));

        List<Guid> jobIds = [first.Id, second.Id];
        List<Guid> selectionIds, fileIds, usageIds;
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            // Revizyon zinciri (Aşama 5'te API'den gelecek): ikinci sonuç birinciden türemiş. NO ACTION olan öz-referans (parent_image_id)
            // ve iş ↔ görsel döngüsü (generation_jobs.base_image_id / generated_images.job_id) tek DELETE içinde sınanır
            await db.GeneratedImages.Where(i => i.Id == second.ResultImageId).ExecuteUpdateAsync(s => s.SetProperty(i => i.ParentImageId, first.ResultImageId), ct);
            await db.GenerationJobs.Where(j => j.Id == second.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.BaseImageId, first.ResultImageId), ct);

            selectionIds = await db.OutfitSelections.Where(s => s.SessionId == session.Id).Select(s => s.Id).ToListAsync(ct);
            fileIds = await db.SessionPhotos.Where(p => p.SessionId == session.Id).Select(p => p.FileId)
                .Concat(db.GeneratedImages.Where(i => i.SessionId == session.Id).Select(i => i.FileId))
                .ToListAsync(ct);
            usageIds = await db.AiUsageLogs.Where(l => l.JobId != null && jobIds.Contains(l.JobId.Value)).Select(l => l.Id).ToListAsync(ct);
        }

        Assert.Equal(2, selectionIds.Count);
        Assert.Equal(3, fileIds.Count); // 1 fotoğraf + 2 sonuç
        Assert.Equal(2, usageIds.Count);
        Assert.NotEmpty(SessionFilesOnDisk(session.Id));

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{session.Id}")).StatusCode);

        (scope, db) = OpenDb();
        using (scope)
        {
            // session_id / current_session_id sütunu olan her tablo sistem kataloğundan bulunur: şemaya yeni tablo eklenirse o da kapsanır
            var counts = await CountSessionRowsAsync(db, session.Id, ct);
            foreach (var expected in new[] { "tryon_sessions", "consent_records", "session_photos", "outfit_selections", "generation_jobs", "generated_images", "display_devices" })
            {
                Assert.True(expected == "tryon_sessions" || counts.Exists(c => c.Table == expected), $"{expected} kataloğda bulunamadı");
            }

            Assert.All(counts, c => Assert.True(c.Rows == 0, $"{c.Table}.{c.Column}: {c.Rows} satır kaldı"));
            Assert.False(await db.TryOnSessions.IgnoreQueryFilters().AnyAsync(s => s.Id == session.Id, ct));
            Assert.False(await db.OutfitSelectionItems.IgnoreQueryFilters().AnyAsync(i => selectionIds.Contains(i.SelectionId), ct));
            Assert.False(await db.StoredFiles.IgnoreQueryFilters().AnyAsync(f => fileIds.Contains(f.Id), ct));

            // Maliyet kaydı kişisel veri içermez ve kalır (bütçe/fatura mutabakatı); iş bağlantısı koparılır
            var usage = await db.AiUsageLogs.IgnoreQueryFilters().Where(l => usageIds.Contains(l.Id)).ToListAsync(ct);
            Assert.Equal(2, usage.Count);
            Assert.All(usage, l => Assert.Null(l.JobId));
            Assert.All(usage, l => Assert.Equal((tenant.OrganizationId, tenant.StoreId), (l.OrganizationId, l.StoreId)));
            Assert.True(await db.AuditLogs.AnyAsync(a => a.EntityId == session.Id && a.Action == "Session.PurgedByStaff", ct));
        }

        Assert.Empty(SessionFilesOnDisk(session.Id));
    }

    [Fact]
    public async Task Uretimde_kullanilan_fotograf_yeniden_cekilince_ve_silinince_is_sonuc_ve_maliyet_kalir_baglanti_kopar()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, client) = await StaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        var (session, original) = await PreparedSessionAsync(client);
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        var fromOriginal = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id)).Id);

        // 1) Yeniden çek (aynı açı): eski fotoğraf silinir; ondan üretilen iş, sonuç ve maliyet kaydı kalır, kaynak bağlantısı NULL olur
        var retaken = await UploadPhotoAsync(client, session.Id, TestImages.Portrait(seed: 7));
        await AssertDetachedAsync(tenant.OrganizationId, original.Id, fromOriginal, ct);

        // 2) Yeni fotoğrafla üret, sonra fotoğrafı sil: aynı kural, oturum kalır
        var fromRetaken = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id)).Id);
        var afterDelete = await ApiClient.ReadAsync<SessionDetail>(await client.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{session.Id}/photos/{retaken.Id}"));
        Assert.Empty(afterDelete.Photos);
        Assert.Equal(2, afterDelete.Images.Count);
        await AssertDetachedAsync(tenant.OrganizationId, retaken.Id, fromRetaken, ct);
        await AssertDetachedAsync(tenant.OrganizationId, original.Id, fromOriginal, ct);
    }

    private async Task AssertDetachedAsync(Guid organizationId, Guid photoId, JobDto job, CancellationToken ct)
    {
        Assert.Equal(GenerationJobStatus.Succeeded, job.Status);
        var (scope, db) = OpenDb(organizationId);
        using (scope)
        {
            Assert.False(await db.SessionPhotos.AnyAsync(p => p.Id == photoId, ct));
            var stored = await db.GenerationJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, ct);
            Assert.Null(stored.SourcePhotoId);
            Assert.Equal(GenerationJobStatus.Succeeded, stored.Status);
            Assert.True(await db.GeneratedImages.AnyAsync(i => i.Id == job.ResultImageId, ct));
            Assert.True(await db.AiUsageLogs.AnyAsync(l => l.JobId == job.Id, ct));
        }
    }

    private List<string> SessionFilesOnDisk(Guid sessionId) =>
        Directory.Exists(Factory.StorageRoot)
            ? [.. Directory.EnumerateFiles(Factory.StorageRoot, "*", SearchOption.AllDirectories).Where(p => p.Contains(sessionId.ToString("N"), StringComparison.OrdinalIgnoreCase))]
            : [];

    private static async Task<List<(string Table, string Column, int Rows)>> CountSessionRowsAsync(AppDbContext db, Guid sessionId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        var targets = new List<(string Table, string Column)>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText = """
                SELECT t.name, c.name
                  FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id
                 WHERE t.is_ms_shipped = 0 AND c.name IN (N'session_id', N'current_session_id')
                """;
            await using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                targets.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var result = new List<(string Table, string Column, int Rows)>();
        foreach (var (table, column) in targets)
        {
            await using var count = connection.CreateCommand();
#pragma warning disable CA2100 // Tablo ve sütun adları sistem kataloğundan gelir, kullanıcı girdisi değil
            count.CommandText = $"SELECT COUNT(*) FROM [dbo].[{table}] WHERE [{column}] = @id";
#pragma warning restore CA2100
            var parameter = count.CreateParameter();
            parameter.ParameterName = "@id";
            parameter.Value = sessionId;
            count.Parameters.Add(parameter);
            result.Add((table, column, (int)(await count.ExecuteScalarAsync(ct))!));
        }

        return result;
    }
}
