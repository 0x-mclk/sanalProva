using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SanalProva.Api.Features.TryOn;
using SanalProva.Domain.Catalog;
using SanalProva.Domain.Files;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Retention;
using SanalProva.Infrastructure.Storage;

namespace SanalProva.IntegrationTests;

/// <summary>
/// M3.5: KVKK silmesinin uçtan uca kanıtı. Üç tetikleyici (personel silmesi, onay geri alma, saklama süresi dolması) aynı kontrol listesinden geçer:
/// depoda dosya yok, <c>stored_files</c> kaydı yok, <b>veritabanının tüm uniqueidentifier sütunlarında</b> (sistem kataloğundan dinamik tarama) oturuma, fotoğraflara, işlere,
/// görsellere veya dosyalara ait hiçbir iz yok (tek istisna: kişisel veri içermeyen denetim kaydının varlık kimliği), maliyet kayıtları kalır ve iş bağlantısı kopar,
/// eski imzalı bağlantılar, favori, iş ve oturum uç noktaları geçersiz. Başka oturum/firma/şube etkilenmez.
/// </summary>
public sealed class KvkkDeletionTests(ApiFactory factory) : ApiTestBase(factory)
{
    private sealed record Footprint(Guid Session, Guid Organization, Guid Store, IReadOnlyList<Guid> Ids, IReadOnlyList<Guid> FileIds, string SignedUrl, Guid ImageId, Guid JobId, int UsageRows);

    private async Task<(TestTenant Tenant, ApiClient Client, Footprint Footprint)> CompletedSessionAsync(TestTenant? tenant = null, ApiClient? client = null, CatalogIds? catalog = null)
    {
        var ct = TestContext.Current.CancellationToken;
        if (tenant is null)
        {
            (tenant, client) = await StaffAsync();
            catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        }

        var (session, photo) = await PreparedSessionAsync(client!);
        await SaveDraftAsync(client!, session.Id, OutfitMode.OutfitSet, catalog!.SetProduct);
        var job = await WaitForJobAsync(client!, (await StartGenerationAsync(client!, session.Id)).Id);
        Assert.NotNull(job.ResultImageId);
        await client!.PatchAsync<GeneratedImageDto>($"/api/v1/images/{job.ResultImageId}/favorite", new { isFavorite = true });
        var images = await client.GetAsync<List<GeneratedImageDto>>($"/api/v1/sessions/{session.Id}/images");
        var url = images.Single(i => i.Id == job.ResultImageId).Urls.Url;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode); // silmeden önce çalışır

        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var photos = await db.SessionPhotos.AsNoTracking().Where(p => p.SessionId == session.Id).ToListAsync(ct);
            var generated = await db.GeneratedImages.AsNoTracking().Where(i => i.SessionId == session.Id).ToListAsync(ct);
            var selection = await db.OutfitSelections.AsNoTracking().Where(s => s.SessionId == session.Id).ToListAsync(ct);
            var items = await db.OutfitSelectionItems.AsNoTracking().Where(i => selection.Select(s => s.Id).Contains(i.SelectionId)).ToListAsync(ct);
            var consent = await db.ConsentRecords.AsNoTracking().Where(c => c.SessionId == session.Id).ToListAsync(ct);
            var jobs = await db.GenerationJobs.AsNoTracking().Where(j => j.SessionId == session.Id).ToListAsync(ct);
            var fileIds = photos.Select(p => p.FileId).Concat(generated.Select(i => i.FileId)).ToList();
            var ids = new List<Guid> { session.Id };
            ids.AddRange(photos.Select(p => p.Id));
            ids.AddRange(generated.Select(i => i.Id));
            ids.AddRange(selection.Select(s => s.Id));
            ids.AddRange(items.Select(i => i.Id));
            ids.AddRange(consent.Select(c => c.Id));
            ids.AddRange(jobs.Select(j => j.Id));
            ids.AddRange(fileIds);
            var usage = await db.AiUsageLogs.AsNoTracking().CountAsync(l => jobs.Select(j => j.Id).Contains(l.JobId!.Value), ct);
            Assert.True(photos.Count > 0 && generated.Count > 0 && items.Count > 0 && consent.Count > 0 && usage > 0, "Test oturumu eksik kuruldu");
            return (tenant, client, new Footprint(session.Id, tenant.OrganizationId, tenant.StoreId, ids, fileIds, url, job.ResultImageId!.Value, job.Id, usage));
        }
    }

    private string SessionDir(Footprint f) => Path.Combine(Factory.StorageRoot, f.Organization.ToString("N"), f.Store.ToString("N"), "sessions", f.Session.ToString("N"));

    /// <summary>Silme sonrası kontrol listesi: dinamik katalog taraması dahil.</summary>
    private async Task AssertErasedAsync(Footprint f, ApiClient client)
    {
        var ct = TestContext.Current.CancellationToken;

        // 1) Depo
        var dir = SessionDir(f);
        Assert.False(Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(), $"Depoda müşteri dosyası kaldı: {dir}");

        // 2) Veritabanı: her tablodaki her uniqueidentifier sütununda kimlik izi yok
        var lab = Factory.MigratorConnectionString!;
        await using var connection = new SqlConnection(lab);
        await connection.OpenAsync(ct);
        var columns = new List<(string Table, string Column)>();
        await using (var command = new SqlCommand("""
            SELECT t.name, c.name FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
             WHERE ty.name = N'uniqueidentifier' AND t.is_ms_shipped = 0 AND t.name <> N'__ef_migrations_history'
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                columns.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        Assert.True(columns.Count > 60, $"Katalog taraması beklenenden az sütun buldu: {columns.Count}");
        var traces = new List<string>();
        var idList = string.Join(", ", f.Ids.Select(i => $"'{i}'"));
        foreach (var (table, column) in columns)
        {
            // Bilinçli istisna: denetim kaydı yalnızca varlık kimliği ve eylem adı tutar (kişisel veri yok), yasal izlenebilirlik için kalır
            if (table == "audit_logs" && column == "entity_id")
            {
                continue;
            }

            await using var scan = new SqlCommand($"SELECT COUNT_BIG(*) FROM [{table}] WHERE [{column}] IN ({idList})", connection);
            var count = Convert.ToInt64(await scan.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
            if (count > 0)
            {
                traces.Add($"{table}.{column}: {count}");
            }
        }

        Assert.True(traces.Count == 0, "Silinmiş oturumun izi kaldı: " + string.Join(", ", traces));

        // 3) Dosya metadata'sı: oturum önekiyle hiç kayıt yok
        await using (var meta = new SqlCommand("SELECT COUNT_BIG(*) FROM stored_files WHERE storage_key LIKE @p", connection))
        {
            meta.Parameters.AddWithValue("@p", $"{f.Organization:N}/{f.Store:N}/sessions/{f.Session:N}/%");
            Assert.Equal(0L, Convert.ToInt64(await meta.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture));
        }

        // 4) Maliyet kayıtları kalır, iş bağlantısı kopar (muhasebe kaydı kişisel veri içermez)
        var (scope, db) = OpenDb();
        using (scope)
        {
            var logs = await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().Where(l => l.OrganizationId == f.Organization && l.JobId == null).CountAsync(ct);
            Assert.True(logs >= f.UsageRows, "Maliyet kaydı kayboldu veya iş bağlantısı kopmadı");

            // 5) Denetim izi: silme eylemi kaydedilmiş, kişisel veri içermiyor (yalnızca kimlikler/eylem adı/IP özeti)
            var audit = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().Where(a => a.EntityId == f.Session && a.Action.StartsWith("Session.Purged")).ToListAsync(ct);
            Assert.Single(audit);
        }

        // 6) Uç noktalar ve eski bağlantılar
        await ApiClient.AssertProblemAsync(await client.GetAsync($"/api/v1/sessions/{f.Session}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await client.GetAsync($"/api/v1/jobs/{f.JobId}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Patch, $"/api/v1/images/{f.ImageId}/favorite", new { isFavorite = false }), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await client.GetAsync($"/api/v1/sessions/{f.Session}/images"), HttpStatusCode.NotFound, "NOT_FOUND");
        using var oldUrl = await client.GetAsync(f.SignedUrl); // imza hâlâ geçerli olsa bile dosya yok: servis edilmez
        Assert.True(oldUrl.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone, $"Eski imzalı bağlantı {(int)oldUrl.StatusCode} döndü");
    }

    private async Task<(TestTenant Tenant, ApiClient Client, Footprint Doomed, Footprint Survivor)> TwoSessionsAsync()
    {
        var (tenant, client, doomed) = await CompletedSessionAsync();
        var (_, _, survivor) = await CompletedSessionAsync(tenant, client, await FirstCatalogAsync(tenant));
        return (tenant, client, doomed, survivor);
    }

    private async Task<CatalogIds> FirstCatalogAsync(TestTenant tenant)
    {
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var product = await db.Products.AsNoTracking().FirstAsync(p => p.ProductType == ProductType.OutfitSet, TestContext.Current.CancellationToken);
            return new CatalogIds(Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, product.Id, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty);
        }
    }

    private async Task AssertSurvivesAsync(Footprint f, ApiClient client)
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.True(Directory.Exists(SessionDir(f)) && Directory.EnumerateFiles(SessionDir(f), "*", SearchOption.AllDirectories).Any(), "Başka oturumun dosyaları silindi");
        Assert.Equal(f.Session, (await client.GetAsync<SessionDetail>($"/api/v1/sessions/{f.Session}")).Id);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync((await client.GetAsync<List<GeneratedImageDto>>($"/api/v1/sessions/{f.Session}/images")).Single().Urls.Url)).StatusCode);
        var (scope, db) = OpenDb(f.Organization);
        using (scope)
        {
            Assert.True(await db.StoredFiles.CountAsync(x => f.FileIds.Contains(x.Id), ct) == f.FileIds.Count, "Başka oturumun dosya kayıtları silindi");
        }
    }

    // ------------------------------------------------------------------ üç tetikleyici

    [Fact]
    public async Task Personel_silmesi_oturumun_butun_verisini_siler_baska_oturum_ve_firma_etkilenmez()
    {
        var (tenant, client, doomed, survivor) = await TwoSessionsAsync();
        var (otherTenant, otherClient, other) = await CompletedSessionAsync(); // başka firma

        // Başka firmanın personeli silemez; hiçbir şey değişmez
        await ApiClient.AssertProblemAsync(await otherClient.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{doomed.Session}"), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.True(Directory.Exists(SessionDir(doomed)));

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{doomed.Session}")).StatusCode);

        await AssertErasedAsync(doomed, client);
        await AssertSurvivesAsync(survivor, client);
        await AssertSurvivesAsync(other, otherClient);
        _ = otherTenant;
        Assert.NotEqual(tenant.OrganizationId, other.Organization);

        // İkinci silme: uç nokta idempotent değil ama güvenli (404), hiçbir şey patlamaz
        await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{doomed.Session}"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Onay_geri_alinca_oturum_ve_butun_gorseller_ayni_kontrol_listesiyle_silinir()
    {
        var (_, client, doomed, survivor) = await TwoSessionsAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{doomed.Session}/consent")).StatusCode);

        await AssertErasedAsync(doomed, client);
        await AssertSurvivesAsync(survivor, client);
    }

    [Fact]
    public async Task Saklama_suresi_dolan_oturum_silinir_dolmayan_ve_baska_firmanin_oturumu_silinmez()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, client, doomed, survivor) = await TwoSessionsAsync();
        var (_, otherClient, otherExpired) = await CompletedSessionAsync();

        // İki oturumun süresi dolar (biri bu firmada, biri başka firmada); üçüncü hâlâ geçerli
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.TryOnSessions.IgnoreQueryFilters().Where(s => s.Id == doomed.Session || s.Id == otherExpired.Session)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RetentionUntil, DateTimeOffset.UtcNow.AddMinutes(-1)), ct);
        }

        var swept = await Services.GetRequiredService<RetentionSweeper>().SweepAsync(ct);
        Assert.True(swept >= 2);

        await AssertErasedAsync(doomed, client);
        await AssertErasedAsync(otherExpired, otherClient);
        await AssertSurvivesAsync(survivor, client);
        _ = tenant;

        // Denetim kaydı eylemi ayırt eder (personel/onay/saklama): yasal izlenebilirlik
        (scope, db) = OpenDb();
        using (scope)
        {
            Assert.Equal("Session.PurgedByRetention", (await db.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.EntityId == doomed.Session && a.Action.StartsWith("Session.Purged"), ct)).Action);
        }
    }

    // ------------------------------------------------------------------ kurtarma: hata ve çökme

    private sealed class FlakyStorage(IImageStorageService inner) : IImageStorageService
    {
        public int FailPrefixDeletes { get; set; }

        /// <summary>İlk önek silmesinden hemen sonra çalışır: silme ile eşzamanlı çalışan bir worker'ın "geç yazımını" benzetir.</summary>
        public Func<string, Task>? AfterFirstPrefixDelete { get; set; }

        public Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct) => inner.SaveAsync(key, content, contentType, ct);

        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) => inner.OpenReadAsync(key, ct);

        public Task DeleteAsync(string key, CancellationToken ct) => inner.DeleteAsync(key, ct);

        public Task<IReadOnlyList<StoredFolder>> ListSessionFoldersAsync(CancellationToken ct) => inner.ListSessionFoldersAsync(ct);

        public Task DeletePrefixAsync(string prefix, CancellationToken ct)
        {
            if (FailPrefixDeletes > 0)
            {
                FailPrefixDeletes--;
                throw new IOException("Depo geçici olarak yazılamıyor (test).");
            }

            return DeleteAndHookAsync(prefix, ct);
        }

        private async Task DeleteAndHookAsync(string prefix, CancellationToken ct)
        {
            await inner.DeletePrefixAsync(prefix, ct);
            if (AfterFirstPrefixDelete is { } hook)
            {
                AfterFirstPrefixDelete = null;
                await hook(prefix);
            }
        }

        public string GetReadUrl(string key, TimeSpan ttl) => inner.GetReadUrl(key, ttl);

        public Task<bool> IsHealthyAsync(CancellationToken ct) => inner.IsHealthyAsync(ct);
    }

    [Fact]
    public async Task Depo_silinemezse_oturum_ve_kayitlar_korunur_oturum_kayboldu_sanilmaz_sonraki_deneme_tamamlar()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, client, doomed) = await CompletedSessionAsync();
        var storage = new FlakyStorage(Services.GetRequiredService<IImageStorageService>()) { FailPrefixDeletes = 1 };

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var purger = new SessionPurger(storage, scope.ServiceProvider.GetRequiredService<SanalProva.Infrastructure.Displays.DisplayStateService>(), TimeProvider.System, NullLogger<SessionPurger>.Instance);

        await Assert.ThrowsAsync<IOException>(() => purger.PurgeAsync(db, doomed.Session, "Session.PurgedByStaff", null, ct));

        // Depo hatası: kayıtlar silinmedi (müşteri dosyası hâlâ depoda ve oturum sistem tarafından biliniyor → tekrar denenebilir, sızıntı "kayıt yok" ile gizlenmez)
        Assert.Equal(doomed.Session, (await client.GetAsync<SessionDetail>($"/api/v1/sessions/{doomed.Session}")).Id);
        Assert.True(Directory.EnumerateFiles(SessionDir(doomed), "*", SearchOption.AllDirectories).Any());

        // Saklama süpürmesi (arka plan işi) aynı oturumu sonraki turda tamamlar
        await db.TryOnSessions.IgnoreQueryFilters().Where(s => s.Id == doomed.Session).ExecuteUpdateAsync(s => s.SetProperty(x => x.RetentionUntil, DateTimeOffset.UtcNow.AddMinutes(-1)), ct);
        await Services.GetRequiredService<RetentionSweeper>().SweepAsync(ct);
        await AssertErasedAsync(doomed, client);
        _ = tenant;
    }

    [Fact]
    public async Task Silme_sirasinda_gec_yazilan_dosya_ve_metadata_ikinci_gecis_ve_onek_silmesiyle_temizlenir()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, client, doomed) = await CompletedSessionAsync();
        var inner = Services.GetRequiredService<IImageStorageService>();
        var lateKey = $"{doomed.Organization:N}/{doomed.Store:N}/sessions/{doomed.Session:N}/{Guid.CreateVersion7():N}.jpg";
        var storage = new FlakyStorage(inner)
        {
            AfterFirstPrefixDelete = async _ =>
            {
                // Worker, ilk silme geçişinden sonra (oturum satırı henüz yokken) bir sonuç dosyası ve kaydı yazdı
                await inner.SaveAsync(lateKey, new MemoryStream([1, 2, 3]), "image/jpeg", ct);
                var (lateScope, lateDb) = OpenDb();
                using (lateScope)
                {
                    lateDb.StoredFiles.Add(new StoredFile { OrganizationId = doomed.Organization, StorageKey = lateKey, ContentType = "image/jpeg", SizeBytes = 3, Sha256 = new string('a', 64), Width = 1, Height = 1, Purpose = FilePurpose.GeneratedImage });
                    await lateDb.SaveChangesAsync(ct);
                }
            },
        };

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var purger = new SessionPurger(storage, scope.ServiceProvider.GetRequiredService<SanalProva.Infrastructure.Displays.DisplayStateService>(), TimeProvider.System, NullLogger<SessionPurger>.Instance);
        await purger.PurgeAsync(db, doomed.Session, "Session.PurgedByStaff", null, ct);

        Assert.Null(await inner.OpenReadAsync(lateKey, ct)); // geç yazılan dosya ikinci depo geçişinde silindi
        Assert.False(await db.StoredFiles.IgnoreQueryFilters().AnyAsync(f => f.StorageKey == lateKey, ct)); // metadata'sı önek eşleşmesiyle silindi
        await AssertErasedAsync(doomed, client);
    }

    [Fact]
    public async Task Silme_ortasinda_cokme_yetim_dosya_ve_kayit_birakirsa_yetim_suprucu_temizler_taze_ve_kullanimdaki_dokunulmaz()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, client, doomed) = await CompletedSessionAsync();
        var (_, _, survivor) = await CompletedSessionAsync(tenant, client, await FirstCatalogAsync(tenant));

        // Çökme benzetimi: oturum satırı (zincirleme fotoğraf/görsel/iş) gitti, ama dosya metadata'sı ve dosyalar kaldı (adım 3 çalışmadı)
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.TryOnSessions.IgnoreQueryFilters().Where(s => s.Id == doomed.Session).ExecuteDeleteAsync(ct);
            Assert.Equal(doomed.FileIds.Count, await db.StoredFiles.IgnoreQueryFilters().CountAsync(f => doomed.FileIds.Contains(f.Id), ct)); // yetim metadata
        }

        Assert.True(Directory.EnumerateFiles(SessionDir(doomed), "*", SearchOption.AllDirectories).Any()); // yetim dosyalar

        // Yetim ama ÇOK TAZE (yazım sürüyor olabilir): bekleme payı içinde dokunulmaz
        var clock = new OffsetClock();
        var sweeper = new OrphanSweeper(Services.GetRequiredService<IServiceScopeFactory>(), Services.GetRequiredService<IImageStorageService>(), clock,
            Options.Create(new RetentionSettings { OrphanGraceMinutes = 60 }), NullLogger<OrphanSweeper>.Instance);
        var fresh = await sweeper.SweepAsync(ct);
        Assert.Equal(0, fresh.FileRecords); // taze yetim kayıtlar bekleme payı içinde dokunulmaz
        Assert.True(Directory.EnumerateFiles(SessionDir(doomed), "*", SearchOption.AllDirectories).Any(), "Taze yetim klasör bekleme payı dolmadan silindi");

        // Yalnızca depoda kalan yetim dosya (hiçbir veritabanı kaydı yok, oturum da yok): yetim klasör süpürmesinin tek başına sınanması
        var ghostSession = Guid.CreateVersion7();
        var ghostKey = $"{doomed.Organization:N}/{doomed.Store:N}/sessions/{ghostSession:N}/{Guid.CreateVersion7():N}.jpg";
        await Services.GetRequiredService<IImageStorageService>().SaveAsync(ghostKey, new MemoryStream([9, 9]), "image/jpeg", ct);
        var ghostFile = Path.Combine(Factory.StorageRoot, ghostKey.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(ghostFile));

        // Pay dolunca (saat 2 saat ileri): yetim klasör ve yetim metadata silinir; kullanımdaki oturumun dosyaları ve kayıtları dokunulmaz
        clock.Offset = TimeSpan.FromHours(2);
        var result = await sweeper.SweepAsync(ct);
        Assert.True(result.Folders >= 1 && result.FileRecords >= doomed.FileIds.Count, $"Süpürücü yetimi temizlemedi: {result}");
        Assert.False(Directory.Exists(SessionDir(doomed)) && Directory.EnumerateFileSystemEntries(SessionDir(doomed)).Any());
        (scope, db) = OpenDb();
        using (scope)
        {
            Assert.Equal(0, await db.StoredFiles.IgnoreQueryFilters().CountAsync(f => doomed.FileIds.Contains(f.Id), ct));
        }

        Assert.False(File.Exists(ghostFile), "Veritabanında oturumu olmayan yetim dosya silinmedi");
        await AssertSurvivesAsync(survivor, client);

        // Tekrar çalıştırmak zararsız (idempotent)
        var again = await sweeper.SweepAsync(ct);
        Assert.Equal(0, again.FileRecords);
        await AssertSurvivesAsync(survivor, client);
    }

    [Fact]
    public async Task Yukleme_sirasinda_oturum_silinirse_dosya_yetim_kalmaz_katalog_gorselleri_suprucuye_girmez()
    {
        var ct = TestContext.Current.CancellationToken;
        var (tenant, client) = await StaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        var (session, _) = await PreparedSessionAsync(client);

        // Yükleme tamamlanmadan oturum silinir; yüklemenin sonradan gelen dosya/kayıt çabası patlar ve dosya geride kalmaz
        var upload = client.UploadAsync($"/api/v1/sessions/{session.Id}/photos", TestImages.Portrait(77));
        var delete = client.SendAsync(HttpMethod.Delete, $"/api/v1/sessions/{session.Id}");
        await Task.WhenAll(upload, delete);
        (await upload).Dispose();
        (await delete).Dispose();

        var dir = Path.Combine(Factory.StorageRoot, tenant.OrganizationId.ToString("N"), tenant.StoreId.ToString("N"), "sessions", session.Id.ToString("N"));
        var clock = new OffsetClock { Offset = TimeSpan.FromHours(2) };
        await new OrphanSweeper(Services.GetRequiredService<IServiceScopeFactory>(), Services.GetRequiredService<IImageStorageService>(), clock,
            Options.Create(new RetentionSettings { OrphanGraceMinutes = 60 }), NullLogger<OrphanSweeper>.Instance).SweepAsync(ct);

        Assert.False(Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(), "Eşzamanlı yükleme müşteri fotoğrafını yetim bıraktı");

        // Katalog görselleri süpürücüye girmez (amaç: ProductImage; yalnızca müşteri fotoğrafı ve üretilen görsel)
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            Assert.True(await db.StoredFiles.CountAsync(f => f.Purpose == FilePurpose.ProductImage, ct) >= 6);
        }

        _ = catalog;
    }

    private sealed class OffsetClock : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
    }
}
