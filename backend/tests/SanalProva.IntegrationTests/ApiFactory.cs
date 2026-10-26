using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Ai.Abstractions;
using SanalProva.Infrastructure.Ai.Mock;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Tenancy;

// Testler paralel çalışabilir: her test kendi benzersiz firmasını oluşturur (TestData.CreateTenantAsync)
[assembly: AssemblyFixture(typeof(SanalProva.IntegrationTests.ApiFactory))]

namespace SanalProva.IntegrationTests;

public enum InjectedFault
{
    /// <summary>Geçici hata: worker yeniden dener, deneme hakkı bitince iş başarısız olur.</summary>
    Retryable,

    /// <summary>Kalıcı hata: yeniden denenmez.</summary>
    Permanent,
}

/// <summary>
/// Test sırasında belirli bir müşteri fotoğrafı için sağlayıcı hatası enjekte eder. Fotoğrafın işlenmiş baytlarının SHA-256 özeti
/// kaydedilir; üretim kodunda test bağımlılığı yoktur.
/// </summary>
public static class TestFaults
{
    private static readonly ConcurrentDictionary<string, InjectedFault> Faults = new();

    public static void FailFor(byte[] photoBytes, InjectedFault fault) => Faults[Hash(photoBytes)] = fault;

    internal static void ThrowIfInjected(ReadOnlyMemory<byte> person)
    {
        if (Faults.TryGetValue(Hash(person.Span), out var fault))
        {
            throw fault == InjectedFault.Retryable
                ? new ProviderException(GenerationErrorCode.ProviderUnavailable, "Enjekte edilen geçici hata.", retryable: true)
                : new ProviderException(GenerationErrorCode.ContentRejected, "Enjekte edilen kalıcı hata.", retryable: false);
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

internal sealed class FaultInjectingProvider(MockImageProvider inner) : ITryOnProvider, IImageGenerationProvider
{
    public Task<ProviderResult> TryOnAsync(ImageInput person, ImageInput garment, ProviderRequestOptions options, CancellationToken ct)
    {
        TestFaults.ThrowIfInjected(person.Content);
        return inner.TryOnAsync(person, garment, options, ct);
    }

    public Task<ProviderResult> ComposeOutfitAsync(ImageInput person, IReadOnlyList<GarmentReference> garments, string prompt, ProviderRequestOptions options, CancellationToken ct)
    {
        TestFaults.ThrowIfInjected(person.Content);
        return inner.ComposeOutfitAsync(person, garments, prompt, options, ct);
    }
}

/// <summary>
/// Gerçek SQL Server test veritabanına (sanalprova_test) karşı çalışan API.
/// Bağlantı dizeleri: SANALPROVA_TEST_DB / SANALPROVA_TEST_DB_MIGRATOR ortam değişkenleri veya API projesinin user-secrets'ındaki
/// ConnectionStrings:Test (uygulama hesabı: yalnızca veri) ve ConnectionStrings:TestMigrator (şema + sıfırlama). Migrator verilmezse
/// aynı dize kullanılır (ör. CI'da tek hesap). API uygulama hesabıyla çalışır: çalışma zamanında DDL deneyen kod testte hata verir.
/// Bağlantı yoksa veritabanı gerektiren testler atlanır (Skip) — hiçbir zaman geliştirme veritabanına yazılmaz.
/// Üretim worker'ı çalışır (hızlı yoklama, gecikmesiz mock); saklama süpürmesi testlerde elle tetiklenir.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string SkipReason = "Test veritabanı yapılandırılmamış (ConnectionStrings:Test / SANALPROVA_TEST_DB).";

    /// <summary>Veritabanı yokken host'un kurulabilmesi için geçersiz ama SQL Server biçimli dize (bağlantı denenmez).</summary>
    internal const string NoDatabase = "Server=tcp:127.0.0.1,1;Database=none;User ID=none;Password=none;Encrypt=False;Connect Timeout=1";

    private const string TestDatabase = "sanalprova_test";

    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), "sanalprova-it", Guid.NewGuid().ToString("N"));

    public ApiFactory()
    {
        var secrets = new ConfigurationBuilder().AddUserSecrets(typeof(Program).Assembly, optional: true).Build();
        TestConnectionString = Environment.GetEnvironmentVariable("SANALPROVA_TEST_DB") ?? secrets.GetConnectionString("Test");
        MigratorConnectionString = Environment.GetEnvironmentVariable("SANALPROVA_TEST_DB_MIGRATOR") ?? secrets.GetConnectionString("TestMigrator") ?? TestConnectionString;
    }

    /// <summary>Uygulama hesabı (API host'u ve testlerin kendi DbContext'leri).</summary>
    public string? TestConnectionString { get; }

    /// <summary>Şema (migration) ve test verisi sıfırlama hesabı.</summary>
    public string? MigratorConnectionString { get; }

    /// <summary>Test host'unun yerel dosya deposu kökü (özel işlemci kurulumları aynı dosyaları okuyabilsin diye).</summary>
    public string StorageRoot => _storageRoot;

    public bool HasDatabase { get; private set; }

    public static void RequireDatabase(ApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Assert.SkipUnless(factory.HasDatabase, SkipReason);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Main", TestConnectionString ?? NoDatabase);
        builder.UseSetting("Seed:Enabled", "false");
        builder.UseSetting("Security:LoginRateLimitPerMinute", "100000"); // testler aynı IP kovasını paylaşır
        builder.UseSetting("Storage:LocalRoot", _storageRoot);
        builder.UseSetting("Storage:SigningKey", new string('t', 48));
        builder.UseSetting("ImageWorker:BaseUrl", "http://127.0.0.1:1");
        builder.UseSetting("Generation:Mock:DelayMs", "0");
        builder.UseSetting("Generation:PollIntervalMs", "30");
        builder.UseSetting("Generation:RetryBackoffBaseSeconds", "0.02");
        builder.UseSetting("Retention:SweepIntervalSeconds", "0");
        builder.UseSetting("Display:SweepIntervalSeconds", "0"); // boşta temizleme testlerde elle tetiklenir
        builder.UseSetting("Security:DisplayPairingRateLimitPerMinute", "100000");
        builder.UseSetting("Security:DisplayPairingStatusRateLimitPerMinute", "100000");
        builder.UseSetting("Security:DisplayClaimRateLimitPerMinute", "100000");
        builder.UseSetting("Security:DisplayStateRateLimitPerMinute", "100000");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");

        builder.ConfigureTestServices(services =>
        {
            services.AddKeyedSingleton<ITryOnProvider>(GenerationStrategy.MockProvider, (sp, _) => new FaultInjectingProvider(sp.GetRequiredService<MockImageProvider>()));
            services.AddKeyedSingleton<IImageGenerationProvider>(GenerationStrategy.MockProvider, (sp, _) => new FaultInjectingProvider(sp.GetRequiredService<MockImageProvider>()));
        });
    }

    public async ValueTask InitializeAsync()
    {
        if (string.IsNullOrEmpty(TestConnectionString) || string.IsNullOrEmpty(MigratorConnectionString))
        {
            return;
        }

        // Güvenlik ağı 1 (bağlanmadan): her iki dize de yalnızca sanalprova_test'i hedeflemeli; SQLEXPRESS örnekleri yasak
        EnsureTestTarget(TestConnectionString);
        EnsureTestTarget(MigratorConnectionString);

        // Host (ve üretim worker'ı) başlamadan ÖNCE şema hazırlanır: worker'ın ilk yoklaması boş şemaya çarpmasın
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(MigratorConnectionString, AppDbContext.ConfigureSqlServer)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new AppDbContext(options, NoTenantContext.Instance))
        {
            // Güvenlik ağı 2 (bağlandıktan sonra): sunucunun bildirdiği veritabanı ve örnek adı doğrulanır
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT DB_NAME(), CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128))";
                await using var reader = await check.ExecuteReaderAsync();
                await reader.ReadAsync();
                var database = reader.GetString(0);
                var instance = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (!string.Equals(database, TestDatabase, StringComparison.OrdinalIgnoreCase)
                    || (instance is not null && instance.StartsWith("SQLEXPRESS", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"Beklenmeyen test hedefi: {instance ?? "(varsayılan örnek)"} / {database}. Hiçbir şey silinmedi.");
                }
            }

            await connection.CloseAsync();

            // Test veritabanı atılabilir: önceki çalıştırmadan kalan veri (ör. bilinçli çapraz-firma satırları) yeni bir migration'ın ön kontrolünü durdurmasın diye,
            // bekleyen migration varsa veri ÖNCE sıfırlanır (üretimde ön kontrol durur ve veri incelenir; burada amaç temiz bir test ortamıdır)
            if (await db.Database.GetPendingMigrationsAsync() is { } pending && pending.Any() && (await db.Database.GetAppliedMigrationsAsync()).Any())
            {
                await ResetDataAsync(db);
            }

            await db.Database.MigrateAsync();
            await ResetDataAsync(db);
        }

        // Host'u paralel testler başlamadan, tek seferde oluştur: WebApplicationFactory ilk erişimde iş parçacığı güvenli değildir;
        // eşzamanlı ilk erişimler birden fazla host (ve birden fazla üretim worker'ı) yaratıp bildirimleri kaybettirir
        _ = Services;
        HasDatabase = true;
    }

    /// <summary>
    /// SQL Server yabancı anahtarlı tabloda TRUNCATE'e izin vermez: constraint'ler geçici kapatılır, tablolar boşaltılır, constraint'ler WITH CHECK ile
    /// (verinin doğrulanmasıyla, "trusted" olarak) yeniden açılır. Migrator hesabı yeter (DDL + DELETE).
    /// </summary>
    private static async Task ResetDataAsync(AppDbContext db)
    {
        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT t.name AS [Value] FROM sys.tables t WHERE t.is_ms_shipped = 0 AND SCHEMA_NAME(t.schema_id) = N'dbo' AND t.name <> N'__ef_migrations_history'")
            .ToListAsync();
        if (tables.Count > 0)
        {
            var quoted = tables.Select(t => $"[dbo].[{t.Replace("]", "]]", StringComparison.Ordinal)}]").ToList();
            var reset = string.Concat(quoted.Select(t => $"ALTER TABLE {t} NOCHECK CONSTRAINT ALL;\n"))
                + string.Concat(quoted.Select(t => $"DELETE FROM {t};\n"))
                + string.Concat(quoted.Select(t => $"ALTER TABLE {t} WITH CHECK CHECK CONSTRAINT ALL;\n"));
#pragma warning disable EF1002 // Tablo adları sistem kataloğundan gelir, kullanıcı girdisi değil
            await db.Database.ExecuteSqlRawAsync(reset);
#pragma warning restore EF1002
        }
    }

    private static void EnsureTestTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!string.Equals(builder.InitialCatalog, TestDatabase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Entegrasyon testleri yalnızca {TestDatabase} veritabanında çalışır.");
        }

        if (builder.DataSource.Contains("SQLEXPRESS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Entegrasyon testleri SQLEXPRESS örneklerinde çalıştırılmaz.");
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }
}
