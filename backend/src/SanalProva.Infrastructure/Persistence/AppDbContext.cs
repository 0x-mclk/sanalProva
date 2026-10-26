using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SanalProva.Domain.Audit;
using SanalProva.Domain.Catalog;
using SanalProva.Domain.Common;
using SanalProva.Domain.Display;
using SanalProva.Domain.Files;
using SanalProva.Domain.Generation;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Identity;
using SanalProva.Infrastructure.Tenancy;

namespace SanalProva.Infrastructure.Persistence;

/// <summary>
/// Uygulamanın tek DbContext'i. Kiracıya ait tüm varlıklarda <see cref="ITenantOwned.OrganizationId"/>
/// için global query filter uygulanır. Sistem işleri (saklama/silme, kuyruk) filtreyi bilinçli olarak
/// <c>IgnoreQueryFilters()</c> ile aşar.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant)
    : IdentityUserContext<AppUser, Guid>(options)
{
    // Query filter'lar bu alanı her sorguda yeniden okur (EF parametre olarak çevirir).
    // Değerler ara katmanda/endpoint filtresinde DbContext oluşturulduktan sonra atanabildiği için bağlam
    // nesnesi tutulur, değerler sorgu anında okunur.
    private readonly ITenantContext _tenant = tenant;

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<UserStoreRole> UserStoreRoles => Set<UserStoreRole>();

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<OutfitSetComponent> OutfitSetComponents => Set<OutfitSetComponent>();

    public DbSet<TryOnSession> TryOnSessions => Set<TryOnSession>();
    public DbSet<ConsentRecord> ConsentRecords => Set<ConsentRecord>();
    public DbSet<SessionPhoto> SessionPhotos => Set<SessionPhoto>();
    public DbSet<OutfitSelection> OutfitSelections => Set<OutfitSelection>();
    public DbSet<OutfitSelectionItem> OutfitSelectionItems => Set<OutfitSelectionItem>();

    public DbSet<GenerationJob> GenerationJobs => Set<GenerationJob>();
    public DbSet<GeneratedImage> GeneratedImages => Set<GeneratedImage>();
    public DbSet<AiUsageLog> AiUsageLogs => Set<AiUsageLog>();
    public DbSet<AiCostReconciliation> AiCostReconciliations => Set<AiCostReconciliation>();

    public DbSet<DisplayDevice> DisplayDevices => Set<DisplayDevice>();
    public DbSet<DisplayPairing> DisplayPairings => Set<DisplayPairing>();

    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>Veritabanı collation'ı. Veritabanı oluşturulurken verilir (migration ALTER DATABASE yetkisi gerektireceği için modelde ayarlanmaz).</summary>
    public const string DatabaseCollation = "Latin1_General_100_CI_AS_SC";

    /// <summary>Türkçe büyük/küçük harf kuralıyla (i/İ, ı/I) aranan metin sütunları.</summary>
    public const string TurkishSearchCollation = "Turkish_100_CI_AS";

    /// <summary>
    /// SQL Server sağlayıcı ayarları; uygulama ve testler aynı ayarı kullanır.
    /// Uyumluluk seviyesi 160 (SQL Server 2022): JSON sütunları nvarchar(max) olur, 2025'in yerel json tipine bağımlılık yoktur.
    /// </summary>
    public static void ConfigureSqlServer(SqlServerDbContextOptionsBuilder sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        sql.MigrationsHistoryTable("__ef_migrations_history");
        sql.UseCompatibilityLevel(160);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        // Enum'lar okunabilir metin olarak saklanır; yeni değer migration gerektirmez (check constraint hariç).
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(40);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        RenameIdentityTables(builder);
        ApplyTenantFilters(builder);
        ApplyEnumCheckConstraints(builder);
    }

    private void ApplyTenantFilters(ModelBuilder builder)
    {
        var tenantField = Expression.Field(Expression.Constant(this), nameof(_tenant));
        var currentOrganization = Expression.Property(tenantField, nameof(ITenantContext.OrganizationId));
        var currentStore = Expression.Property(tenantField, nameof(ITenantContext.StoreId));
        var nullGuid = Expression.Constant(null, typeof(Guid?));

        foreach (var entityType in builder.Model.GetEntityTypes()
                     .Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType) && !t.IsOwned()))
        {
            // e => e.OrganizationId == tenant.OrganizationId
            //      [şubeye ait kayıtlar için] && (tenant.StoreId == null || e.StoreId == tenant.StoreId)
            var parameter = Expression.Parameter(entityType.ClrType, "e");
            Expression body = Expression.Equal(
                Expression.Convert(Expression.Property(parameter, nameof(ITenantOwned.OrganizationId)), typeof(Guid?)),
                currentOrganization);

            if (typeof(IStoreOwned).IsAssignableFrom(entityType.ClrType))
            {
                var storeMatches = Expression.OrElse(
                    Expression.Equal(currentStore, nullGuid),
                    Expression.Equal(
                        Expression.Convert(Expression.Property(parameter, nameof(IStoreOwned.StoreId)), typeof(Guid?)),
                        currentStore));
                body = Expression.AndAlso(body, storeMatches);
            }

            builder.Entity(entityType.ClrType).HasQueryFilter(Expression.Lambda(body, parameter));
        }

        // AppUser bilinçli olarak filtrelenmez: giriş sırasında (kiracı henüz yokken) Identity
        // kullanıcıyı e-postayla bulabilmelidir. Kullanıcı yönetimi sorguları OrganizationId ile açıkça filtrelenir.
    }

    /// <summary>Metin olarak saklanan her enum sütununa yalnızca geçerli değerleri kabul eden check constraint ekler.</summary>
    private static void ApplyEnumCheckConstraints(ModelBuilder builder)
    {
        foreach (var entityType in builder.Model.GetEntityTypes().Where(t => !t.IsOwned()))
        {
            var table = entityType.GetTableName();
            if (table is null)
            {
                continue;
            }

            foreach (var property in entityType.GetProperties())
            {
                var enumType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (!enumType.IsEnum)
                {
                    continue;
                }

                var column = property.GetColumnName();
                var values = string.Join(", ", Enum.GetNames(enumType).Select(n => $"N'{n}'"));
                builder.Entity(entityType.ClrType)
                    .ToTable(t => t.HasCheckConstraint($"ck_{table}_{column}", $"[{column}] IN ({values})"));
            }
        }
    }

    private static void RenameIdentityTables(ModelBuilder builder)
    {
        builder.Entity<AppUser>().ToTable("users");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");

        // SQL Server kümelenmiş indeks anahtarı en fazla 900 bayttır; Identity'nin varsayılan nvarchar(450) bileşik anahtarları bunu aşar
        // (1800 bayta kadar). Identity'nin SQL Server için önerdiği 128 karakter sınırı uygulanır.
        builder.Entity<IdentityUserLogin<Guid>>(b =>
        {
            b.Property(x => x.LoginProvider).HasMaxLength(128);
            b.Property(x => x.ProviderKey).HasMaxLength(128);
        });
        builder.Entity<IdentityUserToken<Guid>>(b =>
        {
            b.Property(x => x.LoginProvider).HasMaxLength(128);
            b.Property(x => x.Name).HasMaxLength(128);
        });
    }
}
