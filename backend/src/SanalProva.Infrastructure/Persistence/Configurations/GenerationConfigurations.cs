using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SanalProva.Domain.Files;
using SanalProva.Domain.Generation;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;

namespace SanalProva.Infrastructure.Persistence.Configurations;

internal sealed class GenerationJobConfiguration : IEntityTypeConfiguration<GenerationJob>
{
    public void Configure(EntityTypeBuilder<GenerationJob> builder)
    {
        // JsonDocument, SQL Server'da JSON metni olarak saklanır (uyumluluk 160: nvarchar(max))
        builder.Property(x => x.RevisionParams)
            .HasColumnType("nvarchar(max)")
            .HasConversion(
                v => v == null ? null : v.RootElement.GetRawText(),
                v => v == null ? null : JsonDocument.Parse(v, default(JsonDocumentOptions)),
                new ValueComparer<JsonDocument?>(
                    (a, b) => (a == null ? null : a.RootElement.GetRawText()) == (b == null ? null : b.RootElement.GetRawText()),
                    v => v == null ? 0 : StringComparer.Ordinal.GetHashCode(v.RootElement.GetRawText()),
                    v => v));
        builder.Property(x => x.Provider).HasMaxLength(40);
        builder.Property(x => x.Model).HasMaxLength(80);
        builder.Property(x => x.PromptTemplateVersion).HasMaxLength(40);
        builder.Property(x => x.ProviderRequestId).HasMaxLength(120);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(64);
        builder.Property(x => x.RequestFingerprint).HasMaxLength(64);
        builder.Property(x => x.ModelKey).HasMaxLength(60);
        builder.Property(x => x.Quality).HasMaxLength(20);
        builder.HasIndex(x => new { x.SessionId, x.RequestFingerprint });
        builder.Property(x => x.ErrorMessage).HasMaxLength(500);

        builder.HasIndex(x => new { x.SessionId, x.IdempotencyKey }).IsUnique();

        // Çit belirteci: deneme numarası yalnızca kuyruktan alınırken artar (ClaimAsync). EF iş satırını "WHERE attempt = <okunan>" koşuluyla
        // günceller; kilidi dolup başka worker'a geçen işte eski worker'ın yazmaları DbUpdateConcurrencyException ile reddedilir.
        builder.Property(x => x.Attempt).IsConcurrencyToken();

        // Kuyruk sorgusu: yalnızca bekleyen işler, sıradaki deneme zamanına göre
        builder.HasIndex(x => x.NextAttemptAt)
            .HasFilter("[status] = N'Queued'")
            .HasDatabaseName("ix_generation_jobs_queue");

        // Kilidi süresi dolan (çöken worker) işleri kurtarmak için
        builder.HasIndex(x => x.LeaseUntil)
            .HasFilter("[status] = N'Running'")
            .HasDatabaseName("ix_generation_jobs_running_lease");

        // SQL Server bir tabloya tek zincirleme silme yolu kabul eder. İş yalnızca oturumdan zincirle silinir (KVKK silmesinin kökü);
        // seçim, fotoğraf ve temel görsel bağlantıları veritabanında NO ACTION'dır:
        // - oturum silinirken hepsi aynı DELETE deyiminde gider,
        // - tek başına fotoğraf silinirken bağlantı uygulamada NULL'a çekilir (SessionEndpoints).
        builder.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TryOnSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<OutfitSelection>().WithMany().HasForeignKey(x => x.SelectionId).OnDelete(DeleteBehavior.ClientSetNull);
        builder.HasOne<SessionPhoto>().WithMany().HasForeignKey(x => x.SourcePhotoId).OnDelete(DeleteBehavior.ClientSetNull);
        builder.HasOne<GeneratedImage>().WithMany().HasForeignKey(x => x.BaseImageId).OnDelete(DeleteBehavior.ClientSetNull);
        builder.ToTable(t => t.HasCheckConstraint("ck_generation_jobs_attempts", "attempt >= 0 AND attempt <= max_attempts"));
    }
}

internal sealed class GeneratedImageConfiguration : IEntityTypeConfiguration<GeneratedImage>
{
    public void Configure(EntityTypeBuilder<GeneratedImage> builder)
    {
        builder.Property(x => x.Label).HasMaxLength(200);
        builder.HasIndex(x => new { x.SessionId, x.VersionNo }).IsUnique();
        builder.HasIndex(x => x.ViewGroupId);

        // Sonuç görseli oturumdan zincirle silinir; iş yolu ikinci zincir, üst görsel bağlantısı öz-referans olduğu için NO ACTION
        builder.HasOne<TryOnSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<GenerationJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GeneratedImage>().WithMany().HasForeignKey(x => x.ParentImageId).OnDelete(DeleteBehavior.ClientSetNull);
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AiUsageLogConfiguration : IEntityTypeConfiguration<AiUsageLog>
{
    public void Configure(EntityTypeBuilder<AiUsageLog> builder)
    {
        builder.Property(x => x.Provider).HasMaxLength(40);
        builder.Property(x => x.Model).HasMaxLength(80);
        builder.Property(x => x.Operation).HasMaxLength(40);
        builder.Property(x => x.CreditsUsed).HasPrecision(10, 2);
        builder.Property(x => x.EstimatedCostUsd).HasPrecision(10, 4);
        builder.Property(x => x.ActualCostUsd).HasPrecision(10, 4);
        builder.Property(x => x.ProviderRequestId).HasMaxLength(120);
        builder.Property(x => x.Quality).HasMaxLength(20);
        builder.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        builder.HasIndex(x => new { x.StoreId, x.CreatedAt });

        // Oturum silindiğinde maliyet kaydı kalır, iş bağlantısı koparılır
        builder.HasOne<GenerationJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
    }
}
