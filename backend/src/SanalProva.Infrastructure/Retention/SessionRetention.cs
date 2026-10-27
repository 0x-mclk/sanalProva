using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Audit;
using SanalProva.Domain.Files;
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

    /// <summary>
    /// Yetim dosya/kayıt süpürücüsünün bekleme payı (dakika): yazma sırasındaki (henüz kaydı işlenmemiş) dosyalar silinmesin diye
    /// bu süreden yeni klasör/kayıtlara dokunulmaz.
    /// </summary>
    public int OrphanGraceMinutes { get; set; } = 60;
}

/// <summary>
/// Oturumu ve ona bağlı tüm kişisel veriyi KVKK uyumlu şekilde kalıcı siler: önce depodaki dosyalar (ana görsel + küçük resim,
/// oturum öneki tek seferde), sonra veritabanı kayıtları (fotoğraf, seçim, iş, sonuç, onay — DB cascade), en son dosya metadata'sı.
/// Maliyet kayıtları (<c>ai_usage_logs</c>) kişisel veri içermediği için kalır; oturum bağlantısı koparılır.
/// </summary>
public sealed class SessionPurger(IImageStorageService storage, DisplayStateService displays, TimeProvider clock, ILogger<SessionPurger> logger)
{
    private const int DbAttempts = 4;

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
        var prefix = StorageKeys.SessionPrefix(session.OrganizationId, session.StoreId, sessionId);
        await storage.DeletePrefixAsync(prefix, ct);

        // 2) Oturum (DB cascade: fotoğraf, seçim, iş, sonuç, onay) ve TV bağlantısı, 3) dosya metadata'sı: eşzamanlı bir yükleme/üretim aynı oturumun satırlarını yazarken
        //    zincirleme silme kilitlenme (1205) kurbanı olabilir: deyimler birbirinden bağımsız ve tekrar çalıştırılabilir olduğundan geçici kilit hatasında yeniden denenir
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.DisplayDevices.IgnoreQueryFilters().Where(d => d.CurrentSessionId == sessionId)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.CurrentSessionId, (Guid?)null), ct);
                await db.TryOnSessions.IgnoreQueryFilters().Where(s => s.Id == sessionId).ExecuteDeleteAsync(ct);

                // Dosya metadata'sı: kimlik listesi (adım başında okundu) + oturum önekindeki her kayıt (listeden sonra yazılmış geç dosyalar dahil)
                await db.StoredFiles.IgnoreQueryFilters().Where(f => fileIds.Contains(f.Id) || f.StorageKey.StartsWith(prefix)).ExecuteDeleteAsync(ct);
                break;
            }
            catch (Exception ex) when (attempt < DbAttempts && (DbErrors.IsTransientLockFailure(ex) || ex.InnerException is { } inner && DbErrors.IsTransientLockFailure(inner)))
            {
                logger.LogWarning(ex, "Oturum silme kilit hatasına takıldı, yeniden deneniyor: {SessionId} ({Attempt}/{Max})", sessionId, attempt, DbAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(40 * attempt), ct);
            }
        }

        // 4) İkinci depo geçişi: ilk silmeden sonra bir worker dosya yazdıysa (kaydı oturum yokluğundan reddedilir ve kendi dosyasını da siler; çökerse
        //    yetim dosya süpürücüsü temizler) burada da süpürülür
        await storage.DeletePrefixAsync(prefix, ct);

        db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = session.OrganizationId, StoreId = session.StoreId, UserId = userId,
            Action = auditAction, EntityType = nameof(TryOnSession), EntityId = sessionId, CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Oturum silindi: {SessionId} ({Action}, {FileCount} dosya)", sessionId, auditAction, fileIds.Count);
    }
}

/// <summary>
/// Yetim müşteri verisi süpürücüsü (kurtarma ağı). Normal akışta gerekmez; şu durumlara karşı tekrar çalıştırılabilir ikinci savunma hattıdır:
/// silme sırasında süreç çökmesi (oturum satırı gitti, dosya/metadata kaldı), yükleme/sonuç yazımında dosya yazılıp kaydın işlenememesi.
/// <list type="bullet">
/// <item>Depoda, <b>veritabanında oturumu olmayan</b> oturum klasörleri (bekleme payından eski olanlar) silinir.</item>
/// <item><c>stored_files</c> kayıtları (müşteri fotoğrafı / üretilen görsel), hiçbir fotoğraf veya sonuç görseli tarafından kullanılmıyorsa (bekleme payından eski)
/// dosyalarıyla birlikte silinir.</item>
/// </list>
/// Katalog görselleri ve payı dolmamış kayıtlar dokunulmaz.
/// </summary>
public sealed class OrphanSweeper(IServiceScopeFactory scopes, IImageStorageService storage, TimeProvider clock, IOptions<RetentionSettings> settings, ILogger<OrphanSweeper> logger)
{
    public sealed record Result(int Folders, int FileRecords);

    /// <param name="organizationId">Verilirse yalnızca bu firmanın klasörleri/kayıtları taranır (firma bazlı bakım ve paylaşılan test ortamında yalıtım). Üretim süpürmesi null kullanır.</param>
    public async Task<Result> SweepAsync(CancellationToken ct, Guid? organizationId = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cutoff = clock.GetUtcNow().AddMinutes(-Math.Max(1, settings.Value.OrphanGraceMinutes));

        var folders = 0;
        foreach (var folder in await storage.ListSessionFoldersAsync(ct))
        {
            if ((organizationId is { } scopeOrg && !folder.Prefix.StartsWith($"{scopeOrg:N}/", StringComparison.Ordinal))
                || folder.LastWriteUtc > cutoff || await db.TryOnSessions.IgnoreQueryFilters().AsNoTracking().AnyAsync(s => s.Id == folder.SessionId, ct))
            {
                continue;
            }

            await storage.DeletePrefixAsync(folder.Prefix, ct);
            folders++;
            logger.LogWarning("Oturumu olmayan yetim klasör silindi: {Prefix}", folder.Prefix);
        }

        var orphans = await db.StoredFiles.IgnoreQueryFilters()
            .Where(f => (organizationId == null || f.OrganizationId == organizationId) && (f.Purpose == FilePurpose.CustomerPhoto || f.Purpose == FilePurpose.GeneratedImage) && f.CreatedAt < cutoff
                && !db.SessionPhotos.IgnoreQueryFilters().Any(p => p.FileId == f.Id) && !db.GeneratedImages.IgnoreQueryFilters().Any(i => i.FileId == f.Id))
            .Take(500).ToListAsync(ct);
        foreach (var file in orphans)
        {
            await storage.DeleteAsync(file.StorageKey, ct);
            await storage.DeleteAsync(StorageKeys.ThumbKey(file.StorageKey), ct);
        }

        if (orphans.Count > 0)
        {
            db.StoredFiles.RemoveRange(orphans);
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Kullanılmayan {Count} dosya kaydı (ve dosyası) silindi.", orphans.Count);
        }

        return new Result(folders, orphans.Count);
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

public sealed class RetentionJob(RetentionSweeper sweeper, OrphanSweeper orphans, IOptions<RetentionSettings> settings, ILogger<RetentionJob> logger) : BackgroundService
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

                await orphans.SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Saklama süpürmesi başarısız oldu.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
