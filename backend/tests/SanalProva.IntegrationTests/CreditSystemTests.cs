using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SanalProva.Domain.Credits;
using SanalProva.Domain.Generation;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Credits;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.IntegrationTests;

/// <summary>Kredi testleri için ortak doğrulayıcılar: bakiye ile defter toplamının tutarlılığı.</summary>
internal static class CreditAssert
{
    /// <summary>
    /// Değişmezler (firma başına): bakiye = Σ(Grant+Adjustment) − Σ(Consume); rezerve = Σ(Reserve) − Σ(Release) − Σ(Consume); son defter girdisinin
    /// "sonrası" değerleri hesapla aynı; bakiye ≥ rezerve ≥ 0; iş başına en fazla bir Reserve ve bir kapanış.
    /// </summary>
    public static async Task ConsistentAsync(IServiceProvider services, Guid organizationId)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.CreditAccounts.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(a => a.OrganizationId == organizationId, TestContext.Current.CancellationToken);
        var entries = await db.CreditLedger.IgnoreQueryFilters().AsNoTracking().Where(e => e.OrganizationId == organizationId).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(TestContext.Current.CancellationToken);
        if (account is null)
        {
            Assert.Empty(entries);
            return;
        }

        int Sum(params CreditEntryType[] types) => entries.Where(e => types.Contains(e.EntryType)).Sum(e => e.Amount);
        Assert.Equal(Sum(CreditEntryType.Grant, CreditEntryType.Adjustment) - Sum(CreditEntryType.Consume), account.BalanceCredits);
        Assert.Equal(Sum(CreditEntryType.Reserve) - Sum(CreditEntryType.Release) - Sum(CreditEntryType.Consume), account.ReservedCredits);
        Assert.True(account.ReservedCredits >= 0 && account.BalanceCredits >= account.ReservedCredits);
        var jobs = entries.Where(e => e.JobId is not null).GroupBy(e => e.JobId).ToList();
        Assert.All(jobs, g =>
        {
            Assert.True(g.Count(e => e.EntryType == CreditEntryType.Reserve) <= 1);
            Assert.True(g.Count(e => e.EntryType is CreditEntryType.Release or CreditEntryType.Consume) <= 1);
        });
        if (entries.Count > 0)
        {
            // Zaman damgası eşitliğinde sıra belirsiz olabilir: son girdi, hesapla aynı çiftlerden biri olmalı
            Assert.Contains(entries.Where(e => e.CreatedAt == entries[^1].CreatedAt), e => e.BalanceAfter == account.BalanceCredits && e.ReservedAfter == account.ReservedCredits);
        }
    }

    public static async Task<(int Balance, int Reserved)> AccountAsync(IServiceProvider services, Guid organizationId)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var account = await db.CreditAccounts.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(a => a.OrganizationId == organizationId, TestContext.Current.CancellationToken);
        return (account?.BalanceCredits ?? 0, account?.ReservedCredits ?? 0);
    }

    public static async Task<List<CreditLedgerEntry>> LedgerAsync(IServiceProvider services, Guid organizationId)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.CreditLedger.IgnoreQueryFilters().AsNoTracking().Where(e => e.OrganizationId == organizationId).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToListAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>
/// M5.4: kredi defteri, hesap değişmezleri, eşzamanlılık, firma izolasyonu, operatör yükleme idempotency'si, kabulde rezervasyon (HTTP) ve kaynak taraması.
/// İş yaşam döngüsü (başarı/başarısızlık/iptal/belirsiz) kapanışları sağlayıcı sahteleriyle <c>PaidProviderPipelineTests</c>'tedir.
/// Hiçbir test gerçek ücretli çağrı veya ödeme yapmaz.
/// </summary>
public sealed class CreditSystemTests(ApiFactory factory) : ApiTestBase(factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly CreditLedgerService _credits = new(TimeProvider.System);

    private async Task<Guid> OrganizationAsync(int credits = 0)
    {
        ApiFactory.RequireDatabase(Factory);
        return (await TestData.CreateTenantAsync(Services, StoreRole.Owner, credits: credits)).OrganizationId;
    }

    private async Task<CreditOperatorResult> OperatorAsync(Guid organizationId, CreditEntryType type, int amount, string reference, string? note = null)
    {
        await using var scope = Services.CreateAsyncScope();
        return await _credits.ApplyOperatorEntryAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), organizationId, type, amount, reference, CreditSource.Operator, note, Ct);
    }

    private async Task<CreditReserveResult> ReserveAsync(Guid organizationId, Guid jobId, int cost)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        var result = await _credits.ReserveAsync(db, organizationId, jobId, cost, "default", Ct);
        await db.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);
        return result;
    }

    private async Task<int> ExecuteRawAsync(string sql)
    {
        var (scope, db) = OpenDb();
        using (scope)
        {
            return await db.Database.ExecuteSqlRawAsync(sql, Ct);
        }
    }

    // ---------------------------------------------------------------- yükleme / idempotency / negatif bakiye

    [Fact]
    public async Task Yukleme_ayni_referansla_tekrar_uygulanmaz_farkli_tutarla_reddedilir_defter_ve_bakiye_tutarlidir()
    {
        var org = await OrganizationAsync();
        var first = await OperatorAsync(org, CreditEntryType.Grant, 100, "havale-2026-0001");
        Assert.Equal((CreditOperatorStatus.Applied, 100, 0), (first.Status, first.BalanceAfter, first.ReservedAfter));

        var replay = await OperatorAsync(org, CreditEntryType.Grant, 100, "havale-2026-0001");
        Assert.Equal((CreditOperatorStatus.Replayed, 100), (replay.Status, replay.BalanceAfter));
        Assert.Equal(CreditOperatorStatus.ReferenceConflict, (await OperatorAsync(org, CreditEntryType.Grant, 50, "havale-2026-0001")).Status);
        Assert.Equal(CreditOperatorStatus.ReferenceConflict, (await OperatorAsync(org, CreditEntryType.Adjustment, 100, "havale-2026-0001")).Status);

        Assert.Equal((100, 0), await CreditAssert.AccountAsync(Services, org));
        Assert.Single(await CreditAssert.LedgerAsync(Services, org));
        await CreditAssert.ConsistentAsync(Services, org);
    }

    [Fact]
    public async Task Eszamanli_ayni_referansli_yuklemeler_tek_kez_uygulanir()
    {
        var org = await OrganizationAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => OperatorAsync(org, CreditEntryType.Grant, 25, "havale-esz-0001")));

        Assert.Equal(1, results.Count(r => r.Status == CreditOperatorStatus.Applied));
        Assert.Equal(7, results.Count(r => r.Status == CreditOperatorStatus.Replayed));
        Assert.Equal((25, 0), await CreditAssert.AccountAsync(Services, org));
        await CreditAssert.ConsistentAsync(Services, org);
    }

    [Fact]
    public async Task Bakiye_negatif_olamaz_duzeltme_rezerve_ve_sifirin_altina_inemez_veritabani_CHECK_de_reddeder()
    {
        var org = await OrganizationAsync();
        await OperatorAsync(org, CreditEntryType.Grant, 10, "yukleme-neg-0001");
        Assert.Equal(CreditReserveStatus.Reserved, (await ReserveAsync(org, Guid.NewGuid(), 4)).Status);

        Assert.Equal(CreditOperatorStatus.WouldGoBelowReserved, (await OperatorAsync(org, CreditEntryType.Adjustment, -7, "duzeltme-neg-0001", "hata")).Status); // 10-7=3 < rezerve 4
        Assert.Equal(CreditOperatorStatus.WouldGoBelowReserved, (await OperatorAsync(org, CreditEntryType.Adjustment, -11, "duzeltme-neg-0002", "hata")).Status);
        var ok = await OperatorAsync(org, CreditEntryType.Adjustment, -6, "duzeltme-neg-0003", "iade");
        Assert.Equal((CreditOperatorStatus.Applied, 4, 4), (ok.Status, ok.BalanceAfter, ok.ReservedAfter));
        Assert.Equal(CreditOperatorStatus.Invalid, (await OperatorAsync(org, CreditEntryType.Grant, -5, "gecersiz-0001")).Status);
        Assert.Equal(CreditOperatorStatus.Invalid, (await OperatorAsync(org, CreditEntryType.Adjustment, 0, "gecersiz-0002", "x")).Status);

        // Veritabanı ikinci savunma hattı: uygulama hatası olsa bile negatif bakiye yazılamaz
        var negative = await Assert.ThrowsAsync<SqlException>(() => ExecuteRawAsync($"UPDATE credit_accounts SET balance_credits = -1 WHERE organization_id = '{org}'"));
        Assert.Equal(547, negative.Number);
        var overReserved = await Assert.ThrowsAsync<SqlException>(() => ExecuteRawAsync($"UPDATE credit_accounts SET reserved_credits = balance_credits + 1 WHERE organization_id = '{org}'"));
        Assert.Equal(547, overReserved.Number);
        await CreditAssert.ConsistentAsync(Services, org);
    }

    // ---------------------------------------------------------------- değiştirilemez defter ve CHECK'ler

    [Fact]
    public async Task Defter_degistirilemez_UPDATE_ve_DELETE_veritabaninda_reddedilir()
    {
        var org = await OrganizationAsync(credits: 5);

        var update = await Assert.ThrowsAsync<SqlException>(() => ExecuteRawAsync($"UPDATE credit_ledger SET amount = 999 WHERE organization_id = '{org}'"));
        var delete = await Assert.ThrowsAsync<SqlException>(() => ExecuteRawAsync($"DELETE FROM credit_ledger WHERE organization_id = '{org}'"));

        Assert.Equal(51200, update.Number);
        Assert.Equal(51200, delete.Number);
        Assert.Equal(5, Assert.Single(await CreditAssert.LedgerAsync(Services, org)).Amount);
    }

    [Theory]
    [InlineData("Bogus", 1, null, "r", "Operator", "ck_credit_ledger_entry_type")]
    [InlineData("Grant", 1, null, "r", "Bogus", "ck_credit_ledger_source")]
    [InlineData("Grant", 0, null, "r", "Operator", "ck_credit_ledger_amount")]
    [InlineData("Grant", -1, null, "r", "Operator", "ck_credit_ledger_amount")]
    [InlineData("Adjustment", 0, null, "r", "Operator", "ck_credit_ledger_amount")]
    [InlineData("Grant", 1, null, null, "Operator", "ck_credit_ledger_shape")]
    [InlineData("Grant", 1, "00000000-0000-0000-0000-000000000001", "r", "Operator", "ck_credit_ledger_shape")]
    [InlineData("Reserve", 1, null, null, "System", "ck_credit_ledger_shape")]
    public async Task Defter_satiri_gecersiz_tur_kaynak_tutar_ve_sekil_icin_CHECK_ile_reddedilir(string type, int amount, string? jobId, string? reference, string source, string constraint)
    {
        var org = await OrganizationAsync();
        var job = jobId is null ? "NULL" : $"'{jobId}'";
        var reference_ = reference is null ? "NULL" : $"N'{reference}-{Guid.NewGuid():N}'";

        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecuteRawAsync(
            $"INSERT INTO credit_ledger (id, organization_id, entry_type, amount, job_id, reference, source, balance_after, reserved_after, created_at) VALUES (NEWID(), '{org}', N'{type}', {amount}, {job}, {reference_}, N'{source}', 0, 0, SYSDATETIMEOFFSET())"));

        Assert.Equal(547, ex.Number);
        // Geçersiz tür aynı anda şekil CHECK'ini de bozar: hangisinin önce bildirildiği SQL Server'a bağlıdır
        Assert.Contains(type == "Bogus" ? "ck_credit_ledger_" : constraint, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Is_basina_tek_Reserve_ve_tek_kapanis_benzersiz_indeksle_zorlanir()
    {
        var org = await OrganizationAsync(credits: 10);
        var job = Guid.NewGuid();
        Assert.Equal(CreditReserveStatus.Reserved, (await ReserveAsync(org, job, 2)).Status);

        // Aynı iş için ikinci rezervasyon: benzersiz indeks reddeder, transaction geri alınır (hesap değişmez)
        await Assert.ThrowsAsync<DbUpdateException>(() => ReserveAsync(org, job, 2));
        Assert.Equal((10, 2), await CreditAssert.AccountAsync(Services, org));

        await using (var scope = Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CreditLedger.Add(new CreditLedgerEntry { OrganizationId = org, EntryType = CreditEntryType.Release, Amount = 2, JobId = job, Source = CreditSource.System, BalanceAfter = 10, ReservedAfter = 0, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
            db.CreditLedger.Add(new CreditLedgerEntry { OrganizationId = org, EntryType = CreditEntryType.Consume, Amount = 2, JobId = job, Source = CreditSource.System, BalanceAfter = 8, ReservedAfter = 0, CreatedAt = DateTimeOffset.UtcNow });
            var second = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct)); // iade + düşüm aynı işte olamaz (çift düşüm/çift iade yok)
            Assert.IsType<SqlException>(second.InnerException);
        }
    }

    // ---------------------------------------------------------------- eşzamanlılık ve izolasyon

    [Fact]
    public async Task Eszamanli_rezervasyonlar_kullanilabilir_krediyi_asamaz_cift_harcama_yok()
    {
        var org = await OrganizationAsync(credits: 7);

        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => ReserveAsync(org, Guid.NewGuid(), 1)));

        Assert.Equal(7, results.Count(r => r.Status == CreditReserveStatus.Reserved));
        Assert.Equal(17, results.Count(r => r.Status == CreditReserveStatus.Insufficient));
        Assert.Equal((7, 7), await CreditAssert.AccountAsync(Services, org)); // negatif yok, rezerve = bakiye
        Assert.Equal(7, (await CreditAssert.LedgerAsync(Services, org)).Count(e => e.EntryType == CreditEntryType.Reserve));
        await CreditAssert.ConsistentAsync(Services, org);
    }

    [Fact]
    public async Task Firmalar_birbirinin_kredisini_kullanamaz()
    {
        var poor = await OrganizationAsync();
        var rich = await OrganizationAsync(credits: 100);

        var denied = await ReserveAsync(poor, Guid.NewGuid(), 1); // hesap yok: kullanılabilir 0
        Assert.Equal((CreditReserveStatus.Insufficient, 0), (denied.Status, denied.Available));
        await OperatorAsync(poor, CreditEntryType.Grant, 3, "yoksul-yukleme-0001");
        Assert.Equal(CreditReserveStatus.Insufficient, (await ReserveAsync(poor, Guid.NewGuid(), 4)).Status); // zengin firmanın 100'ü görünmez

        Assert.Equal((100, 0), await CreditAssert.AccountAsync(Services, rich));
        Assert.Equal((3, 0), await CreditAssert.AccountAsync(Services, poor));
        Assert.DoesNotContain(await CreditAssert.LedgerAsync(Services, poor), e => e.OrganizationId == rich);
        await CreditAssert.ConsistentAsync(Services, poor);
        await CreditAssert.ConsistentAsync(Services, rich);
    }

    // ---------------------------------------------------------------- API: bakiye ve geçmiş

    private sealed record SummaryDto(int Balance, int Reserved, int Available, int OpenJobs, bool Enforced, int DefaultCostPerJob, List<TariffDto> Tariff);

    private sealed record TariffDto(string Key, int Credits);

    private sealed record LedgerItemDto(Guid Id, CreditEntryType Type, int Amount, int BalanceAfter, int ReservedAfter, Guid? JobId, string? Reference, CreditSource Source, string? Note, DateTimeOffset CreatedAt);

    [Fact]
    public async Task Owner_kendi_bakiyesini_rezervesini_ve_gecmisini_gorur_baska_firmanin_kredisini_gormez()
    {
        ApiFactory.RequireDatabase(Factory);
        var a = await TestData.CreateTenantAsync(Services, StoreRole.Owner, credits: 0);
        var b = await TestData.CreateTenantAsync(Services, StoreRole.Owner, credits: 0);
        await OperatorAsync(a.OrganizationId, CreditEntryType.Grant, 40, "a-yukleme-000001", "ilk paket");
        await OperatorAsync(b.OrganizationId, CreditEntryType.Grant, 900, "b-yukleme-000001");
        Assert.Equal(CreditReserveStatus.Reserved, (await ReserveAsync(a.OrganizationId, Guid.NewGuid(), 3)).Status);
        var clientA = await ApiClient.LoginAsync(Factory, a.Email, a.StoreId);

        var summary = await clientA.GetAsync<SummaryDto>("/api/v1/credits");
        var ledger = await clientA.GetAsync<List<LedgerItemDto>>("/api/v1/credits/ledger");

        Assert.Equal((40, 3, 37, 1), (summary.Balance, summary.Reserved, summary.Available, summary.OpenJobs));
        Assert.Equal(1, summary.DefaultCostPerJob);
        Assert.Equal(2, ledger.Count);
        Assert.Equal(CreditEntryType.Reserve, ledger[0].Type); // en yeni önce
        Assert.Equal("a-yukleme-000001", ledger[1].Reference);
        Assert.DoesNotContain(ledger, e => e.Reference == "b-yukleme-000001");
        Assert.Single(await clientA.GetAsync<List<LedgerItemDto>>("/api/v1/credits/ledger?take=1&skip=1"));
    }

    [Theory]
    [InlineData(StoreRole.Manager)]
    [InlineData(StoreRole.Staff)]
    public async Task Owner_olmayan_kullanici_kredi_ekranini_goremez(StoreRole role)
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, role);
        var client = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);

        await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/credits"), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/credits/ledger"), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        Assert.Equal(HttpStatusCode.Unauthorized, (await new ApiClient(Factory.CreateClient()).GetAsync("/api/v1/credits")).StatusCode);
    }

    [Fact]
    public async Task Hicbir_API_kredi_yukleyemez_duzeltemez_yalnizca_okuma_uclari_vardir()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Owner, credits: 0);
        var client = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            var response = await client.SendAsync(method, "/api/v1/credits", new { balance = 1000, amount = 1000 });
            Assert.True(response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound, $"{method}: {(int)response.StatusCode}");
        }

        Assert.Empty(await CreditAssert.LedgerAsync(Services, tenant.OrganizationId));
    }

    // ---------------------------------------------------------------- kabulde rezervasyon (gerçek mod, HTTP)

    private static Task<HttpResponseMessage> StartRawAsync(ApiClient client, Guid sessionId, string key) =>
        client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/generations", new { }, configure: r => r.Headers.Add("Idempotency-Key", key));

    private static async Task<(TestTenant Tenant, ApiClient Client, Guid SessionId)> LiveSessionAsync(RealModeApiFactory real, int credits)
    {
        var tenant = await TestData.CreateTenantAsync(real.Services, credits: credits);
        var catalog = await TestData.CreateCatalogAsync(real.Services, tenant.OrganizationId);
        var client = await ApiClient.LoginAsync(real, tenant.Email, tenant.StoreId);
        var (session, _) = await PreparedSessionAsync(client);
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
        return (tenant, client, session.Id);
    }

    [Fact]
    public async Task Gercek_modda_kabul_krediyi_ayirir_bedel_ise_yazilir_tekrar_istek_ikinci_rezervasyon_yapmaz()
    {
        ApiFactory.RequireDatabase(Factory);
        using var real = new RealModeApiFactory(Factory.StorageRoot);
        var (tenant, client, sessionId) = await LiveSessionAsync(real, credits: 5);
        var key = $"kredi-{Guid.NewGuid():N}"[..30];

        var accepted = await StartRawAsync(client, sessionId, key);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var replay = await StartRawAsync(client, sessionId, key);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);

        Assert.Equal((5, 1), await CreditAssert.AccountAsync(real.Services, tenant.OrganizationId)); // tek rezervasyon (varsayılan tarife 1)
        var ledger = await CreditAssert.LedgerAsync(real.Services, tenant.OrganizationId);
        var reserve = Assert.Single(ledger, e => e.EntryType == CreditEntryType.Reserve);
        Assert.Equal((1, "default"), (reserve.Amount, reserve.TariffKey));
        await using (var scope = real.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(j => j.Id == reserve.JobId, Ct);
            Assert.Equal(1, job.CreditCost); // işlem anındaki bedel işte kayıtlı (sonradan tarife değişse de değişmez)
        }

        await CreditAssert.ConsistentAsync(real.Services, tenant.OrganizationId);
    }

    [Fact]
    public async Task Gercek_modda_yetersiz_kredi_402_Turkce_hata_is_ve_defter_kaydi_olusmaz()
    {
        ApiFactory.RequireDatabase(Factory);
        using var real = new RealModeApiFactory(Factory.StorageRoot);
        var (tenant, client, sessionId) = await LiveSessionAsync(real, credits: 0);

        var response = await StartRawAsync(client, sessionId, $"yetersiz-{Guid.NewGuid():N}"[..30]);

        await ApiClient.AssertProblemAsync(response, HttpStatusCode.PaymentRequired, "INSUFFICIENT_CREDITS");
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("\"required\":1", body, StringComparison.Ordinal);
        Assert.Contains("\"available\":0", body, StringComparison.Ordinal);
        Assert.Empty(await CreditAssert.LedgerAsync(real.Services, tenant.OrganizationId));
        await using var scope = real.Services.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().GenerationJobs.IgnoreQueryFilters().CountAsync(j => j.SessionId == sessionId, Ct));
    }

    [Fact]
    public async Task Baska_firmanin_kredisi_bu_firmanin_islerini_karsilamaz_krediler_sirayla_biter()
    {
        ApiFactory.RequireDatabase(Factory);
        using var real = new RealModeApiFactory(Factory.StorageRoot);
        var rich = await TestData.CreateTenantAsync(real.Services, credits: 1000);
        var (tenant, client, sessionId) = await LiveSessionAsync(real, credits: 1);

        Assert.Equal(HttpStatusCode.Accepted, (await StartRawAsync(client, sessionId, $"ilk-{Guid.NewGuid():N}"[..30])).StatusCode);
        // Tek kredi ayrıldı: ikinci (farklı anahtarlı, aynı istek) kabul edilirse ikinci kredi gerekirdi; aynı ücretli istek tekil iş kuralı zaten o işi döndürür
        Assert.Equal((1, 1), await CreditAssert.AccountAsync(real.Services, tenant.OrganizationId));
        Assert.Equal((1000, 0), await CreditAssert.AccountAsync(real.Services, rich.OrganizationId)); // zengin firmaya dokunulmadı
    }

    [Fact]
    public async Task Mock_modda_uretim_kredi_tuketmez_ve_kredisiz_firma_engellenmez()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, credits: 0);
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        var client = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        var (session, _) = await PreparedSessionAsync(client);
        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);

        var response = await StartRawAsync(client, session.Id, $"mock-{Guid.NewGuid():N}"[..30]);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); // mock: ücret yok, kredi aranmaz
        Assert.Empty(await CreditAssert.LedgerAsync(Services, tenant.OrganizationId));
    }

    // ---------------------------------------------------------------- kaynak taraması

    [Fact]
    public void Kaynak_kodda_kredi_hesabini_ve_defterini_yazan_tek_yer_defter_servisidir()
    {
        var src = Directory.GetParent(Path.GetDirectoryName(MigrationLab.RepoFile("backend/src/SanalProva.Api/SanalProva.Api.csproj"))!)!.FullName;
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "SanalProva.Infrastructure/Credits/CreditLedgerService.cs" };
        var writes = new Regex(@"CreditLedger\.(Add|AddRange|Remove)\(|CreditAccounts\.(Add|AddRange|Remove)\(|\b(BalanceCredits|ReservedCredits)\s*(=(?![=>])|\+=|-=)|SetProperty\([^;]*(BalanceCredits|ReservedCredits)|credit_ledger|credit_accounts",
            RegexOptions.CultureInvariant);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/Migrations/", StringComparison.Ordinal)
                || relative.EndsWith("Credits/Credits.cs", StringComparison.Ordinal) || relative.EndsWith("CreditConfigurations.cs", StringComparison.Ordinal))
            {
                continue; // varlık tanımı ve EF eşlemesi (yazma değil)
            }

            if (writes.IsMatch(File.ReadAllText(file)) && !allowed.Contains(relative))
            {
                offenders.Add(relative);
            }
        }

        Assert.True(offenders.Count == 0, "Kredi hesabını/defterini yazan izinsiz dosya(lar): " + string.Join(", ", offenders));
    }
}
