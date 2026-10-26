using Microsoft.Data.SqlClient;

namespace SanalProva.IntegrationTests;

/// <summary>
/// M3.4 tasarım doğrulaması: "çocuk satır, ebeveyniyle aynı firmaya ait olmalı" kuralını SQL Server'a yaptırmanın mekanizması. Gerçek şemaya dokunmadan, geçici tablolarda
/// bileşik yabancı anahtarın (ebeveyn_id, firma_id) → ebeveyn(id, firma_id) davranışları kanıtlanır: CASCADE, NO ACTION, NULL'lanabilir bağlantı ve
/// SET NULL ile birlikte kullanım (bileşik FK'da SET NULL firma sütununu da boşaltmaya çalışır; çözüm: SET NULL için ayrı tek sütunlu FK + NO ACTION bileşik FK).
/// </summary>
public sealed class CompositeForeignKeyMechanismTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Bilesik_yabanci_anahtar_firma_tutarliligini_veritabaninda_zorunlu_kilar_CASCADE_ve_SET_NULL_ile_birlikte_kullanilabilir()
    {
        await using var lab = await MigrationLab.CreateAsync(Ct);
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        await lab.ExecuteAsync("""
            CREATE TABLE dbo.m34_parent (id uniqueidentifier NOT NULL PRIMARY KEY, organization_id uniqueidentifier NOT NULL, CONSTRAINT ak_m34_parent UNIQUE (id, organization_id));
            -- 1) CASCADE: zorunlu bağlantı
            CREATE TABLE dbo.m34_cascade (id uniqueidentifier NOT NULL PRIMARY KEY, organization_id uniqueidentifier NOT NULL, parent_id uniqueidentifier NOT NULL,
                CONSTRAINT fk_m34_cascade FOREIGN KEY (parent_id, organization_id) REFERENCES dbo.m34_parent (id, organization_id) ON DELETE CASCADE);
            -- 2) NULL'lanabilir bağlantı, NO ACTION
            CREATE TABLE dbo.m34_nullable (id uniqueidentifier NOT NULL PRIMARY KEY, organization_id uniqueidentifier NOT NULL, parent_id uniqueidentifier NULL,
                CONSTRAINT fk_m34_nullable FOREIGN KEY (parent_id, organization_id) REFERENCES dbo.m34_parent (id, organization_id));
            -- 3) SET NULL gereken bağlantı: tek sütunlu FK (SET NULL) + bileşik FK (NO ACTION, tutarlılık için)
            CREATE TABLE dbo.m34_setnull (id uniqueidentifier NOT NULL PRIMARY KEY, organization_id uniqueidentifier NOT NULL, parent_id uniqueidentifier NULL,
                CONSTRAINT fk_m34_setnull_single FOREIGN KEY (parent_id) REFERENCES dbo.m34_parent (id) ON DELETE SET NULL,
                CONSTRAINT fk_m34_setnull_tenant FOREIGN KEY (parent_id, organization_id) REFERENCES dbo.m34_parent (id, organization_id));
            """, ct: Ct);

        var parentA = Guid.NewGuid();
        await lab.ExecuteAsync($"INSERT INTO dbo.m34_parent VALUES ('{parentA}', '{orgA}')", ct: Ct);

        async Task Rejected(string sql, string constraint)
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => lab.ExecuteAsync(sql, ct: Ct));
            Assert.Equal(547, ex.Number);
            Assert.Contains(constraint, ex.Message, StringComparison.Ordinal);
        }

        // Aynı firma kabul, farklı firma (ve var olmayan ebeveyn) reddedilir; NULL bağlantı kısıt dışıdır
        foreach (var (table, constraint) in new[] { ("m34_cascade", "fk_m34_cascade"), ("m34_nullable", "fk_m34_nullable"), ("m34_setnull", "fk_m34_setnull_tenant") })
        {
            await lab.ExecuteAsync($"INSERT INTO dbo.{table} (id, organization_id, parent_id) VALUES (NEWID(), '{orgA}', '{parentA}')", ct: Ct);
            await Rejected($"INSERT INTO dbo.{table} (id, organization_id, parent_id) VALUES (NEWID(), '{orgB}', '{parentA}')", table == "m34_setnull" ? "fk_m34_setnull" : constraint);
            await Rejected($"INSERT INTO dbo.{table} (id, organization_id, parent_id) VALUES (NEWID(), '{orgA}', '{Guid.NewGuid()}')", table == "m34_setnull" ? "fk_m34_setnull" : constraint);
            if (table != "m34_cascade")
            {
                await lab.ExecuteAsync($"INSERT INTO dbo.{table} (id, organization_id, parent_id) VALUES (NEWID(), '{orgB}', NULL)", ct: Ct); // bağlantısız satır herhangi bir firmada olabilir
            }

            // UPDATE ile firma/ebeveyn değiştirerek de çapraz bağ kurulamaz
            await Rejected($"UPDATE dbo.{table} SET organization_id = '{orgB}' WHERE parent_id = '{parentA}'", table == "m34_setnull" ? "fk_m34_setnull" : constraint);
        }

        // Silme davranışları korunur: CASCADE zincirleme siler; SET NULL ebeveyn silinince bağlantıyı boşaltır (firma sütununa dokunmadan); NO ACTION engeller
        var blocked = await Assert.ThrowsAsync<SqlException>(() => lab.ExecuteAsync($"DELETE FROM dbo.m34_parent WHERE id = '{parentA}'", ct: Ct));
        Assert.Equal(547, blocked.Number); // m34_nullable satırı hâlâ bağlı (NO ACTION)
        await lab.ExecuteAsync($"DELETE FROM dbo.m34_nullable WHERE parent_id = '{parentA}'", ct: Ct);
        await lab.ExecuteAsync($"DELETE FROM dbo.m34_parent WHERE id = '{parentA}'", ct: Ct);
        Assert.Equal(0, await lab.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.m34_cascade", ct: Ct));
        Assert.Equal(1, await lab.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.m34_setnull WHERE parent_id IS NULL AND organization_id = '{orgA}'", ct: Ct)); // SET NULL çalıştı, firma korundu

        // Tuzak: tek bileşik FK ile SET NULL, firma sütunu NOT NULL olduğu için SQL Server tarafından tanımlama anında reddedilir
        // → SET NULL ilişkilerinde yukarıdaki ikili yapı (tek sütunlu SET NULL FK + bileşik NO ACTION FK) kullanılır
        var bad = await Assert.ThrowsAsync<SqlException>(() => lab.ExecuteAsync("""
            CREATE TABLE dbo.m34_bad (id uniqueidentifier NOT NULL PRIMARY KEY, organization_id uniqueidentifier NOT NULL, parent_id uniqueidentifier NULL,
                CONSTRAINT fk_m34_bad FOREIGN KEY (parent_id, organization_id) REFERENCES dbo.m34_parent (id, organization_id) ON DELETE SET NULL);
            """, ct: Ct));
        Assert.Contains("SET NULL", bad.Message, StringComparison.Ordinal);
        var parentB = Guid.NewGuid();
        await lab.ExecuteAsync($"INSERT INTO dbo.m34_parent VALUES ('{parentB}', '{orgA}')", ct: Ct);

        // Mevcut veriyi doğrulayan ön kontrol sorgusu: ihlal varken FK eklemek (WITH CHECK) başarısız olur, ihlal yokken geçer
        await lab.ExecuteAsync($"CREATE TABLE dbo.m34_legacy (id uniqueidentifier NOT NULL PRIMARY KEY, organization_id uniqueidentifier NOT NULL, parent_id uniqueidentifier NOT NULL); INSERT INTO dbo.m34_legacy VALUES (NEWID(), '{orgB}', '{parentB}')", ct: Ct);
        Assert.Equal(1, await lab.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.m34_legacy c JOIN dbo.m34_parent p ON p.id = c.parent_id WHERE c.organization_id <> p.organization_id", ct: Ct));
        var legacy = await Assert.ThrowsAsync<SqlException>(() => lab.ExecuteAsync("ALTER TABLE dbo.m34_legacy WITH CHECK ADD CONSTRAINT fk_m34_legacy FOREIGN KEY (parent_id, organization_id) REFERENCES dbo.m34_parent (id, organization_id)", ct: Ct));
        Assert.Equal(547, legacy.Number);
        await lab.ExecuteAsync($"UPDATE dbo.m34_legacy SET organization_id = '{orgA}'; ALTER TABLE dbo.m34_legacy WITH CHECK ADD CONSTRAINT fk_m34_legacy FOREIGN KEY (parent_id, organization_id) REFERENCES dbo.m34_parent (id, organization_id)", ct: Ct);
        Assert.Equal(0, await lab.ScalarAsync<int>("SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'fk_m34_legacy' AND is_not_trusted = 1", ct: Ct)); // güvenilir (sorgu iyileştiricisi kullanabilir)
    }
}
