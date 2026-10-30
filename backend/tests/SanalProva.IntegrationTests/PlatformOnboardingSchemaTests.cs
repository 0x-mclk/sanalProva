using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.IntegrationTests;

/// <summary>
/// M5.1a: platform onboarding <b>şeması</b> (migration, EF model yapılandırması, bütünlük ve sertleştirme). Onboarding API'si, token tüketme akışı
/// ve React ekranları henüz yok (M5.1b+): bu testler yalnızca veritabanı davranışını sınar. Her test kendi geçici laboratuvar veritabanında
/// (<see cref="MigrationLab"/>) çalışır; <c>sanalprova_dev</c>'e dokunulmaz. Çapraz firma/şube FK reddi genel testte zaten kapsanır
/// (<see cref="DatabaseIntegrityTests.Capraz_firma_ve_capraz_sube_iliskisi_kuran_satirlar_veritabaninda_reddedilir"/>, <c>ExpectedForeignKeys</c>
/// güncellendi); burada yalnızca bu aşamaya özgü davranışlar sınanır.
/// </summary>
public sealed class PlatformOnboardingSchemaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string AgreementAcceptanceScript = "scripts/mssql/harden-agreement-acceptances.sql";

    private static string[] AllMigrations()
    {
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=tcp:127.0.0.1,1;Database=none;User ID=n;Password=n;Encrypt=False", AppDbContext.ConfigureSqlServer).UseSnakeCaseNamingConvention().Options,
            SanalProva.Infrastructure.Tenancy.NoTenantContext.Instance);
        return [.. db.Database.GetMigrations()];
    }

    private static string MigrationId(string name) => AllMigrations().Single(m => m.EndsWith("_" + name, StringComparison.Ordinal));

    [Fact]
    public async Task Yinelenen_eposta_varken_migration_51201_ile_durur_hicbir_sey_degismez_duzeltilince_uygulanir_ve_artik_engellenir()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct, MigrationId("TenantConsistency")); // PlatformOnboarding'den hemen önceki şema
        var rows = await RowFactory.LoadAsync(lab.MigratorCs, Ct);

        // İki farklı firmada aynı normalized_email: bugünkü şema hiçbir şeyi engellemiyor (D4 sistem geneli benzersizlik yeni)
        const string duplicate = "AYNI@ORNEK.TEST";
        await rows.CreateAsync("users", new Dictionary<string, object?> { ["normalized_email"] = duplicate }, Ct);
        var userB = (Guid)(await rows.CreateAsync("users", new Dictionary<string, object?> { ["normalized_email"] = duplicate }, Ct))["id"]!;

        var historyBefore = await lab.ScalarAsync<int>("SELECT COUNT(*) FROM __ef_migrations_history", ct: Ct);

        var ex = await Assert.ThrowsAsync<SqlException>(() => lab.MigrateAsync(Ct));
        Assert.Equal(51201, ex.Number);
        Assert.Contains("normalized_email", ex.Message, StringComparison.Ordinal);

        // Hiçbir şey değişmedi: migration (tek transaction) geri alındı — yeni migration kaydı yok, yeni tablo yok, veri aynen duruyor
        Assert.Equal(historyBefore, await lab.ScalarAsync<int>("SELECT COUNT(*) FROM __ef_migrations_history", ct: Ct));
        Assert.Equal(0, await lab.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name = N'account_setup_tokens'", ct: Ct));
        Assert.Equal(2, await lab.ScalarAsync<int>($"SELECT COUNT(*) FROM users WHERE normalized_email = N'{duplicate}'", ct: Ct));

        // Düzeltilince migration (idempotent olarak) baştan uygulanır
        await lab.ExecuteAsync($"UPDATE users SET normalized_email = N'FARKLI@ORNEK.TEST' WHERE id = '{userB}'", ct: Ct);
        await lab.MigrateAsync(Ct);
        Assert.Equal(1, await lab.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name = N'account_setup_tokens'", ct: Ct));

        // Artık sistem genelinde aynı e-posta ikinci kez yazılamaz (filtreli benzersiz indeks EmailIndex, 2601)
        var dupEx = await Assert.ThrowsAsync<SqlException>(() => rows.CreateAsync("users", new Dictionary<string, object?> { ["normalized_email"] = "FARKLI@ORNEK.TEST" }, Ct));
        Assert.Equal(2601, dupEx.Number);
    }

    [Fact]
    public async Task Sozlesme_kabul_kaydi_yalnizca_eklenir_sanalprova_app_guncelleyip_silemez_firma_basina_surum_tektir_betik_idempotenttir()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        await lab.RunSqlFileAsync(AgreementAcceptanceScript, Ct);
        var rows = await RowFactory.LoadAsync(lab.MigratorCs, Ct);

        var first = await rows.CreateAsync("organization_agreement_acceptances", new Dictionary<string, object?> { ["agreement_version"] = "2026-11-v1" }, Ct);
        var id = (Guid)first["id"]!;
        var org = first["organization_id"]!;

        // sanalprova_app okuyabilir ve ekleyebilir, ama güncelleyip silemez (append-only DENY)
        Assert.Equal(1, await lab.ScalarAsync<int>($"SELECT COUNT(*) FROM organization_agreement_acceptances WHERE id = '{id}'", lab.AppCs, Ct));
        var updateDenied = await Assert.ThrowsAsync<SqlException>(() => lab.ExecuteAsync($"UPDATE organization_agreement_acceptances SET agreement_version = agreement_version WHERE id = '{id}'", lab.AppCs, Ct));
        Assert.Equal(229, updateDenied.Number);
        var deleteDenied = await Assert.ThrowsAsync<SqlException>(() => lab.ExecuteAsync($"DELETE FROM organization_agreement_acceptances WHERE id = '{id}'", lab.AppCs, Ct));
        Assert.Equal(229, deleteDenied.Number);

        // Firma başına aynı sürüm ikinci kez kabul edilemez (benzersizlik); farklı sürüm serbest
        await rows.CreateAsync("organization_agreement_acceptances", new Dictionary<string, object?> { ["organization_id"] = org, ["agreement_version"] = "2027-01-v1" }, Ct);
        var duplicateVersion = await Assert.ThrowsAsync<SqlException>(() => rows.CreateAsync("organization_agreement_acceptances",
            new Dictionary<string, object?> { ["organization_id"] = org, ["agreement_version"] = "2026-11-v1" }, Ct));
        Assert.Equal(2601, duplicateVersion.Number);

        // Çapraz firma: başka firmanın kullanıcısı bu firmanın kabulünü yazamaz (bileşik FK, 547) — açık kanıt (genel test zaten kapsıyor)
        var otherOrgUser = (Guid)(await rows.CreateAsync("users", null, Ct))["id"]!;
        var crossOrg = await Assert.ThrowsAsync<SqlException>(() => rows.CreateAsync("organization_agreement_acceptances",
            new Dictionary<string, object?> { ["organization_id"] = org, ["user_id"] = otherOrgUser, ["agreement_version"] = "2027-02-v1" }, Ct));
        Assert.Equal(547, crossOrg.Number);

        // Betik yeniden çalıştırılsa (idempotent) DENY korunur
        await lab.RunSqlFileAsync(AgreementAcceptanceScript, Ct);
        var stillDenied = await Assert.ThrowsAsync<SqlException>(() => lab.ExecuteAsync($"UPDATE organization_agreement_acceptances SET agreement_version = agreement_version WHERE id = '{id}'", lab.AppCs, Ct));
        Assert.Equal(229, stillDenied.Number);
    }

    [Fact]
    public async Task Kurulum_tokeni_suresi_olusturmadan_once_olamaz_hash_benzersizdir_kullanici_silinince_token_da_gider()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        var rows = await RowFactory.LoadAsync(lab.MigratorCs, Ct);

        var now = DateTimeOffset.UtcNow;
        var hash = new byte[32];
        Random.Shared.NextBytes(hash);
        var token = await rows.CreateAsync("account_setup_tokens", new Dictionary<string, object?>
        {
            ["token_hash"] = hash, ["created_at"] = now, ["expires_at"] = now.AddHours(72), ["purpose"] = "InitialPassword",
        }, Ct);
        var userId = token["user_id"]!;
        var orgId = token["organization_id"]!;

        // CHECK: expires_at > created_at (eşit veya önce olamaz)
        var otherHash = new byte[32];
        Random.Shared.NextBytes(otherHash);
        var checkViolation = await Assert.ThrowsAsync<SqlException>(() => rows.CreateAsync("account_setup_tokens", new Dictionary<string, object?>
        {
            ["organization_id"] = orgId, ["user_id"] = userId, ["token_hash"] = otherHash, ["created_at"] = now, ["expires_at"] = now, ["purpose"] = "InitialPassword",
        }, Ct));
        Assert.Equal(547, checkViolation.Number);

        // Token hash benzersiz: aynı hash ikinci kez yazılamaz (iki ayrı token aynı düz token'a işaret edemez)
        var duplicateHash = await Assert.ThrowsAsync<SqlException>(() => rows.CreateAsync("account_setup_tokens", new Dictionary<string, object?>
        {
            ["organization_id"] = orgId, ["user_id"] = userId, ["token_hash"] = hash, ["purpose"] = "InitialPassword",
        }, Ct));
        Assert.Equal(2601, duplicateHash.Number);

        // Kullanıcı silinince token da gider (CASCADE; ephemeral kayıt, append-only değil)
        await lab.ExecuteAsync($"DELETE FROM users WHERE id = '{userId}'", ct: Ct);
        Assert.Equal(0, await lab.ScalarAsync<int>($"SELECT COUNT(*) FROM account_setup_tokens WHERE user_id = '{userId}'", ct: Ct));
    }

    [Fact]
    public async Task Yeni_hata_kodu_PaidAiNotEnabled_generation_jobs_ve_ai_usage_logs_CHECK_kisitindan_gecer_EF_okur()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        var rows = await RowFactory.LoadAsync(lab.MigratorCs, Ct);

        var job = await rows.CreateAsync("generation_jobs", new Dictionary<string, object?> { ["error_code"] = "PaidAiNotEnabled" }, Ct);
        var jobId = (Guid)job["id"]!;
        await using (var db = MigrationLab.OpenDb(lab.AppCs))
        {
            var read = await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(j => j.Id == jobId, Ct);
            Assert.Equal(GenerationErrorCode.PaidAiNotEnabled, read.ErrorCode);
        }

        // ai_usage_logs.error_code CHECK'i de aynı şekilde genişledi (EF her iki tabloyu da güncelledi)
        var usage = await rows.CreateAsync("ai_usage_logs", new Dictionary<string, object?> { ["error_code"] = "PaidAiNotEnabled", ["success"] = false, ["estimated_cost_usd"] = 0m }, Ct);
        Assert.Equal("PaidAiNotEnabled", await lab.ScalarAsync<string>($"SELECT error_code FROM ai_usage_logs WHERE id = '{usage["id"]}'", ct: Ct));

        // Eski değerler hâlâ geçerli (CHECK genişledi, daraltılmadı)
        await rows.CreateAsync("generation_jobs", new Dictionary<string, object?> { ["error_code"] = "Unknown" }, Ct);
    }
}
