using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SanalProva.Domain.Catalog;
using SanalProva.Domain.Files;
using SanalProva.Domain.Tenancy;

namespace SanalProva.Infrastructure.Persistence.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(80);
        builder.Property(x => x.Slug).HasMaxLength(60);
        builder.Property(x => x.AiHint).HasMaxLength(500);
        // ConflictsWith: ilkel koleksiyon, JSON dizisi olarak nvarchar(max) (yalnızca bellekte okunur, sorgulanmaz)
        builder.HasIndex(x => new { x.OrganizationId, x.Slug }).IsUnique();
        builder.HasAlternateKey(x => new { x.Id, x.OrganizationId }).HasName("ak_categories_id_organization_id");
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t => t.HasCheckConstraint("ck_categories_max_selection", "max_selection >= 1"));
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(40);
        // Personel ürünü adıyla arar: Türkçe kuralı "istanbul" ile "İSTANBUL"u eşler (Latin1 collation eşlemez)
        builder.Property(x => x.Name).HasMaxLength(120).UseCollation(AppDbContext.TurkishSearchCollation);
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.Tagline).HasMaxLength(60);
        builder.Property(x => x.AiNotes).HasMaxLength(500);
        builder.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique();
        builder.HasIndex(x => new { x.OrganizationId, x.CategoryId, x.IsActive });

        builder.HasAlternateKey(x => new { x.Id, x.OrganizationId }).HasName("ak_products_id_organization_id");
        builder.HasOne(x => x.Category).WithMany().HasForeignKey(x => new { x.CategoryId, x.OrganizationId }).HasPrincipalKey(c => new { c.Id, c.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Variants).WithOne().HasForeignKey(x => new { x.ProductId, x.OrganizationId }).HasPrincipalKey(p => new { p.Id, p.OrganizationId }).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Images).WithOne().HasForeignKey(x => new { x.ProductId, x.OrganizationId }).HasPrincipalKey(p => new { p.Id, p.OrganizationId }).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Components).WithOne().HasForeignKey(x => new { x.SetProductId, x.OrganizationId }).HasPrincipalKey(p => new { p.Id, p.OrganizationId }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(40);
        builder.Property(x => x.ColorName).HasMaxLength(60);
        builder.Property(x => x.ColorHex).HasMaxLength(7).IsFixedLength();
        builder.HasIndex(x => new { x.ProductId, x.Code }).IsUnique();
        builder.HasAlternateKey(x => new { x.Id, x.OrganizationId }).HasName("ak_product_variants_id_organization_id");

        // Ürün başına tek varsayılan varyant
        builder.HasIndex(x => x.ProductId).IsUnique().HasFilter("[is_default] = 1").HasDatabaseName("ux_product_variants_default");
        // T-SQL'de regex yok: LIKE karakter sınıflarıyla "#" + 6 onaltılık hane (sütun sabit 7 karakter)
        builder.ToTable(t => t.HasCheckConstraint("ck_product_variants_color_hex",
            "[color_hex] IS NULL OR [color_hex] LIKE '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'"));
    }
}

internal sealed class ProductImageConfiguration : IEntityTypeConfiguration<ProductImage>
{
    public void Configure(EntityTypeBuilder<ProductImage> builder)
    {
        // SQL Server tek zincirleme silme yoluna izin verir: görsel üründen siliniyor (ürün → görsel), varyant yolu NO ACTION.
        // Varyant silen bir akış eklenirse önce o varyantın görselleri silinmelidir (bugün varyant silinmiyor).
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(x => new { x.VariantId, x.OrganizationId }).HasPrincipalKey(v => new { v.Id, v.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => new { x.FileId, x.OrganizationId }).HasPrincipalKey(f => new { f.Id, f.OrganizationId }).OnDelete(DeleteBehavior.Restrict);

        // Ürün + varyant (null = tüm varyantlar) + açı başına en fazla bir AI referans görseli.
        // FASHN tek ürün görseli aldığı için hazır takımda bu görsel takımın tamamını gösterir.
        // SQL Server benzersiz indekste NULL'ları eşit sayar: "varyantsız" referans da tek kalır.
        builder.HasIndex(x => new { x.ProductId, x.VariantId, x.Angle })
            .IsUnique()
            .HasFilter("[is_ai_reference] = 1")
            .HasDatabaseName("ux_product_images_ai_reference");
    }
}

internal sealed class OutfitSetComponentConfiguration : IEntityTypeConfiguration<OutfitSetComponent>
{
    public void Configure(EntityTypeBuilder<OutfitSetComponent> builder)
    {
        builder.HasKey(x => new { x.SetProductId, x.ComponentProductId });
        builder.HasOne<Product>().WithMany().HasForeignKey(x => new { x.ComponentProductId, x.OrganizationId }).HasPrincipalKey(p => new { p.Id, p.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        // Bileşen takım ürününden siliniyor (ürün → bileşen); varyant yolu ikinci zincir olacağı için veritabanında NO ACTION,
        // EF yalnızca izlenen kayıtlarda NULL'a çeker (bugün varyant silinmiyor)
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(x => new { x.ComponentVariantId, x.OrganizationId }).HasPrincipalKey(v => new { v.Id, v.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t => t.HasCheckConstraint("ck_outfit_set_components_not_self", "set_product_id <> component_product_id"));
    }
}
