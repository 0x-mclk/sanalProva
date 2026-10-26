using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SanalProva.Domain.Catalog;
using SanalProva.Domain.Common;
using SanalProva.Domain.Files;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Tenancy;

namespace SanalProva.IntegrationTests;

public sealed class PersistenceTests(ApiFactory factory)
{
    private static AppDbContext CreateContext(IServiceScope scope, ITenantContext tenant) =>
        new(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>(), tenant);

    private static Category NewCategory(Guid organizationId, string slug) =>
        new() { OrganizationId = organizationId, Name = slug, Slug = slug, BodyZone = BodyZone.Lower, LayerOrder = 10 };

    [Fact]
    public async Task Kiraci_filtresi_firmalar_arasi_veriyi_izole_eder()
    {
        ApiFactory.RequireDatabase(factory);
        var a = await TestData.CreateTenantAsync(factory.Services);
        var b = await TestData.CreateTenantAsync(factory.Services);
        var ct = TestContext.Current.CancellationToken;

        using var scope = factory.Services.CreateScope();
        await using (var seed = CreateContext(scope, NoTenantContext.Instance))
        {
            seed.Categories.AddRange(NewCategory(a.OrganizationId, "salvar"), NewCategory(b.OrganizationId, "salvar"));
            await seed.SaveChangesAsync(ct);
        }

        await using var asA = CreateContext(scope, new FixedTenantContext(a.OrganizationId));
        await using var anonymous = CreateContext(scope, NoTenantContext.Instance);

        var visibleToA = await asA.Categories.ToListAsync(ct);
        Assert.Single(visibleToA);
        Assert.All(visibleToA, c => Assert.Equal(a.OrganizationId, c.OrganizationId));
        Assert.Single(await asA.Stores.ToListAsync(ct));
        Assert.Empty(await anonymous.Categories.ToListAsync(ct));
        Assert.Empty(await anonymous.Stores.ToListAsync(ct));
    }

    [Fact]
    public async Task Zaman_damgalari_ve_jsonb_ayarlar_kaydedilir()
    {
        ApiFactory.RequireDatabase(factory);
        var tenant = await TestData.CreateTenantAsync(factory.Services);
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        await using var db = CreateContext(scope, new FixedTenantContext(tenant.OrganizationId));

        var organization = await db.Organizations.SingleAsync(o => o.Id == tenant.OrganizationId, ct);

        Assert.Equal(24, organization.Settings.DefaultRetentionHours);
        Assert.NotEqual(default, organization.CreatedAt);
        Assert.Equal(TimeSpan.Zero, organization.CreatedAt.Offset);
    }

    [Fact]
    public async Task Ayni_urun_varyant_ve_aci_icin_ikinci_AI_referans_gorseli_reddedilir()
    {
        ApiFactory.RequireDatabase(factory);
        var tenant = await TestData.CreateTenantAsync(factory.Services);
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        await using var db = CreateContext(scope, new FixedTenantContext(tenant.OrganizationId));

        var category = NewCategory(tenant.OrganizationId, "bindalli");
        var product = new Product { OrganizationId = tenant.OrganizationId, CategoryId = category.Id, Code = "BD-014", Name = "Sultan Bindallı", ProductType = ProductType.OutfitSet };
        StoredFile File() => new() { OrganizationId = tenant.OrganizationId, StorageKey = $"{Guid.NewGuid():N}/catalog/x/{Guid.NewGuid():N}.jpg", ContentType = "image/jpeg", Sha256 = new string('0', 64), Purpose = FilePurpose.ProductImage };
        ProductImage Image(StoredFile file) => new() { OrganizationId = tenant.OrganizationId, ProductId = product.Id, FileId = file.Id, Angle = ImageAngle.Front, Kind = ProductImageKind.OnModel, IsAiReference = true };
        StoredFile f1 = File(), f2 = File();
        db.AddRange(category, product, f1, f2, Image(f1));
        await db.SaveChangesAsync(ct);

        db.Add(Image(f2));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));

        Assert.Contains("ux_product_images_ai_reference", ex.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gecersiz_enum_degeri_check_constraint_ile_reddedilir()
    {
        ApiFactory.RequireDatabase(factory);
        var tenant = await TestData.CreateTenantAsync(factory.Services);
        using var scope = factory.Services.CreateScope();
        await using var db = CreateContext(scope, NoTenantContext.Instance);

        var ex = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE user_store_roles SET role = 'SuperAdmin' WHERE organization_id = {tenant.OrganizationId}",
            TestContext.Current.CancellationToken));

        // 547: CHECK / FOREIGN KEY constraint ihlali; SQL Server constraint adını yalnızca mesajda verir
        Assert.Equal(547, ex.Number);
        Assert.Contains("ck_user_store_roles_role", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Veritabani_collation_RCSI_uyumluluk_ve_turkce_arama_sutunu_beklendigi_gibidir()
    {
        ApiFactory.RequireDatabase(factory);
        using var scope = factory.Services.CreateScope();
        await using var db = CreateContext(scope, NoTenantContext.Instance);
        var ct = TestContext.Current.CancellationToken;

        // Bu ayarlar migration'da değil veritabanı oluşturulurken verilir (migrator ALTER DATABASE yetkisine sahip değil): sapma testte yakalanır
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128)),
                   d.is_read_committed_snapshot_on,
                   d.compatibility_level,
                   (SELECT c.collation_name FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dbo.products') AND c.name = N'name')
            FROM sys.databases d WHERE d.name = DB_NAME()
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));

        Assert.Equal(AppDbContext.DatabaseCollation, reader.GetString(0));
        Assert.True(reader.GetBoolean(1), "READ_COMMITTED_SNAPSHOT kapalı");
        Assert.Equal(160, reader.GetByte(2));
        Assert.Equal(AppDbContext.TurkishSearchCollation, reader.GetString(3));
    }
}
