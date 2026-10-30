using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SanalProva.IntegrationTests;

/// <summary>
/// Laboratuvar veritabanı için genel amaçlı satır üreticisi. Şemayı sistem kataloğundan okur ve herhangi bir tabloya <b>geçerli</b> bir satır ekler:
/// NOT NULL yabancı anahtarların ebeveynlerini özyinelemeli olarak kendisi oluşturur, enum CHECK kısıtlarından geçerli bir değer seçer, benzersiz
/// indekslerle çakışmasın diye metinleri rastgele üretir. Böylece bütünlük testleri (FK matrisi, enum kısıtları) elle yazılmış tohum verisine değil,
/// veritabanının gerçek şemasına dayanır; şemaya yeni tablo/FK eklenince testler onu da kapsar.
/// </summary>
internal sealed partial class RowFactory
{
    /// <summary>Genel kuralın yetmediği, değeri CHECK ile belirlenen sayısal sütunlar (tablo.sütun → değer).</summary>
    private static readonly Dictionary<string, object> NumericOverrides = new(StringComparer.Ordinal)
    {
        ["categories.max_selection"] = 1,
        ["ai_cost_reconciliations.sequence"] = 1,
        ["credit_ledger.amount"] = 1,
    };

    private readonly string _connectionString;
    private readonly Dictionary<string, List<Column>> _columns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _primaryKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _enumLiterals = new(StringComparer.Ordinal);

    private RowFactory(string connectionString) => _connectionString = connectionString;

    internal sealed record Column(string Name, string Type, int MaxLength, bool Nullable, bool HasDefault, bool Identity, bool Computed);

    internal sealed record ForeignKey(string Name, string Child, string Parent, string Action, IReadOnlyList<string> ChildColumns, IReadOnlyList<string> ParentColumns);

    public IReadOnlyList<ForeignKey> ForeignKeys { get; private set; } = [];

    public IReadOnlyDictionary<string, List<Column>> Columns => _columns;

    public IReadOnlyList<string> PrimaryKey(string table) => _primaryKeys[table];

    public IReadOnlyList<string> EnumLiterals(string table, string column) => _enumLiterals.GetValueOrDefault($"{table}.{column}") ?? [];

    [GeneratedRegex(@"\[(?<c>[A-Za-z0-9_]+)\]\s*=\s*N?'(?<v>[^']*)'", RegexOptions.CultureInvariant)]
    private static partial Regex EqualityLiteral();

    public static async Task<RowFactory> LoadAsync(string connectionString, CancellationToken ct)
    {
        var factory = new RowFactory(connectionString);
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

        foreach (var r in await Rows("""
            SELECT t.name, c.name, ty.name, c.max_length, c.is_nullable, CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END, c.is_identity, c.is_computed
              FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
             WHERE t.is_ms_shipped = 0 AND t.name <> N'__ef_migrations_history' ORDER BY t.name, c.column_id
            """))
        {
            var table = (string)r[0]!;
            if (!factory._columns.TryGetValue(table, out var list))
            {
                factory._columns[table] = list = [];
            }

            list.Add(new Column((string)r[1]!, (string)r[2]!, Convert.ToInt32(r[3], System.Globalization.CultureInfo.InvariantCulture), (bool)r[4]!, Convert.ToInt32(r[5], System.Globalization.CultureInfo.InvariantCulture) == 1, (bool)r[6]!, (bool)r[7]!));
        }

        foreach (var r in await Rows("""
            SELECT t.name, c.name FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
              JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
             WHERE i.is_primary_key = 1 ORDER BY t.name, ic.key_ordinal
            """))
        {
            var table = (string)r[0]!;
            if (!factory._primaryKeys.TryGetValue(table, out var keys))
            {
                factory._primaryKeys[table] = keys = [];
            }

            keys.Add((string)r[1]!);
        }

        var foreignKeys = new Dictionary<string, (string Child, string Parent, string Action, List<string> Cols, List<string> Refs)>(StringComparer.Ordinal);
        foreach (var r in await Rows("""
            SELECT fk.name, tp.name, tr.name, fk.delete_referential_action_desc, pc.name, rc.name
              FROM sys.foreign_keys fk JOIN sys.tables tp ON tp.object_id = fk.parent_object_id JOIN sys.tables tr ON tr.object_id = fk.referenced_object_id
              JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
              JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
              JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
             ORDER BY fk.name, fkc.constraint_column_id
            """))
        {
            var name = (string)r[0]!;
            if (!foreignKeys.TryGetValue(name, out var fk))
            {
                foreignKeys[name] = fk = ((string)r[1]!, (string)r[2]!, (string)r[3]!, [], []);
            }

            fk.Cols.Add((string)r[4]!);
            fk.Refs.Add((string)r[5]!);
        }

        factory.ForeignKeys = [.. foreignKeys.Select(p => new ForeignKey(p.Key, p.Value.Child, p.Value.Parent, p.Value.Action, p.Value.Cols, p.Value.Refs)).OrderBy(f => f.Name, StringComparer.Ordinal)];

        foreach (var r in await Rows("SELECT t.name, cc.definition FROM sys.check_constraints cc JOIN sys.tables t ON t.object_id = cc.parent_object_id WHERE t.name <> N'__ef_migrations_history'"))
        {
            foreach (Match match in EqualityLiteral().Matches((string)r[1]!))
            {
                var key = $"{r[0]}.{match.Groups["c"].Value}";
                if (!factory._enumLiterals.TryGetValue(key, out var literals))
                {
                    factory._enumLiterals[key] = literals = [];
                }

                literals.Add(match.Groups["v"].Value);
            }
        }

        return factory;
    }

    /// <summary>
    /// Tabloya geçerli bir satır ekler ve eklenen değerleri (üretilen kimlik dahil) döndürür. <paramref name="overrides"/> verilen sütunlar aynen yazılır;
    /// NULL olamayan yabancı anahtar sütunları (override edilmemişse) için ebeveyn satır önce oluşturulur.
    /// </summary>
    public async Task<Dictionary<string, object?>> CreateAsync(string table, IReadOnlyDictionary<string, object?>? overrides = null, CancellationToken ct = default)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in overrides ?? new Dictionary<string, object?>())
        {
            values[pair.Key] = pair.Value;
        }

        var columns = _columns[table];

        // 0a) Çocuğa bir ebeveyn kimliği verildiyse eksik firma/şube sütunları o ebeveyn satırından devralınır (uygulama da çocuğu ebeveyninin firmasında yaratır)
        foreach (var fk in ForeignKeys.Where(f => f.Child == table && f.ChildColumns.Count > 1).OrderByDescending(f => f.ChildColumns.Count))
        {
            if (!values.TryGetValue(fk.ChildColumns[0], out var parentId) || parentId is null || fk.ChildColumns.Skip(1).All(values.ContainsKey))
            {
                continue;
            }

            await using var lookup = new SqlConnection(_connectionString);
            await lookup.OpenAsync(ct);
            await using var lookupCommand = new SqlCommand($"SELECT {string.Join(", ", fk.ParentColumns.Skip(1).Select(c => $"[{c}]"))} FROM [{fk.Parent}] WHERE [{fk.ParentColumns[0]}] = @id", lookup);
            lookupCommand.Parameters.AddWithValue("@id", parentId);
            await using var reader = await lookupCommand.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                for (var i = 1; i < fk.ChildColumns.Count; i++)
                {
                    values.TryAdd(fk.ChildColumns[i], reader.GetValue(i - 1));
                }
            }
        }

        // 0) Tek firma kimliği tüm satır grafiği için: firma sütunu olan tabloda verilmemişse gerçek bir firma satırı oluşturulur ve ebeveynlere aktarılır
        //    (bileşik FK'lar çocuk ve ebeveynin aynı firmada olmasını ister)
        if (columns.Any(c => c.Name == "organization_id") && !values.ContainsKey("organization_id"))
        {
            values["organization_id"] = (await CreateAsync("organizations", null, ct))["id"];
        }

        // 1) NULL olamayan yabancı anahtarların ebeveynleri. Çok sütunlu FK'lar önce işlenir (ör. iş → oturum: kimlik + şube + firma); çocukta zaten bilinen sütunlar
        //    (firma, şube) ebeveyne aktarılır, böylece tüm bileşik FK'lar aynı grafikte tutarlı kalır.
        foreach (var fk in ForeignKeys.Where(f => f.Child == table).OrderByDescending(f => f.ChildColumns.Count).ThenBy(f => f.Name, StringComparer.Ordinal))
        {
            if (fk.ChildColumns.All(values.ContainsKey) || fk.ChildColumns.Any(c => columns.Single(x => x.Name == c).Nullable))
            {
                continue;
            }

            var parentOverrides = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < fk.ChildColumns.Count; i++)
            {
                if (values.TryGetValue(fk.ChildColumns[i], out var known))
                {
                    parentOverrides[fk.ParentColumns[i]] = known;
                }
            }

            var parent = await CreateAsync(fk.Parent, parentOverrides, ct);
            for (var i = 0; i < fk.ChildColumns.Count; i++)
            {
                values[fk.ChildColumns[i]] = parent[fk.ParentColumns[i]];
            }
        }

        // 2) Kalan NULL olamayan sütunlar (varsayılanı olmayanlar)
        foreach (var column in columns.Where(c => !c.Identity && !c.Computed && !values.ContainsKey(c.Name)))
        {
            if (column.Name == "id" && column.Type == "uniqueidentifier")
            {
                values["id"] = Guid.CreateVersion7();
            }
            else if (!column.Nullable && !column.HasDefault)
            {
                values[column.Name] = DefaultFor(table, column);
            }
        }

        // Tablo bazlı ek varsayılanlar: CHECK'in zorunlu kıldığı NULL olabilen sütunlar (kredi defteri: Grant satırı referans ister)
        if (table == "credit_ledger")
        {
            values.TryAdd("reference", Guid.NewGuid().ToString("N"));
        }

        var names = values.Keys.ToList();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand($"INSERT INTO [{table}] ({string.Join(", ", names.Select(n => $"[{n}]"))}) VALUES ({string.Join(", ", names.Select((_, i) => $"@p{i}"))})", connection);
        for (var i = 0; i < names.Count; i++)
        {
            command.Parameters.Add(new SqlParameter($"@p{i}", values[names[i]] ?? DBNull.Value));
        }

        var identity = columns.SingleOrDefault(c => c.Identity);
        if (identity is null)
        {
            await command.ExecuteNonQueryAsync(ct);
        }
        else
        {
            command.CommandText += "; SELECT CAST(SCOPE_IDENTITY() AS bigint)"; // kimlik sütunu (ör. audit_logs.id): üretilen değer geri okunur
            values[identity.Name] = Convert.ChangeType(await command.ExecuteScalarAsync(ct), identity.Type == "int" ? typeof(int) : typeof(long), System.Globalization.CultureInfo.InvariantCulture);
        }

        return values;
    }

    private object DefaultFor(string table, Column column)
    {
        if (NumericOverrides.TryGetValue($"{table}.{column.Name}", out var fixedValue))
        {
            return fixedValue;
        }

        var literals = EnumLiterals(table, column.Name);
        if (literals.Count > 0)
        {
            return literals[0];
        }

        switch (column.Type)
        {
            case "uniqueidentifier":
                return Guid.NewGuid();
            case "nvarchar" or "varchar" or "nchar" or "char":
                var length = column.MaxLength == -1 ? int.MaxValue : column.Type is "nvarchar" or "nchar" ? column.MaxLength / 2 : column.MaxLength;
                return length == int.MaxValue || column.Name.Contains("settings", StringComparison.Ordinal) ? "{}" : Guid.NewGuid().ToString("N")[..Math.Min(length, 32)];
            case "int" or "bigint" or "smallint" or "tinyint":
                return 0;
            case "bit":
                return false;
            case "decimal" or "numeric":
                return 0m;
            case "datetimeoffset":
                return DateTimeOffset.UtcNow;
            case "datetime2" or "datetime":
                return DateTime.UtcNow;
            case "varbinary" or "binary":
                // Rastgele: sabit bir bayt dizisi (ör. {1}) benzersiz indeksli ikilik sütunlarda (M5.1: account_setup_tokens.token_hash) her çağrıda çakışırdı.
                var bytes = new byte[column.MaxLength > 0 ? column.MaxLength : 16];
                Random.Shared.NextBytes(bytes);
                return bytes;
            default:
                throw new InvalidOperationException($"{table}.{column.Name}: tür {column.Type} için üretici değer tanımlı değil.");
        }
    }
}
