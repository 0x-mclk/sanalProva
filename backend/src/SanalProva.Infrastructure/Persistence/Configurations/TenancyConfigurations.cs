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

        // M5.1 idempotency: platform yöneticisinin oluşturduğu firmanın oluşturma isteği. Bilinçli olarak FK yok (CreatedByUserId başka firmadadır).
        builder.Property(x => x.CreationRequestKey).HasMaxLength(64);
        builder.Property(x => x.CreationRequestHash).HasMaxLength(32).IsFixedLength();
        builder.HasIndex(x => new { x.CreatedByUserId, x.CreationRequestKey }).IsUnique()
            .HasFilter("[creation_request_key] IS NOT NULL")
            .HasDatabaseName("ux_organizations_creation_request");
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

        // M5.1 D4: e-posta tüm sistemde benzersiz (bir kişi iki firmada aynı e-postayla hesap açamaz). Identity'nin kendi "EmailIndex"i
        // (NormalizedEmail, benzersiz değil) burada benzersiz + filtreli hale getirilir; aynı isim korunur (ikinci bir indeks açılmaz).
        builder.HasIndex(x => x.NormalizedEmail).IsUnique().HasFilter("[normalized_email] IS NOT NULL").HasDatabaseName("EmailIndex");
    }
}

/// <summary>M5.1: tek kullanımlık hesap kurulum/parola belirleme bağlantısı.</summary>
internal sealed class AccountSetupTokenConfiguration : IEntityTypeConfiguration<AccountSetupToken>
{
    public void Configure(EntityTypeBuilder<AccountSetupToken> builder)
    {
        builder.Property(x => x.TokenHash).HasMaxLength(32).IsFixedLength();
        builder.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_account_setup_tokens_token_hash");
        // Kullanıcının açık (henüz tüketilmemiş/iptal edilmemiş) token'ını bulmak için: yeniden üretimde öncekiler bu indeksle aranıp iptal edilir.
        builder.HasIndex(x => new { x.UserId, x.Purpose })
            .HasDatabaseName("ix_account_setup_tokens_user_purpose_open")
            .HasFilter("[used_at] IS NULL AND [revoked_at] IS NULL");
        builder.HasIndex(x => x.OrganizationId);
        builder.ToTable(t => t.HasCheckConstraint("ck_account_setup_tokens_expires_after_created", "[expires_at] > [created_at]"));

        // Token sahibi her zaman aynı firmada: (user_id, organization_id) -> users(id, organization_id); kullanıcı silinirse token da gider.
        // (Fluent API çağrı sırası EF'in CreateTable'da ürettiği FK sırasını belirlemez: EF ismen sıralar, bu yüzden organization_id hem bu
        // bileşik FK'nın hem aşağıdaki düz FK'nın parçasıyken hangisinin önce oluşacağı buradaki sıraya bağlı değildir; DatabaseIntegrityTests
        // bunu "değişen sütunu kullanan FK'lardan biri" olarak genel şekilde doğrular, tek bir FK adına bağlı kalmaz.)
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => new { x.UserId, x.OrganizationId }).HasPrincipalKey(u => new { u.Id, u.OrganizationId }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>M5.1: Owner'ın sürümlü hizmet sözleşmesi kabulü. Yalnızca ekleme (append-only); bkz. <c>harden-agreement-acceptances.sql</c>.</summary>
internal sealed class OrganizationAgreementAcceptanceConfiguration : IEntityTypeConfiguration<OrganizationAgreementAcceptance>
{
    public void Configure(EntityTypeBuilder<OrganizationAgreementAcceptance> builder)
    {
        builder.Property(x => x.AgreementVersion).HasMaxLength(20);
        builder.Property(x => x.TextSha256).HasMaxLength(32).IsFixedLength();
        builder.Property(x => x.IpHash).HasMaxLength(64);
        // Firma başına sürüm kabulü tektir (aynı sürüm iki kez kabul edilemez).
        builder.HasIndex(x => new { x.OrganizationId, x.AgreementVersion }).IsUnique().HasDatabaseName("ux_org_agreement_acceptances_org_version");

        // Kabul eden kullanıcı her zaman aynı firmada: (user_id, organization_id) -> users(id, organization_id). Append-only kayıt: kullanıcı silinse de kalır (Restrict).
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => new { x.UserId, x.OrganizationId }).HasPrincipalKey(u => new { u.Id, u.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
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
