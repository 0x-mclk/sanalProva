using Microsoft.Data.SqlClient;

namespace SanalProva.IntegrationTests;

/// <summary>
/// Çapraz-firma / çapraz-şube referans tarayıcısı. Şemanın yabancı anahtarlarını kataloğdan okur ve her ilişki için "çocuk, ebeveyniyle aynı firmaya (ve aynı şubeye) mı
/// ait?" sorusunu setle sorar. Tek tek ilişki yazmaya gerek yoktur: yeni FK eklenince tarayıcı onu da kapsar. Üç amaca hizmet eder:
/// (1) testlerde "saldırı sonrası hiçbir çapraz referans oluşmadı" kanıtı, (2) migration öncesi mevcut veride ihlal olup olmadığının denetimi,
/// (3) gelecekte operasyonel sağlık denetimi.
/// </summary>
internal static class TenantScan
{
    internal sealed record Finding(string Kind, string Relation, long Count);

    /// <param name="onlyOrganizations">Verilirse yalnızca bu firmaların çocuk satırları taranır (paylaşılan test veritabanında diğer testlerin verisi karışmasın).</param>
    public static async Task<List<Finding>> ScanAsync(string connectionString, CancellationToken ct, IReadOnlyCollection<Guid>? onlyOrganizations = null)
    {
        var rows = await RowFactory.LoadAsync(connectionString, ct);
        var findings = new List<Finding>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        bool Has(string table, string column) => rows.Columns[table].Any(c => c.Name == column);
        var scope = onlyOrganizations is null ? string.Empty : $" AND c.organization_id IN ({string.Join(", ", onlyOrganizations.Select(g => $"'{g}'"))})";

        async Task<long> Count(string sql)
        {
            await using var command = new SqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        }

        foreach (var fk in rows.ForeignKeys)
        {
            var join = string.Join(" AND ", fk.ChildColumns.Select((c, i) => $"c.[{c}] = p.[{fk.ParentColumns[i]}]"));
            if (Has(fk.Child, "organization_id") && Has(fk.Parent, "organization_id") && fk.Child != "organizations")
            {
                var count = await Count($"SELECT COUNT_BIG(*) FROM [{fk.Child}] c JOIN [{fk.Parent}] p ON {join} WHERE c.organization_id <> p.organization_id{scope}");
                if (count > 0)
                {
                    findings.Add(new Finding("FARKLI_FIRMA", $"{fk.Child}.{string.Join(',', fk.ChildColumns)} → {fk.Parent}", count));
                }
            }

            // Şube düzeyi: çocuk ve ebeveyn ikisi de şubeye aitse (ör. iş ↔ oturum, TV ↔ oturum) aynı şubede olmalı
            if (Has(fk.Child, "store_id") && Has(fk.Parent, "store_id") && Has(fk.Child, "organization_id"))
            {
                var count = await Count($"SELECT COUNT_BIG(*) FROM [{fk.Child}] c JOIN [{fk.Parent}] p ON {join} WHERE c.store_id <> p.store_id{scope}");
                if (count > 0)
                {
                    findings.Add(new Finding("FARKLI_SUBE", $"{fk.Child}.{string.Join(',', fk.ChildColumns)} → {fk.Parent}", count));
                }
            }
        }

        // Firma sütunu olan her tabloda: firma gerçekten var mı (firma FK'sı olmayan tablolarda ayrıca anlamlı)
        foreach (var table in rows.Columns.Keys.Where(t => Has(t, "organization_id") && t != "organizations"))
        {
            var count = await Count($"SELECT COUNT_BIG(*) FROM [{table}] c WHERE NOT EXISTS (SELECT 1 FROM organizations o WHERE o.id = c.organization_id){scope}");
            if (count > 0)
            {
                findings.Add(new Finding("VAR_OLMAYAN_FIRMA", table, count));
            }
        }

        // Şube sütunu olan tabloda şubenin firması, satırın firmasıyla aynı olmalı
        foreach (var table in rows.Columns.Keys.Where(t => Has(t, "organization_id") && Has(t, "store_id")))
        {
            var count = await Count($"SELECT COUNT_BIG(*) FROM [{table}] c JOIN stores s ON s.id = c.store_id WHERE s.organization_id <> c.organization_id{scope}");
            if (count > 0)
            {
                findings.Add(new Finding("SUBE_BASKA_FIRMADA", table, count));
            }
        }

        return findings;
    }
}
