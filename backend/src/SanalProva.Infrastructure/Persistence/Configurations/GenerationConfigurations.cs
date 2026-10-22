using Microsoft.EntityFrameworkCore;
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
        builder.Property(x => x.RevisionParams).HasColumnType("jsonb");
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

        // Kuyruk sorgusu: yalnızca bekleyen işler, sıradaki deneme zamanına göre
        builder.HasIndex(x => x.NextAttemptAt)
            .HasFilter("status = 'Queued'")
            .HasDatabaseName("ix_generation_jobs_queue");

        // Kilidi süresi dolan (çöken worker) işleri kurtarmak için
        builder.HasIndex(x => x.LeaseUntil)
            .HasFilter("status = 'Running'")
            .HasDatabaseName("ix_generation_jobs_running_lease");

        builder.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TryOnSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<OutfitSelection>().WithMany().HasForeignKey(x => x.SelectionId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<SessionPhoto>().WithMany().HasForeignKey(x => x.SourcePhotoId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<GeneratedImage>().WithMany().HasForeignKey(x => x.BaseImageId).OnDelete(DeleteBehavior.SetNull);
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

        builder.HasOne<TryOnSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<GenerationJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<GeneratedImage>().WithMany().HasForeignKey(x => x.ParentImageId).OnDelete(DeleteBehavior.SetNull);
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
