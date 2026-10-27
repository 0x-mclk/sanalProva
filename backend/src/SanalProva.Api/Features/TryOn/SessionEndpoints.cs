using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Catalog;
using SanalProva.Domain.Common;
using SanalProva.Domain.Files;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Displays;
using SanalProva.Infrastructure.Images;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Retention;
using SanalProva.Infrastructure.Storage;

namespace SanalProva.Api.Features.TryOn;

/// <summary>Güncel müşteri bilgilendirme metni. Hukuki inceleme bekleyen taslaktır; sürüm onay kaydına yazılır.</summary>
public static class ConsentNotice
{
    public const string Version = "2026-10-v1";

    public static ConsentNoticeDto Current { get; } = new(
        Version,
        "Müşteri bilgilendirmesi",
        [
            "Bu uygulama, mağazamızdaki kıyafetleri sizin üzerinizde yapay zekâ ile önizlemek için boydan fotoğrafınızı geçici olarak işler.",
            "Fotoğrafınız yalnızca bu prova için kullanılır. Pazarlama veya başka bir amaçla kullanılmaz; istediğiniz anda silinmesini isteyebilirsiniz.",
            "Üretim hizmeti yurt dışında bulunan yapay zekâ sağlayıcıları tarafından sağlanabilir. Bu durumda fotoğrafınız yalnızca bu işlem için onlara iletilir.",
            "Oluşturulan görüntüler yapay zekâ önizlemesidir. Beden uyumu, kalıp ve nakış detaylarının gerçek ürünle birebir aynı olacağı garanti edilmez.",
        ],
        "Fotoğraflar varsayılan olarak 24 saat içinde otomatik silinir.");
}

/// <summary>Oturum yaşam döngüsü: oluşturma, onay, fotoğraf, taslak seçim, kapatma ve silme. Şube seviyesinde yetkilidir.</summary>
public static class SessionEndpoints
{
    private const int MaxPhotosPerSession = 5;

    public static RouteGroupBuilder MapSessionEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/consent-notice/current", () => TypedResults.Ok(ConsentNotice.Current)).WithTags("TryOn").RequireAuthorization().Produces<ConsentNoticeDto>();

        var group = api.MapGroup("/sessions").WithTags("TryOn").RequireStore(StoreRole.Staff);
        group.MapPost("", CreateAsync).Produces<SessionDetail>(StatusCodes.Status201Created);
        group.MapGet("", ListAsync).Produces<Page<SessionSummary>>();
        group.MapGet("/{id:guid}", GetAsync).Produces<SessionDetail>();
        group.MapPost("/{id:guid}/consent", RecordConsentAsync).Produces<SessionDetail>();
        group.MapDelete("/{id:guid}/consent", WithdrawConsentAsync).Produces(StatusCodes.Status204NoContent);
        group.MapPost("/{id:guid}/photos", UploadPhotoAsync).DisableAntiforgery().Produces<PhotoDto>(StatusCodes.Status201Created);
        group.MapDelete("/{id:guid}/photos/{photoId:guid}", DeletePhotoAsync).Produces<SessionDetail>();
        group.MapPut("/{id:guid}/draft", SaveDraftAsync).Produces<DraftDto>();
        group.MapPost("/{id:guid}/close", CloseAsync).Produces<SessionDetail>();
        group.MapDelete("/{id:guid}", DeleteAsync).Produces(StatusCodes.Status204NoContent);
        return api;
    }

    private static async Task<IResult> CreateAsync(CreateSessionRequest request, AppDbContext db, CurrentUser user, SessionReader reader, AuditTrail audit, TimeProvider clock, CancellationToken ct)
    {
        var settings = await reader.GetSettingsAsync(user.OrganizationId, ct);
        var now = clock.GetUtcNow();
        var session = new TryOnSession
        {
            OrganizationId = user.OrganizationId, StoreId = user.RequireStoreId(), CreatedByUserId = user.UserId,
            CustomerLabel = string.IsNullOrWhiteSpace(request.CustomerLabel) ? null : request.CustomerLabel.Trim(),
            LastActivityAt = now,
            RetentionUntil = RetentionPolicy.CalculateRetentionUntil(now, settings.DefaultRetentionHours, settings.ExtendedRetentionDays, false),
        };
        db.TryOnSessions.Add(session);
        audit.Record("Session.Created", nameof(TryOnSession), session.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/sessions/{session.Id}", await reader.BuildDetailAsync(session, ct));
    }

    private static async Task<IResult> ListAsync(AppDbContext db, SessionReader reader, CancellationToken ct, string status = "all", int page = 1, int pageSize = 20)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var query = db.TryOnSessions.AsNoTracking().AsQueryable();
        query = status switch
        {
            "open" => query.Where(s => s.Status == SessionStatus.Open),
            "closed" => query.Where(s => s.Status == SessionStatus.Closed),
            _ => query,
        };

        var total = await query.CountAsync(ct);
        var sessions = await query.Include(s => s.Consent).OrderByDescending(s => s.LastActivityAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return TypedResults.Ok(new Page<SessionSummary>(await reader.BuildSummariesAsync(sessions, ct), page, pageSize, total));
    }

    private static async Task<IResult> GetAsync(Guid id, SessionReader reader, CancellationToken ct)
    {
        var session = await reader.LoadAsync(id, ct);
        return session is null ? ApiProblems.NotFound("Prova bulunamadı.") : TypedResults.Ok(await reader.BuildDetailAsync(session, ct));
    }

    // ---- Onay (KVKK) ----

    private static async Task<IResult> RecordConsentAsync(Guid id, ConsentRequest request, AppDbContext db, CurrentUser user, SessionReader reader, AuditTrail audit, TimeProvider clock, CancellationToken ct)
    {
        var session = await reader.LoadAsync(id, ct);
        if (session is null)
        {
            return ApiProblems.NotFound("Prova bulunamadı.");
        }

        if (session.Status != SessionStatus.Open)
        {
            return ApiProblems.Conflict("SESSION_CLOSED", "Bu prova kapatılmış.");
        }

        if (session.Consent is { WithdrawnAt: null })
        {
            return ApiProblems.Conflict("CONSENT_EXISTS", "Bu prova için onay zaten alınmış.");
        }

        if (request.NoticeVersion != ConsentNotice.Version)
        {
            return ApiProblems.BadRequest("NOTICE_OUTDATED", "Bilgilendirme metni güncellendi. Sayfayı yenileyin.");
        }

        if (!request.ProcessingConsent || !request.ForeignTransferConsent)
        {
            return ApiProblems.BadRequest("CONSENT_INCOMPLETE", "Fotoğrafın işlenmesi ve yurt dışı sağlayıcıya aktarımı için onay gerekli.");
        }

        if (request.IsMinor && request.GuardianConsent != true)
        {
            return ApiProblems.BadRequest("GUARDIAN_CONSENT_REQUIRED", "18 yaş altı için veli veya yasal temsilci onayı gerekli.");
        }

        // Geri alınmış eski onay kaydı varsa değiştirilir (oturum başına tek kayıt)
        if (session.Consent is { } old)
        {
            db.ConsentRecords.Remove(old);
            await db.SaveChangesAsync(ct);
        }

        // Anahtarı önceden üretilen yeni varlık gezinti özelliğiyle eklenirse EF onu "Modified" sanar: açıkça Add edilir
        var consent = new ConsentRecord
        {
            OrganizationId = user.OrganizationId, SessionId = session.Id, NoticeVersion = request.NoticeVersion,
            ProcessingConsent = request.ProcessingConsent, ForeignTransferConsent = request.ForeignTransferConsent,
            ExtendedRetentionConsent = request.ExtendedRetentionConsent, GuardianConsent = request.IsMinor ? request.GuardianConsent : null,
            Method = request.Method, RecordedByUserId = user.UserId, RecordedAt = clock.GetUtcNow(),
        };
        db.ConsentRecords.Add(consent);
        session.Consent = consent;
        reader.Touch(session, await reader.GetSettingsAsync(user.OrganizationId, ct));
        audit.Record("Consent.Recorded", nameof(ConsentRecord), consent.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await reader.BuildDetailAsync(session, ct));
    }

    /// <summary>Onayın geri alınması oturumu ve tüm görselleri anında kalıcı olarak siler.</summary>
    private static async Task<IResult> WithdrawConsentAsync(Guid id, AppDbContext db, CurrentUser user, SessionReader reader, SessionPurger purger, AuditTrail audit, CancellationToken ct)
    {
        var session = await reader.LoadAsync(id, ct);
        if (session is null)
        {
            return ApiProblems.NotFound("Prova bulunamadı.");
        }

        audit.Record("Consent.Withdrawn", nameof(TryOnSession), id);
        await db.SaveChangesAsync(ct);
        await purger.PurgeAsync(db, id, "Session.PurgedByConsentWithdrawal", user.UserId, ct);
        return TypedResults.NoContent();
    }

    // ---- Fotoğraf ----

    private static async Task<IResult> UploadPhotoAsync(
        Guid id, [FromForm] PhotoUploadForm form, AppDbContext db, CurrentUser user, SessionReader reader, ImageStore images, ImageLinks links, DisplayStateService displays, AuditTrail audit, CancellationToken ct)
    {
        var session = await reader.LoadAsync(id, ct);
        if (session is null)
        {
            return ApiProblems.NotFound("Prova bulunamadı.");
        }

        if (session.Status != SessionStatus.Open)
        {
            return ApiProblems.Conflict("SESSION_CLOSED", "Bu prova kapatılmış.");
        }

        // Onay olmadan fotoğraf kabul edilmez: kontrol kullanıcı arayüzüne değil sunucuya bağlıdır
        if (session.Consent?.AllowsProcessing != true)
        {
            return ApiProblems.Conflict("CONSENT_REQUIRED", "Fotoğraf yüklemeden önce müşteri onayı alınmalı.");
        }

        if (session.Photos.Count >= MaxPhotosPerSession)
        {
            return ApiProblems.Conflict("PHOTO_LIMIT", "Bu prova için en fazla 5 fotoğraf yüklenebilir. Eskisini silin.");
        }

        SavedImage saved;
        try
        {
            var raw = await UploadReader.ReadAsync(form.File, ImageProcessingOptions.CustomerPhoto.MaxBytes, ct);
            saved = await images.SaveAsync(db, raw, ImageProcessingOptions.CustomerPhoto, user.OrganizationId, FilePurpose.CustomerPhoto, session.RetentionUntil,
                fileId => StorageKeys.SessionFile(user.OrganizationId, session.StoreId, session.Id, fileId, "jpg"), ct);
        }
        catch (ImageRejectedException ex)
        {
            return ApiProblems.FromImage(ex);
        }

        // "Yeniden çek": aynı açının önceki fotoğrafı silinir (veri azaltma); yenisi birincil olur
        var replaced = session.Photos.Where(p => p.Angle == form.Angle).ToList();
        var photo = new SessionPhoto
        {
            OrganizationId = user.OrganizationId, SessionId = session.Id, FileId = saved.File.Id, Angle = form.Angle,
            Width = saved.Image.Width, Height = saved.Image.Height, IsPrimary = true,
            QualityFlags = PhotoQualityEvaluator.Evaluate(saved.Image.Width, saved.Image.Height, saved.Image.Sharpness),
        };
        foreach (var old in replaced)
        {
            db.SessionPhotos.Remove(old);
        }

        db.SessionPhotos.Add(photo);
        reader.Touch(session, await reader.GetSettingsAsync(user.OrganizationId, ct));
        audit.Record("Photo.Uploaded", nameof(SessionPhoto), photo.Id);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await DetachPhotosFromJobsAsync(db, [.. replaced.Select(p => p.Id)], ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            // Dosya depoya yazıldı ama kayıt işlenemedi (ör. oturum eşzamanlı silindi): yetim müşteri fotoğrafı bırakılmaz
            await images.DeleteAsync(saved.File, CancellationToken.None);
            throw;
        }
        if (replaced.Count > 0)
        {
            // Yeniden çekimde eski fotoğraf silinir: TV'de o oturumun görüntüsü kalmasın
            await displays.ResetBySessionAsync(session.Id, DisplayStateReason.PrivacyCleared, detach: false, ct);
        }

        await DeleteFilesAsync(db, images, replaced.Select(p => p.FileId), ct);
        return TypedResults.Created($"/api/v1/sessions/{id}", reader.ToPhotoDto(photo, links.Customer(saved.File)));
    }

    private static async Task<IResult> DeletePhotoAsync(Guid id, Guid photoId, AppDbContext db, SessionReader reader, ImageStore images, DisplayStateService displays, AuditTrail audit, CancellationToken ct)
    {
        var session = await reader.LoadAsync(id, ct);
        var photo = session?.Photos.SingleOrDefault(p => p.Id == photoId);
        if (session is null || photo is null)
        {
            return ApiProblems.NotFound("Fotoğraf bulunamadı.");
        }

        db.SessionPhotos.Remove(photo);
        session.Photos.Remove(photo);
        if (photo.IsPrimary && session.Photos.OrderByDescending(p => p.CreatedAt).FirstOrDefault() is { } next)
        {
            next.IsPrimary = true;
        }

        audit.Record("Photo.Deleted", nameof(SessionPhoto), photoId);
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await DetachPhotosFromJobsAsync(db, [photoId], ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        // Fotoğraf silme müşteri verisinin geri çekilmesidir: o oturumun TV görüntüsü anında temizlenir (oturum bağlı kalır)
        await displays.ResetBySessionAsync(id, DisplayStateReason.PrivacyCleared, detach: false, ct);
        await DeleteFilesAsync(db, images, [photo.FileId], ct);
        return TypedResults.Ok(await reader.BuildDetailAsync(session, ct));
    }

    /// <summary>
    /// Silinecek fotoğraflardan üretilmiş işlerin kaynak bağlantısını NULL'a çeker; iş, sonuç ve maliyet kaydı kalır.
    /// SQL Server bir tabloya tek zincirleme silme yolu kabul ettiği için bu bağlantı veritabanında NO ACTION'dır
    /// (PostgreSQL'de ON DELETE SET NULL idi). Çağıran, fotoğraf silme ile aynı transaction'da çağırır.
    /// </summary>
    private static async Task DetachPhotosFromJobsAsync(AppDbContext db, IReadOnlyCollection<Guid> photoIds, CancellationToken ct)
    {
        if (photoIds.Count == 0)
        {
            return;
        }

        await db.GenerationJobs
            .Where(j => j.SourcePhotoId != null && photoIds.Contains(j.SourcePhotoId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.SourcePhotoId, (Guid?)null), ct);
    }

    private static async Task DeleteFilesAsync(AppDbContext db, ImageStore images, IEnumerable<Guid> fileIds, CancellationToken ct)
    {
        var ids = fileIds.ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var files = await db.StoredFiles.Where(f => ids.Contains(f.Id)).ToListAsync(ct);
        foreach (var file in files)
        {
            await images.DeleteAsync(file, ct);
        }

        db.StoredFiles.RemoveRange(files);
        await db.SaveChangesAsync(ct);
    }

    // ---- Taslak seçim ----

    private static async Task<IResult> SaveDraftAsync(Guid id, DraftRequest request, AppDbContext db, CurrentUser user, SessionReader reader, CancellationToken ct)
    {
        var session = await reader.LoadAsync(id, ct);
        if (session is null)
        {
            return ApiProblems.NotFound("Prova bulunamadı.");
        }

        if (session.Status != SessionStatus.Open)
        {
            return ApiProblems.Conflict("SESSION_CLOSED", "Bu prova kapatılmış.");
        }

        var selected = await ResolveSelectionAsync(db, request.Items, ct);
        if (selected.Problem is { } problem)
        {
            return problem;
        }

        var items = selected.Items;
        if (request.Mode == OutfitMode.OutfitSet && items.Any(i => !CombinationRules.IsSingleTryOnItem(i)))
        {
            return ApiProblems.BadRequest("WRONG_PRODUCT_TYPE", "Bu modda yalnızca hazır takım veya kaftan gibi tek parça tüm vücut ürünü seçilebilir.");
        }

        if (request.Mode == OutfitMode.FreeCombination && items.Any(i => i.Product.ProductType != ProductType.Single))
        {
            return ApiProblems.BadRequest("WRONG_PRODUCT_TYPE", "Serbest kombinde yalnızca bağımsız ürünler seçilebilir.");
        }

        var warnings = CombinationWarnings(request.Mode, items);
        if (warnings.Contains(nameof(CombinationError.TooManyInCategory)) && request.Mode == OutfitMode.FreeCombination)
        {
            return ApiProblems.BadRequest("TOO_MANY_IN_CATEGORY", "Bu kategoriden daha fazla ürün seçilemez.");
        }

        session.Draft = new SessionDraft
        {
            Mode = request.Mode,
            Items = request.Items.Select(i => new DraftItem
            {
                ProductId = i.ProductId, VariantId = i.VariantId, CategoryId = items.Single(x => x.Product.Id == i.ProductId).Category.Id,
            }).ToList(),
        };
        reader.Touch(session, await reader.GetSettingsAsync(user.OrganizationId, ct));
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(SessionReader.ToDraftDto(session.Draft, warnings));
    }

    internal sealed record SelectionResult(List<CombinationRules.SelectedItem> Items, IResult? Problem);

    /// <summary>İstenen ürünleri ve varyantlarını doğrular: aynı firmada, aktif, varyant ürüne ait.</summary>
    internal static async Task<SelectionResult> ResolveSelectionAsync(AppDbContext db, IReadOnlyList<DraftItemRequest> requested, CancellationToken ct)
    {
        var ids = requested.Select(i => i.ProductId).Distinct().ToList();
        if (ids.Count != requested.Count)
        {
            return new SelectionResult([], ApiProblems.BadRequest("DUPLICATE_PRODUCT", "Aynı ürün birden fazla seçilemez."));
        }

        var products = await db.Products.Include(p => p.Category).Include(p => p.Variants).Include(p => p.Images)
            .Where(p => ids.Contains(p.Id) && p.IsActive && p.ArchivedAt == null).AsSplitQuery().ToListAsync(ct);
        if (products.Count != ids.Count)
        {
            return new SelectionResult([], ApiProblems.BadRequest("UNKNOWN_PRODUCT", "Seçilen ürünlerden biri bulunamadı veya pasif."));
        }

        foreach (var item in requested)
        {
            var product = products.Single(p => p.Id == item.ProductId);
            if (item.VariantId is { } variantId && !product.Variants.Any(v => v.Id == variantId && v.IsActive))
            {
                return new SelectionResult([], ApiProblems.BadRequest("UNKNOWN_VARIANT", "Seçilen renk bu ürüne ait değil."));
            }
        }

        return new SelectionResult(products.Select(p => new CombinationRules.SelectedItem(p, p.Category!)).ToList(), null);
    }

    internal static List<string> CombinationWarnings(OutfitMode mode, IReadOnlyCollection<CombinationRules.SelectedItem> items)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var errors = mode == OutfitMode.OutfitSet ? CombinationRules.ValidateOutfitSet(items) : CombinationRules.ValidateFreeCombination(items);
        return errors.Where(e => e != CombinationError.Empty).Select(e => e.ToString()).ToList();
    }

    // ---- Kapatma ve silme ----

    private static async Task<IResult> CloseAsync(Guid id, AppDbContext db, CurrentUser user, SessionReader reader, DisplayStateService displays, AuditTrail audit, TimeProvider clock, CancellationToken ct)
    {
        var session = await reader.LoadAsync(id, ct);
        if (session is null)
        {
            return ApiProblems.NotFound("Prova bulunamadı.");
        }

        if (session.Status == SessionStatus.Open)
        {
            session.Status = SessionStatus.Closed;
            session.ClosedAt = clock.GetUtcNow();
            reader.Touch(session, await reader.GetSettingsAsync(user.OrganizationId, ct));
            audit.Record("Session.Closed", nameof(TryOnSession), id);
            await db.SaveChangesAsync(ct);
        }

        // Kapanan oturum TV'den kalkar: görüntü boşalır, TV serbest kalır, bağlı olan erişim iptal olur
        await displays.ResetBySessionAsync(id, DisplayStateReason.SessionEnded, detach: true, ct);

        return TypedResults.Ok(await reader.BuildDetailAsync(session, ct));
    }

    private static async Task<IResult> DeleteAsync(Guid id, AppDbContext db, CurrentUser user, SessionReader reader, SessionPurger purger, CancellationToken ct)
    {
        var session = await reader.LoadAsync(id, ct);
        if (session is null)
        {
            return ApiProblems.NotFound("Prova bulunamadı.");
        }

        await purger.PurgeAsync(db, id, "Session.PurgedByStaff", user.UserId, ct);
        return TypedResults.NoContent();
    }
}
