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
        builder.Property(x => x.ReservedCostUsd).HasPrecision(10, 4);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(64);
        builder.Property(x => x.RequestFingerprint).HasMaxLength(64);
        builder.Property(x => x.DedupeKey).HasMaxLength(64);

        // Aynı ücretli istek için oturumda tek etkin iş (kabul kilidinin arkasındaki ikinci savunma hattı). Başarısız/iptal iş filtreden
        // çıkar: yeniden deneme yeni iş açabilir. Durum değişince SQL Server filtreli indeksi kendisi günceller.
        builder.HasIndex(x => new { x.SessionId, x.DedupeKey })
            .IsUnique()
            .HasFilter("[dedupe_key] IS NOT NULL AND [status] IN (N'Queued', N'Running', N'Succeeded')")
            .HasDatabaseName("ux_generation_jobs_active_dedupe");
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
        // Firma tutarlılığı veritabanında zorunlu: iş, oturum, seçim, fotoğraf ve temel görsel AYNI firmada olmalı; iş ve oturum ayrıca aynı şubede
        builder.HasAlternateKey(x => new { x.Id, x.OrganizationId }).HasName("ak_generation_jobs_id_organization_id");
        builder.HasOne<Store>().WithMany().HasForeignKey(x => new { x.StoreId, x.OrganizationId }).HasPrincipalKey(s => new { s.Id, s.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TryOnSession>().WithMany().HasForeignKey(x => new { x.SessionId, x.StoreId, x.OrganizationId }).HasPrincipalKey(s => new { s.Id, s.StoreId, s.OrganizationId }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<OutfitSelection>().WithMany().HasForeignKey(x => new { x.SelectionId, x.OrganizationId }).HasPrincipalKey(s => new { s.Id, s.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SessionPhoto>().WithMany().HasForeignKey(x => new { x.SourcePhotoId, x.OrganizationId }).HasPrincipalKey(p => new { p.Id, p.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GeneratedImage>().WithMany().HasForeignKey(x => new { x.BaseImageId, x.OrganizationId }).HasPrincipalKey(i => new { i.Id, i.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
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
        builder.HasAlternateKey(x => new { x.Id, x.OrganizationId }).HasName("ak_generated_images_id_organization_id");
        builder.HasOne<TryOnSession>().WithMany().HasForeignKey(x => new { x.SessionId, x.OrganizationId }).HasPrincipalKey(s => new { s.Id, s.OrganizationId }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<GenerationJob>().WithMany().HasForeignKey(x => new { x.JobId, x.OrganizationId }).HasPrincipalKey(j => new { j.Id, j.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GeneratedImage>().WithMany().HasForeignKey(x => new { x.ParentImageId, x.OrganizationId }).HasPrincipalKey(i => new { i.Id, i.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StoredFile>().WithMany().HasForeignKey(x => new { x.FileId, x.OrganizationId }).HasPrincipalKey(f => new { f.Id, f.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
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
        builder.Property(x => x.EventKey).HasMaxLength(100);
        builder.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
        builder.HasIndex(x => new { x.StoreId, x.CreatedAt });

        // Aynı sistem olayı (belirsiz maliyet, mutabakat düzeltmesi) ikinci kez yazılamaz. Kurtarma SQL'i, süpürücü ve işlemci aynı anahtarı kullanır:
        // hangisi önce yazarsa diğerleri sessizce atlar. Sıradan deneme satırlarında anahtar boştur (filtreli indeks onları kapsamaz).
        builder.HasIndex(x => x.EventKey).IsUnique().HasFilter("[event_key] IS NOT NULL").HasDatabaseName("ux_ai_usage_logs_event_key");

        // Mutabakat bekleyen/kapanmış belirsiz kayıtların listesi (platform yöneticisi)
        builder.HasIndex(x => x.ReconciliationState).HasFilter("[reconciliation_state] IS NOT NULL").HasDatabaseName("ix_ai_usage_logs_reconciliation");

        // Düzeltme satırı asıl kayda ve olay anahtarına bağlıdır
        builder.ToTable(t => t.HasCheckConstraint("ck_ai_usage_logs_adjustment", "[is_adjustment] = 0 OR ([adjusts_log_id] IS NOT NULL AND [event_key] IS NOT NULL)"));

        // Oturum silindiğinde maliyet kaydı kalır, iş bağlantısı koparılır
        // İş bağlantısı SET NULL (tek sütunlu FK) + aynı firmada olma kuralı (bileşik NO ACTION FK; bağlantı boşken devre dışı)
        builder.HasAlternateKey(x => new { x.Id, x.OrganizationId }).HasName("ak_ai_usage_logs_id_organization_id");
        builder.HasOne<GenerationJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<GenerationJob>().WithMany().HasForeignKey(x => new { x.JobId, x.OrganizationId }).HasPrincipalKey(j => new { j.Id, j.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Store>().WithMany().HasForeignKey(x => new { x.StoreId, x.OrganizationId }).HasPrincipalKey(s => new { s.Id, s.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AiUsageLog>().WithMany().HasForeignKey(x => new { x.AdjustsLogId, x.OrganizationId }).HasPrincipalKey(l => new { l.Id, l.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AiCostReconciliationConfiguration : IEntityTypeConfiguration<AiCostReconciliation>
{
    public void Configure(EntityTypeBuilder<AiCostReconciliation> builder)
    {
        builder.Property(x => x.EffectiveCostUsd).HasPrecision(10, 4);
        builder.Property(x => x.ConfirmedAmountUsd).HasPrecision(10, 4);
        builder.Property(x => x.Note).HasMaxLength(200);
        builder.Property(x => x.IdempotencyKey).HasMaxLength(64);
        builder.Property(x => x.RequestHash).HasMaxLength(64);

        // Karar sırası vaka başına benzersiz: iki yönetici aynı sırayı yazamaz (ikincisi 409 alır). Aynı Idempotency-Key ikinci karar üretmez.
        builder.HasIndex(x => new { x.UsageLogId, x.Sequence }).IsUnique().HasDatabaseName("ux_ai_cost_reconciliations_case_sequence");
        builder.HasIndex(x => new { x.UsageLogId, x.IdempotencyKey }).IsUnique().HasDatabaseName("ux_ai_cost_reconciliations_case_key");
        builder.HasIndex(x => x.OrganizationId);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_ai_cost_reconciliations_sequence", "[sequence] >= 1");
            t.HasCheckConstraint("ck_ai_cost_reconciliations_amount", "[effective_cost_usd] >= 0 AND ([confirmed_amount_usd] IS NULL OR [confirmed_amount_usd] > 0)");
        });

        // Muhasebe kaydı oturum silinince de kalır: işe anahtar yok; belirsiz maliyet satırına bağlıdır (o da silinmez)
        builder.HasOne<AiUsageLog>().WithMany().HasForeignKey(x => new { x.UsageLogId, x.OrganizationId }).HasPrincipalKey(l => new { l.Id, l.OrganizationId }).OnDelete(DeleteBehavior.Restrict);
    }
}
