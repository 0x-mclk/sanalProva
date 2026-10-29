using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SanalProva.Infrastructure.Tenancy;

namespace SanalProva.Infrastructure.Persistence;

/// <summary>
/// EF araçlarının (migrations add/script/bundle, database update) tasarım zamanı fabrikası. Uygulama host'unu, yapılandırmayı ve
/// user-secrets'ı YÜKLEMEZ: migration paketi (efbundle) Docker/CI'da <c>ConnectionStrings:Main</c> (uygulama hesabı) olmadan çalışır.
/// Modeli etkileyen ayarlar çalışma zamanıyla aynıdır (SQL Server + <see cref="AppDbContext.ConfigureSqlServer"/> + snake_case).
/// Gerçek bağlantı araca her zaman açıkça verilir (<c>--connection</c>, migrator hesabı); verilmezse yer tutucu hiçbir sunucuya ulaşmaz,
/// yani yanlış veritabanına migration uygulanamaz.
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>Kimlik bilgisi içermeyen, erişilemez yer tutucu (port 1).</summary>
    public const string PlaceholderConnection =
        "Server=tcp:127.0.0.1,1;Database=sanalprova_design_time;Integrated Security=False;Encrypt=True;TrustServerCertificate=True;Connect Timeout=1";

    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(PlaceholderConnection, AppDbContext.ConfigureSqlServer)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options, NoTenantContext.Instance);
    }
}
