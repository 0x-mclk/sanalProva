using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SanalProva.Domain.Credits;
using SanalProva.Infrastructure.Credits;

namespace SanalProva.IntegrationTests;

/// <summary>
/// Pilot Faz A: üretim SQL betikleri (en az yetki sertleştirmesi, yetki doğrulaması, yedek, geri yükleme, geri yükleme sonrası karantina ve tutarlılık denetimi) gerçek SQL Server'da,
/// yalnızca atılabilir <c>sanalprova_lab_*</c> veritabanlarında sınanır (<c>SANALPROVA_LAB_ADMIN</c> verilmezse atlanır). Üretim veritabanı/sunucu girişleri (provision) YALNIZCA sözdizimi
/// denetiminden geçirilir (PARSEONLY): sunucu düzeyi login'lere dokunulmaz. Hiçbir test dev/test/e2e veritabanına yazmaz.
/// </summary>
public sealed class ProductionOpsLabTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sql(string name) => MigrationLab.RepoFile($"deploy/sql/{name}");

    /// <summary>sqlcmd betiğini çalıştırır: $(Değişken) metin olarak yerleştirilir, GO satırlarından bölünür, tek bağlantıda sırayla yürütülür (THROW → SqlException).</summary>
    private static async Task RunAsync(string connectionString, string file, IReadOnlyDictionary<string, string> variables, bool parseOnly = false)
    {
        var text = await File.ReadAllTextAsync(Sql(file), Ct);
        foreach (var (key, value) in variables)
        {
            text = text.Replace($"$({key})", value, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("$(", text, StringComparison.Ordinal); // tanımsız betik değişkeni kalmamalı
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        if (parseOnly)
        {
            await using var on = new SqlCommand("SET PARSEONLY ON", connection);
            await on.ExecuteNonQueryAsync(Ct);
        }

        foreach (var batch in Regex.Split(text, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant))
        {
            if (string.IsNullOrWhiteSpace(batch) || (parseOnly && Regex.IsMatch(batch, @"^\s*USE\s", RegexOptions.IgnoreCase)))
            {
                continue;
            }

            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    private static Dictionary<string, string> Vars(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private static string MasterOf(string cs) => new SqlConnectionStringBuilder(cs) { InitialCatalog = "master" }.ConnectionString;

    // ---------------------------------------------------------------- sözdizimi (sunucuya dokunmadan)

    [Fact]
    public async Task Butun_uretim_betikleri_sozdizimi_gecerlidir_PARSEONLY_hicbir_sey_calistirilmaz()
    {
        MigrationLab.RequireLab();
        var admin = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SANALPROVA_LAB_ADMIN")!) { InitialCatalog = "master" }.ConnectionString;
        var all = Vars(
            ("Database", "sanalprova"), ("SourceDatabase", "sanalprova"), ("TargetDatabase", "sanalprova_restore_x"), ("BackupFile", "C:\\x\\y.bak"), ("Recovery", "RECOVERY"),
            ("StopAt", ""), ("RunCheckDb", "0"), ("AllowLoginlessUsers", "0"), ("SP_MIGRATOR_PW", "p1"), ("SP_APP_PW", "p2"), ("SP_BACKUP_PW", "p3"));

        foreach (var file in new[] { "provision-production.sql", "harden-production.sql", "verify-production.sql", "backup-full.sql", "backup-log.sql", "restore.sql", "restore-log.sql", "post-restore-quarantine.sql", "post-restore-check.sql", "post-restore-fix-users.sql" })
        {
            await RunAsync(admin, file, all, parseOnly: true);
        }
    }

    [Fact]
    public async Task Uretim_kurulum_ve_sertlestirme_korumali_veritabani_adlarini_reddeder()
    {
        MigrationLab.RequireLab();
        var admin = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SANALPROVA_LAB_ADMIN")!) { InitialCatalog = "master" }.ConnectionString;
        // provision: hiçbir şey oluşmadan ad denetiminde durur (ad denetimi ilk deyimdir)
        foreach (var name in new[] { "sanalprova_dev", "sanalprova_test", "sanalprova_e2e", "master", "sanalprova_lab_abcd1234" })
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => RunAsync(admin, "provision-production.sql",
                Vars(("Database", name), ("SP_MIGRATOR_PW", "x"), ("SP_APP_PW", "x"), ("SP_BACKUP_PW", "x"))));
            Assert.Equal(51300, ex.Number);
        }

        // harden: dev/test/e2e/sistem adlarında (USE sonrası ilk deyim) durur; sanalprova_dev/test veritabanlarına dokunmaz
        foreach (var name in new[] { "sanalprova_dev", "sanalprova_test" })
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => RunAsync(admin, "harden-production.sql", Vars(("Database", name), ("AllowLoginlessUsers", "0"))));
            Assert.Equal(51310, ex.Number);
        }
    }

    // ---------------------------------------------------------------- sertleştirme + yetki doğrulaması

    [Fact]
    public async Task Sertlestirme_uygulama_hesabini_en_az_yetkiye_indirir_idempotenttir_dogrulama_sapmayi_yakalar_ve_defterler_eklemeye_aciktir()
    {
        MigrationLab.RequireLab();
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        var vars = Vars(("Database", lab.Name), ("AllowLoginlessUsers", "0"));

        // Başlangıç (MigrationLab test kurulumu): app db_datareader + db_datawriter üyesidir → doğrulama BAŞARISIZ olmalı (geniş yetki)
        var before = await Assert.ThrowsAsync<SqlException>(() => RunAsync(lab.AdminCs, "verify-production.sql", vars));
        Assert.Equal(51321, before.Number);

        await RunAsync(lab.AdminCs, "harden-production.sql", vars);
        await RunAsync(lab.AdminCs, "verify-production.sql", vars); // geçer
        await RunAsync(lab.AdminCs, "harden-production.sql", vars); // idempotent: ikinci çalıştırma hata vermez
        await RunAsync(lab.AdminCs, "verify-production.sql", vars);

        // Sapma: app'e geniş rol geri verilirse doğrulama yakalar; sertleştirme düzeltir
        await lab.ExecuteAsync("ALTER ROLE [db_datawriter] ADD MEMBER [sanalprova_app]", lab.AdminCs, Ct);
        Assert.Equal(51321, (await Assert.ThrowsAsync<SqlException>(() => RunAsync(lab.AdminCs, "verify-production.sql", vars))).Number);
        await RunAsync(lab.AdminCs, "harden-production.sql", vars);
        await RunAsync(lab.AdminCs, "verify-production.sql", vars);

        // Gerçek davranış (yetki bürünmesiyle): app veri yazar, ama defteri değiştiremez/silemez ve şema değiştiremez
        var rowFactory = await RowFactory.LoadAsync(lab.MigratorCs, Ct);
        var org = (await rowFactory.CreateAsync("organizations", null, Ct))["id"];
        var statements = new (string Sql, bool ShouldWork)[]
        {
            ($"INSERT INTO credit_ledger (id, organization_id, entry_type, amount, reference, source, balance_after, reserved_after, created_at) VALUES (NEWID(), '{org}', N'Grant', 1, N'ref-{Guid.NewGuid():N}', N'Operator', 1, 0, SYSDATETIMEOFFSET())", true),
            ($"UPDATE credit_ledger SET amount = 5 WHERE organization_id = '{org}'", false),
            ($"DELETE FROM credit_ledger WHERE organization_id = '{org}'", false),
            ("UPDATE audit_logs SET action = N'x'", false),
            ("DELETE FROM audit_logs", false),
            ("UPDATE ai_cost_reconciliations SET note = N'x'", false),
            ("DELETE FROM organization_agreement_acceptances", false),
            ("INSERT INTO __ef_migrations_history (migration_id, product_version) VALUES (N'x', N'x')", false),
            ("CREATE TABLE dbo.kotu_niyet (id int)", false),
            ("ALTER TABLE credit_ledger DISABLE TRIGGER tr_credit_ledger_append_only", false),
            ("DROP TABLE credit_ledger", false),
            ($"BACKUP DATABASE [{lab.Name}] TO DISK = N'NUL'", false),
        };
        await using var connection = new SqlConnection(lab.AdminCs);
        await connection.OpenAsync(Ct);
        foreach (var (statement, shouldWork) in statements)
        {
            await using var command = new SqlCommand($"EXECUTE AS USER = N'sanalprova_app'; BEGIN TRY {statement}; END TRY BEGIN CATCH REVERT; THROW; END CATCH; REVERT;", connection);
            if (shouldWork)
            {
                await command.ExecuteNonQueryAsync(Ct);
            }
            else
            {
                var ex = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync(Ct));
                Assert.Contains(ex.Number, new[] { 229, 262, 300, 297, 1088, 3701, 4902, 15151 }); // izin reddi (DENY/yetki yok); tetikleyici hatası (51200) DEĞİL: DENY önce devreye girer
            }
        }
    }

    [Fact]
    public async Task Yalnizca_ekleme_tablolari_sertlestirme_listesiyle_tetikleyici_envanteri_ayni_kalir()
    {
        MigrationLab.RequireLab();
        await using var lab = await MigrationLab.CreateAsync(Ct);
        await lab.MigrateAsync(Ct);
        var script = await File.ReadAllTextAsync(Sql("harden-production.sql"), Ct);
        var listed = Regex.Matches(script, @"\(N'(?<t>[a-z_]+)', [01]\)").Select(m => m.Groups["t"].Value).ToHashSet(StringComparer.Ordinal);

        await using var connection = new SqlConnection(lab.AdminCs);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand("SELECT OBJECT_NAME(parent_id) FROM sys.triggers WHERE is_instead_of_trigger = 1 AND parent_class = 1", connection);
        var triggered = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                triggered.Add(reader.GetString(0));
            }
        }

        Assert.NotEmpty(triggered); // credit_ledger
        Assert.All(triggered, t => Assert.Contains(t, listed)); // tetikleyicili her yalnızca-ekleme tablo sertleştirme listesinde olmalı
        Assert.Contains("credit_ledger", listed);
        // Listedeki her ad gerçek bir tablo olmalı (yazım hatası sessizce atlanmasın)
        foreach (var table in listed)
        {
            await using var exists = new SqlCommand($"SELECT OBJECT_ID(N'dbo.{table}', N'U')", connection);
            Assert.NotEqual(DBNull.Value, await exists.ExecuteScalarAsync(Ct));
        }
    }

    // ---------------------------------------------------------------- yedek → geri yükleme → karantina → tutarlılık

    private static async Task DropAsync(string adminServerCs, string name)
    {
        Assert.StartsWith(MigrationLab.Prefix, name, StringComparison.Ordinal); // yalnızca lab adları
        await using var connection = new SqlConnection(MasterOf(adminServerCs));
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new SqlCommand($"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END", connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>
    /// Yedek klasörü: kullanıcının geçici klasörü altında BENZERSİZ bir alt klasör; SQL Server hizmet hesabına yalnızca bu klasör için yazma izni verilir (icacls, kalıtımlı:
    /// hizmetin yazdığı dosyalar kullanıcı tarafından silinebilir). Örneğin varsayılan yedek klasörüne (gerçek yedekler orada durur) HİÇ yazılmaz.
    /// </summary>
    private static async Task<string> BackupDirectoryAsync(string adminCs)
    {
        await using var connection = new SqlConnection(adminCs);
        await connection.OpenAsync(Ct);
        await using var query = new SqlCommand("SELECT TOP 1 service_account FROM sys.dm_server_services WHERE servicename LIKE N'SQL Server (%'", connection);
        var account = (string?)await query.ExecuteScalarAsync(Ct);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(account), "SQL Server hizmet hesabı öğrenilemedi.");
        var directory = Path.Combine(Path.GetTempPath(), "sanalprova-lab-backups", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(directory);
        var start = new System.Diagnostics.ProcessStartInfo("icacls") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { directory, "/grant", $"{account}:(OI)(CI)F" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync(Ct);
        Assert.SkipWhen(process.ExitCode != 0, "SQL Server hizmet hesabına geçici yedek klasörü için izin verilemedi (icacls).");
        return directory;
    }

    private static async Task<(int Balance, int Reserved, int Entries)> CreditStateAsync(string cs, Guid org)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(
            $"SELECT ISNULL((SELECT balance_credits FROM credit_accounts WHERE organization_id = '{org}'), -1), ISNULL((SELECT reserved_credits FROM credit_accounts WHERE organization_id = '{org}'), -1), (SELECT COUNT(*) FROM credit_ledger WHERE organization_id = '{org}')", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private static async Task<T> ScalarAsync<T>(string cs, string sql)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(sql, connection);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync(Ct))!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Yedek_dogrulanir_yeni_veritabanina_geri_yuklenir_ustune_yazmaz_karantina_ve_tutarlilik_denetimi_calisir_gunluk_zinciriyle_son_duruma_gidilir()
    {
        MigrationLab.RequireLab();
        await using var source = await MigrationLab.CreateAsync(Ct);
        await source.MigrateAsync(Ct);
        await source.ExecuteAsync($"ALTER DATABASE [{source.Name}] SET RECOVERY FULL", source.AdminCs, Ct); // üretimle aynı (zaman noktasına geri dönüş)
        var rows = await RowFactory.LoadAsync(source.MigratorCs, Ct);
        var service = new CreditLedgerService(TimeProvider.System);

        // --- Tohum: firma, kredi (yükleme + gönderilmiş işte rezervasyon), çalışan/kuyruktaki işler, açık kurulum bağlantısı
        var org = (Guid)(await rows.CreateAsync("organizations", null, Ct))["id"]!;
        var store = (Guid)(await rows.CreateAsync("stores", new Dictionary<string, object?> { ["organization_id"] = org }, Ct))["id"]!;
        var user = (Guid)(await rows.CreateAsync("users", new Dictionary<string, object?> { ["organization_id"] = org, ["is_active"] = true }, Ct))["id"]!;
        var session = (Guid)(await rows.CreateAsync("tryon_sessions", new Dictionary<string, object?> { ["organization_id"] = org, ["store_id"] = store, ["created_by_user_id"] = user }, Ct))["id"]!;
        async Task<Guid> JobAsync(string status, bool submitted) => (Guid)(await rows.CreateAsync("generation_jobs", new Dictionary<string, object?>
        {
            ["organization_id"] = org, ["store_id"] = store, ["session_id"] = session, ["status"] = status, ["attempt"] = 1, ["max_attempts"] = 3,
            ["provider_submit_started_at"] = submitted ? DateTimeOffset.UtcNow : null,
        }, Ct))["id"]!;
        var submittedJob = await JobAsync("Running", submitted: true); // gönderilmiş: yedek anında sağlayıcıda olabilir
        var unsentJob = await JobAsync("Queued", submitted: false);
        var now = DateTimeOffset.UtcNow;
        await rows.CreateAsync("account_setup_tokens", new Dictionary<string, object?> { ["organization_id"] = org, ["user_id"] = user, ["purpose"] = "InitialPassword", ["created_at"] = now, ["expires_at"] = now.AddHours(10) }, Ct);

        await using (var db = source.OpenMigrator())
        {
            Assert.Equal(CreditOperatorStatus.Applied, (await service.ApplyOperatorEntryAsync(db, org, CreditEntryType.Grant, 100, "lab-yukleme-0001", CreditSource.Operator, null, Ct)).Status);
            foreach (var job in new[] { submittedJob, unsentJob })
            {
                await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                Assert.Equal(CreditReserveStatus.Reserved, (await service.ReserveAsync(db, org, job, 2, "default", Ct)).Status);
                await db.SaveChangesAsync(Ct);
                await transaction.CommitAsync(Ct);
            }
        }

        Assert.Equal((100, 4, 3), await CreditStateAsync(source.MigratorCs, org));

        // --- Tam yedek (SQL Server sıkıştırır, sağlama toplamı ve VERIFYONLY betiğin içindedir)
        var directory = await BackupDirectoryAsync(source.AdminCs);
        var fullFile = Path.Combine(directory, "tam.bak");
        var logFile = Path.Combine(directory, "gunluk.trn");
        var restoredOnlyFull = MigrationLab.Prefix + Guid.NewGuid().ToString("N")[..8];
        var restoredWithLog = MigrationLab.Prefix + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RunAsync(source.AdminCs, "backup-full.sql", Vars(("Database", source.Name), ("BackupFile", fullFile)));

            // --- Yedekten SONRA kaynakta olanlar: ek kredi yüklemesi, işin tamamlanması (ücretlendi) → bunlar yalnızca günlük zinciriyle geri gelir
            await using (var db = source.OpenMigrator())
            {
                Assert.Equal(CreditOperatorStatus.Applied, (await service.ApplyOperatorEntryAsync(db, org, CreditEntryType.Grant, 50, "lab-yukleme-0002", CreditSource.Operator, null, Ct)).Status);
            }

            await source.ExecuteAsync($"UPDATE generation_jobs SET status = N'Succeeded' WHERE id = '{submittedJob}'", source.MigratorCs, Ct);
            await RunAsync(source.AdminCs, "backup-log.sql", Vars(("Database", source.Name), ("BackupFile", logFile)));
            Assert.Equal((150, 4, 4), await CreditStateAsync(source.MigratorCs, org));

            // --- (A) Yalnızca tam yedek: yedek anındaki duruma döner (sonraki yükleme YOK)
            await RunAsync(source.AdminCs, "restore.sql", Vars(("SourceDatabase", source.Name), ("TargetDatabase", restoredOnlyFull), ("BackupFile", fullFile), ("Recovery", "RECOVERY")));
            var cs = new SqlConnectionStringBuilder(source.AdminCs) { InitialCatalog = restoredOnlyFull }.ConnectionString;
            Assert.Equal((100, 4, 3), await CreditStateAsync(cs, org)); // 150 DEĞİL: yedekten sonraki işlemler yedekte yok (operatör sağlayıcı faturası/kayıtlarıyla mutabık olmalı)

            // Üzerine yazma yok: aynı hedefe ikinci geri yükleme reddedilir; kaynak veritabanı da hedef olamaz
            Assert.Equal(51334, (await Assert.ThrowsAsync<SqlException>(() => RunAsync(source.AdminCs, "restore.sql",
                Vars(("SourceDatabase", source.Name), ("TargetDatabase", restoredOnlyFull), ("BackupFile", fullFile), ("Recovery", "RECOVERY"))))).Number);
            Assert.Equal(51334, (await Assert.ThrowsAsync<SqlException>(() => RunAsync(source.AdminCs, "restore.sql",
                Vars(("SourceDatabase", source.Name), ("TargetDatabase", source.Name), ("BackupFile", fullFile), ("Recovery", "RECOVERY"))))).Number);
            Assert.Equal(51333, (await Assert.ThrowsAsync<SqlException>(() => RunAsync(source.AdminCs, "restore.sql",
                Vars(("SourceDatabase", source.Name), ("TargetDatabase", "sanalprova_dev"), ("BackupFile", fullFile), ("Recovery", "RECOVERY"))))).Number);

            // Karantina ÖNCE: denetim "çalışmaya hazır iş / açık bağlantı var" diye BAŞARISIZ olur (geri yüklenen veritabanı karantinasız üretime alınmaz)
            var check = Vars(("Database", restoredOnlyFull), ("RunCheckDb", "0"));
            Assert.Equal(51350, (await Assert.ThrowsAsync<SqlException>(() => RunAsync(source.AdminCs, "post-restore-check.sql", check))).Number);

            await RunAsync(source.AdminCs, "post-restore-fix-users.sql", Vars(("Database", restoredOnlyFull))); // aynı sunucuda eşleşme sağlam: hiçbir şey değişmez, hata yok
            await RunAsync(source.AdminCs, "post-restore-fix-users.sql", Vars(("Database", restoredOnlyFull)));
            var stampBefore = await ScalarAsync<string>(cs, $"SELECT security_stamp FROM users WHERE id = '{user}'");
            await RunAsync(source.AdminCs, "post-restore-quarantine.sql", Vars(("Database", restoredOnlyFull)));
            await RunAsync(source.AdminCs, "post-restore-quarantine.sql", Vars(("Database", restoredOnlyFull))); // idempotent
            Assert.Equal("Failed|OutcomeUncertain", await ScalarAsync<string>(cs, $"SELECT status + '|' + error_code FROM generation_jobs WHERE id = '{submittedJob}'")); // gönderilmiş: belirsiz (çift çağrı yok)
            Assert.Equal("Cancelled", await ScalarAsync<string>(cs, $"SELECT status FROM generation_jobs WHERE id = '{unsentJob}'")); // sağlayıcıya gitmemiş: iptal (çift çağrı yok)
            Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM generation_jobs WHERE status IN (N'Queued', N'Running')"));
            Assert.Equal(0, await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM account_setup_tokens WHERE used_at IS NULL AND revoked_at IS NULL"));
            Assert.NotEqual(stampBefore, await ScalarAsync<string>(cs, $"SELECT security_stamp FROM users WHERE id = '{user}'")); // oturumlar geçersiz

            // Kredi, uygulamanın kendi kapanış kuralıyla tutarlı hale gelir: iptal edilen (gönderilmemiş) iade, belirsiz bloke
            await using (var restored = MigrationLab.OpenDb(new SqlConnectionStringBuilder(source.MigratorCs) { InitialCatalog = restoredOnlyFull }.ConnectionString))
            {
                Assert.Equal(1, await service.SettleOpenAsync(restored, 50, Ct));
            }

            Assert.Equal((100, 2, 4), await CreditStateAsync(cs, org)); // 2 (belirsiz işin rezervasyonu) bloke; gönderilmemiş işin 2'si iade
            await RunAsync(source.AdminCs, "post-restore-check.sql", Vars(("Database", restoredOnlyFull), ("RunCheckDb", "1"))); // kredi/kısıt tutarlılığı + DBCC CHECKDB

            // --- (B) Tam + günlük zinciri: yedekten SONRAKİ duruma gelir
            await RunAsync(source.AdminCs, "restore.sql", Vars(("SourceDatabase", source.Name), ("TargetDatabase", restoredWithLog), ("BackupFile", fullFile), ("Recovery", "NORECOVERY")));
            Assert.Equal(51336, (await Assert.ThrowsAsync<SqlException>(() => RunAsync(source.AdminCs, "restore-log.sql",
                Vars(("TargetDatabase", restoredOnlyFull), ("BackupFile", logFile), ("Recovery", "RECOVERY"), ("StopAt", ""))))).Number); // RESTORING olmayan hedefe günlük uygulanmaz
            await RunAsync(source.AdminCs, "restore-log.sql", Vars(("TargetDatabase", restoredWithLog), ("BackupFile", logFile), ("Recovery", "RECOVERY"), ("StopAt", "")));
            var cs2 = new SqlConnectionStringBuilder(source.AdminCs) { InitialCatalog = restoredWithLog }.ConnectionString;
            Assert.Equal((150, 4, 4), await CreditStateAsync(cs2, org)); // günlükle son duruma ulaşıldı
            Assert.Equal("Succeeded", await ScalarAsync<string>(cs2, $"SELECT status FROM generation_jobs WHERE id = '{submittedJob}'"));
        }
        finally
        {
            await DropAsync(source.AdminCs, restoredOnlyFull);
            await DropAsync(source.AdminCs, restoredWithLog);
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Lab yedek klasörü silinemedi (elle silin): {directory}");
            }
        }
    }
}
