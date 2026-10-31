using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SanalProva.Domain.Audit;
using SanalProva.Domain.Display;
using SanalProva.Domain.Files;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;

namespace SanalProva.Infrastructure.Persistence.Configurations;

internal sealed class DisplayDeviceConfiguration : IEntityTypeConfiguration<DisplayDevice>
{
    public void Configure(EntityTypeBuilder<DisplayDevice> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(80);
        builder.Property(x => x.TokenHash).HasMaxLength(64);
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => x.StoreId);

        // Bir oturum aynı anda yalnızca bir TV'de gösterilir
        builder.HasIndex(x => x.CurrentSessionId)
            .IsUnique()
            .HasFilter("current_session_id IS NOT NULL")
            .HasDatabaseName("ux_display_devices_current_session");

        builder.Property(x => x.StateVersion).IsConcurrencyToken();
        builder.OwnsOne(x => x.State, s => s.ToJson("display_state"));

        builder.HasOne<Store>().WithMany().HasForeignKey(x => new { x.StoreId, x.OrganizationId }).HasPrincipalKey(s => new { s.Id, s.OrganizationId }).OnDelete(DeleteBehavior.Restrict);

        // Oturum silinince TV bağlantısı boşalır (SET NULL: tek sütunlu FK). SQL Server bileşik FK'da SET NULL'a izin vermez (firma sütunu NOT NULL),
        // bu yüzden tutarlılık ayrı bir NO ACTION bileşik FK ile sağlanır: TV ve oturum aynı şubede ve aynı firmada olmalıdır. Bağlantı boşken kısıt devre dışıdır.
        builder.HasOne<TryOnSession>().WithMany().HasForeignKey(x => x.CurrentSessionId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<TryOnSession>().WithMany().HasForeignKey(x => new { x.CurrentSessionId, x.StoreId, x.OrganizationId }).HasPrincipalKey(s => new { s.Id, s.StoreId, s.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DisplayPairingConfiguration : IEntityTypeConfiguration<DisplayPairing>
{
    public void Configure(EntityTypeBuilder<DisplayPairing> builder)
    {
        builder.Property(x => x.CodeHash).HasMaxLength(64);
        builder.Property(x => x.PairingSecretHash).HasMaxLength(64);
        builder.HasIndex(x => x.CodeHash);
        builder.HasIndex(x => x.PairingSecretHash).IsUnique();
        builder.HasIndex(x => x.ExpiresAt);
        builder.HasOne<DisplayDevice>().WithMany().HasForeignKey(x => x.ClaimedDeviceId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class StoredFileConfiguration : IEntityTypeConfiguration<StoredFile>
{
    public void Configure(EntityTypeBuilder<StoredFile> builder)
    {
        builder.Property(x => x.StorageKey).HasMaxLength(300);
        builder.Property(x => x.ContentType).HasMaxLength(60);
        builder.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength();
        builder.HasIndex(x => x.StorageKey).IsUnique();
        builder.HasAlternateKey(x => new { x.Id, x.OrganizationId }).HasName("ak_stored_files_id_organization_id");
        builder.HasIndex(x => x.RetentionUntil).HasFilter("deleted_at IS NULL AND retention_until IS NOT NULL");
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.Property(x => x.Id).UseIdentityColumn();
        builder.Property(x => x.Action).HasMaxLength(60);
        builder.Property(x => x.EntityType).HasMaxLength(60);
        builder.Property(x => x.IpHash).HasMaxLength(64);
        builder.Property(x => x.Detail).HasMaxLength(300);
        builder.HasIndex(x => new { x.OrganizationId, x.CreatedAt });

        // Denetim kaydı firmaya ve (varsa) o firmanın şubesine bağlıdır. Kullanıcıya bağlanmaz: platform yöneticisi başka firma adına kayıt bırakır.
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Store>().WithMany().HasForeignKey(x => new { x.StoreId, x.OrganizationId }).HasPrincipalKey(s => new { s.Id, s.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
    }
}
