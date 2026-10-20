using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Generation;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.TryOn;

/// <summary>
/// Oturum okuma ve DTO üretimi. Tüm sorgular şube filtresinden geçer: başka şubenin oturumu "yok" gibi (404) davranır.
/// Müşteri görselleri kısa ömürlü imzalı URL ile verilir.
/// </summary>
public sealed class SessionReader(AppDbContext db, ImageLinks links, TimeProvider clock)
{
    public Task<TryOnSession?> LoadAsync(Guid id, CancellationToken ct) =>
        db.TryOnSessions.Include(s => s.Consent).Include(s => s.Photos).AsSplitQuery().SingleOrDefaultAsync(s => s.Id == id, ct);

    public async Task<OrganizationSettings> GetSettingsAsync(Guid organizationId, CancellationToken ct) =>
        (await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == organizationId, ct)).Settings;

    /// <summary>Her etkinlikte saklama süresi yeniden hesaplanır (kapalı oturumlarda kapanıştan itibaren).</summary>
    public void Touch(TryOnSession session, OrganizationSettings settings)
    {
        var now = clock.GetUtcNow();
        session.LastActivityAt = now;
        session.RetentionUntil = RetentionPolicy.CalculateRetentionUntil(
            session.ClosedAt ?? now, settings.DefaultRetentionHours, settings.ExtendedRetentionDays, session.Consent?.ExtendedRetentionConsent == true);
    }

    public async Task<SessionDetail> BuildDetailAsync(TryOnSession session, CancellationToken ct)
    {
        var images = await db.GeneratedImages.AsNoTracking().Where(i => i.SessionId == session.Id).OrderBy(i => i.VersionNo).ToListAsync(ct);
        var jobs = await db.GenerationJobs.AsNoTracking().Where(j => j.SessionId == session.Id).OrderByDescending(j => j.CreatedAt).Take(20).ToListAsync(ct);
        var files = await ImageLinks.LoadFilesAsync(db, session.Photos.Select(p => p.FileId).Concat(images.Select(i => i.FileId)), ct);

        var photos = session.Photos.OrderByDescending(p => p.IsPrimary).ThenByDescending(p => p.CreatedAt)
            .Where(p => files.ContainsKey(p.FileId)).Select(p => ToPhotoDto(p, links.Customer(files[p.FileId]))).ToList();
        var imageDtos = images.Where(i => files.ContainsKey(i.FileId)).Select(i => ToImageDto(i, links.Customer(files[i.FileId]))).ToList();
        var jobDtos = new List<JobDto>();
        foreach (var job in jobs)
        {
            jobDtos.Add(await ToJobDtoAsync(job, images.FirstOrDefault(i => i.JobId == job.Id), ct));
        }

        var active = jobs.FirstOrDefault(j => j.Status is GenerationJobStatus.Queued or GenerationJobStatus.Running);
        var step = SessionProgress.NextStep(session.Status, session.Consent?.AllowsProcessing == true, photos.Count > 0, session.Draft is not null, active is not null, imageDtos.Count > 0);
        return new SessionDetail(
            session.Id, session.CustomerLabel, SessionProgress.DisplayCode(session.Id), session.Status, step,
            session.CreatedAt, session.LastActivityAt, session.RetentionUntil, session.Consent?.ExtendedRetentionConsent == true,
            session.Consent is { WithdrawnAt: null } c ? new ConsentDto(c.NoticeVersion, c.ProcessingConsent, c.ForeignTransferConsent, c.ExtendedRetentionConsent, c.GuardianConsent, c.Method, c.RecordedAt) : null,
            photos, ToDraftDto(session.Draft, []), imageDtos, jobDtos, active?.Id);
    }

    public async Task<List<SessionSummary>> BuildSummariesAsync(IReadOnlyList<TryOnSession> sessions, CancellationToken ct)
    {
        var ids = sessions.Select(s => s.Id).ToList();
        var photos = await db.SessionPhotos.AsNoTracking().Where(p => ids.Contains(p.SessionId) && p.IsPrimary).ToListAsync(ct);
        var images = await db.GeneratedImages.AsNoTracking().Where(i => ids.Contains(i.SessionId)).OrderBy(i => i.VersionNo).ToListAsync(ct);
        var activeSessionIds = await db.GenerationJobs.AsNoTracking()
            .Where(j => ids.Contains(j.SessionId) && (j.Status == GenerationJobStatus.Queued || j.Status == GenerationJobStatus.Running))
            .Select(j => j.SessionId).Distinct().ToListAsync(ct);
        var files = await ImageLinks.LoadFilesAsync(db, photos.Select(p => p.FileId).Concat(images.Select(i => i.FileId)), ct);

        return sessions.Select(s =>
        {
            var photo = photos.FirstOrDefault(p => p.SessionId == s.Id);
            var mine = images.Where(i => i.SessionId == s.Id).ToList();
            var latest = mine.LastOrDefault();
            var step = SessionProgress.NextStep(s.Status, s.Consent?.AllowsProcessing == true, photo is not null, s.Draft is not null, activeSessionIds.Contains(s.Id), mine.Count > 0);
            return new SessionSummary(
                s.Id, s.CustomerLabel, SessionProgress.DisplayCode(s.Id), s.Status, step, s.CreatedAt, s.LastActivityAt, s.RetentionUntil, mine.Count,
                photo is not null && files.TryGetValue(photo.FileId, out var pf) ? links.Customer(pf) : null,
                latest is not null && files.TryGetValue(latest.FileId, out var rf) ? links.Customer(rf) : null,
                latest?.IsDemo ?? false);
        }).ToList();
    }

    public PhotoDto ToPhotoDto(SessionPhoto photo, ImageUrls urls)
    {
        var q = photo.QualityFlags;
        return new PhotoDto(photo.Id, photo.Angle, photo.Width, photo.Height, photo.IsPrimary,
            new PhotoQualityDto(q.Blurry, q.LowResolution, q.NotPortrait, q.HasWarnings, JobMessages.PhotoWarnings(q)), urls);
    }

    public static GeneratedImageDto ToImageDto(Domain.Generation.GeneratedImage i, ImageUrls urls) =>
        new(i.Id, i.VersionNo, i.ParentImageId, i.JobId, i.Label, i.IsDemo, i.IsColorPreview, i.IsFavorite, i.CreatedAt, urls);

    public static DraftDto? ToDraftDto(SessionDraft? draft, IReadOnlyList<string> warnings) =>
        draft is null ? null : new DraftDto(draft.Mode, draft.Items.Select(i => new DraftItemDto(i.CategoryId, i.ProductId, i.VariantId)).ToList(), warnings);

    public async Task<JobDto> ToJobDtoAsync(GenerationJob job, Domain.Generation.GeneratedImage? image, CancellationToken ct)
    {
        int? position = null;
        if (job.Status == GenerationJobStatus.Queued)
        {
            // Kuyruk sırası firmalar arası ortaktır; yalnızca sayı döner
            position = 1 + await db.GenerationJobs.IgnoreQueryFilters().CountAsync(j =>
                j.Status == GenerationJobStatus.Queued && j.Id != job.Id && (j.NextAttemptAt < job.NextAttemptAt || j.NextAttemptAt == job.NextAttemptAt && j.CreatedAt < job.CreatedAt), ct);
        }

        string? message = null;
        var retryable = false;
        if (job.Status == GenerationJobStatus.Failed && job.ErrorCode is { } code)
        {
            (message, retryable) = JobMessages.For(code);
        }

        return new JobDto(
            job.Id, job.SessionId, job.JobType, job.Status, job.Stage, position, job.Attempt, job.MaxAttempts, job.ErrorCode, message, retryable, image?.Id,
            job.CreatedAt, job.StartedAt, job.FinishedAt, job.Provider, job.Model, image?.IsDemo ?? job.Provider == "Mock");
    }
}
