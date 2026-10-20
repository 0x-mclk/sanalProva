using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Audit;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Displays;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Storage;

namespace SanalProva.Infrastructure.Retention;

public sealed class RetentionSettings
{
    public const string Section = "Retention";

    /// <summary>Süresi dolan oturumların temizlik aralığı. 0 veya negatifse arka plan işi çalışmaz.</summary>
    public int SweepIntervalSeconds { get; set; } = 900;
}

/// <summary>
/// Oturumu ve ona bağlı tüm kişisel veriyi KVKK uyumlu şekilde kalıcı siler: önce depodaki dosyalar (ana görsel + küçük resim,
/// oturum öneki tek seferde), sonra veritabanı kayıtları (fotoğraf, seçim, iş, sonuç, onay — DB cascade), en son dosya metadata'sı.
/// Maliyet kayıtları (<c>ai_usage_logs</c>) kişisel veri içermediği için kalır; oturum bağlantısı koparılır.
/// </summary>
public sealed class SessionPurger(IImageStorageService storage, DisplayStateService displays, TimeProvider clock, ILogger<SessionPurger> logger)
{
    public async Task PurgeAsync(AppDbContext db, Guid sessionId, string auditAction, Guid? userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var session = await db.TryOnSessions.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return;
        }

        // 0) Önce TV: müşteri görüntüsü ekrandan ve erişimden anında kalkar (dosya silme uzun sürse bile)
        await displays.ResetBySessionAsync(sessionId, DisplayStateReason.SessionEnded, detach: true, ct);

        var fileIds = await db.SessionPhotos.IgnoreQueryFilters().Where(p => p.SessionId == sessionId).Select(p => p.FileId)
            .Concat(db.GeneratedImages.IgnoreQueryFilters().Where(i => i.SessionId == sessionId).Select(i => i.FileId))
            .ToListAsync(ct);

        // 1) Depo: hata olursa veritabanı kaydı silinmez, sonraki süpürmede tekrar denenir (veri sızıntısına karşı güvenli taraf)
        await storage.DeletePrefixAsync(StorageKeys.SessionPrefix(session.OrganizationId, session.StoreId, sessionId), ct);

        // 2) Oturum (DB cascade: fotoğraf, seçim, iş, sonuç, onay) ve TV bağlantısı
        await db.DisplayDevices.IgnoreQueryFilters().Where(d => d.CurrentSessionId == sessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.CurrentSessionId, (Guid?)null), ct);
        await db.TryOnSessions.IgnoreQueryFilters().Where(s => s.Id == sessionId).ExecuteDeleteAsync(ct);

        // 3) Dosya metadata'sı
        if (fileIds.Count > 0)
        {
            await db.StoredFiles.IgnoreQueryFilters().Where(f => fileIds.Contains(f.Id)).ExecuteDeleteAsync(ct);
        }

        db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = session.OrganizationId, StoreId = session.StoreId, UserId = userId,
            Action = auditAction, EntityType = nameof(TryOnSession), EntityId = sessionId, CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Oturum silindi: {SessionId} ({Action}, {FileCount} dosya)", sessionId, auditAction, fileIds.Count);
    }
}

/// <summary>Saklama süresi dolan oturumları siler. Bir oturumun silinememesi diğerlerini engellemez.</summary>
public sealed class RetentionSweeper(IServiceScopeFactory scopes, TimeProvider clock, ILogger<RetentionSweeper> logger)
{
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var purger = scope.ServiceProvider.GetRequiredService<SessionPurger>();

        var now = clock.GetUtcNow();
        var expired = await db.TryOnSessions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.RetentionUntil <= now && s.Status != SessionStatus.Purged)
            .OrderBy(s => s.RetentionUntil).Select(s => s.Id).Take(200).ToListAsync(ct);

        var purged = 0;
        foreach (var id in expired)
        {
            try
            {
                await purger.PurgeAsync(db, id, "Session.PurgedByRetention", null, ct);
                purged++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Süresi dolan oturum silinemedi: {SessionId}", id);
            }
        }

        return purged;
    }
}

public sealed class RetentionJob(RetentionSweeper sweeper, IOptions<RetentionSettings> settings, ILogger<RetentionJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = settings.Value.SweepIntervalSeconds;
        if (interval <= 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
        do
        {
            try
            {
                var purged = await sweeper.SweepAsync(stoppingToken);
                if (purged > 0)
                {
                    logger.LogInformation("Saklama süresi dolan {Count} oturum silindi.", purged);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Saklama süpürmesi başarısız oldu.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
