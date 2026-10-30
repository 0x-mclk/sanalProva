using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SanalProva.Domain.Credits;
using SanalProva.Domain.Tenancy;

namespace SanalProva.Infrastructure.Persistence.Configurations;

/// <summary>M5.4: firma kredi hesabı (firma başına tek satır). Bakiye negatif olamaz; rezerve bakiyeyi aşamaz (CHECK).</summary>
internal sealed class CreditAccountConfiguration : IEntityTypeConfiguration<CreditAccount>
{
    public void Configure(EntityTypeBuilder<CreditAccount> builder)
    {
        builder.HasKey(x => x.OrganizationId);
        builder.Ignore(x => x.AvailableCredits);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_credit_accounts_balance_non_negative", "[balance_credits] >= 0");
            t.HasCheckConstraint("ck_credit_accounts_reserved_valid", "[reserved_credits] >= 0 AND [reserved_credits] <= [balance_credits]");
        });
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// M5.4: değiştirilemez kredi defteri. UPDATE/DELETE <c>tr_credit_ledger_append_only</c> tetikleyicisiyle reddedilir (migration). Filtreli benzersiz indeksler:
/// firma içinde referans benzersiz (yükleme idempotency'si); iş başına tek Reserve ve tek kapanış (Release|Consume): çift düşüm/çift iade veritabanında imkânsız.
/// </summary>
internal sealed class CreditLedgerEntryConfiguration : IEntityTypeConfiguration<CreditLedgerEntry>
{
    public void Configure(EntityTypeBuilder<CreditLedgerEntry> builder)
    {
        builder.ToTable("credit_ledger", t =>
        {
            t.HasTrigger("tr_credit_ledger_append_only");
            t.HasCheckConstraint("ck_credit_ledger_amount", "([entry_type] = N'Adjustment' AND [amount] <> 0) OR ([entry_type] <> N'Adjustment' AND [amount] > 0)");
            t.HasCheckConstraint("ck_credit_ledger_shape",
                "([entry_type] IN (N'Grant', N'Adjustment') AND [reference] IS NOT NULL AND [job_id] IS NULL) OR ([entry_type] IN (N'Reserve', N'Release', N'Consume') AND [job_id] IS NOT NULL)");
            t.HasCheckConstraint("ck_credit_ledger_after_valid", "[balance_after] >= 0 AND [reserved_after] >= 0 AND [reserved_after] <= [balance_after]");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Reference).HasMaxLength(64);
        builder.Property(x => x.Note).HasMaxLength(200);
        builder.Property(x => x.TariffKey).HasMaxLength(100);
        builder.HasIndex(x => new { x.OrganizationId, x.CreatedAt, x.Id }).HasDatabaseName("ix_credit_ledger_org_created");
        builder.HasIndex(x => new { x.OrganizationId, x.Reference }).IsUnique().HasFilter("[reference] IS NOT NULL").HasDatabaseName("ux_credit_ledger_org_reference");
        // (job_id, entry_type) ile: EF aynı özellik kümesine iki ayrı indeks tanımına izin vermez; Reserve satırı için bu da iş başına tek satırdır
        builder.HasIndex(x => new { x.JobId, x.EntryType }).IsUnique().HasFilter("[entry_type] = N'Reserve' AND [job_id] IS NOT NULL").HasDatabaseName("ux_credit_ledger_job_reserve");
        builder.HasIndex(x => x.JobId).IsUnique().HasFilter("[entry_type] IN (N'Release', N'Consume') AND [job_id] IS NOT NULL").HasDatabaseName("ux_credit_ledger_job_close");
        // Hesap her zaman aynı firmada: hesap silinmez (Restrict); firma silinirse defter kalır (mali kayıt)
        builder.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}
