using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.UnitTests.Infrastructure;

/// <summary>
/// Tasarım zamanı fabrikası (EF araçları, migration paketi): uygulama yapılandırması olmadan bağlam üretir, kimlik bilgisi taşımaz ve
/// modeli migration'larla birebir aynıdır. Veritabanına bağlanmaz.
/// </summary>
public sealed class AppDbContextFactoryTests
{
    [Fact]
    public void Yapilandirma_olmadan_SQL_Server_baglami_uretir_ve_yer_tutucu_kimlik_bilgisi_icermez()
    {
        using var db = new AppDbContextFactory().CreateDbContext([]);

        Assert.True(db.Database.IsSqlServer());
        var connection = new SqlConnectionStringBuilder(db.Database.GetConnectionString());
        Assert.Equal("tcp:127.0.0.1,1", connection.DataSource); // erişilemez port: --connection verilmezse hiçbir sunucuya migration uygulanamaz
        Assert.Equal(string.Empty, connection.UserID);
        Assert.Equal(string.Empty, connection.Password);
        Assert.False(connection.IntegratedSecurity);
    }

    [Fact]
    public void Tasarim_zamani_modeli_migration_anlik_goruntusuyle_ayni()
    {
        using var db = new AppDbContextFactory().CreateDbContext([]);

        // Fabrika modeli etkileyen bir ayarı (snake_case, uyumluluk düzeyi, geçmiş tablosu) kaçırırsa migration'lar sapar
        Assert.False(db.Database.HasPendingModelChanges(), "Tasarım zamanı modeli migration'larla aynı değil.");
        Assert.Contains(db.Database.GetMigrations(), m => m.EndsWith("_TenantConsistency", StringComparison.Ordinal));
    }
}
