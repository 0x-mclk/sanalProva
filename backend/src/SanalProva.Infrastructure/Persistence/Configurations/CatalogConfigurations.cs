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
        builder.Property(x => x.ConflictsWith).HasColumnType("uuid[]");
        builder.HasIndex(x => new { x.OrganizationId, x.Slug }).IsUnique();
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.ToTable(t => t.HasCheckConstraint("ck_categories_max_selection", "max_selection >= 1"));
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(40);
        builder.Property(x => x.Name).HasMaxLength(120);
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.Tagline).HasMaxLength(60);
        builder.Property(x => x.AiNotes).HasMaxLength(500);
        builder.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique();
        builder.HasIndex(x => new { x.OrganizationId, x.CategoryId, x.IsActive });

        builder.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(x => x.Variants).WithOne().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Images).WithOne().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Components).WithOne().HasForeignKey(x => x.SetProductId).OnDelete(DeleteBehavior.Cascade);
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

        // Ürün başına tek varsayılan varyant
        builder.HasIndex(x => x.ProductId).IsUnique().HasFilter("is_default").HasDatabaseName("ux_product_variants_default");
        builder.ToTable(t => t.HasCheckConstraint("ck_product_variants_color_hex", "color_hex IS NULL OR color_hex ~ '^#[0-9A-Fa-f]{6}$'"));
    }
}

internal sealed class ProductImageConfiguration : IEntityTypeConfiguration<ProductImage>
{
    public void Configure(EntityTypeBuilder<ProductImage> builder)
    {
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict);

        // Ürün + varyant (null = tüm varyantlar) + açı başına en fazla bir AI referans görseli.
        // FASHN tek ürün görseli aldığı için hazır takımda bu görsel takımın tamamını gösterir.
        builder.HasIndex(x => new { x.ProductId, x.VariantId, x.Angle })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter("is_ai_reference")
            .HasDatabaseName("ux_product_images_ai_reference");
    }
}

internal sealed class OutfitSetComponentConfiguration : IEntityTypeConfiguration<OutfitSetComponent>
{
    public void Configure(EntityTypeBuilder<OutfitSetComponent> builder)
    {
        builder.HasKey(x => new { x.SetProductId, x.ComponentProductId });
        builder.HasOne<Product>().WithMany().HasForeignKey(x => x.ComponentProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(x => x.ComponentVariantId).OnDelete(DeleteBehavior.SetNull);
        builder.ToTable(t => t.HasCheckConstraint("ck_outfit_set_components_not_self", "set_product_id <> component_product_id"));
    }
}
