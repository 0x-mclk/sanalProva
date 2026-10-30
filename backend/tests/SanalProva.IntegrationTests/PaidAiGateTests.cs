using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Features.Platform;
using SanalProva.Domain.Generation;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;

namespace SanalProva.IntegrationTests;

/// <summary>
/// M5.1f: firma düzeyinde ücretli AI kapısı — <b>kabul</b> aşaması (HTTP). İkinci kapı (sağlayıcı çağrısından hemen önce, rezervasyon yok, fail-closed, eşzamanlı) işleyici
/// düzeyinde <c>PaidProviderPipelineTests</c>'tedir. Burada: gerçek modda yetkisiz firmanın işi kuyruğa girmez ve ücret doğmaz; mock mod engellenmez; yeni firma varsayılan kapalıdır;
/// değeri yalnızca operatör değiştirir (hiçbir API yazamaz, kaynak taraması testle sabit). Hiçbir test gerçek ücretli çağrı yapmaz (ücretli çağrı bayrağı kapalı, anahtar yok, işçi yok).
/// </summary>
public sealed class PaidAiGateTests(ApiFactory factory) : ApiTestBase(factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SetPaidAiAsync(Guid organizationId, bool enabled)
    {
        var (scope, db) = OpenDb();
        using (scope)
        {
            var organization = await db.Organizations.SingleAsync(o => o.Id == organizationId, Ct);
            organization.Settings.PaidAiEnabled = enabled;
            await db.SaveChangesAsync(Ct);
        }
    }

    private async Task<(int Jobs, int UsageLogs)> CountsAsync(Guid organizationId)
    {
        var (scope, db) = OpenDb(organizationId);
        using (scope)
        {
            return (await db.GenerationJobs.CountAsync(Ct), await db.AiUsageLogs.CountAsync(Ct));
        }
    }

    private static Task<HttpResponseMessage> StartRawAsync(ApiClient client, Guid sessionId, string key) =>
        client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/generations", new { }, configure: r => r.Headers.Add("Idempotency-Key", key));

    [Fact]
    public async Task Gercek_modda_yetkisiz_firma_kabulde_reddedilir_is_kuyruga_girmez_ucret_ve_rezervasyon_yok()
    {
        ApiFactory.RequireDatabase(Factory);
        using var real = new RealModeApiFactory(Factory.StorageRoot);
        var tenant = await TestData.CreateTenantAsync(real.Services, paidAiEnabled: false);
        var catalog = await TestData.CreateCatalogAsync(real.Services, tenant.OrganizationId);
        var client = await ApiClient.LoginAsync(real, tenant.Email, tenant.StoreId);
        var (session, _) = await PreparedSessionAsync(client);
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);

        for (var i = 0; i < 3; i++)
        {
            var response = await StartRawAsync(client, session.Id, $"anahtar-{i}-{Guid.NewGuid():N}"[..30]);
            await ApiClient.AssertProblemAsync(response, HttpStatusCode.Forbidden, "PAID_AI_NOT_ENABLED");
        }

        Assert.Equal((0, 0), await CountsAsync(tenant.OrganizationId)); // iş de maliyet kaydı da oluşmadı

        // Yetki operatörce açılınca (burada doğrudan veritabanı) aynı istek kabul edilir; hâlâ hiçbir ücretli çağrı yapılmaz (işçi yok, ücretli bayrak kapalı)
        await SetPaidAiAsync(tenant.OrganizationId, true);
        var accepted = await StartRawAsync(client, session.Id, $"acik-{Guid.NewGuid():N}"[..30]);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(1, (await CountsAsync(tenant.OrganizationId)).Jobs);
    }

    [Fact]
    public async Task Gercek_modda_yetkisiz_firmanin_basarisiz_isi_yeniden_denenemez()
    {
        ApiFactory.RequireDatabase(Factory);
        using var real = new RealModeApiFactory(Factory.StorageRoot);
        var tenant = await TestData.CreateTenantAsync(real.Services, paidAiEnabled: false);
        await TestData.CreateCatalogAsync(real.Services, tenant.OrganizationId);
        var client = await ApiClient.LoginAsync(real, tenant.Email, tenant.StoreId);
        var (session, photo) = await PreparedSessionAsync(client);

        Guid failedJobId;
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var user = await db.Users.SingleAsync(u => u.Email == tenant.Email, Ct);
            var selection = new OutfitSelection { OrganizationId = tenant.OrganizationId, SessionId = session.Id, Mode = OutfitMode.OutfitSet, CreatedByUserId = user.Id };
            var now = DateTimeOffset.UtcNow;
            var job = new GenerationJob
            {
                OrganizationId = tenant.OrganizationId, StoreId = tenant.StoreId, SessionId = session.Id, JobType = GenerationJobType.OutfitSet, SelectionId = selection.Id,
                SourcePhotoId = photo.Id, IdempotencyKey = Guid.NewGuid().ToString("N"), RequestedByUserId = user.Id, Status = GenerationJobStatus.Failed,
                ErrorCode = GenerationErrorCode.Unknown, Attempt = 1, NextAttemptAt = now.AddHours(1),
            };
            db.OutfitSelections.Add(selection);
            db.GenerationJobs.Add(job);
            await db.SaveChangesAsync(Ct);
            failedJobId = job.Id;
        }

        var retry = await client.SendAsync(HttpMethod.Post, $"/api/v1/jobs/{failedJobId}/retry", new { }, configure: r => r.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N")));
        await ApiClient.AssertProblemAsync(retry, HttpStatusCode.Forbidden, "PAID_AI_NOT_ENABLED");
        Assert.Equal(1, (await CountsAsync(tenant.OrganizationId)).Jobs); // yeni iş açılmadı
    }

    [Fact]
    public async Task Mock_modda_firma_yetkisi_olmasa_da_akis_engellenmez_sonuc_DEMO_olarak_kalir()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, paidAiEnabled: false);
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        var client = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        var (session, _) = await PreparedSessionAsync(client);
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);

        var job = await StartGenerationAsync(client, session.Id);
        var done = await WaitForJobAsync(client, job.Id);
        Assert.Equal(GenerationJobStatus.Succeeded, done.Status); // mock: ücret yok, kapı uygulanmaz
    }

    [Fact]
    public async Task Yeni_firma_varsayilan_kapali_olusturma_istegi_bu_alani_ayarlayamaz()
    {
        ApiFactory.RequireDatabase(Factory);
        var platform = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.Users.Where(u => u.Email == platform.Email).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsPlatformAdmin, true), Ct);
        }

        var admin = await ApiClient.LoginAsync(Factory, platform.Email, platform.StoreId);
        var slug = $"ai-{Guid.NewGuid():N}"[..20];
        var body = new
        {
            name = "Ücretli AI Denemesi", slug, storeName = "Merkez", storeCode = "mrk", ownerEmail = $"o-{Guid.NewGuid():N}"[..18] + "@ornek.test", ownerDisplayName = "Sahip Kişi",
            paidAiEnabled = true, settings = new { paidAiEnabled = true }, // istemci bu alanı göndermeyi deneyebilir: yok sayılır
        };
        var created = await ApiClient.ReadAsync<CreateOrganizationResponse>(await PlatformOrganizationTests.CreateRawAsync(admin, body, PlatformOrganizationTests.NewKey()), HttpStatusCode.Created);

        (scope, db) = OpenDb();
        using (scope)
        {
            Assert.False((await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == created.OrganizationId, Ct)).Settings.PaidAiEnabled);
        }
    }

    [Fact]
    public async Task Firma_sahibi_ve_yoneticisi_ayar_uclarindan_ucretli_AI_yetkisini_degistiremez()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Owner, paidAiEnabled: false);
        var owner = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);

        // Firma ayarlarını yazan uç nokta: gövdeye fazladan alan eklemek hiçbir şey değiştirmez
        var response = await owner.SendAsync(HttpMethod.Put, "/api/v1/admin/ai-quality", new { resolution = "1k", mode = "balanced", paidAiEnabled = true, settings = new { paidAiEnabled = true } });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));

        var (scope, db) = OpenDb();
        using (scope)
        {
            var settings = (await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == tenant.OrganizationId, Ct)).Settings;
            Assert.Equal("1k", settings.FashnResolution); // gerçek alan güncellendi (uç nokta çalışıyor)
            Assert.False(settings.PaidAiEnabled); // ama yetki yazılmadı
        }

        await SetPaidAiAsync(tenant.OrganizationId, true); // yalnızca doğrudan veri erişimi (operatör/test) açabilir
        var after = await owner.SendAsync(HttpMethod.Put, "/api/v1/admin/ai-quality", new { resolution = "2k", mode = "balanced", paidAiEnabled = false });
        Assert.True(after.IsSuccessStatusCode);
        (scope, db) = OpenDb();
        using (scope)
        {
            Assert.True((await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == tenant.OrganizationId, Ct)).Settings.PaidAiEnabled); // kapatamaz da (ayar uçları bu alana dokunmaz)
        }
    }

    [Fact]
    public void Kaynak_kodda_ucretli_AI_yetkisini_yazan_hicbir_yer_yok_yalnizca_okunur()
    {
        var src = Directory.GetParent(Path.GetDirectoryName(MigrationLab.RepoFile("backend/src/SanalProva.Api/SanalProva.Api.csproj"))!)!.FullName;
        // Operatör komutu (M5.1g) eklendiğinde BU liste gerekçeyle genişletilir; başka hiçbir dosya yazamaz.
        var allowedWriters = new HashSet<string>(StringComparer.Ordinal);
        var writes = new Regex(@"PaidAiEnabled\s*(=(?![=>])|\+=)|SetProperty\([^;]*PaidAiEnabled|JSON_MODIFY", RegexOptions.CultureInvariant);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/Migrations/", StringComparison.Ordinal))
            {
                continue;
            }

            if (writes.IsMatch(File.ReadAllText(file)) && !allowedWriters.Contains(relative))
            {
                offenders.Add(relative);
            }
        }

        Assert.True(offenders.Count == 0, "PaidAiEnabled yazan izinsiz dosya(lar): " + string.Join(", ", offenders));
    }
}
