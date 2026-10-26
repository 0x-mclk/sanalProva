using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Identity;

namespace SanalProva.Infrastructure.Persistence.Configurations;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(120);
        builder.Property(x => x.Slug).HasMaxLength(60);
        builder.HasIndex(x => x.Slug).IsUnique();
        builder.OwnsOne(x => x.Settings, s => s.ToJson("settings"));
    }
}

internal sealed class StoreConfiguration : IEntityTypeConfiguration<Store>
{
    public void Configure(EntityTypeBuilder<Store> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(120);
        builder.Property(x => x.Code).HasMaxLength(30);
        builder.Property(x => x.Timezone).HasMaxLength(60);
        builder.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique();
        // Firma tutarlılığı: çocuk kayıtlar (id, organization_id) çiftine bağlanır; böylece başka firmanın şubesine bağlanmak veritabanında imkânsızdır
        builder.HasAlternateKey(x => new { x.Id, x.OrganizationId }).HasName("ak_stores_id_organization_id");
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.Property(x => x.DisplayName).HasMaxLength(80);
        builder.HasIndex(x => x.OrganizationId);
        builder.HasAlternateKey(x => new { x.Id, x.OrganizationId }).HasName("ak_users_id_organization_id");
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class UserStoreRoleConfiguration : IEntityTypeConfiguration<UserStoreRole>
{
    public void Configure(EntityTypeBuilder<UserStoreRole> builder)
    {
        builder.HasKey(x => new { x.UserId, x.StoreId });
        builder.HasIndex(x => x.StoreId);
        // Rol satırı: kullanıcı ve şube AYNI firmada olmalı (başka firmanın şubesinde rol verilemez)
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => new { x.UserId, x.OrganizationId }).HasPrincipalKey(u => new { u.Id, u.OrganizationId }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Store>().WithMany().HasForeignKey(x => new { x.StoreId, x.OrganizationId }).HasPrincipalKey(s => new { s.Id, s.OrganizationId }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}
