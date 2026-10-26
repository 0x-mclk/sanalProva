using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SanalProva.Api.Features.Admin;
using SanalProva.Domain.Generation;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.IntegrationTests;

/// <summary>
/// M3.1: sonucu belirsiz AI maliyetinin mutabakatı. Kanıtlananlar: (1) yalnızca platform yöneticisi karar verir (mağaza Owner'ı dahil kimse veremez, yetki her
/// istekte veritabanından okunur); (2) her karar tek transaction'da düzeltme satırı + durum + denetim yazar; (3) aynı istek ikinci kayıt üretmez, eşzamanlı
/// iki yönetici aynı sırayı yazamaz, ama sonraki düzeltme kararları engellenmez; (4) usage raporu çağrı sayısını bozmadan tutarı düzeltir; (5) veritabanı
/// kısıtları (benzersiz olay anahtarı, düzeltme CHECK'i, karar enum'u) uygulamadan bağımsız korur.
/// </summary>
public sealed class CostReconciliationTests(ApiFactory factory) : ApiTestBase(factory)
{
    private const string Cases = "/api/v1/platform/cost-cases";

    private async Task<(TestTenant Tenant, ApiClient Client)> PlatformAdminAsync()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.Users.Where(u => u.Email == tenant.Email).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsPlatformAdmin, true), TestContext.Current.CancellationToken);
        }

        return (tenant, await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId));
    }

    private async Task<AiUsageLog> SeedCaseAsync(TestTenant tenant, decimal reserved = 0.15m, DateTimeOffset? occurredAt = null)
    {
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var log = new AiUsageLog
            {
                OrganizationId = tenant.OrganizationId, StoreId = tenant.StoreId, Provider = "Fashn", Model = "tryon-max", Operation = "OutfitSet", Attempt = 1,
                Success = false, ErrorCode = GenerationErrorCode.OutcomeUncertain, EstimatedCostUsd = reserved, EventKey = $"uncertain:{Guid.NewGuid()}",
                ReconciliationState = CostReconciliationState.Open, OccurredAt = occurredAt,
            };
            db.AiUsageLogs.Add(log);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return log;
        }
    }

    private static string NewKey() => Guid.NewGuid().ToString("N");

    private static Task<HttpResponseMessage> DecideRawAsync(ApiClient client, Guid id, object body, string? key) =>
        client.SendAsync(HttpMethod.Post, $"{Cases}/{id}/decisions", body, configure: r =>
        {
            if (key is not null)
            {
                r.Headers.Add("Idempotency-Key", key);
            }
        });

    private static object Charged(decimal amount, int expected = 0, string? note = null) => new { decision = "ConfirmedCharged", amountUsd = amount, note, expectedSequence = expected };

    private static object NotCharged(int expected = 0) => new { decision = "ConfirmedNotCharged", expectedSequence = expected };

    private async Task<(List<AiCostReconciliation> Decisions, List<AiUsageLog> Adjustments, AiUsageLog Case, List<Domain.Audit.AuditLog> Audit)> LoadAsync(AiUsageLog seeded)
    {
        var ct = TestContext.Current.CancellationToken;
        var (scope, db) = OpenDb();
        using (scope)
        {
            var decisions = await db.AiCostReconciliations.IgnoreQueryFilters().AsNoTracking().Where(d => d.UsageLogId == seeded.Id).OrderBy(d => d.Sequence).ToListAsync(ct);
            var adjustments = await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().Where(l => l.AdjustsLogId == seeded.Id).OrderBy(l => l.CreatedAt).ToListAsync(ct);
            var current = await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == seeded.Id, ct);
            var audit = await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().Where(a => a.EntityId == seeded.Id && a.Action.StartsWith("Cost.")).OrderBy(a => a.Id).ToListAsync(ct);
            return (decisions, adjustments, current, audit);
        }
    }

    // ------------------------------------------------------------------ yetki

    [Fact]
    public async Task Magaza_rolleri_ve_anonim_kullanici_platform_maliyet_uc_noktalarina_erisemez()
    {
        ApiFactory.RequireDatabase(Factory);
        var owner = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var manager = await TestData.CreateTenantAsync(Services, StoreRole.Manager);
        var staff = await TestData.CreateTenantAsync(Services, StoreRole.Staff);
        var seeded = await SeedCaseAsync(owner);

        foreach (var tenant in new[] { owner, manager, staff })
        {
            var client = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
            await ApiClient.AssertProblemAsync(await client.GetAsync(Cases), HttpStatusCode.Forbidden, "PLATFORM_ADMIN_REQUIRED");
            await ApiClient.AssertProblemAsync(await client.GetAsync($"{Cases}/{seeded.Id}"), HttpStatusCode.Forbidden, "PLATFORM_ADMIN_REQUIRED");
            await ApiClient.AssertProblemAsync(await DecideRawAsync(client, seeded.Id, NotCharged(), NewKey()), HttpStatusCode.Forbidden, "PLATFORM_ADMIN_REQUIRED");
        }

        var anonymous = new ApiClient(Factory.CreateClient());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Cases)).StatusCode);

        var (decisions, adjustments, current, audit) = await LoadAsync(seeded);
        Assert.Empty(decisions);
        Assert.Empty(adjustments);
        Assert.Equal(CostReconciliationState.Open, current.ReconciliationState); // reddedilen hiçbir istek veriyi değiştirmedi
        Assert.Empty(audit);
    }

    [Fact]
    public async Task Platform_yetkisi_her_istekte_veritabanindan_okunur_geri_alinan_ve_pasif_hesap_hemen_reddedilir()
    {
        var (tenant, admin) = await PlatformAdminAsync();
        var seeded = await SeedCaseAsync(tenant);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(Cases)).StatusCode);

        var (scope, db) = OpenDb();
        using (scope)
        {
            await db.Users.Where(u => u.Email == tenant.Email).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsPlatformAdmin, false), TestContext.Current.CancellationToken);
        }

        // Aynı oturum çerezi: yetki geri alındığı anda reddedilir (claim'e değil veritabanına güvenilir)
        await ApiClient.AssertProblemAsync(await admin.GetAsync(Cases), HttpStatusCode.Forbidden, "PLATFORM_ADMIN_REQUIRED");
        await ApiClient.AssertProblemAsync(await DecideRawAsync(admin, seeded.Id, NotCharged(), NewKey()), HttpStatusCode.Forbidden, "PLATFORM_ADMIN_REQUIRED");

        (scope, db) = OpenDb();
        using (scope)
        {
            await db.Users.Where(u => u.Email == tenant.Email).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsPlatformAdmin, true).SetProperty(u => u.IsActive, false), TestContext.Current.CancellationToken);
        }

        Assert.True((await admin.GetAsync(Cases)).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
        Assert.Empty((await LoadAsync(seeded)).Decisions);
    }

    [Fact]
    public async Task Platform_yoneticisi_tum_firmalarin_vakalarini_gorur_mağaza_yoneticisi_yalniz_kendi_maliyetini_gorur()
    {
        var (_, admin) = await PlatformAdminAsync();
        var other = await TestData.CreateTenantAsync(Services, StoreRole.Manager);
        var seeded = await SeedCaseAsync(other);

        var list = await admin.GetAsync<List<CostCaseDto>>($"{Cases}?state=Open&take=200");
        var found = Assert.Single(list, c => c.Id == seeded.Id);
        Assert.Equal((other.OrganizationId, 0.15m, 0, CostReconciliationState.Open), (found.OrganizationId, found.EffectiveCostUsd, found.DecisionCount, found.State));
        Assert.False(string.IsNullOrWhiteSpace(found.OrganizationName));

        // Mağaza yöneticisi kendi firmasının usage raporunu görür; başka firmanın vakası raporuna girmez
        var manager = await ApiClient.LoginAsync(Factory, other.Email, other.StoreId);
        var report = await manager.GetAsync<UsageReport>("/api/v1/admin/usage");
        Assert.Equal(0.15m, report.Totals.EstimatedCostUsd);
        var stranger = await TestData.CreateTenantAsync(Services, StoreRole.Manager);
        var strangerReport = await (await ApiClient.LoginAsync(Factory, stranger.Email, stranger.StoreId)).GetAsync<UsageReport>("/api/v1/admin/usage");
        Assert.Equal(0m, strangerReport.Totals.EstimatedCostUsd);
    }

    // ------------------------------------------------------------------ kararlar

    [Fact]
    public async Task Gercek_tutar_kararı_duzeltme_satiri_durum_ve_denetimi_tek_seferde_yazar_rapor_cagri_sayisini_bozmaz()
    {
        var (_, admin) = await PlatformAdminAsync();
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Manager);
        var seeded = await SeedCaseAsync(tenant, 0.15m);

        var detail = await ApiClient.ReadAsync<CostCaseDetail>(await DecideRawAsync(admin, seeded.Id, Charged(0.12m, note: "Fashn paneli 0.12"), NewKey()), HttpStatusCode.Created);
        Assert.Equal((CostReconciliationState.ConfirmedCharged, 0.12m, 1), (detail.Case.State, detail.Case.EffectiveCostUsd, detail.Case.DecisionCount));
        Assert.Equal(0.15m, detail.Case.ReservedCostUsd); // ilk kayıt değişmedi

        var (decisions, adjustments, current, audit) = await LoadAsync(seeded);
        var decision = Assert.Single(decisions);
        Assert.Equal((1, CostDecision.ConfirmedCharged, 0.12m), (decision.Sequence, decision.Decision, decision.EffectiveCostUsd));
        var adjustment = Assert.Single(adjustments);
        Assert.True(adjustment.IsAdjustment);
        Assert.Equal(-0.03m, adjustment.EstimatedCostUsd); // fark: 0,12 − 0,15
        Assert.Equal(0.12m, adjustment.ActualCostUsd);
        Assert.Equal($"reconcile:{decision.Id}", adjustment.EventKey);
        Assert.Equal(0.15m, current.EstimatedCostUsd); // asıl satır değiştirilmez/silinmez
        Assert.Equal(CostReconciliationState.ConfirmedCharged, current.ReconciliationState);
        var log = Assert.Single(audit);
        Assert.Equal(("Cost.ConfirmedCharged", "AiUsageLog", tenant.OrganizationId), (log.Action, log.EntityType, log.OrganizationId));
        Assert.NotNull(log.UserId);

        var manager = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        var report = await manager.GetAsync<UsageReport>("/api/v1/admin/usage");
        Assert.Equal(0.12m, report.Totals.EstimatedCostUsd); // tutar düzeltildi
        Assert.Equal(1, report.Totals.Calls); // düzeltme satırı çağrı sayılmaz
        Assert.Equal(1, report.Totals.Failures);
    }

    [Fact]
    public async Task Ucretlenmedi_karari_butceyi_serbest_birakir_duzeltme_karari_engellenmez_zincir_tutarli_kalir()
    {
        var (_, admin) = await PlatformAdminAsync();
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Manager);
        var seeded = await SeedCaseAsync(tenant, 0.15m);

        await ApiClient.ReadAsync<CostCaseDetail>(await DecideRawAsync(admin, seeded.Id, NotCharged(0), NewKey()), HttpStatusCode.Created);
        var second = await ApiClient.ReadAsync<CostCaseDetail>(await DecideRawAsync(admin, seeded.Id, Charged(0.15m, expected: 1, note: "Fatura geldi"), NewKey()), HttpStatusCode.Created);
        var third = await ApiClient.ReadAsync<CostCaseDetail>(await DecideRawAsync(admin, seeded.Id, new { decision = "WrittenOff", expectedSequence = 2 }, NewKey()), HttpStatusCode.Created);

        Assert.Equal(0.15m, second.Case.EffectiveCostUsd);
        Assert.Equal((CostReconciliationState.WrittenOff, 0.15m, 3), (third.Case.State, third.Case.EffectiveCostUsd, third.Case.DecisionCount)); // yazılan zarar en kötü tutarda kalır
        var (decisions, adjustments, current, audit) = await LoadAsync(seeded);
        Assert.Equal([1, 2, 3], decisions.Select(d => d.Sequence).ToArray());
        Assert.Equal(["Cost.ConfirmedNotCharged", "Cost.ConfirmedCharged", "Cost.WrittenOff"], audit.Select(a => a.Action).ToArray());
        // Defter tutarlılığı: ilk tutar + düzeltmelerin toplamı = son karardaki efektif maliyet
        Assert.Equal(decisions[^1].EffectiveCostUsd, current.EstimatedCostUsd + adjustments.Sum(a => a.EstimatedCostUsd));
        Assert.Equal([-0.15m, 0.15m, 0m], adjustments.Select(a => a.EstimatedCostUsd).ToArray());
        Assert.Equal(CostReconciliationState.WrittenOff, current.ReconciliationState);
    }

    [Fact]
    public async Task Duzeltme_satiri_harcamanin_dogdugu_gune_yazilir_bugunun_butcesini_bozmaz()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, admin) = await PlatformAdminAsync();
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Manager);
        var seeded = await SeedCaseAsync(tenant, 0.15m, occurredAt: DateTimeOffset.UtcNow.AddDays(-1)); // harcama dün doğdu
        var budget = Services.GetRequiredService<SanalProva.Infrastructure.Generation.BudgetGuard>();

        async Task<decimal> TodayAsync()
        {
            var (scope, db) = OpenDb();
            using (scope)
            {
                return (await budget.GetSpendAsync(db, tenant.OrganizationId, null, ct)).Today;
            }
        }

        Assert.Equal(0m, await TodayAsync()); // dünkü belirsiz harcama bugünün bütçesinde değil
        await ApiClient.ReadAsync<CostCaseDetail>(await DecideRawAsync(admin, seeded.Id, NotCharged(), NewKey()), HttpStatusCode.Created);
        Assert.Equal(0m, await TodayAsync()); // bugün verilen karar bugünü eksiye çekmez: düzeltme dünün satırıdır
        var (_, adjustments, _, _) = await LoadAsync(seeded);
        Assert.Equal(seeded.OccurredAt, Assert.Single(adjustments).OccurredAt);
    }

    [Fact]
    public async Task Ayni_Idempotency_Key_ikinci_kayit_olusturmaz_farkli_govdeyle_kullanilirsa_reddedilir()
    {
        var (_, admin) = await PlatformAdminAsync();
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Manager);
        var seeded = await SeedCaseAsync(tenant);
        var key = NewKey();

        var first = await ApiClient.ReadAsync<CostCaseDetail>(await DecideRawAsync(admin, seeded.Id, NotCharged(), key), HttpStatusCode.Created);
        var replay = await ApiClient.ReadAsync<CostCaseDetail>(await DecideRawAsync(admin, seeded.Id, NotCharged(), key), HttpStatusCode.OK);
        Assert.Equal(first.Decisions[0].Sequence, replay.Decisions[0].Sequence);
        await ApiClient.AssertProblemAsync(await DecideRawAsync(admin, seeded.Id, Charged(0.10m), key), HttpStatusCode.UnprocessableEntity, "IDEMPOTENCY_KEY_REUSED");

        var (decisions, adjustments, _, audit) = await LoadAsync(seeded);
        Assert.Single(decisions);
        Assert.Single(adjustments);
        Assert.Single(audit); // tekrar isteği denetim satırını da çoğaltmaz
    }

    [Fact]
    public async Task Eszamanli_ayni_istek_tek_karar_yazar_eszamanli_iki_farkli_karardan_yalnizca_biri_kazanir()
    {
        var (_, admin) = await PlatformAdminAsync();
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Manager);

        // 12 eşzamanlı tekrar (aynı anahtar): tek karar, biri 201 diğerleri 200
        var seeded = await SeedCaseAsync(tenant);
        var key = NewKey();
        var repeated = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => DecideRawAsync(admin, seeded.Id, NotCharged(), key)));
        Assert.Equal(1, repeated.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(repeated.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var (decisions, adjustments, _, audit) = await LoadAsync(seeded);
        Assert.Equal((1, 1, 1), (decisions.Count, adjustments.Count, audit.Count));

        // 8 eşzamanlı FARKLI anahtar, aynı beklenen sıra: yalnızca biri yazar, diğerleri 409 (iki yönetici birbirinin kararını ezemez)
        var contested = await SeedCaseAsync(tenant);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => DecideRawAsync(admin, contested.Id, i % 2 == 0 ? NotCharged() : Charged(0.10m), NewKey())));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        foreach (var rejected in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            Assert.Contains("DECISION_CONFLICT", await rejected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        var after = await LoadAsync(contested);
        Assert.Equal((1, 1, 1), (after.Decisions.Count, after.Adjustments.Count, after.Audit.Count));
    }

    [Fact]
    public async Task Gecersiz_istekler_reddedilir_ve_hicbir_sey_yazilmaz()
    {
        var (_, admin) = await PlatformAdminAsync();
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Manager);
        var seeded = await SeedCaseAsync(tenant);

        await ApiClient.AssertProblemAsync(await DecideRawAsync(admin, seeded.Id, NotCharged(), null), HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_REQUIRED");
        await ApiClient.AssertProblemAsync(await DecideRawAsync(admin, seeded.Id, NotCharged(), "kisa"), HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_REQUIRED");
        await ApiClient.AssertProblemAsync(await DecideRawAsync(admin, seeded.Id, new { decision = "ConfirmedCharged", expectedSequence = 0 }, NewKey()), HttpStatusCode.BadRequest, "INVALID_AMOUNT");
        await ApiClient.AssertProblemAsync(await DecideRawAsync(admin, seeded.Id, Charged(0m), NewKey()), HttpStatusCode.BadRequest, "INVALID_AMOUNT");
        await ApiClient.AssertProblemAsync(await DecideRawAsync(admin, seeded.Id, Charged(-1m), NewKey()), HttpStatusCode.BadRequest, "INVALID_AMOUNT");
        await ApiClient.AssertProblemAsync(await DecideRawAsync(admin, seeded.Id, Charged(1000.01m), NewKey()), HttpStatusCode.BadRequest, "INVALID_AMOUNT");
        await ApiClient.AssertProblemAsync(await DecideRawAsync(admin, seeded.Id, Charged(0.12345m), NewKey()), HttpStatusCode.BadRequest, "INVALID_AMOUNT");
        await ApiClient.AssertProblemAsync(await DecideRawAsync(admin, seeded.Id, new { decision = "ConfirmedNotCharged", amountUsd = 0.1m, expectedSequence = 0 }, NewKey()), HttpStatusCode.BadRequest, "INVALID_AMOUNT");
        await ApiClient.AssertProblemAsync(await DecideRawAsync(admin, seeded.Id, new { decision = "WrittenOff", amountUsd = 0.1m, expectedSequence = 0 }, NewKey()), HttpStatusCode.BadRequest, "INVALID_AMOUNT");
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideRawAsync(admin, seeded.Id, new { decision = "Open", expectedSequence = 0 }, NewKey())).StatusCode); // "Open" karar değildir
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideRawAsync(admin, seeded.Id, Charged(0.1m, note: new string('x', 201)), NewKey())).StatusCode);
        await ApiClient.AssertProblemAsync(await DecideRawAsync(admin, seeded.Id, NotCharged(expected: 3), NewKey()), HttpStatusCode.Conflict, "DECISION_CONFLICT");
        await ApiClient.AssertProblemAsync(await DecideRawAsync(admin, Guid.NewGuid(), NotCharged(), NewKey()), HttpStatusCode.NotFound, "NOT_FOUND");

        // Sıradan (belirsiz olmayan) kullanım satırı vaka değildir
        var (scope, db) = OpenDb(tenant.OrganizationId);
        Guid ordinary;
        using (scope)
        {
            var row = new AiUsageLog { OrganizationId = tenant.OrganizationId, StoreId = tenant.StoreId, Provider = "Fashn", Model = "m", Operation = "OutfitSet", Success = true, EstimatedCostUsd = 0.15m };
            db.AiUsageLogs.Add(row);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            ordinary = row.Id;
        }

        await ApiClient.AssertProblemAsync(await DecideRawAsync(admin, ordinary, NotCharged(), NewKey()), HttpStatusCode.NotFound, "NOT_FOUND");

        var (decisions, adjustments, current, audit) = await LoadAsync(seeded);
        Assert.Equal((0, 0, 0, CostReconciliationState.Open), (decisions.Count, adjustments.Count, audit.Count, current.ReconciliationState));
    }

    // ------------------------------------------------------------------ veritabanı kısıtları

    [Fact]
    public async Task Veritabani_ayni_olay_anahtarini_ayni_karar_sirasini_ve_yetim_duzeltmeyi_reddeder()
    {
        var ct = TestContext.Current.CancellationToken;
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Manager);
        var seeded = await SeedCaseAsync(tenant);

        AiUsageLog Row(string? key, bool adjustment = false, Guid? parent = null) => new()
        {
            OrganizationId = tenant.OrganizationId, StoreId = tenant.StoreId, Provider = "Fashn", Model = "m", Operation = "OutfitSet", EstimatedCostUsd = 0.1m,
            EventKey = key, IsAdjustment = adjustment, AdjustsLogId = parent,
        };

        // 1) Aynı olay anahtarı ikinci kez yazılamaz (işlemci, kurtarma SQL'i, süpürücü ve karar servisi aynı korumaya dayanır)
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            db.AiUsageLogs.Add(Row(seeded.EventKey));
            var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
            Assert.True(DbErrors.IsUniqueViolation(duplicate));
            Assert.Contains("ux_ai_usage_logs_event_key", duplicate.InnerException?.Message, StringComparison.Ordinal);
        }

        // 2) Anahtarsız sıradan satırlar çoğalabilir (bir iş birden çok deneme yazar): filtreli indeks onları kapsamaz
        (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            db.AiUsageLogs.AddRange(Row(null), Row(null));
            await db.SaveChangesAsync(ct);
        }

        // 3) Düzeltme satırı asıl kayıt ve olay anahtarı olmadan yazılamaz (CHECK)
        (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            db.AiUsageLogs.Add(Row("reconcile:x", adjustment: true, parent: null));
            var orphan = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
            Assert.Contains("ck_ai_usage_logs_adjustment", orphan.InnerException?.Message, StringComparison.Ordinal);
        }

        // 4) Aynı vakada aynı sıra / aynı anahtar ikinci kez yazılamaz; "Open" bir karar değildir (enum CHECK)
        AiCostReconciliation Decision(int sequence, string key) => new()
        {
            OrganizationId = tenant.OrganizationId, UsageLogId = seeded.Id, Sequence = sequence, Decision = CostDecision.WrittenOff, EffectiveCostUsd = 0.15m,
            DecidedByUserId = Guid.NewGuid(), IdempotencyKey = key, RequestHash = "h",
        };

        (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            db.AiCostReconciliations.Add(Decision(1, "key-1"));
            await db.SaveChangesAsync(ct);
        }

        foreach (var (sequence, key, index) in new[] { (1, "key-2", "ux_ai_cost_reconciliations_case_sequence"), (2, "key-1", "ux_ai_cost_reconciliations_case_key") })
        {
            (scope, db) = OpenDb(tenant.OrganizationId);
            using (scope)
            {
                db.AiCostReconciliations.Add(Decision(sequence, key));
                var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
                Assert.True(DbErrors.IsUniqueViolation(ex));
                Assert.Contains(index, ex.InnerException?.Message, StringComparison.Ordinal);
            }
        }

        (scope, db) = OpenDb();
        using (scope)
        {
            var open = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO ai_cost_reconciliations (id, created_at, updated_at, organization_id, usage_log_id, sequence, decision, effective_cost_usd, decided_by_user_id, idempotency_key, request_hash)
                VALUES (NEWID(), SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), {tenant.OrganizationId}, {seeded.Id}, 9, N'Open', 0, NEWID(), N'k9', N'h')
                """, ct));
            Assert.Contains("ck_ai_cost_reconciliations_decision", open.Message, StringComparison.Ordinal);
        }
    }
}
