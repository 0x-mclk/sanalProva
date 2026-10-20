using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SanalProva.Domain.Catalog;
using SanalProva.Domain.Files;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Identity;

namespace SanalProva.Infrastructure.Persistence.Configurations;

internal sealed class TryOnSessionConfiguration : IEntityTypeConfiguration<TryOnSession>
{
    public void Configure(EntityTypeBuilder<TryOnSession> builder)
    {
        builder.ToTable("tryon_sessions");
        builder.Property(x => x.CustomerLabel).HasMaxLength(40);
        builder.HasIndex(x => new { x.StoreId, x.Status });
        builder.HasIndex(x => x.RetentionUntil);

        builder.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Consent).WithOne().HasForeignKey<ConsentRecord>(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Photos).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.OwnsOne(x => x.Draft, d =>
        {
            d.ToJson("draft");
            d.OwnsMany(x => x.Items);
        });
    }
}

internal sealed class ConsentRecordConfiguration : IEntityTypeConfiguration<ConsentRecord>
{
    public void Configure(EntityTypeBuilder<ConsentRecord> builder)
    {
        builder.Property(x => x.NoticeVersion).HasMaxLength(20);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.RecordedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SessionPhotoConfiguration : IEntityTypeConfiguration<SessionPhoto>
{
    public void Configure(EntityTypeBuilder<SessionPhoto> builder)
    {
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict);
        builder.OwnsOne(x => x.QualityFlags, q => q.ToJson("quality_flags"));
    }
}

internal sealed class OutfitSelectionConfiguration : IEntityTypeConfiguration<OutfitSelection>
{
    public void Configure(EntityTypeBuilder<OutfitSelection> builder)
    {
        builder.HasOne<TryOnSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.SelectionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OutfitSelectionItemConfiguration : IEntityTypeConfiguration<OutfitSelectionItem>
{
    public void Configure(EntityTypeBuilder<OutfitSelectionItem> builder)
    {
        builder.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}
