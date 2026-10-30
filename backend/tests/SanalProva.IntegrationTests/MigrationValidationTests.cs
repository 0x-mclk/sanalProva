using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Generation;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.IntegrationTests;

/// <summary>
/// M3.2: SQL Server migration zincirinin güvenilirliği. Her senaryo kendi geçici veritabanında (<see cref="MigrationLab"/>) çalışır; dev/test veritabanlarına
/// yazılmaz. Kanıtlananlar: sıfırdan kurulum, tekrar çalıştırılabilirlik, eşzamanlı başlatma, idempotent betik, EF snapshot'ı ve gerçek şemanın uyumu
/// (tür, null, indeks, filtre, CHECK, FK davranışı, güvenilirlik), veri içeren veritabanında ileri migration ve M3.1 maliyet kayıtlarının korunması,
/// migrator/app hesap yetkileri ve migration'ların veri kaybı riski.
/// </summary>
public sealed class MigrationValidationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] AllMigrations()
    {
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=tcp:127.0.0.1,1;Database=none;User ID=n;Password=n;Encrypt=False", AppDbContext.ConfigureSqlServer).UseSnakeCaseNamingConvention().Options,
            SanalProva.Infrastructure.Tenancy.NoTenantContext.Instance);
        return [.. db.Database.GetMigrations()];
    }

    private static string MigrationId(string name) => AllMigrations().Single(m => m.EndsWith("_" + name, StringComparison.Ordinal));

    // ------------------------------------------------------------------ sıfırdan kurulum, sıra, tekrar çalıştırma

    [Fact]
    public async Task Bos_veritabanina_butun_zincir_sirayla_uygulanir_tekrar_calistirmak_hicbir_sey_yapmaz()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        var migrations = AllMigrations();

        // Zincir: zaman damgalı, benzersiz, sıralı, beklenen son halka
        Assert.Equal(migrations.Order(StringComparer.Ordinal).ToArray(), migrations);
        Assert.Equal(migrations.Length, migrations.Distinct().Count());
        Assert.All(migrations, m => Assert.Matches(@"^\d{14}_[A-Za-z0-9]+$", m));
        Assert.EndsWith("_CreditSystem", migrations[^1], StringComparison.Ordinal);

        await lab.MigrateAsync(Ct);
        await using (var db = lab.OpenMigrator())
        {
            Assert.Equal(migrations, (await db.Database.GetAppliedMigrationsAsync(Ct)).ToArray()); // hepsi, aynı sırayla
            Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        }

        var before = await SchemaInspector.ReadAsync(lab.MigratorCs, Ct);
        await lab.MigrateAsync(Ct); // ikinci çalıştırma
        await using (var db = lab.OpenMigrator())
        {
            Assert.Equal(migrations.Length, await lab.ScalarAsync<int>("SELECT COUNT(*) FROM __ef_migrations_history", ct: Ct));
            Assert.Equal(before.Fingerprint(), (await SchemaInspector.ReadAsync(lab.MigratorCs, Ct)).Fingerprint());
        }
    }

    [Fact]
    public async Task EF_snapshot_ile_model_ve_gercek_sema_birebir_uyumludur()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);

        await using var db = lab.OpenMigrator();
        Assert.False(db.Database.HasPendingModelChanges(), "Model ile snapshot ayrışmış: yeni migration gerekir.");

        // Tür, null, indeks anahtarı/benzersizlik/filtre, CHECK içeriği, FK silme davranışı ve güvenilirlik: model ile veritabanı farksız olmalı
        var differences = await SchemaInspector.DifferencesAsync(db, lab.MigratorCs, Ct);
        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));

        // Kararlaştırılan veritabanı ayarları
        Assert.Equal(160, await lab.ScalarAsync<int>("SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()", ct: Ct));
        Assert.Equal(1, await lab.ScalarAsync<int>("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()", ct: Ct));
        Assert.Equal("Latin1_General_100_CI_AS_SC", await lab.ScalarAsync<string>("SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(100))", ct: Ct));
        Assert.Equal("Turkish_100_CI_AS", await lab.ScalarAsync<string>("SELECT collation_name FROM sys.columns WHERE object_id = OBJECT_ID(N'products') AND name = N'name'", ct: Ct));
    }

    [Fact]
    public async Task Eszamanli_baslayan_uygulamalar_migration_yarisi_yasamaz()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);

        // Birden çok uygulama örneği aynı anda açılırsa (dağıtım): EF'in migration kilidi tek birinin uygulamasını sağlar, diğerleri bekleyip no-op olur
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => lab.MigrateAsync(Ct)));

        Assert.Equal(AllMigrations().Length, await lab.ScalarAsync<int>("SELECT COUNT(*) FROM __ef_migrations_history", ct: Ct));
        await using var db = lab.OpenMigrator();
        Assert.Empty(await SchemaInspector.DifferencesAsync(db, lab.MigratorCs, Ct));
    }

    // ------------------------------------------------------------------ idempotent betik

    [Fact]
    public async Task Idempotent_betik_sifirdan_ve_ara_surumden_ayni_sema_uretir_tekrar_calistirmak_zararsizdir()
    {
        await using var clean = await MigrationLab.CreateAsync(Ct);
        await using var scripted = await MigrationLab.CreateAsync(Ct);
        await using var midway = await MigrationLab.CreateAsync(Ct);
        await using var upToDate = await MigrationLab.CreateAsync(Ct);

        await clean.MigrateAsync(Ct);
        var expected = (await SchemaInspector.ReadAsync(clean.MigratorCs, Ct)).Fingerprint();

        string script;
        await using (var db = scripted.OpenMigrator())
        {
            script = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        }

        Assert.Contains("__ef_migrations_history", script, StringComparison.Ordinal);
        Assert.Contains("IF NOT EXISTS", script, StringComparison.Ordinal); // her migration yalnızca uygulanmamışsa çalışır

        // 1) Boş veritabanı: betik iki kez çalıştırılır
        await scripted.RunScriptAsync(script, Ct);
        var once = (await SchemaInspector.ReadAsync(scripted.MigratorCs, Ct)).Fingerprint();
        await scripted.RunScriptAsync(script, Ct);
        Assert.Equal(expected, once);
        Assert.Equal(once, (await SchemaInspector.ReadAsync(scripted.MigratorCs, Ct)).Fingerprint());
        Assert.Equal(AllMigrations().Length, await scripted.ScalarAsync<int>("SELECT COUNT(*) FROM __ef_migrations_history", ct: Ct));

        // 2) Ara sürümde duran veritabanı (ilk 3 migration): betik yalnızca eksikleri uygular
        await midway.MigrateAsync(Ct, MigrationId("AdmissionDedupeKey"));
        await midway.RunScriptAsync(script, Ct);
        Assert.Equal(expected, (await SchemaInspector.ReadAsync(midway.MigratorCs, Ct)).Fingerprint());

        // 3) Zaten güncel (Migrate ile kurulmuş) veritabanı: betik hata vermez, hiçbir şeyi değiştirmez
        await upToDate.MigrateAsync(Ct);
        await upToDate.RunScriptAsync(script, Ct);
        Assert.Equal(expected, (await SchemaInspector.ReadAsync(upToDate.MigratorCs, Ct)).Fingerprint());
    }

    // ------------------------------------------------------------------ veri içeren veritabanında ileri migration

    [Fact]
    public async Task Veri_iceren_veritabaninda_ileri_migration_veriyi_korur_temiz_kurulumla_ayni_semaya_varir()
    {
        await using var upgraded = await MigrationLab.CreateAsync(Ct);
        await using var clean = await MigrationLab.CreateAsync(Ct);
        await clean.MigrateAsync(Ct);
        var expected = (await SchemaInspector.ReadAsync(clean.MigratorCs, Ct)).Fingerprint();

        // M3.1 öncesi (R4) şema + gerçeğe yakın veri: sonucu belirsiz iş + R4 döneminde yazılmış (anahtarsız) maliyet satırı, ayrıca satırı olmayan belirsiz iş
        await upgraded.MigrateAsync(Ct, MigrationId("ProviderOutcomeUncertain"));
        Assert.False(await HasColumnAsync(upgraded, "ai_usage_logs", "event_key"));
        var seed = await Seed.BaselineAsync(upgraded, Ct);

        var costBefore = await upgraded.ScalarAsync<decimal>("SELECT SUM(estimated_cost_usd) FROM ai_usage_logs", ct: Ct);
        var rowsBefore = await upgraded.ScalarAsync<int>("SELECT COUNT(*) FROM ai_usage_logs", ct: Ct);
        var idsBefore = await upgraded.ScalarAsync<string>("SELECT STRING_AGG(CONVERT(nvarchar(36), id), ',') WITHIN GROUP (ORDER BY id) FROM ai_usage_logs", ct: Ct);
        var jobsBefore = await upgraded.ScalarAsync<string>("SELECT STRING_AGG(CONVERT(nvarchar(36), id) + ':' + status + ':' + ISNULL(error_code, ''), ',') WITHIN GROUP (ORDER BY id) FROM generation_jobs", ct: Ct);

        await upgraded.MigrateAsync(Ct); // ileri migration (CostReconciliation)

        // Veri kaybı yok: satırlar, kimlikler, toplam maliyet, iş durumları birebir
        Assert.Equal(rowsBefore, await upgraded.ScalarAsync<int>("SELECT COUNT(*) FROM ai_usage_logs", ct: Ct));
        Assert.Equal(idsBefore, await upgraded.ScalarAsync<string>("SELECT STRING_AGG(CONVERT(nvarchar(36), id), ',') WITHIN GROUP (ORDER BY id) FROM ai_usage_logs", ct: Ct));
        Assert.Equal(costBefore, await upgraded.ScalarAsync<decimal>("SELECT SUM(estimated_cost_usd) FROM ai_usage_logs", ct: Ct));
        Assert.Equal(jobsBefore, await upgraded.ScalarAsync<string>("SELECT STRING_AGG(CONVERT(nvarchar(36), id) + ':' + status + ':' + ISNULL(error_code, ''), ',') WITHIN GROUP (ORDER BY id) FROM generation_jobs", ct: Ct));

        // Yeni sütunlar güvenli varsayılanlarla geldi; mevcut kullanıcı platform yöneticisi olmadı (yetki yükselmesi yok)
        Assert.Equal(0, await upgraded.ScalarAsync<int>("SELECT COUNT(*) FROM users WHERE is_platform_admin <> 0", ct: Ct));
        Assert.Equal(0, await upgraded.ScalarAsync<int>("SELECT COUNT(*) FROM ai_usage_logs WHERE is_adjustment <> 0 OR event_key IS NOT NULL OR reconciliation_state IS NOT NULL OR occurred_at IS NOT NULL", ct: Ct));
        Assert.Equal(0, await upgraded.ScalarAsync<int>("SELECT COUNT(*) FROM generation_jobs WHERE reserved_cost_usd IS NOT NULL", ct: Ct));

        // Yükseltilmiş şema temiz kurulumla aynı; tüm kısıtlar güvenilir
        await using (var db = upgraded.OpenMigrator())
        {
            Assert.Empty(await SchemaInspector.DifferencesAsync(db, upgraded.MigratorCs, Ct));
        }

        Assert.Equal(expected, (await SchemaInspector.ReadAsync(upgraded.MigratorCs, Ct)).Fingerprint());

        // M3.1 uyumu: eski maliyet satırı süpürücüyle mutabakata alınır, eksik satır eklenir, ikinci çalıştırma hiçbir şeyi değiştirmez
        var sweep = new CostReconciliationSweep(null!, new FixedOptions<GenerationSettings>(new GenerationSettings()), TimeProvider.System, NullLogger<CostReconciliationSweep>.Instance);
        await using (var db = MigrationLab.OpenDb(upgraded.AppCs)) // çalışma zamanı hesabı (yalnızca veri yetkisi)
        {
            var first = await sweep.RunOnceAsync(db, Ct);
            Assert.True(first.MissingCostRows == 1 && first.LegacyKeyed == 1, $"eksik {first.MissingCostRows}, eski {first.LegacyKeyed}");

            var legacy = await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == seed.LegacyUncertainLogId, Ct);
            Assert.Equal(($"uncertain:{seed.UncertainJobWithRow}", CostReconciliationState.Open, 0.15m), (legacy.EventKey, legacy.ReconciliationState, legacy.EstimatedCostUsd));
            var added = await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.JobId == seed.UncertainJobWithoutRow, Ct);
            Assert.Equal(($"uncertain:{seed.UncertainJobWithoutRow}", CostReconciliationState.Open), (added.EventKey, added.ReconciliationState));

            var second = await sweep.RunOnceAsync(db, Ct);
            Assert.Equal((0, 0), (second.MissingCostRows, second.LegacyKeyed));
            Assert.Equal(rowsBefore + 1, await db.AiUsageLogs.IgnoreQueryFilters().CountAsync(Ct));

            // Eski kayıt artık mutabakat kararı alabilir; karar ve düzeltme satırı yazılır, usage toplamı beklenen değere gelir
            var decided = await new CostReconciliationService(TimeProvider.System).DecideAsync(
                db, legacy.Id, Guid.NewGuid(), Guid.NewGuid().ToString("N"), new DecideCommand(CostDecision.ConfirmedCharged, 0.12m, "Panel", 0), null, Ct);
            Assert.Equal(DecisionOutcome.Recorded, decided.Outcome);
        }

        // Karar sonrası şema betiği yeniden uygulanması: mutabakat kararları ve düzeltme satırları aynen durur
        await using (var db = upgraded.OpenMigrator())
        {
            await upgraded.RunScriptAsync(db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent), Ct);
        }

        Assert.Equal(1, await upgraded.ScalarAsync<int>("SELECT COUNT(*) FROM ai_cost_reconciliations", ct: Ct));
        Assert.Equal(1, await upgraded.ScalarAsync<int>("SELECT COUNT(*) FROM ai_usage_logs WHERE is_adjustment = 1", ct: Ct));
        Assert.Equal(-0.03m, await upgraded.ScalarAsync<decimal>("SELECT SUM(estimated_cost_usd) FROM ai_usage_logs WHERE is_adjustment = 1", ct: Ct));
    }

    [Fact]
    public async Task Tenant_tutarlilik_migrationi_mevcut_veride_capraz_firma_veya_sube_iliskisi_bulursa_acik_hatayla_durur_hicbir_sey_degismez_duzeltince_uygulanir()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct, MigrationId("CostReconciliation")); // bileşik FK'lardan önceki şema
        var rows = await RowFactory.LoadAsync(lab.MigratorCs, Ct);

        async Task<Guid> Insert(string table, params (string Column, object? Value)[] values) =>
            (Guid)(await rows.CreateAsync(table, values.ToDictionary(v => v.Column, v => v.Value, StringComparer.Ordinal), Ct))["id"]!;

        // Tutarlı veri: firma A (iki şube, oturum, seçim, iş) ve firma B
        var orgA = await Insert("organizations");
        var storeA1 = await Insert("stores", ("organization_id", orgA));
        var storeA2 = await Insert("stores", ("organization_id", orgA));
        var userA = await Insert("users", ("organization_id", orgA));
        var session = await Insert("tryon_sessions", ("organization_id", orgA), ("store_id", storeA1), ("created_by_user_id", userA));
        var selection = await Insert("outfit_selections", ("organization_id", orgA), ("session_id", session));
        await Insert("generation_jobs", ("organization_id", orgA), ("store_id", storeA1), ("session_id", session), ("selection_id", selection));
        var orgB = await Insert("organizations");
        var storeB = await Insert("stores", ("organization_id", orgB));
        var userB = await Insert("users", ("organization_id", orgB));
        var sessionB = await Insert("tryon_sessions", ("organization_id", orgB), ("store_id", storeB), ("created_by_user_id", userB));

        async Task<(int Jobs, int History, int AlternateKeys, int CompositeFks)> StateAsync() => (
            await lab.ScalarAsync<int>("SELECT COUNT(*) FROM generation_jobs", ct: Ct), await lab.ScalarAsync<int>("SELECT COUNT(*) FROM __ef_migrations_history", ct: Ct),
            await lab.ScalarAsync<int>("SELECT COUNT(*) FROM sys.key_constraints WHERE name LIKE N'ak[_]%'", ct: Ct),
            await lab.ScalarAsync<int>("SELECT COUNT(*) FROM sys.foreign_keys WHERE name LIKE N'%organization[_]id'", ct: Ct));

        // Tarayıcı temiz veride ihlal bulmaz
        Assert.Empty(await TenantScan.ScanAsync(lab.MigratorCs, Ct));

        // İhlal 1: başka firmanın seçimini kullanan iş (eski şema bunu kabul eder; "TenantScan" pozitif kontrol)
        var foreignSelection = await Insert("generation_jobs", ("organization_id", orgB), ("store_id", storeB), ("session_id", sessionB), ("selection_id", selection));
        Assert.Contains(await TenantScan.ScanAsync(lab.MigratorCs, Ct), f => f.Kind == "FARKLI_FIRMA" && f.Relation.StartsWith("generation_jobs.selection_id", StringComparison.Ordinal));
        var before = await StateAsync();

        var ex = await Assert.ThrowsAsync<SqlException>(() => lab.MigrateAsync(Ct));
        Assert.Equal(51200, ex.Number);
        Assert.Contains("generation_jobs(selection_id, organization_id) -> outfit_selections", ex.Message, StringComparison.Ordinal); // ilişki adıyla açık hata
        Assert.Equal(before, await StateAsync()); // migration geri alındı: veri, geçmiş, anahtarlar ve FK'lar aynen
        Assert.Equal(0, before.AlternateKeys);

        // İhlal 2 (şube): aynı firmada ama oturumundan farklı şubede iş
        await lab.ExecuteAsync($"DELETE FROM generation_jobs WHERE id = '{foreignSelection}'", ct: Ct);
        var wrongStore = await Insert("generation_jobs", ("organization_id", orgA), ("store_id", storeA2), ("session_id", session));
        var storeEx = await Assert.ThrowsAsync<SqlException>(() => lab.MigrateAsync(Ct));
        Assert.Equal(51200, storeEx.Number);
        Assert.Contains("generation_jobs(session_id, store_id, organization_id) -> tryon_sessions", storeEx.Message, StringComparison.Ordinal);
        Assert.Contains(await TenantScan.ScanAsync(lab.MigratorCs, Ct), f => f.Kind == "FARKLI_SUBE");

        // Düzeltince migration uygulanır; veri korunur; tarayıcı temiz; anahtar ve FK'lar yerinde
        await lab.ExecuteAsync($"DELETE FROM generation_jobs WHERE id = '{wrongStore}'", ct: Ct);
        var jobsBefore = await lab.ScalarAsync<int>("SELECT COUNT(*) FROM generation_jobs", ct: Ct);
        await lab.MigrateAsync(Ct);
        var after = await StateAsync();
        Assert.Equal(jobsBefore, after.Jobs);
        Assert.Equal(13, after.AlternateKeys);
        Assert.True(after.CompositeFks >= 30, $"Bileşik FK sayısı: {after.CompositeFks}");
        Assert.Empty(await TenantScan.ScanAsync(lab.MigratorCs, Ct));

        // Artık aynı ihlaller veritabanında yazılamaz
        await Assert.ThrowsAsync<SqlException>(() => Insert("generation_jobs", ("organization_id", orgB), ("store_id", storeB), ("session_id", sessionB), ("selection_id", selection)));
        await Assert.ThrowsAsync<SqlException>(() => Insert("generation_jobs", ("organization_id", orgA), ("store_id", storeA2), ("session_id", session)));
    }

    private static readonly System.Text.Json.JsonSerializerOptions PerfJson = new() { WriteIndented = true };

    private sealed record PerfRun(double BulkInsertMs, double SingleInsertMsPerRow, double CascadeDeleteMs, double SizeMb, int Sessions, int Jobs, int UsageLogs);

    /// <summary>Aynı sentetik veri (oturum, iş, maliyet satırı) üzerinde ekleme, tek satır ekleme ve zincirleme silme süreleri ile veritabanı boyutu.</summary>
    private static async Task<PerfRun> MeasureAsync(MigrationLab lab, CancellationToken ct)
    {
        var rows = await RowFactory.LoadAsync(lab.MigratorCs, ct);
        var org = (Guid)(await rows.CreateAsync("organizations", null, ct))["id"]!;
        var store = (Guid)(await rows.CreateAsync("stores", new Dictionary<string, object?> { ["organization_id"] = org }, ct))["id"]!;
        var user = (Guid)(await rows.CreateAsync("users", new Dictionary<string, object?> { ["organization_id"] = org }, ct))["id"]!;
        const int sessions = 1000;

        async Task Exec(string sql, params (string, object)[] parameters)
        {
            await using var connection = new SqlConnection(lab.MigratorCs);
            await connection.OpenAsync(ct);
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await command.ExecuteNonQueryAsync(ct);
        }

        var parametersCommon = new (string, object)[] { ("@org", org), ("@store", store), ("@user", user) };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Exec($"""
            DECLARE @now datetimeoffset = SYSDATETIMEOFFSET();
            ;WITH n AS (SELECT TOP ({sessions}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO tryon_sessions (id, created_at, updated_at, organization_id, store_id, created_by_user_id, status, retention_until, last_activity_at)
            SELECT NEWID(), @now, @now, @org, @store, @user, N'Open', DATEADD(day, 1, @now), @now FROM n;
            INSERT INTO generation_jobs (id, created_at, updated_at, organization_id, store_id, session_id, job_type, status, idempotency_key, attempt, max_attempts, next_attempt_at, requested_by_user_id)
            SELECT NEWID(), @now, @now, @org, @store, s.id, N'OutfitSet', N'Failed', CONVERT(nvarchar(64), NEWID()), 0, 3, @now, @user
              FROM tryon_sessions s CROSS JOIN (SELECT TOP (10) 1 AS x FROM sys.all_objects) k WHERE s.organization_id = @org;
            INSERT INTO ai_usage_logs (id, created_at, updated_at, organization_id, store_id, job_id, provider, model, operation, attempt, success, duration_ms, estimated_cost_usd)
            SELECT NEWID(), @now, @now, @org, @store, j.id, N'Fashn', N'tryon-max', N'OutfitSet', 1, 1, 100, 0.15 FROM generation_jobs j WHERE j.organization_id = @org;
            """, parametersCommon);
        var bulk = watch.Elapsed.TotalMilliseconds;

        // Uygulama gibi tek satır ekleme (parametreli, tek bağlantı)
        var firstSession = await lab.ScalarAsync<Guid>($"SELECT TOP (1) id FROM tryon_sessions WHERE organization_id = '{org}' ORDER BY id", ct: ct);
        watch.Restart();
        await using (var connection = new SqlConnection(lab.MigratorCs))
        {
            await connection.OpenAsync(ct);
            for (var i = 0; i < 2000; i++)
            {
                await using var command = new SqlCommand("""
                    INSERT INTO generation_jobs (id, created_at, updated_at, organization_id, store_id, session_id, job_type, status, idempotency_key, attempt, max_attempts, next_attempt_at, requested_by_user_id)
                    VALUES (NEWID(), SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), @org, @store, @session, N'OutfitSet', N'Failed', @key, 0, 3, SYSDATETIMEOFFSET(), @user)
                    """, connection);
                command.Parameters.AddWithValue("@org", org);
                command.Parameters.AddWithValue("@store", store);
                command.Parameters.AddWithValue("@session", firstSession);
                command.Parameters.AddWithValue("@key", Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("@user", user);
                await command.ExecuteNonQueryAsync(ct);
            }
        }

        var single = watch.Elapsed.TotalMilliseconds / 2000;
        var size = await lab.ScalarAsync<double>("SELECT SUM(used_page_count) * 8.0 / 1024 FROM sys.dm_db_partition_stats", lab.AdminCs, ct); // boyut görünümü yönetici yetkisi ister
        var jobs = await lab.ScalarAsync<int>("SELECT COUNT(*) FROM generation_jobs", ct: ct);
        var logs = await lab.ScalarAsync<int>("SELECT COUNT(*) FROM ai_usage_logs", ct: ct);

        // Zincirleme silme: 500 oturum (iş ve ilgili kayıtlarla; maliyet satırlarında iş bağlantısı SET NULL)
        watch.Restart();
        await Exec("DELETE FROM tryon_sessions WHERE id IN (SELECT TOP (500) id FROM tryon_sessions WHERE organization_id = @org ORDER BY id DESC)", ("@org", org));
        return new PerfRun(bulk, single, watch.Elapsed.TotalMilliseconds, size, sessions, jobs, logs);
    }

    [Fact]
    public async Task Olcum_bilesik_FK_ve_ek_anahtarlarin_maliyeti_dolu_veritabaninda_migration_suresi_ekleme_ve_zincirleme_silme_makul_sinirdadir()
    {
        await using var before = await MigrationLab.CreateAsync(Ct);
        await using var after = await MigrationLab.CreateAsync(Ct);
        await before.MigrateAsync(Ct, MigrationId("CostReconciliation")); // bileşik FK'lardan önceki şema
        await after.MigrateAsync(Ct);

        // Her iki şemada aynı yük (ısınma turu dışarıda bırakılmadı: iki taraf da soğuk başlar, oranlar karşılaştırılır)
        var old = await MeasureAsync(before, Ct);
        var current = await MeasureAsync(after, Ct);

        // Dolu (eski şemalı) veritabanında ileri migration süresi: ön kontrol + 13 benzersiz anahtar + 38 FK + indeks yeniden kurma
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await before.MigrateAsync(Ct);
        var migrationMs = watch.Elapsed.TotalMilliseconds;
        var sizeAfterMigration = await before.ScalarAsync<double>("SELECT SUM(used_page_count) * 8.0 / 1024 FROM sys.dm_db_partition_stats", before.AdminCs, Ct);
        Assert.Empty(await SchemaInspector.DifferencesAsync(before.OpenMigrator(), before.MigratorCs, Ct)); // yükseltilmiş dolu veritabanı modelle birebir
        Assert.Empty(await TenantScan.ScanAsync(before.MigratorCs, Ct));

        var report = new
        {
            Veri = new { old.Sessions, Jobs = current.Jobs, UsageLogs = current.UsageLogs },
            EskiSema = old, YeniSema = current, DoluVeritabaniMigrationMs = migrationMs, MigrationSonrasiBoyutMb = sizeAfterMigration,
            OranTopluEkleme = current.BulkInsertMs / old.BulkInsertMs, OranTekSatirEkleme = current.SingleInsertMsPerRow / old.SingleInsertMsPerRow,
            OranZincirleSilme = current.CascadeDeleteMs / old.CascadeDeleteMs, OranBoyut = current.SizeMb / old.SizeMb,
        };
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "sanalprova_m34_perf.json"), System.Text.Json.JsonSerializer.Serialize(report, PerfJson), Ct);

        // Geniş ama anlamlı sınırlar: ek kontrol maliyeti makul kalmalı (kesin süreler ortama bağlı, oranlar ve mutlak üst sınırlar sınanır)
        Assert.True(migrationMs < 60_000, $"Dolu veritabanında migration çok uzun sürdü: {migrationMs:0} ms");
        Assert.True(current.SingleInsertMsPerRow / old.SingleInsertMsPerRow < 4, $"Tek satır ekleme {current.SingleInsertMsPerRow / old.SingleInsertMsPerRow:0.0}× yavaşladı");
        Assert.True(current.CascadeDeleteMs / old.CascadeDeleteMs < 4, $"Zincirleme silme {current.CascadeDeleteMs / old.CascadeDeleteMs:0.0}× yavaşladı");
        Assert.True(current.SizeMb / old.SizeMb < 1.6, $"Boyut {current.SizeMb / old.SizeMb:0.00}× büyüdü");
    }

    [Fact]
    public async Task Geri_alma_migrationi_mutabakat_verisini_siler_bu_yuzden_politika_yalnizca_ileri_yonludur()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        var seed = await Seed.BaselineAsync(lab, Ct);
        var sweep = new CostReconciliationSweep(null!, new FixedOptions<GenerationSettings>(new GenerationSettings()), TimeProvider.System, NullLogger<CostReconciliationSweep>.Instance);
        await using (var db = MigrationLab.OpenDb(lab.AppCs))
        {
            await sweep.RunOnceAsync(db, Ct);
            var caseId = (await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.EventKey == $"uncertain:{seed.UncertainJobWithRow}", Ct)).Id;
            await new CostReconciliationService(TimeProvider.System).DecideAsync(
                db, caseId, Guid.NewGuid(), Guid.NewGuid().ToString("N"), new DecideCommand(CostDecision.ConfirmedNotCharged, null, null, 0), null, Ct);
        }

        Assert.Equal(1, await lab.ScalarAsync<int>("SELECT COUNT(*) FROM ai_cost_reconciliations", ct: Ct));

        // Down: karar tablosu ve alanlar kaybolur; düzeltme satırı artık "düzeltme" olduğunu bilmeyen sıradan negatif maliyet satırı olarak kalır.
        // Bu davranış bilinçli olarak testlidir: üretimde Down çalıştırılmaz, geri dönüş yedekten yapılır.
        await lab.MigrateAsync(Ct, MigrationId("ProviderOutcomeUncertain"));
        Assert.False(await HasColumnAsync(lab, "ai_usage_logs", "is_adjustment"));
        Assert.Equal(0, await lab.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name = N'ai_cost_reconciliations'", ct: Ct));
        Assert.True(await lab.ScalarAsync<decimal>("SELECT MIN(estimated_cost_usd) FROM ai_usage_logs", ct: Ct) < 0m);
    }

    // ------------------------------------------------------------------ veri kaybı riski: migration'ların statik analizi

    [Fact]
    public void Ileri_migrationlar_veri_kaybi_ve_mevcut_veride_patlama_riski_tasimaz()
    {
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=tcp:127.0.0.1,1;Database=none;User ID=n;Password=n;Encrypt=False", AppDbContext.ConfigureSqlServer).UseSnakeCaseNamingConvention().Options,
            SanalProva.Infrastructure.Tenancy.NoTenantContext.Instance);
        var assembly = db.GetService<IMigrationsAssembly>();
        var problems = new List<string>();

        foreach (var (id, type) in assembly.Migrations.Where(m => !m.Key.EndsWith("_InitialSqlServer", StringComparison.Ordinal)))
        {
            var migration = (Migration)Activator.CreateInstance(type.AsType())!;
            foreach (var operation in migration.UpOperations)
            {
                switch (operation)
                {
                    case DropTableOperation or DropColumnOperation or RenameTableOperation or RenameColumnOperation or DropSchemaOperation:
                        problems.Add($"{id}: veri kaybettirebilir/yeniden adlandırır → {operation.GetType().Name}");
                        break;
                    case AlterColumnOperation alter:
                        problems.Add($"{id}: sütun türü/null durumu değişiyor (veri dönüşümü/kısaltma riski) → {alter.Table}.{alter.Name}");
                        break;
                    case AddColumnOperation { IsNullable: false, DefaultValue: null, DefaultValueSql: null } add:
                        problems.Add($"{id}: NULL olmayan sütun varsayılansız ({add.Table}.{add.Name}): dolu tabloda migration patlar");
                        break;
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // ------------------------------------------------------------------ filtreli indeks / SET seçenekleri

    [Fact]
    public async Task Filtreli_indeksler_beklenen_filtreyle_vardir_ve_QUOTED_IDENTIFIER_kapaliyken_yazma_reddedilir()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        var seed = await Seed.BaselineAsync(lab, Ct);

        var filtered = (await SchemaInspector.ReadAsync(lab.MigratorCs, Ct)).Indexes.Select(i => i.Split('|')).Where(p => p[5].Length > 0).ToDictionary(p => p[2], p => p[5]);
        foreach (var expected in new[] { "ix_ai_usage_logs_reconciliation", "ix_generation_jobs_queue", "ix_generation_jobs_running_lease", "ux_ai_usage_logs_event_key", "ux_generation_jobs_active_dedupe" })
        {
            Assert.True(filtered.ContainsKey(expected), $"Filtreli indeks yok: {expected}");
        }

        Assert.Contains("lit:Queued", filtered["ix_generation_jobs_queue"], StringComparison.Ordinal);
        Assert.Contains("lit:Succeeded", filtered["ux_generation_jobs_active_dedupe"], StringComparison.Ordinal);
        Assert.DoesNotContain("lit:Failed", filtered["ux_generation_jobs_active_dedupe"], StringComparison.Ordinal); // başarısız iş yeniden denemeyi engellemez

        // SQL Server tuzağı: filtreli indeksli tabloya yazan oturumda QUOTED_IDENTIFIER açık olmalı (SqlClient varsayılanı açık; eski araçlar/sqlcmd -I'sız kapalı)
        const string insert = "INSERT INTO ai_usage_logs (id, created_at, updated_at, organization_id, store_id, provider, model, operation, attempt, success, duration_ms, estimated_cost_usd) VALUES (NEWID(), SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), @o, @s, N'p', N'm', N'op', 1, 0, 0, 0)";
        await using var connection = new SqlConnection(lab.AppCs);
        await connection.OpenAsync(Ct);
        async Task Run(string prefix)
        {
            await using var command = new SqlCommand(prefix + insert, connection);
            command.Parameters.AddWithValue("@o", seed.OrganizationId);
            command.Parameters.AddWithValue("@s", seed.StoreId);
            await command.ExecuteNonQueryAsync(Ct);
        }

        await Run(string.Empty); // varsayılan: çalışır
        var ex = await Assert.ThrowsAsync<SqlException>(() => Run("SET QUOTED_IDENTIFIER OFF; "));
        Assert.Equal(1934, ex.Number); // "SET options have incorrect settings" (filtreli indeks)
    }

    // ------------------------------------------------------------------ hesap yetkileri

    [Fact]
    public async Task Migrator_semayi_degistirebilir_sunucu_yetkisi_yoktur_app_hesabi_hicbir_DDL_yapamaz()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        var seed = await Seed.BaselineAsync(lab, Ct);

        // --- migrator: gereken (şema) yetkiler var, fazlası yok
        Assert.Equal(0, await lab.ScalarAsync<int>("SELECT IS_SRVROLEMEMBER('sysadmin')", lab.MigratorCs, Ct));
        Assert.Equal(0, await lab.ScalarAsync<int>("SELECT IS_MEMBER('db_owner')", lab.MigratorCs, Ct));
        Assert.Equal(1, await lab.ScalarAsync<int>("SELECT IS_MEMBER('db_ddladmin')", lab.MigratorCs, Ct));
        foreach (var permission in new[] { "CONTROL SERVER", "CREATE ANY DATABASE", "ALTER ANY LOGIN", "ALTER ANY DATABASE", "VIEW SERVER STATE" })
        {
            Assert.Equal(0, await lab.ScalarAsync<int>($"SELECT HAS_PERMS_BY_NAME(NULL, NULL, '{permission}')", lab.MigratorCs, Ct));
        }

        await lab.ExecuteAsync("CREATE TABLE dbo.m32_scratch (id int NOT NULL PRIMARY KEY, v nvarchar(20) NULL); CREATE INDEX ix_m32_scratch_v ON dbo.m32_scratch (v); ALTER TABLE dbo.m32_scratch ADD extra int NULL; DROP TABLE dbo.m32_scratch;", ct: Ct);
        await AssertDeniedAsync(lab, lab.MigratorCs, "CREATE DATABASE sanalprova_lab_should_never_exist");
        await AssertDeniedAsync(lab, lab.MigratorCs, "CREATE LOGIN m32_evil WITH PASSWORD = 'Aa1!aaaaaaaaaaaa'");
        await AssertDeniedAsync(lab, lab.MigratorCs, "ALTER SERVER ROLE sysadmin ADD MEMBER sanalprova_migrator");

        // --- app: veri işlemleri çalışır (kuyruk, applock, silme)
        Assert.Equal(0, await lab.ScalarAsync<int>("SELECT IS_SRVROLEMEMBER('sysadmin')", lab.AppCs, Ct));
        Assert.Equal(0, await lab.ScalarAsync<int>("SELECT IS_MEMBER('db_owner')", lab.AppCs, Ct));
        Assert.Equal(0, await lab.ScalarAsync<int>("SELECT IS_MEMBER('db_ddladmin')", lab.AppCs, Ct));
        await lab.ExecuteAsync($"UPDATE generation_jobs SET attempt = attempt WHERE id = '{seed.UncertainJobWithRow}'", lab.AppCs, Ct);
        await lab.ExecuteAsync("BEGIN TRAN; DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = N'm32', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 1000; IF @r < 0 THROW 51001, N'applock', 1; ROLLBACK;", lab.AppCs, Ct);

        // --- app: hiçbir DDL ve hiçbir yönetim işlemi
        var denied = new[]
        {
            "CREATE TABLE dbo.m32_app_ddl (id int)",
            "ALTER TABLE dbo.ai_usage_logs ADD m32_extra int NULL",
            "DROP TABLE dbo.ai_usage_logs",
            "DROP TABLE dbo.users",
            "TRUNCATE TABLE dbo.audit_logs",
            "CREATE INDEX ix_m32_app ON dbo.ai_usage_logs (provider)",
            "DROP INDEX ux_ai_usage_logs_event_key ON dbo.ai_usage_logs",
            "ALTER TABLE dbo.ai_usage_logs NOCHECK CONSTRAINT ALL",
            "ALTER TABLE dbo.ai_usage_logs DROP CONSTRAINT ck_ai_usage_logs_adjustment",
            "CREATE SCHEMA m32_schema",
            "CREATE VIEW dbo.m32_view AS SELECT 1 AS x",
            "CREATE USER m32_user WITHOUT LOGIN",
            "ALTER ROLE db_owner ADD MEMBER sanalprova_app",
            "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT OFF",
            "CREATE DATABASE sanalprova_lab_should_never_exist",
            "CREATE LOGIN m32_evil WITH PASSWORD = 'Aa1!aaaaaaaaaaaa'",
            "EXEC sys.sp_executesql N'DROP TABLE dbo.users'",
            "CREATE TRIGGER m32_ddl_trigger ON DATABASE FOR DROP_TABLE AS PRINT N'x'",
        };
        foreach (var statement in denied)
        {
            await AssertDeniedAsync(lab, lab.AppCs, statement);
        }

        // Hiçbiri etki etmedi: şema hâlâ modelle birebir
        await using var db = lab.OpenMigrator();
        Assert.Empty(await SchemaInspector.DifferencesAsync(db, lab.MigratorCs, Ct));
    }

    private const string HardenScript = "scripts/mssql/harden-migration-history.sql";

    [Fact]
    public async Task Uygulama_hesabi_migration_gecmisine_yazabilir_bu_bir_bulgudur_gonderilen_sertlestirme_betigi_kapatir()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        const string fake = "INSERT INTO __ef_migrations_history (migration_id, product_version) VALUES (N'29990101000000_Sahte', N'10.0.0')";

        // Sıra bağımsızlığı: geçmiş tablosu henüz yokken betik hata vermeden atlar (provision → migrate → harden; harden erken çalışırsa zarar vermez)
        await lab.RunSqlFileAsync(HardenScript, Ct);
        await lab.MigrateAsync(Ct);

        // Bulgu: sertleştirmeden önce db_datawriter her tabloya yazar; ele geçirilen uygulama hesabı migration geçmişini değiştirebilir
        await lab.ExecuteAsync(fake, lab.AppCs, Ct);
        await lab.ExecuteAsync("UPDATE __ef_migrations_history SET product_version = N'0' WHERE migration_id = N'29990101000000_Sahte'", lab.AppCs, Ct);
        await lab.ExecuteAsync("DELETE FROM __ef_migrations_history WHERE migration_id = N'29990101000000_Sahte'", lab.AppCs, Ct);

        // Gönderilen betik (yönetici bağlantısıyla), iki kez: idempotent
        await lab.RunSqlFileAsync(HardenScript, Ct);
        await lab.RunSqlFileAsync(HardenScript, Ct);

        // Gerçek SQL login'leriyle: app için INSERT/UPDATE/DELETE reddedilir, SELECT çalışır
        await AssertDeniedAsync(lab, lab.AppCs, fake);
        await AssertDeniedAsync(lab, lab.AppCs, "UPDATE __ef_migrations_history SET product_version = N'0'");
        await AssertDeniedAsync(lab, lab.AppCs, "DELETE FROM __ef_migrations_history");
        Assert.Equal(AllMigrations().Length, await lab.ScalarAsync<int>("SELECT COUNT(*) FROM __ef_migrations_history", lab.AppCs, Ct));
        foreach (var permission in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            Assert.Equal(0, await lab.ScalarAsync<int>($"SELECT HAS_PERMS_BY_NAME(N'dbo.__ef_migrations_history', 'OBJECT', '{permission}')", lab.AppCs, Ct));
            Assert.Equal(1, await lab.ScalarAsync<int>($"SELECT HAS_PERMS_BY_NAME(N'dbo.__ef_migrations_history', 'OBJECT', '{permission}')", lab.MigratorCs, Ct));
        }

        // Tam olarak üç DENY, yalnızca sanalprova_app için; başka kimseye ve başka tabloya dokunulmadı
        var denies = await lab.ScalarAsync<string>("""
            SELECT STRING_AGG(CONVERT(nvarchar(400), pr.name + N':' + p.permission_name + N':' + p.state_desc + N':' + OBJECT_NAME(p.major_id)) COLLATE DATABASE_DEFAULT, N',') WITHIN GROUP (ORDER BY p.permission_name COLLATE DATABASE_DEFAULT)
              FROM sys.database_permissions p JOIN sys.database_principals pr ON pr.principal_id = p.grantee_principal_id
             WHERE p.class = 1 AND p.state_desc = N'DENY'
            """, lab.AdminCs, Ct); // izin katalogunu yalnızca yönetici görür (migrator başkasının izinlerini göremez)
        Assert.Equal("sanalprova_app:DELETE:DENY:__ef_migrations_history,sanalprova_app:INSERT:DENY:__ef_migrations_history,sanalprova_app:UPDATE:DENY:__ef_migrations_history", denies);

        // Migrator geçmişi yönetir: geri alma (DELETE) ve yeniden uygulama (INSERT) çalışır, ayrıca sahte kayıt yazıp silebilir
        await lab.MigrateAsync(Ct, MigrationId("AdmissionDedupeKey"));
        Assert.Equal(3, await lab.ScalarAsync<int>("SELECT COUNT(*) FROM __ef_migrations_history", ct: Ct));
        await lab.MigrateAsync(Ct);
        Assert.Equal(AllMigrations().Length, await lab.ScalarAsync<int>("SELECT COUNT(*) FROM __ef_migrations_history", ct: Ct));
        await lab.ExecuteAsync(fake, ct: Ct);
        await lab.ExecuteAsync("UPDATE __ef_migrations_history SET product_version = N'1' WHERE migration_id = N'29990101000000_Sahte'; DELETE FROM __ef_migrations_history WHERE migration_id = N'29990101000000_Sahte'", ct: Ct);

        // Betik sonradan tekrar çalıştırılsa da DENY korunur
        await lab.RunSqlFileAsync(HardenScript, Ct);
        await AssertDeniedAsync(lab, lab.AppCs, fake);
    }

    private const string AppendOnlyScript = "scripts/mssql/harden-append-only.sql";
    private const string AgreementAcceptanceScript = "scripts/mssql/harden-agreement-acceptances.sql";

    [Fact]
    public async Task Karar_kayitlari_yalnizca_eklemeli_olur_DENY_UPDATE_DELETE_karar_duzeltme_listeleme_ve_silme_akislarini_bozmaz()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);

        // Sıra bağımsızlığı: tablo yokken betik atlar
        await lab.RunSqlFileAsync(AppendOnlyScript, Ct);
        await lab.MigrateAsync(Ct);
        var rows = await RowFactory.LoadAsync(lab.MigratorCs, Ct);

        var org = (Guid)(await rows.CreateAsync("organizations", null, Ct))["id"]!;
        var store = (Guid)(await rows.CreateAsync("stores", new Dictionary<string, object?> { ["organization_id"] = org }, Ct))["id"]!;
        async Task<Guid> Case(Guid? jobId = null, Guid? inOrg = null, Guid? inStore = null) => (Guid)(await rows.CreateAsync("ai_usage_logs", new Dictionary<string, object?>
        {
            ["organization_id"] = inOrg ?? org, ["store_id"] = inStore ?? store, ["job_id"] = jobId, ["estimated_cost_usd"] = 0.15m, ["error_code"] = "OutcomeUncertain", ["success"] = false,
            ["event_key"] = $"uncertain:{Guid.NewGuid()}", ["reconciliation_state"] = "Open", ["provider"] = "Fashn", ["model"] = "tryon-max",
        }, Ct))["id"]!;

        var service = new CostReconciliationService(TimeProvider.System);
        async Task<DecisionResult> Decide(Guid caseId, CostDecision decision, decimal? amount, int expected, string? key = null)
        {
            await using var db = MigrationLab.OpenDb(lab.AppCs); // çalışma zamanı hesabı
            return await service.DecideAsync(db, caseId, Guid.NewGuid(), key ?? Guid.NewGuid().ToString("N"), new DecideCommand(decision, amount, null, expected), null, Ct);
        }

        // --- Bulgu (sertleştirmeden önce): app hesabı karar kaydını değiştirebilir ve silebilir
        // (Ayrı firmada: bozulan defter, aşağıdaki değişmez denetimlerini kirletmesin; kirlenmenin kendisi bulgudur: karar silinince düzeltme satırı sahipsiz kalır)
        var demoOrg = (Guid)(await rows.CreateAsync("organizations", null, Ct))["id"]!;
        var demoStore = (Guid)(await rows.CreateAsync("stores", new Dictionary<string, object?> { ["organization_id"] = demoOrg }, Ct))["id"]!;
        var before = await Case(inOrg: demoOrg, inStore: demoStore);
        Assert.Equal(DecisionOutcome.Recorded, (await Decide(before, CostDecision.ConfirmedNotCharged, null, 0)).Outcome);
        await lab.ExecuteAsync($"UPDATE ai_cost_reconciliations SET effective_cost_usd = 9 WHERE usage_log_id = '{before}'", lab.AppCs, Ct);
        await lab.ExecuteAsync($"DELETE FROM ai_cost_reconciliations WHERE usage_log_id = '{before}'", lab.AppCs, Ct);
        Assert.NotEqual(0, await lab.ScalarAsync<int>(DatabaseIntegrityTests.CostInvariants[1].Sql.Replace("{0}", demoOrg.ToString(), StringComparison.Ordinal), ct: Ct)); // defter bozuldu: sahipsiz düzeltme satırı

        // --- Sertleştirme (gönderilen betik, iki kez)
        await lab.RunSqlFileAsync(AppendOnlyScript, Ct);
        await lab.RunSqlFileAsync(AppendOnlyScript, Ct);

        // Tam olarak iki DENY, yalnızca sanalprova_app için, yalnızca bu tabloda
        var denies = await lab.ScalarAsync<string>("""
            SELECT STRING_AGG(CONVERT(nvarchar(400), pr.name + N':' + p.permission_name + N':' + p.state_desc + N':' + OBJECT_NAME(p.major_id)) COLLATE DATABASE_DEFAULT, N',') WITHIN GROUP (ORDER BY p.permission_name COLLATE DATABASE_DEFAULT)
              FROM sys.database_permissions p JOIN sys.database_principals pr ON pr.principal_id = p.grantee_principal_id
             WHERE p.class = 1 AND p.state_desc = N'DENY'
            """, lab.AdminCs, Ct);
        Assert.Equal("sanalprova_app:DELETE:DENY:ai_cost_reconciliations,sanalprova_app:UPDATE:DENY:ai_cost_reconciliations", denies);

        // --- Karar oluşturma, düzeltme, tekrar, çakışma, anahtar yeniden kullanımı: hepsi çalışır (yalnızca INSERT + vaka satırı UPDATE'i + düzeltme INSERT'ü)
        var main = await Case();
        var key = Guid.NewGuid().ToString("N");
        Assert.Equal(DecisionOutcome.Recorded, (await Decide(main, CostDecision.ConfirmedCharged, 0.12m, 0, key)).Outcome);
        Assert.Equal(DecisionOutcome.Replayed, (await Decide(main, CostDecision.ConfirmedCharged, 0.12m, 0, key)).Outcome);
        Assert.Equal(DecisionOutcome.KeyReused, (await Decide(main, CostDecision.WrittenOff, null, 0, key)).Outcome);
        Assert.Equal(DecisionOutcome.SequenceMismatch, (await Decide(main, CostDecision.ConfirmedNotCharged, null, 0)).Outcome);
        Assert.Equal(DecisionOutcome.Recorded, (await Decide(main, CostDecision.ConfirmedNotCharged, null, 1)).Outcome); // düzeltme: yeni karar eklenir, eskisi değişmez
        Assert.Equal(DecisionOutcome.Recorded, (await Decide(main, CostDecision.WrittenOff, null, 2)).Outcome);

        // Platform yöneticisi listeleme/ayrıntı sorguları (uç noktadakiyle aynı EF sorguları) çalışır
        await using (var db = MigrationLab.OpenDb(lab.AppCs))
        {
            var decisions = await db.AiCostReconciliations.IgnoreQueryFilters().AsNoTracking().Where(d => d.UsageLogId == main).OrderBy(d => d.Sequence).ToListAsync(Ct);
            Assert.Equal([1, 2, 3], decisions.Select(d => d.Sequence).ToArray());
            Assert.Equal([0.12m, 0m, 0.15m], decisions.Select(d => d.EffectiveCostUsd).ToArray());
            var counts = await db.AiCostReconciliations.IgnoreQueryFilters().AsNoTracking().GroupBy(d => d.UsageLogId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(Ct);
            Assert.Equal(3, counts.Single(c => c.Key == main).Count);
            Assert.Equal(CostReconciliationState.WrittenOff, (await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == main, Ct)).ReconciliationState);
        }

        // Eşzamanlı kararlar (6 yönetici × 4 vaka) hâlâ tutarlı
        var cases = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            cases.Add(await Case());
        }

        await Task.WhenAll(cases.SelectMany(c => Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = MigrationLab.OpenDb(lab.AppCs);
            var expected = await db.AiCostReconciliations.IgnoreQueryFilters().CountAsync(d => d.UsageLogId == c, Ct);
            return await service.DecideAsync(db, c, Guid.NewGuid(), Guid.NewGuid().ToString("N"), new DecideCommand(CostDecision.ConfirmedNotCharged, null, null, expected), null, Ct);
        })));
        foreach (var (name, sql) in DatabaseIntegrityTests.CostInvariants)
        {
            Assert.True(0 == await lab.ScalarAsync<int>(sql.Replace("{0}", org.ToString(), StringComparison.Ordinal), ct: Ct), $"Değişmez ihlali: {name}");
        }

        // --- Sertleştirmeden sonra: app karar kaydını güncelleyemez/silemez; INSERT ve SELECT çalışır; migrator (test sıfırlaması) etkilenmez
        await AssertDeniedAsync(lab, lab.AppCs, "UPDATE ai_cost_reconciliations SET effective_cost_usd = 0");
        await AssertDeniedAsync(lab, lab.AppCs, "DELETE FROM ai_cost_reconciliations");
        await AssertDeniedAsync(lab, lab.AppCs, "UPDATE ai_cost_reconciliations SET note = N'x' WHERE usage_log_id = '" + main + "'");
        Assert.True(await lab.ScalarAsync<int>("SELECT COUNT(*) FROM ai_cost_reconciliations", lab.AppCs, Ct) >= 3);
        foreach (var permission in new[] { "UPDATE", "DELETE" })
        {
            Assert.Equal(0, await lab.ScalarAsync<int>($"SELECT HAS_PERMS_BY_NAME(N'dbo.ai_cost_reconciliations', 'OBJECT', '{permission}')", lab.AppCs, Ct));
            Assert.Equal(1, await lab.ScalarAsync<int>($"SELECT HAS_PERMS_BY_NAME(N'dbo.ai_cost_reconciliations', 'OBJECT', '{permission}')", lab.MigratorCs, Ct));
        }

        await lab.ExecuteAsync("DELETE FROM ai_cost_reconciliations", ct: Ct); // migrator: ApiFactory'nin test sıfırlaması bu yolla çalışır

        // --- KVKK silmesi (oturum silme → iş silinir, maliyet kaydının iş bağlantısı SET NULL): karar tablosuna dokunmaz, DENY'dan etkilenmez
        var session = await rows.CreateAsync("tryon_sessions", new Dictionary<string, object?> { ["organization_id"] = org, ["store_id"] = store }, Ct);
        var job = (Guid)(await rows.CreateAsync("generation_jobs", new Dictionary<string, object?> { ["session_id"] = session["id"], ["organization_id"] = org, ["store_id"] = store }, Ct))["id"]!;
        var withJob = await Case(job);
        Assert.Equal(DecisionOutcome.Recorded, (await Decide(withJob, CostDecision.ConfirmedCharged, 0.15m, 0)).Outcome);
        await lab.ExecuteAsync($"DELETE FROM tryon_sessions WHERE id = '{session["id"]}'", lab.AppCs, Ct);
        Assert.Equal(1, await lab.ScalarAsync<int>($"SELECT COUNT(*) FROM ai_usage_logs WHERE id = '{withJob}' AND job_id IS NULL", lab.AppCs, Ct));
        Assert.Equal(1, await lab.ScalarAsync<int>($"SELECT COUNT(*) FROM ai_cost_reconciliations WHERE usage_log_id = '{withJob}'", lab.AppCs, Ct));
        Assert.Equal(DecisionOutcome.Recorded, (await Decide(withJob, CostDecision.WrittenOff, null, 1)).Outcome); // silinmiş oturumun vakası için de düzeltme verilebilir
    }

    [Fact]
    public async Task Sertlestirme_sonrasi_uygulamanin_normal_veri_islemleri_bozulmaz()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        var seed = await Seed.BaselineAsync(lab, Ct);
        await lab.RunSqlFileAsync(HardenScript, Ct);

        // Çalışma zamanı hesabıyla: EF yazmaları (çok tablolu), ham T-SQL (süpürücü), kuyruk alma (UPDATE..OUTPUT + READPAST), applock, silme
        var sweep = new CostReconciliationSweep(null!, new FixedOptions<GenerationSettings>(new GenerationSettings()), TimeProvider.System, NullLogger<CostReconciliationSweep>.Instance);
        await using (var db = MigrationLab.OpenDb(lab.AppCs))
        {
            Assert.Equal(1, (await sweep.RunOnceAsync(db, Ct)).MissingCostRows);
            var legacy = await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.JobId == seed.UncertainJobWithRow && l.ReconciliationState != null, Ct);
            var decided = await new CostReconciliationService(TimeProvider.System).DecideAsync(
                db, legacy.Id, Guid.NewGuid(), Guid.NewGuid().ToString("N"), new DecideCommand(CostDecision.ConfirmedNotCharged, null, null, 0), null, Ct);
            Assert.Equal(DecisionOutcome.Recorded, decided.Outcome);
        }

        await lab.ExecuteAsync($"UPDATE generation_jobs SET status = N'Queued', next_attempt_at = SYSDATETIMEOFFSET() WHERE id = '{seed.UncertainJobWithRow}'", lab.AppCs, Ct);
        Assert.Equal(seed.UncertainJobWithRow, await lab.ScalarAsync<Guid>("""
            WITH next_job AS (SELECT TOP (1) id, status FROM generation_jobs WITH (UPDLOCK, READPAST, ROWLOCK) WHERE status = N'Queued' ORDER BY next_attempt_at)
            UPDATE next_job SET status = N'Running' OUTPUT inserted.id
            """, lab.AppCs, Ct));
        await lab.ExecuteAsync("BEGIN TRAN; DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = N'm33', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 1000; IF @r < 0 THROW 51001, N'applock', 1; ROLLBACK;", lab.AppCs, Ct);
        Assert.Equal(1, await lab.ScalarAsync<int>($"DELETE FROM generation_jobs WHERE id = '{seed.UncertainJobWithoutRow}'; SELECT @@ROWCOUNT", lab.AppCs, Ct));
    }

    [Fact]
    public void Sertlestirme_betikleri_ayni_DENY_deyimini_tasir_ve_uygulama_komutu_guvenli_varsayilanlidir()
    {
        var harden = File.ReadAllText(MigrationLab.RepoFile(HardenScript));
        var provision = File.ReadAllText(MigrationLab.RepoFile("scripts/mssql/provision.sql"));
        const string deny = "DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.__ef_migrations_history TO [sanalprova_app];";
        Assert.Contains(deny, harden, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(provision, System.Text.RegularExpressions.Regex.Escape(deny))); // dev + test, tablo varsa

        // Yalnızca ekleme sertleştirmesi: aynı deyim iki dosyada (betik + provision dev/test)
        var appendOnly = File.ReadAllText(MigrationLab.RepoFile(AppendOnlyScript));
        const string denyAppendOnly = "DENY UPDATE, DELETE ON OBJECT::dbo.ai_cost_reconciliations TO [sanalprova_app];";
        Assert.Contains(denyAppendOnly, appendOnly, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(provision, System.Text.RegularExpressions.Regex.Escape(denyAppendOnly)));
        Assert.DoesNotContain("INSERT", denyAppendOnly, StringComparison.Ordinal); // eklemek serbest
        Assert.DoesNotContain("SELECT", denyAppendOnly, StringComparison.Ordinal);

        // M5.1: sözleşme kabul kayıtları da yalnızca ekleme (ayrı betik, ai_cost_reconciliations'tan bağımsız; aynı desen, aynı deyim iki dosyada)
        var agreementAcceptance = File.ReadAllText(MigrationLab.RepoFile(AgreementAcceptanceScript));
        const string denyAgreementAcceptance = "DENY UPDATE, DELETE ON OBJECT::dbo.organization_agreement_acceptances TO [sanalprova_app];";
        Assert.Contains(denyAgreementAcceptance, agreementAcceptance, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(provision, System.Text.RegularExpressions.Regex.Escape(denyAgreementAcceptance)));
        Assert.DoesNotContain("INSERT", denyAgreementAcceptance, StringComparison.Ordinal); // eklemek serbest
        Assert.DoesNotContain("SELECT", denyAgreementAcceptance, StringComparison.Ordinal);

        // Takipli provision betiği parola içermez: parolalar sqlcmd değişkenidir
        Assert.DoesNotMatch(@"PASSWORD\s*=\s*N?'(?!\$\()", provision); // değişken olmayan (düz metin) parola yok
        Assert.Contains("$(SP_APP_PW)", provision, StringComparison.Ordinal);
        Assert.Contains("$(SP_MIGRATOR_PW)", provision, StringComparison.Ordinal);

        // Mevcut veritabanına uygulama komutu: dosya geçerli, hedefler kısıtlı, varsayılan kuru çalışma (-Apply ve onay olmadan değiştirmez)
        var script = MigrationLab.RepoFile("scripts/apply-history-hardening.ps1");
        // Söz dizimi: ek paket eklememek için Windows PowerShell'in kendi ayrıştırıcısı (yoksa bu denetim atlanır)
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(shell))
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(shell,
                $"-NoProfile -Command \"$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('{script}', [ref]$null, [ref]$e); if ($e) {{ $e | ForEach-Object {{ $_.Message }}; exit 1 }}\"")
            { RedirectStandardOutput = true, UseShellExecute = false })!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"PowerShell söz dizimi hatası: {output}");
        }

        var text = File.ReadAllText(script);
        Assert.Contains("ValidateSet('sanalprova_dev', 'sanalprova_test')", text, StringComparison.Ordinal);
        Assert.Contains("[switch]$Apply", text, StringComparison.Ordinal);
        Assert.Contains("ValidateSet('History', 'AppendOnly', 'All')", text, StringComparison.Ordinal);
        Assert.Contains("SupportsShouldProcess", text, StringComparison.Ordinal);
        Assert.Contains("if (-not $Apply)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_veritabanindaki_gercek_hesaplar_ayni_yetki_modelini_tasir()
    {
        var (app, migrator) = MigrationLab.SecretConnections();
        Assert.SkipUnless(app is not null && migrator is not null, ApiFactory.SkipReason);
        Assert.Equal("sanalprova_test", new SqlConnectionStringBuilder(app).InitialCatalog); // yalnızca test veritabanı
        Assert.Equal("sanalprova_test", new SqlConnectionStringBuilder(migrator).InitialCatalog);

        foreach (var (connection, isMigrator) in new[] { (app!, false), (migrator!, true) })
        {
            Assert.Equal(0, await Scalar<int>(connection, "SELECT IS_SRVROLEMEMBER('sysadmin')"));
            Assert.Equal(0, await Scalar<int>(connection, "SELECT IS_MEMBER('db_owner')"));
            Assert.Equal(isMigrator ? 1 : 0, await Scalar<int>(connection, "SELECT IS_MEMBER('db_ddladmin')"));
            Assert.Equal(0, await Scalar<int>(connection, "SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE')"));
        }

        await Assert.ThrowsAsync<SqlException>(() => Exec(app!, "CREATE TABLE dbo.m32_app_probe (id int)"));
        await Exec(migrator!, "CREATE TABLE dbo.m32_migrator_probe (id int); DROP TABLE dbo.m32_migrator_probe;");

        static async Task<T> Scalar<T>(string connectionString, string sql)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(Ct);
            await using var command = new SqlCommand(sql, connection);
            return (T)Convert.ChangeType((await command.ExecuteScalarAsync(Ct))!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }

        static async Task Exec(string connectionString, string sql)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(Ct);
            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    [Fact]
    public async Task Test_veritabaninda_migration_gecmisi_sertlestirmesi_uygulanmissa_gercek_hesaplar_beklendigi_gibi_davranir()
    {
        var (app, migrator) = MigrationLab.SecretConnections();
        Assert.SkipUnless(app is not null && migrator is not null, ApiFactory.SkipReason);
        Assert.Equal("sanalprova_test", new SqlConnectionStringBuilder(app).InitialCatalog);

        async Task<int> Perm(string connection, string permission)
        {
            await using var c = new SqlConnection(connection);
            await c.OpenAsync(Ct);
            await using var command = new SqlCommand($"SELECT HAS_PERMS_BY_NAME(N'dbo.__ef_migrations_history', 'OBJECT', '{permission}')", c);
            return Convert.ToInt32(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
        }

        // Ortama bağlı durum: sertleştirme (scripts/apply-history-hardening.ps1) bu veritabanına uygulanmadıysa test atlanır, hiçbir yazma denenmez
        Assert.SkipUnless(await Perm(app!, "INSERT") == 0, "Migration geçmişi sertleştirmesi bu test veritabanına henüz uygulanmamış.");

        foreach (var permission in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            Assert.Equal(0, await Perm(app!, permission));
            Assert.Equal(1, await Perm(migrator!, permission));
        }

        await using var connection = new SqlConnection(app);
        await connection.OpenAsync(Ct);
        foreach (var sql in new[]
                 {
                     "INSERT INTO __ef_migrations_history (migration_id, product_version) VALUES (N'29990101000000_Sahte', N'10.0.0')",
                     "UPDATE __ef_migrations_history SET product_version = N'0'",
                     "DELETE FROM __ef_migrations_history",
                 })
        {
            await using var command = new SqlCommand(sql, connection);
            var ex = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync(Ct));
            Assert.Equal(229, ex.Number);
        }

        await using var read = new SqlCommand("SELECT COUNT(*) FROM __ef_migrations_history", connection);
        Assert.True(Convert.ToInt32(await read.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture) >= AllMigrations().Length);
    }

    // ------------------------------------------------------------------ yardımcılar

    private static async Task AssertDeniedAsync(MigrationLab lab, string connectionString, string statement)
    {
        SqlException? ex = null;
        try
        {
            await lab.ExecuteAsync(statement, connectionString, Ct);
        }
        catch (SqlException caught)
        {
            ex = caught;
        }

        Assert.True(ex is not null, $"Reddedilmedi: {statement}");
        // 2104 = tetikleyici oluşturma izni yok; 262/230/229/297/300 = izin reddedildi (CREATE/ALTER/DROP/EXECUTE/VIEW), 15247 = kullanıcı bu eylem için yetkili değil, 15151 = nesne yok ya da izin yok
        Assert.True(ex.Number is 262 or 229 or 230 or 297 or 300 or 15247 or 15151 or 1088 or 3701 or 5011 or 4701 or 2104, $"Beklenmeyen hata {ex.Number}: {ex.Message} ({statement})");
    }

    private static async Task<bool> HasColumnAsync(MigrationLab lab, string table, string column) =>
        await lab.ScalarAsync<int>($"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'{table}') AND name = N'{column}'", ct: Ct) == 1;

    private sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    /// <summary>Eski (R4) şemaya uygun, gerçeğe yakın veri. Satırlar <see cref="RowFactory"/> ile (şema kataloğundan) üretilir.</summary>
    private sealed record Seed(Guid OrganizationId, Guid StoreId, Guid UncertainJobWithRow, Guid UncertainJobWithoutRow, Guid LegacyUncertainLogId)
    {
        public static async Task<Seed> BaselineAsync(MigrationLab lab, CancellationToken ct)
        {
            var rows = await RowFactory.LoadAsync(lab.MigratorCs, ct);

            async Task<Guid> Insert(string table, params (string Column, object? Value)[] values) =>
                (Guid)(await rows.CreateAsync(table, values.ToDictionary(v => v.Column, v => v.Value, StringComparer.Ordinal), ct))["id"]!;

            var organization = await Insert("organizations", ("name", "Eski Firma"));
            var store = await Insert("stores", ("organization_id", organization));
            var user = await Insert("users", ("organization_id", organization), ("display_name", "Eski Personel"));
            var session = await Insert("tryon_sessions", ("organization_id", organization), ("store_id", store), ("created_by_user_id", user));

            Task<Guid> Job(string status, string? error) => Insert("generation_jobs",
                ("organization_id", organization), ("store_id", store), ("session_id", session), ("requested_by_user_id", user), ("status", status), ("error_code", error),
                ("attempt", 1), ("max_attempts", 3), ("provider", "Fashn"), ("model", "tryon-max"));

            Task<Guid> Log(Guid? job, bool success, decimal cost, string? error) => Insert("ai_usage_logs",
                ("organization_id", organization), ("store_id", store), ("job_id", job), ("provider", "Fashn"), ("model", "tryon-max"), ("operation", "OutfitSet"),
                ("success", success), ("estimated_cost_usd", cost), ("error_code", error));

            var succeeded = await Job("Succeeded", null);
            var uncertainWithRow = await Job("Failed", "OutcomeUncertain");
            var uncertainWithoutRow = await Job("Failed", "OutcomeUncertain");
            await Log(succeeded, true, 0.15m, null);
            await Log(uncertainWithRow, false, 0m, "ProviderUnavailable"); // önceki deneme
            var legacy = await Log(uncertainWithRow, false, 0.15m, "OutcomeUncertain"); // R4 döneminde yazılmış belirsiz maliyet: olay anahtarı yok
            return new Seed(organization, store, uncertainWithRow, uncertainWithoutRow, legacy);
        }
    }
}
