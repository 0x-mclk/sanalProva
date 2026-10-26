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

        // Çocuk tabloların bağlandığı anahtarlar: firma düzeyi (id, organization_id) ve şube düzeyi (id, store_id, organization_id; iş ve TV aynı şubede olmalı)
        builder.HasAlternateKey(x => new { x.Id, x.OrganizationId }).HasName("ak_tryon_sessions_id_organization_id");
        builder.HasAlternateKey(x => new { x.Id, x.StoreId, x.OrganizationId }).HasName("ak_tryon_sessions_id_store_id_organization_id");

        builder.HasOne<Store>().WithMany().HasForeignKey(x => new { x.StoreId, x.OrganizationId }).HasPrincipalKey(s => new { s.Id, s.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => new { x.CreatedByUserId, x.OrganizationId }).HasPrincipalKey(u => new { u.Id, u.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Consent).WithOne().HasForeignKey<ConsentRecord>(x => new { x.SessionId, x.OrganizationId }).HasPrincipalKey<TryOnSession>(s => new { s.Id, s.OrganizationId }).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Photos).WithOne().HasForeignKey(x => new { x.SessionId, x.OrganizationId }).HasPrincipalKey(s => new { s.Id, s.OrganizationId }).OnDelete(DeleteBehavior.Cascade);
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
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => new { x.RecordedByUserId, x.OrganizationId }).HasPrincipalKey(u => new { u.Id, u.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SessionPhotoConfiguration : IEntityTypeConfiguration<SessionPhoto>
{
    public void Configure(EntityTypeBuilder<SessionPhoto> builder)
    {
        builder.HasAlternateKey(x => new { x.Id, x.OrganizationId }).HasName("ak_session_photos_id_organization_id");
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => new { x.FileId, x.OrganizationId }).HasPrincipalKey(f => new { f.Id, f.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        builder.OwnsOne(x => x.QualityFlags, q => q.ToJson("quality_flags"));
    }
}

internal sealed class OutfitSelectionConfiguration : IEntityTypeConfiguration<OutfitSelection>
{
    public void Configure(EntityTypeBuilder<OutfitSelection> builder)
    {
        builder.HasAlternateKey(x => new { x.Id, x.OrganizationId }).HasName("ak_outfit_selections_id_organization_id");
        builder.HasOne<TryOnSession>().WithMany().HasForeignKey(x => new { x.SessionId, x.OrganizationId }).HasPrincipalKey(s => new { s.Id, s.OrganizationId }).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Items).WithOne().HasForeignKey(x => new { x.SelectionId, x.OrganizationId }).HasPrincipalKey(s => new { s.Id, s.OrganizationId }).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OutfitSelectionItemConfiguration : IEntityTypeConfiguration<OutfitSelectionItem>
{
    public void Configure(EntityTypeBuilder<OutfitSelectionItem> builder)
    {
        builder.HasOne<Product>().WithMany().HasForeignKey(x => new { x.ProductId, x.OrganizationId }).HasPrincipalKey(p => new { p.Id, p.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProductVariant>().WithMany().HasForeignKey(x => new { x.VariantId, x.OrganizationId }).HasPrincipalKey(v => new { v.Id, v.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Category>().WithMany().HasForeignKey(x => new { x.CategoryId, x.OrganizationId }).HasPrincipalKey(c => new { c.Id, c.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
    }
}
