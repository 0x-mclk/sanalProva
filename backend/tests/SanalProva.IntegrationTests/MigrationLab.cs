using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Tenancy;

namespace SanalProva.IntegrationTests;

/// <summary>
/// M3.2 için <b>geçici, yalıtılmış</b> SQL Server veritabanı. Her test kendi <c>sanalprova_lab_&lt;rastgele&gt;</c> veritabanını oluşturur ve sonunda siler.
/// Dev/test veritabanlarına, SQLEXPRESS örneklerine ve PostgreSQL'e dokunmaz.
/// <para>
/// Veritabanı oluşturmak sunucu düzeyinde yetki ister (uygulama ve migrator hesaplarında bilerek yok), bu yüzden yönetici bağlantısı gerekir ve
/// <b>açıkça isteğe bağlıdır</b>: <c>SANALPROVA_LAB_ADMIN</c> ortam değişkeni (Windows kimlik doğrulamalı, yalnızca SANALPROVA örneği) verilmezse testler atlanır.
/// Güvenlik ağları: örnek adı SANALPROVA olmalı; silme yalnızca bu sınıfın kendi oluşturduğu, önek + 8 onaltılık haneli adlara uygulanır.
/// </para>
/// Uygulama ve migrator bağlantı dizeleri, <c>sanalprova_test</c> için kullanılanların veritabanı adı değiştirilmiş halidir (gizli değerler hiçbir yere yazılmaz).
/// Yetkiler gerçek kurulumla aynıdır (migrator: db_ddladmin + okuma/yazma; app: yalnızca okuma/yazma; ikisinde de db_owner yok).
/// </summary>
internal sealed class MigrationLab : IAsyncDisposable
{
    public const string Prefix = "sanalprova_lab_";
    private static readonly Regex NamePattern = new("^sanalprova_lab_[0-9a-f]{8}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _adminServerCs;

    private MigrationLab(string name, string adminServerCs, string migratorCs, string appCs)
    {
        Name = name;
        _adminServerCs = adminServerCs;
        MigratorCs = migratorCs;
        AppCs = appCs;
        AdminCs = new SqlConnectionStringBuilder(adminServerCs) { InitialCatalog = name }.ConnectionString;
    }

    public string Name { get; }
    public string MigratorCs { get; }
    public string AppCs { get; }
    public string AdminCs { get; }

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SANALPROVA_LAB_ADMIN")) && SecretConnections().Migrator is not null;

    public static void RequireLab() => Assert.SkipUnless(IsConfigured, "Migration laboratuvarı yapılandırılmamış (SANALPROVA_LAB_ADMIN ortam değişkeni ve ConnectionStrings:Test/TestMigrator gerekir).");

    /// <summary>Test veritabanının bağlantı dizeleri (user-secrets/ortam). Yoksa null.</summary>
    public static (string? App, string? Migrator) SecretConnections()
    {
        var secrets = new ConfigurationBuilder().AddUserSecrets(typeof(Program).Assembly, optional: true).Build();
        var app = Environment.GetEnvironmentVariable("SANALPROVA_TEST_DB") ?? secrets.GetConnectionString("Test");
        var migrator = Environment.GetEnvironmentVariable("SANALPROVA_TEST_DB_MIGRATOR") ?? secrets.GetConnectionString("TestMigrator");
        return (app, migrator);
    }

    public static async Task<MigrationLab> CreateAsync(CancellationToken ct)
    {
        RequireLab();
        var admin = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SANALPROVA_LAB_ADMIN")!) { InitialCatalog = "master" }.ConnectionString;
        var (app, migrator) = SecretConnections();
        var name = Prefix + Guid.NewGuid().ToString("N")[..8];
        Assert.Matches(NamePattern, name);

        await using (var connection = new SqlConnection(admin))
        {
            await connection.OpenAsync(ct);
            // Güvenlik ağı: yalnızca SANALPROVA örneği ve yönetici yetkisi
            var (instance, sysadmin) = await QueryAsync(connection, "SELECT CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128)), IS_SRVROLEMEMBER('sysadmin')", ct);
            if (!string.Equals(instance, "SANALPROVA", StringComparison.OrdinalIgnoreCase) || sysadmin != "1")
            {
                throw new InvalidOperationException($"Laboratuvar yalnızca SANALPROVA örneğinde ve yönetici yetkisiyle çalışır (örnek: {instance ?? "(varsayılan)"}). Hiçbir şey yapılmadı.");
            }

            // Gerçek kurulumla aynı ayarlar (docs: provision.sql): collation, uyumluluk 160, RCSI, sahip sa
            await ExecAsync(connection, $"CREATE DATABASE [{name}] COLLATE Latin1_General_100_CI_AS_SC", ct);
            await ExecAsync(connection, $"ALTER DATABASE [{name}] SET COMPATIBILITY_LEVEL = 160", ct);
            await ExecAsync(connection, $"ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE", ct);
            await ExecAsync(connection, $"ALTER DATABASE [{name}] SET RECOVERY SIMPLE", ct);
            await ExecAsync(connection, $"ALTER DATABASE [{name}] SET PAGE_VERIFY CHECKSUM", ct);
            await ExecAsync(connection, $"ALTER AUTHORIZATION ON DATABASE::[{name}] TO [sa]", ct);
            connection.ChangeDatabase(name);
            foreach (var user in new[] { "sanalprova_migrator", "sanalprova_app" })
            {
                await ExecAsync(connection, $"CREATE USER [{user}] FOR LOGIN [{user}] WITH DEFAULT_SCHEMA = [dbo]", ct);
                await ExecAsync(connection, $"ALTER ROLE [db_datareader] ADD MEMBER [{user}]", ct);
                await ExecAsync(connection, $"ALTER ROLE [db_datawriter] ADD MEMBER [{user}]", ct);
            }

            await ExecAsync(connection, "ALTER ROLE [db_ddladmin] ADD MEMBER [sanalprova_migrator]", ct);
        }

        string With(string? cs) => new SqlConnectionStringBuilder(cs) { InitialCatalog = name }.ConnectionString;
        return new MigrationLab(name, admin, With(migrator), With(app));
    }

    public static AppDbContext OpenDb(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString, AppDbContext.ConfigureSqlServer)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options, NoTenantContext.Instance);
    }

    public AppDbContext OpenMigrator() => OpenDb(MigratorCs);

    /// <summary>Migrator hesabıyla tüm (veya belirtilen) migration'a kadar uygular.</summary>
    public async Task MigrateAsync(CancellationToken ct, string? target = null)
    {
        await using var db = OpenMigrator();
        if (target is null)
        {
            await db.Database.MigrateAsync(ct);
        }
        else
        {
            await db.GetService<IMigrator>().MigrateAsync(target, ct);
        }
    }

    /// <summary>Verilen hesapla (varsayılan: migrator) tek bir deyim çalıştırır.</summary>
    public async Task<int> ExecuteAsync(string sql, string? connectionString = null, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString ?? MigratorCs);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<T?> ScalarAsync<T>(string sql, string? connectionString = null, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString ?? MigratorCs);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>EF'in idempotent betiğini (<c>GO</c> ayırıcılı) parça parça çalıştırır; EF betik çalıştırıcısı olmadığı için testin kendisi yapar.</summary>
    public async Task RunScriptAsync(string script, CancellationToken ct)
    {
        await using var connection = new SqlConnection(MigratorCs);
        await connection.OpenAsync(ct);
        foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Depodaki bir dosyanın tam yolu (testin çalıştığı klasörden yukarı doğru arar).</summary>
    public static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Depo dosyası bulunamadı: {relative}");
    }

    /// <summary>
    /// Gönderilen bir sqlcmd betiğini (<c>$(Database)</c> değişkeni ve <c>GO</c> ayırıcılarıyla) laboratuvar veritabanında yönetici bağlantısıyla çalıştırır:
    /// betiğin kendisi sınanır, test içinde kopyalanmış bir sürüm değil. Betik <c>USE</c> ile yalnızca bu laboratuvar veritabanına yönlenir.
    /// </summary>
    public async Task RunSqlFileAsync(string relativePath, CancellationToken ct)
    {
        var text = (await File.ReadAllTextAsync(RepoFile(relativePath), ct)).Replace("$(Database)", Name, StringComparison.Ordinal);
        await using var connection = new SqlConnection(AdminCs);
        await connection.OpenAsync(ct);
        foreach (var batch in Regex.Split(text, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant))
        {
            if (!string.IsNullOrWhiteSpace(batch))
            {
                await ExecAsync(connection, batch, ct);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        // Silme yalnızca bu sınıfın oluşturduğu ad biçimine uygulanır
        if (!NamePattern.IsMatch(Name))
        {
            return;
        }

        await using var connection = new SqlConnection(_adminServerCs);
        await connection.OpenAsync();
        await ExecAsync(connection,
            $"IF DB_ID(N'{Name}') IS NOT NULL BEGIN ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]; END", CancellationToken.None);
    }

    private static async Task ExecAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(string? First, string? Second)> QueryAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.IsDBNull(0) ? null : Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// Veritabanındaki gerçek şemayı okur ve EF modeliyle karşılaştırır. Karşılaştırma "ad var mı" düzeyinde kalmaz: sütun türü/null durumu, indeks anahtarları,
/// benzersizlik, filtre, CHECK içeriği, yabancı anahtar silme davranışı ve güvenilirlik (<c>is_not_trusted</c>) doğrulanır.
/// SQL Server filtre/CHECK metnini yeniden yazdığı için (<c>IN</c> → <c>OR</c>, parantezler) metin değil belirteç kümeleri (sütun, sabit, sayı) karşılaştırılır.
/// </summary>
internal static partial class SchemaInspector
{
    private const string History = "__ef_migrations_history";

    [GeneratedRegex(@"\[(?<id>[A-Za-z0-9_]+)\]|N?'(?<lit>(?:[^']|'')*)'|(?<![\w\[.])(?<num>\d+(?:\.\d+)?)(?![\w\]])|(?<![\w\[.'])(?<bare>[A-Za-z_][A-Za-z0-9_]*)(?![\w\]'])", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase) { "AND", "OR", "NOT", "NULL", "IS", "IN", "BETWEEN", "LIKE" };

    internal static string Tokens(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return string.Empty;
        }

        // EF modeli sütunları köşeli parantezsiz yazabilir; SQL Server hepsini köşeli parantezle saklar: ikisi de aynı belirtece çevrilir
        var tokens = TokenPattern().Matches(sql)
            .Where(m => !m.Groups["bare"].Success || !SqlKeywords.Contains(m.Groups["bare"].Value))
            .Select(m =>
                m.Groups["id"].Success ? $"id:{m.Groups["id"].Value}"
                : m.Groups["bare"].Success ? $"id:{m.Groups["bare"].Value}"
                : m.Groups["lit"].Success ? $"lit:{m.Groups["lit"].Value}"
                : $"num:{m.Groups["num"].Value}");
        return string.Join(' ', tokens.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    private static string StoreType(string type, int maxLength, int precision, int scale) => type.ToLowerInvariant() switch
    {
        "nvarchar" or "nchar" => $"{type}({(maxLength == -1 ? "max" : (maxLength / 2).ToString(System.Globalization.CultureInfo.InvariantCulture))})",
        "varchar" or "char" or "varbinary" or "binary" => $"{type}({(maxLength == -1 ? "max" : maxLength.ToString(System.Globalization.CultureInfo.InvariantCulture))})",
        "decimal" or "numeric" => $"decimal({precision},{scale})",
        _ => type,
    };

    private static string NormalizeModelType(string storeType) => storeType.Replace("numeric", "decimal", StringComparison.OrdinalIgnoreCase)
        .Replace("datetimeoffset(7)", "datetimeoffset", StringComparison.OrdinalIgnoreCase).Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    internal sealed record Snapshot(
        IReadOnlyList<string> Columns, IReadOnlyList<string> Indexes, IReadOnlyList<string> Checks, IReadOnlyList<string> ForeignKeys, IReadOnlyList<string> Untrusted)
    {
        public string Fingerprint() => string.Join('\n', Columns.Concat(Indexes).Concat(Checks).Concat(ForeignKeys).Order(StringComparer.Ordinal));
    }

    /// <summary>Veritabanının fiilî şeması (sıralı, karşılaştırılabilir satırlar).</summary>
    public static async Task<Snapshot> ReadAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        async Task<List<object?[]>> Rows(string sql)
        {
            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<object?[]>();
            while (await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                reader.GetValues(row!);
                rows.Add(row.Select(v => v is DBNull ? null : v).ToArray());
            }

            return rows;
        }

        var tables = $"t.is_ms_shipped = 0 AND t.name <> N'{History}'";
        var columns = (await Rows($"""
            SELECT t.name, c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable
              FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
             WHERE {tables}
            """)).Select(r => $"col|{r[0]}.{r[1]}|{NormalizeModelType(StoreType((string)r[2]!, Convert.ToInt32(r[3], System.Globalization.CultureInfo.InvariantCulture), Convert.ToInt32(r[4], System.Globalization.CultureInfo.InvariantCulture), Convert.ToInt32(r[5], System.Globalization.CultureInfo.InvariantCulture)))}|{((bool)r[6]! ? "null" : "notnull")}").ToList();

        var indexes = (await Rows($"""
            SELECT t.name, i.name, i.is_unique, i.is_primary_key, i.filter_definition,
                   STRING_AGG(CASE WHEN ic.is_included_column = 0 THEN c.name END, N',') WITHIN GROUP (ORDER BY ic.key_ordinal)
              FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
              JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
             WHERE {tables} AND i.name IS NOT NULL
             GROUP BY t.name, i.name, i.is_unique, i.is_primary_key, i.filter_definition
            """)).Select(r => $"idx|{r[0]}|{r[1]}|{((bool)r[3]! ? "pk" : (bool)r[2]! ? "unique" : "plain")}|{r[5]}|{Tokens((string?)r[4])}").ToList();

        var checks = (await Rows($"""
            SELECT t.name, cc.name, cc.definition FROM sys.check_constraints cc JOIN sys.tables t ON t.object_id = cc.parent_object_id WHERE {tables}
            """)).Select(r => $"ck|{r[0]}|{r[1]}|{Tokens((string?)r[2])}").ToList();

        var foreignKeys = (await Rows($"""
            SELECT t.name, fk.name, tr.name, fk.delete_referential_action_desc
              FROM sys.foreign_keys fk JOIN sys.tables t ON t.object_id = fk.parent_object_id JOIN sys.tables tr ON tr.object_id = fk.referenced_object_id WHERE {tables}
            """)).Select(r => $"fk|{r[0]}|{r[1]}|{r[2]}|{r[3]}").ToList();

        var untrusted = (await Rows($"""
            SELECT N'fk ' + fk.name FROM sys.foreign_keys fk JOIN sys.tables t ON t.object_id = fk.parent_object_id WHERE {tables} AND (fk.is_not_trusted = 1 OR fk.is_disabled = 1)
            UNION ALL
            SELECT N'ck ' + cc.name FROM sys.check_constraints cc JOIN sys.tables t ON t.object_id = cc.parent_object_id WHERE {tables} AND (cc.is_not_trusted = 1 OR cc.is_disabled = 1)
            """)).Select(r => (string)r[0]!).ToList();

        return new Snapshot(columns, indexes, checks, foreignKeys, untrusted);
    }

    /// <summary>EF modelinin beklediği şema, <see cref="ReadAsync"/> ile aynı biçimde.</summary>
    public static Snapshot FromModel(DbContext db)
    {
        var model = db.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var columns = new List<string>();
        var indexes = new List<string>();
        var checks = new List<string>();
        var foreignKeys = new List<string>();

        foreach (var table in model.Tables.Where(t => t.Name != History))
        {
            columns.AddRange(table.Columns.Select(c => $"col|{table.Name}.{c.Name}|{NormalizeModelType(c.StoreType)}|{(c.IsNullable ? "null" : "notnull")}"));

            if (table.PrimaryKey is { } pk)
            {
                indexes.Add($"idx|{table.Name}|{pk.Name}|pk|{string.Join(',', pk.Columns.Select(c => c.Name))}|");
            }

            var tableId = StoreObjectIdentifier.Table(table.Name, table.Schema);
            // Alternatif anahtarlar (benzersiz kısıt): bileşik yabancı anahtarların hedefi
            indexes.AddRange(table.UniqueConstraints.Where(u => u != table.PrimaryKey).Select(u => $"idx|{table.Name}|{u.Name}|unique|{string.Join(',', u.Columns.Select(c => c.Name))}|"));
            indexes.AddRange(table.Indexes.Select(i => $"idx|{table.Name}|{i.Name}|{(i.IsUnique ? "unique" : "plain")}|{string.Join(',', i.Columns.Select(c => c.Name))}|{Tokens(i.MappedIndexes.First().GetFilter(tableId))}"));
            checks.AddRange(table.CheckConstraints.Select(c => $"ck|{table.Name}|{c.Name}|{Tokens(c.Sql)}"));
            foreignKeys.AddRange(table.ForeignKeyConstraints.Select(f => $"fk|{table.Name}|{f.Name}|{f.PrincipalTable.Name}|{DeleteAction(f.OnDeleteAction)}"));
        }

        return new Snapshot(columns, indexes, checks, foreignKeys, []);
    }

    private static string DeleteAction(ReferentialAction action) => action switch
    {
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET_NULL",
        ReferentialAction.SetDefault => "SET_DEFAULT",
        _ => "NO_ACTION", // Restrict ve NoAction SQL Server'da aynıdır
    };

    /// <summary>Model ile veritabanı arasındaki farklar (boşsa uyumlu). Yalnızca eksik/fazla/farklı nesneleri listeler.</summary>
    public static async Task<IReadOnlyList<string>> DifferencesAsync(DbContext db, string connectionString, CancellationToken ct)
    {
        var expected = FromModel(db);
        var actual = await ReadAsync(connectionString, ct);
        var differences = new List<string>();
        foreach (var (name, e, a) in new[]
                 {
                     ("sütun", expected.Columns, actual.Columns), ("indeks", expected.Indexes, actual.Indexes),
                     ("CHECK", expected.Checks, actual.Checks), ("yabancı anahtar", expected.ForeignKeys, actual.ForeignKeys),
                 })
        {
            differences.AddRange(e.Except(a, StringComparer.Ordinal).Select(x => $"{name} modelde var, veritabanında yok/farklı: {x}"));
            differences.AddRange(a.Except(e, StringComparer.Ordinal).Select(x => $"{name} veritabanında var, modelde yok/farklı: {x}"));
        }

        differences.AddRange(actual.Untrusted.Select(x => $"güvenilmeyen veya kapalı kısıt: {x}"));
        return differences;
    }
}
