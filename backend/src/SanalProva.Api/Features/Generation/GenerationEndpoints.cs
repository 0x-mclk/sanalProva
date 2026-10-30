using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Features.Admin;
using SanalProva.Api.Features.TryOn;
using SanalProva.Api.Hubs;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Catalog;
using SanalProva.Domain.Common;
using SanalProva.Domain.Generation;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Ai.TryOn;
using SanalProva.Infrastructure.Generation;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.Generation;

/// <summary>
/// Üretim işleri. API işi yalnızca kuyruğa yazar (202); asıl iş <see cref="GenerationWorker"/>'dadır.
/// Durum SignalR ile bildirilir, <c>GET /jobs/{id}</c> yoklama yedeğidir.
/// </summary>
public static class GenerationEndpoints
{
    public static RouteGroupBuilder MapGenerationEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("").WithTags("Generation").RequireStore(StoreRole.Staff);
        group.MapPost("/sessions/{id:guid}/generations", StartAsync).Produces<JobDto>(StatusCodes.Status202Accepted);
        group.MapPost("/sessions/{id:guid}/revisions", RevisionAsync).Produces(StatusCodes.Status501NotImplemented);
        group.MapGet("/sessions/{id:guid}/images", ListImagesAsync).Produces<IReadOnlyList<GeneratedImageDto>>();
        group.MapGet("/jobs/{id:guid}", GetJobAsync).Produces<JobDto>();
        group.MapPost("/jobs/{id:guid}/cancel", CancelAsync).Produces<JobDto>();
        group.MapPost("/jobs/{id:guid}/retry", RetryAsync).Produces<JobDto>(StatusCodes.Status202Accepted);
        group.MapPatch("/images/{id:guid}/favorite", FavoriteAsync).Produces<GeneratedImageDto>();
        return api;
    }

    private static IResult PaidAiNotEnabled() =>
        ApiProblems.Forbidden("PAID_AI_NOT_ENABLED", "Bu işletme için gerçek (ücretli) AI henüz etkinleştirilmedi. Yöneticiyle iletişime geçin.");

    private static async Task<IResult> StartAsync(
        Guid id, StartGenerationRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        AppDbContext db, CurrentUser user, SessionReader reader, IJobNotifier notifier, GenerationStrategy strategy, Microsoft.Extensions.Options.IOptionsMonitor<GenerationSettings> generationSettings,
        AuditTrail audit, TimeProvider clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 64)
        {
            return ApiProblems.BadRequest("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key başlığı (en fazla 64 karakter) gerekli.");
        }

        var session = await reader.LoadAsync(id, ct);
        if (session is null)
        {
            return ApiProblems.NotFound("Prova bulunamadı.");
        }

        // Aynı anahtarla tekrar gelen istek yeni iş oluşturmaz
        var existing = await db.GenerationJobs.SingleOrDefaultAsync(j => j.SessionId == id && j.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null)
        {
            // Aynı anahtar farklı bir istekle (başka fotoğraf, model, kalite veya "yeni varyasyon") kullanıldıysa eski işi sessizce döndürmek istemciyi yanıltır: açık hata
            if (KeyReusedWithDifferentRequest(existing, request, live: strategy.Resolve(existing.JobType).Provider != GenerationStrategy.MockProvider))
            {
                return ApiProblems.Unprocessable("IDEMPOTENCY_KEY_REUSED", "Bu Idempotency-Key farklı bir istek için kullanılmış. Yeni istek için yeni anahtar kullanın.");
            }

            // Yeniden oynatma, ilk yanıtla aynı bilgiyi taşır: tamamlanmış işin sonuç görseli dahil (istemci ağ hatasından sonra sonucu bulabilsin)
            var finished = await db.GeneratedImages.AsNoTracking().FirstOrDefaultAsync(i => i.JobId == existing.Id, ct);
            return TypedResults.Accepted($"/api/v1/jobs/{existing.Id}", await reader.ToJobDtoAsync(existing, finished, ct));
        }

        if (session.Status != SessionStatus.Open)
        {
            return ApiProblems.Conflict("SESSION_CLOSED", "Bu prova kapatılmış.");
        }

        if (session.Consent?.AllowsProcessing != true)
        {
            return ApiProblems.Conflict("CONSENT_REQUIRED", "Üretim için müşteri onayı gerekli.");
        }

        var photo = request.PhotoId is { } photoId
            ? session.Photos.SingleOrDefault(p => p.Id == photoId)
            : session.Photos.OrderByDescending(p => p.IsPrimary).ThenByDescending(p => p.CreatedAt).FirstOrDefault();
        if (photo is null)
        {
            return ApiProblems.Conflict("NO_PHOTO", "Önce müşterinin fotoğrafı yüklenmeli.");
        }

        if (session.Draft is not { Items.Count: > 0 } draft)
        {
            return ApiProblems.BadRequest("EMPTY_SELECTION", "Önce kıyafet seçin.");
        }

        var selected = await SessionEndpoints.ResolveSelectionAsync(db, draft.Items.Select(i => new DraftItemRequest(i.ProductId, i.VariantId)).ToList(), ct);
        if (selected.Problem is { } problem)
        {
            return problem;
        }

        var errors = draft.Mode == OutfitMode.OutfitSet ? CombinationRules.ValidateOutfitSet(selected.Items) : CombinationRules.ValidateFreeCombination(selected.Items);
        if (errors.Count > 0)
        {
            return ApiProblems.Create(StatusCodes.Status422UnprocessableEntity, "INVALID_COMBINATION", "Seçilen kombin geçerli değil.",
                new Dictionary<string, object?> { ["errors"] = errors.Select(e => e.ToString()).ToList() });
        }

        foreach (var item in draft.Items)
        {
            if (!selected.Items.Single(s => s.Product.Id == item.ProductId).Product.IsReadyForAi(item.VariantId))
            {
                return ApiProblems.Unprocessable("PRODUCT_NOT_READY", "Seçilen ürünlerden birinin AI referans görseli yok.");
            }
        }

        var jobType = draft.Mode == OutfitMode.OutfitSet ? GenerationJobType.OutfitSet : GenerationJobType.FreeCombination;
        var live = strategy.Resolve(jobType).Provider != GenerationStrategy.MockProvider;
        if (live && !await PaidAiPolicy.IsEnabledForAsync(db, user.OrganizationId, ct))
        {
            // Birinci kapı (kabul): ücretli sağlayıcıya gidecek iş, firma yetkisi yoksa kuyruğa hiç girmez. Mock modda uygulanmaz.
            return PaidAiNotEnabled();
        }

        if (jobType == GenerationJobType.FreeCombination && live && !generationSettings.CurrentValue.LiveFreeCombinationEnabled)
        {
            // Beş parçalı kombin gerçek modelle doğrulanmadı: gerçek modda çalışıyormuş gibi sunulmaz
            return ApiProblems.Conflict("FREE_COMBINATION_DISABLED", "Çok parçalı kombin henüz etkin değil. Tek kaftan veya hazır takım deneyin.");
        }

        string? modelKey = null;
        string? quality = null;
        if (jobType == GenerationJobType.OutfitSet)
        {
            var policy = TryOnPolicyResolver.Resolve(await reader.GetSettingsAsync(user.OrganizationId, ct));
            var (choice, selectedQuality, error) = TryOnPolicyResolver.Select(policy, request.ModelKey, request.Quality);
            if (error is not null)
            {
                return error is TryOnSelectionError.UnknownModel or TryOnSelectionError.ModelNotAllowed
                    ? ApiProblems.BadRequest("MODEL_NOT_ALLOWED", "Seçilen AI modeli kullanıma açık değil.")
                    : ApiProblems.BadRequest("QUALITY_NOT_ALLOWED", "Seçilen kalite bu model için kullanıma açık değil.");
            }

            modelKey = choice!.Model.Key;
            quality = selectedQuality;

            // Yüksek maliyetli kalite iş başı bütçe sınırını aşıyorsa iş kuyruğa hiç alınmaz (işleyicideki bütçe kapısı ikinci savunma hattıdır)
            var (_, worst) = TryOnModels.DisplayPrice(choice.Model, choice.Model.Quality(quality!)!, generationSettings.CurrentValue.Pricing);
            if (live && AiModelEndpoints.ExceedsJobLimit(generationSettings.CurrentValue, await reader.GetSettingsAsync(user.OrganizationId, ct), worst))
            {
                return ApiProblems.Conflict("QUALITY_OVER_BUDGET", "Seçilen kalitenin en kötü durum maliyeti iş başı bütçe sınırını aşıyor.");
            }
        }

        var fingerprint = Fingerprint(photo.Id, jobType, draft.Items, modelKey, quality);

        // Gerçek (ücretli) sağlayıcıda aynı fotoğraf + aynı seçim için süren/başarılı iş varsa yenisi açılmaz: aynı sonuç için ikinci ücret yok.
        // Bilerek yeni varyasyon istenirse veya sağlayıcı mock ise tekrar serbesttir.
        var dedupeKey = live && !request.AllowVariation ? fingerprint : null;

        var selection = new OutfitSelection { OrganizationId = user.OrganizationId, SessionId = id, Mode = draft.Mode, CreatedByUserId = user.UserId };
        foreach (var item in draft.Items)
        {
            var match = selected.Items.Single(s => s.Product.Id == item.ProductId);
            selection.Items.Add(new OutfitSelectionItem
            {
                OrganizationId = user.OrganizationId, ProductId = item.ProductId, VariantId = item.VariantId, CategoryId = match.Category.Id,
                LayerOrder = match.Product.LayerOrderOverride ?? match.Category.LayerOrder,
            });
        }

        var job = NewJob(session, user, jobType, selection.Id, photo.Id, idempotencyKey, clock.GetUtcNow(), fingerprint, modelKey, quality, dedupeKey);
        // Sonucu belirsiz (ücretlenmiş olabilir) aynı istek de "etkin" sayılır: tekrar tıklamak yeni ücretli iş açmaz, o iş ve uyarısı döner
        var admission = await AdmitAsync(db, reader, user, session, job, selection, "Generation.Requested", audit, clock, includeUncertain: true, ct);
        return await RespondAsync(admission, db, reader, notifier, ct);
    }

    private sealed record Admission(GenerationJob? Job, bool Created, IResult? Problem);

    /// <summary>Kabul kilidini bekleme süresi. Kilit yalnızca birkaç sorgu + ekleme boyunca tutulur.</summary>
    private static readonly TimeSpan AdmissionLockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Yeni işi kabul eder. Kota ve "aynı ücretli istek için tek iş" kuralları sayım/sorgu + ekleme biçimindedir; kilitsiz yapılırsa eşzamanlı
    /// istekler aynı boşluğu görür (ölçüldü: kota 3 iken 12 isteğin 11'i, aynı istek için 10 ücretli iş). Bu yüzden kontrol ve ekleme şube başına
    /// uygulama kilidi (sp_getapplock, transaction sahipli) altında tek transaction'da yapılır: aynı şubenin istekleri sırayla kabul edilir,
    /// farklı şubeler birbirini beklemez. Gelecekteki kredi düşümü/rezervasyonu da bu kilit içinde yapılacaktır.
    /// Veritabanı ikinci savunma hattıdır: idempotency anahtarı ve etkin tekil iş (<c>ux_generation_jobs_active_dedupe</c>) benzersiz indekslidir.
    /// </summary>
    private static async Task<Admission> AdmitAsync(
        AppDbContext db, SessionReader reader, CurrentUser user, TryOnSession session, GenerationJob job, OutfitSelection? selection,
        string auditAction, AuditTrail audit, TimeProvider clock, bool includeUncertain, CancellationToken ct)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await AppLocks.AcquireExclusiveAsync(db, AppLocks.StoreAdmission(session.StoreId), AdmissionLockTimeout, ct);

            // Kilit altında yeniden: aynı anahtarlı veya aynı ücretli istek için etkin iş varsa o döner (hiçbir şey yazılmaz)
            if (await FindExistingAsync(db, session.Id, job.IdempotencyKey, job.DedupeKey, includeUncertain, ct) is { } existing)
            {
                return new Admission(existing, Created: false, Problem: null);
            }

            if (await CheckQuotaAsync(db, reader, user, session, clock, ct) is { } quotaProblem)
            {
                return new Admission(null, Created: false, quotaProblem);
            }

            if (selection is not null)
            {
                db.OutfitSelections.Add(selection);
            }

            db.GenerationJobs.Add(job);
            reader.Touch(session, await reader.GetSettingsAsync(user.OrganizationId, ct));
            audit.Record(auditAction, nameof(GenerationJob), job.Id);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new Admission(job, Created: true, Problem: null);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // Kilidi kullanmayan bir yazma yarışı kazandıysa veritabanı ikinci işi reddetti: kazananı döndür
            db.ChangeTracker.Clear();
            var winner = await FindExistingAsync(db, session.Id, job.IdempotencyKey, job.DedupeKey, includeUncertain, ct)
                ?? throw new InvalidOperationException("Benzersizlik ihlali oluştu ama mevcut iş bulunamadı.", ex);
            return new Admission(winner, Created: false, Problem: null);
        }
        catch (Exception ex) when (DbErrors.IsTransientLockFailure(ex))
        {
            // Kilit zaman aşımı / kilitlenme: hiçbir şey yazılmadı, istemci güvenle tekrar deneyebilir (aynı Idempotency-Key ile)
            db.ChangeTracker.Clear();
            return new Admission(null, Created: false,
                ApiProblems.Create(StatusCodes.Status503ServiceUnavailable, "ADMISSION_BUSY", "Şu an çok fazla istek var. Lütfen tekrar deneyin."));
        }
    }

    /// <param name="includeUncertain">Sonucu belirsiz (<see cref="GenerationErrorCode.OutcomeUncertain"/>) iş de etkin sayılsın mı. Yeni istekte evet;
    /// personelin bilinçli "yeniden dene" isteğinde hayır (iletide ikinci ücret olasılığı açıkça yazar).</param>
    private static async Task<GenerationJob?> FindExistingAsync(AppDbContext db, Guid sessionId, string idempotencyKey, string? dedupeKey, bool includeUncertain, CancellationToken ct)
    {
        var sameKey = await db.GenerationJobs.AsNoTracking().SingleOrDefaultAsync(j => j.SessionId == sessionId && j.IdempotencyKey == idempotencyKey, ct);
        if (sameKey is not null || dedupeKey is null)
        {
            return sameKey;
        }

        return await db.GenerationJobs.AsNoTracking()
            .Where(j => j.SessionId == sessionId && j.DedupeKey == dedupeKey
                && (j.Status == GenerationJobStatus.Queued || j.Status == GenerationJobStatus.Running || j.Status == GenerationJobStatus.Succeeded
                    || (includeUncertain && j.Status == GenerationJobStatus.Failed && j.ErrorCode == GenerationErrorCode.OutcomeUncertain)))
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<IResult> RespondAsync(Admission admission, AppDbContext db, SessionReader reader, IJobNotifier notifier, CancellationToken ct)
    {
        if (admission.Problem is { } problem)
        {
            return problem;
        }

        var job = admission.Job!;
        if (admission.Created)
        {
            await notifier.NotifyAsync(new JobUpdate(job.Id, job.SessionId, job.StoreId, job.Status, job.Stage, null, null), ct);
            return TypedResults.Accepted($"/api/v1/jobs/{job.Id}", await reader.ToJobDtoAsync(job, null, ct));
        }

        var image = await db.GeneratedImages.AsNoTracking().FirstOrDefaultAsync(i => i.JobId == job.Id, ct);
        return TypedResults.Accepted($"/api/v1/jobs/{job.Id}", await reader.ToJobDtoAsync(job, image, ct));
    }

    /// <summary>
    /// Anahtarın bağlı olduğu iş ile bu isteğin gövdesi uyuşuyor mu. Yalnızca istekte <b>açıkça verilen</b> alanlar karşılaştırılır (boş = sunucu varsayılanı, her zaman uyumlu).
    /// "Yeni varyasyon" iş satırında yalnızca gerçek modda izlenebilir (tekil anahtarı yok = varyasyon); mock modda varyasyonun anlamı yoktur.
    /// </summary>
    private static bool KeyReusedWithDifferentRequest(GenerationJob existing, StartGenerationRequest request, bool live) =>
        (request.PhotoId is { } photo && existing.SourcePhotoId is { } source && photo != source)
        || (request.ModelKey is { } model && existing.ModelKey is { } jobModel && !string.Equals(model, jobModel, StringComparison.Ordinal))
        || (request.Quality is { } quality && existing.Quality is { } jobQuality && !string.Equals(quality, jobQuality, StringComparison.Ordinal))
        || (live && request.AllowVariation != (existing.DedupeKey is null));

    /// <summary>Fotoğraf + iş tipi + sıralı (ürün, varyant) çiftlerinin özeti. Kişisel veri içermez.</summary>
    private static string Fingerprint(Guid photoId, GenerationJobType type, IEnumerable<DraftItem> items, string? modelKey, string? quality)
    {
        // Model ve kalite de dahil: aynı seçimi farklı modelle/kaliteyle denemek bilinçli yeni bir istektir
        var parts = items.Select(i => $"{i.ProductId:N}:{i.VariantId:N}").Order(StringComparer.Ordinal);
        var text = $"{photoId:N}|{type}|{string.Join(',', parts)}|{modelKey}|{quality}";
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..32];
    }

    private static GenerationJob NewJob(
        TryOnSession session, CurrentUser user, GenerationJobType type, Guid? selectionId, Guid? photoId, string key, DateTimeOffset now,
        string? fingerprint = null, string? modelKey = null, string? quality = null, string? dedupeKey = null) => new()
    {
        RequestFingerprint = fingerprint,
        DedupeKey = dedupeKey,
        ModelKey = modelKey,
        Quality = quality,
        OrganizationId = session.OrganizationId, StoreId = session.StoreId, SessionId = session.Id, JobType = type, SelectionId = selectionId,
        SourcePhotoId = photoId, IdempotencyKey = key, NextAttemptAt = now, RequestedByUserId = user.UserId,
    };

    /// <summary>Mağaza günlük kotası (mağaza saat dilimine göre gün) ve oturum başına üst sınır. Yalnızca kabul kilidi altında çağrılır (<see cref="AdmitAsync"/>).</summary>
    private static async Task<IResult?> CheckQuotaAsync(AppDbContext db, SessionReader reader, CurrentUser user, TryOnSession session, TimeProvider clock, CancellationToken ct)
    {
        var settings = await reader.GetSettingsAsync(user.OrganizationId, ct);
        var store = await db.Stores.AsNoTracking().SingleAsync(s => s.Id == session.StoreId, ct);
        var dailyLimit = store.DailyGenerationQuota ?? settings.DailyGenerationQuota;

        var now = clock.GetUtcNow();
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(store.Timezone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
        }

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var dayStart = new DateTimeOffset(local.Date, zone.GetUtcOffset(local.Date)).ToUniversalTime();

        var today = await db.GenerationJobs.CountAsync(j => j.StoreId == session.StoreId && j.CreatedAt >= dayStart && j.Status != GenerationJobStatus.Cancelled, ct);
        if (today >= dailyLimit)
        {
            return ApiProblems.Create(StatusCodes.Status429TooManyRequests, "QUOTA_EXCEEDED", "Bu şubenin günlük üretim kotası doldu.");
        }

        var inSession = await db.GenerationJobs.CountAsync(j => j.SessionId == session.Id && j.Status != GenerationJobStatus.Cancelled && j.Status != GenerationJobStatus.Failed, ct);
        return inSession >= settings.MaxGenerationsPerSession
            ? ApiProblems.Create(StatusCodes.Status429TooManyRequests, "SESSION_QUOTA_EXCEEDED", "Bu prova için üretim sınırına ulaşıldı.")
            : null;
    }

    private static IResult RevisionAsync(Guid id, [FromBody] RevisionRequest request)
    {
        // Revizyon arayüzü hazır; motor (SAM 2 + OpenCV renk değişimi, Gemini düzenleme) Aşama 5'te uygulanacak.
        // Sahte sonuç üretilmez: istek doğrulanmaz, 501 döner.
        _ = (id, request);
        return ApiProblems.Create(StatusCodes.Status501NotImplemented, "NOT_IMPLEMENTED", "Revizyon motoru henüz etkin değil. Yeni bir kombin oluşturabilirsiniz.");
    }

    private static async Task<IResult> ListImagesAsync(Guid id, AppDbContext db, ImageLinks links, CancellationToken ct)
    {
        if (!await db.TryOnSessions.AnyAsync(s => s.Id == id, ct))
        {
            return ApiProblems.NotFound("Prova bulunamadı.");
        }

        var images = await db.GeneratedImages.AsNoTracking().Where(i => i.SessionId == id).OrderBy(i => i.VersionNo).ToListAsync(ct);
        var files = await ImageLinks.LoadFilesAsync(db, images.Select(i => i.FileId), ct);
        var labels = await SessionReader.ModelLabelsAsync(db, images.Select(i => i.JobId), ct);
        return TypedResults.Ok(images.Where(i => files.ContainsKey(i.FileId)).Select(i => SessionReader.ToImageDto(i, links.Customer(files[i.FileId]), labels.GetValueOrDefault(i.JobId))).ToList());
    }

    private static async Task<IResult> GetJobAsync(Guid id, AppDbContext db, SessionReader reader, CancellationToken ct)
    {
        var job = await db.GenerationJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
        {
            return ApiProblems.NotFound("İş bulunamadı.");
        }

        var image = await db.GeneratedImages.AsNoTracking().FirstOrDefaultAsync(i => i.JobId == id, ct);
        return TypedResults.Ok(await reader.ToJobDtoAsync(job, image, ct));
    }

    /// <summary>Kuyruktaki veya çalışan iş iptal edilir (atomik güncelleme yarışı önler). Çalışan işte worker sağlayıcı bekleyişini bırakır; çağrı başlamışsa sağlayıcı yine de ücretlendirebilir ve bu en kötü durum tahminiyle kaydedilir.</summary>
    private static async Task<IResult> CancelAsync(Guid id, AppDbContext db, SessionReader reader, IJobNotifier notifier, TimeProvider clock, CancellationToken ct)
    {
        var job = await db.GenerationJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
        {
            return ApiProblems.NotFound("İş bulunamadı.");
        }

        var now = clock.GetUtcNow();
        var updated = await db.GenerationJobs.Where(j => j.Id == id && (j.Status == GenerationJobStatus.Queued || j.Status == GenerationJobStatus.Running))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, GenerationJobStatus.Cancelled).SetProperty(j => j.Stage, (GenerationStage?)null)
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null).SetProperty(j => j.FinishedAt, now), ct);
        if (updated == 0)
        {
            return ApiProblems.Conflict("JOB_NOT_CANCELLABLE", "İş zaten işleniyor veya tamamlandı; iptal edilemez.");
        }

        job = await db.GenerationJobs.AsNoTracking().SingleAsync(j => j.Id == id, ct);
        await notifier.NotifyAsync(new JobUpdate(job.Id, job.SessionId, job.StoreId, job.Status, null, null, null), ct);
        return TypedResults.Ok(await reader.ToJobDtoAsync(job, null, ct));
    }

    /// <summary>
    /// Başarısız işi aynı seçim ve fotoğrafla yeni iş olarak yeniden kuyruğa alır. Kota ve tekil ücretli iş kuralı yeni istekle aynıdır (kabul kilidi):
    /// aynı başarısız işe eşzamanlı "yeniden dene" tıklamaları gerçek modda tek ücretli iş açar.
    /// </summary>
    private static async Task<IResult> RetryAsync(
        Guid id, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, AppDbContext db, CurrentUser user, SessionReader reader, IJobNotifier notifier, AuditTrail audit,
        GenerationStrategy strategy, TimeProvider clock, CancellationToken ct)
    {
        // Başlık isteğe bağlıdır (eski istemciler anahtarsız çağırır: her çağrı yeni anahtar alır). Verilirse ağ hatasında aynı anahtarla tekrar göndermek tek yeni iş açar.
        if (idempotencyKey is not null && (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 64))
        {
            return ApiProblems.BadRequest("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key başlığı (en fazla 64 karakter) gerekli.");
        }

        var failed = await db.GenerationJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, ct);
        if (failed is null)
        {
            return ApiProblems.NotFound("İş bulunamadı.");
        }

        if (failed.Status != GenerationJobStatus.Failed)
        {
            return ApiProblems.Conflict("JOB_NOT_FAILED", "Yalnızca başarısız işler yeniden denenebilir.");
        }

        var session = await reader.LoadAsync(failed.SessionId, ct);
        if (session is null || session.Status != SessionStatus.Open)
        {
            return ApiProblems.Conflict("SESSION_CLOSED", "Bu prova kapatılmış.");
        }

        if (failed.SourcePhotoId is null || !session.Photos.Any(p => p.Id == failed.SourcePhotoId))
        {
            return ApiProblems.Conflict("NO_PHOTO", "Fotoğraf silinmiş. Yeni fotoğraf yükleyip tekrar üretin.");
        }

        if (idempotencyKey is not null)
        {
            // Aynı anahtar daha önce bu yeniden denemede kullanıldıysa o iş döner; başka bir işlem (özgün iş veya farklı istek) için kullanıldıysa açık hata
            var used = await db.GenerationJobs.AsNoTracking().SingleOrDefaultAsync(j => j.SessionId == session.Id && j.IdempotencyKey == idempotencyKey, ct);
            if (used is not null)
            {
                if (used.Id == failed.Id || used.RequestFingerprint != failed.RequestFingerprint)
                {
                    return ApiProblems.Unprocessable("IDEMPOTENCY_KEY_REUSED", "Bu Idempotency-Key farklı bir istek için kullanılmış. Yeni istek için yeni anahtar kullanın.");
                }

                return TypedResults.Accepted($"/api/v1/jobs/{used.Id}", await reader.ToJobDtoAsync(used, await db.GeneratedImages.AsNoTracking().FirstOrDefaultAsync(i => i.JobId == used.Id, ct), ct));
            }
        }

        var live = strategy.Resolve(failed.JobType).Provider != GenerationStrategy.MockProvider;
        if (live && !await PaidAiPolicy.IsEnabledForAsync(db, user.OrganizationId, ct))
        {
            return PaidAiNotEnabled();
        }

        var job = NewJob(session, user, failed.JobType, failed.SelectionId, failed.SourcePhotoId, idempotencyKey ?? Guid.NewGuid().ToString("N"), clock.GetUtcNow(),
            failed.RequestFingerprint, failed.ModelKey, failed.Quality, dedupeKey: live ? failed.RequestFingerprint : null);
        // Bilinçli personel kararı: sonucu belirsiz iş yeniden denenebilir (ikinci ücret olasılığı iletide yazar); etkin aynı iş varsa yine o döner
        var admission = await AdmitAsync(db, reader, user, session, job, selection: null, "Generation.Retried", audit, clock, includeUncertain: false, ct);
        return await RespondAsync(admission, db, reader, notifier, ct);
    }

    private static async Task<IResult> FavoriteAsync(Guid id, FavoriteRequest request, AppDbContext db, ImageLinks links, CancellationToken ct)
    {
        var image = await db.GeneratedImages.SingleOrDefaultAsync(i => i.Id == id && db.TryOnSessions.Any(s => s.Id == i.SessionId), ct);
        if (image is null)
        {
            return ApiProblems.NotFound("Görsel bulunamadı.");
        }

        image.IsFavorite = request.IsFavorite;
        await db.SaveChangesAsync(ct);
        var files = await ImageLinks.LoadFilesAsync(db, [image.FileId], ct);
        var labels = await SessionReader.ModelLabelsAsync(db, [image.JobId], ct);
        return TypedResults.Ok(SessionReader.ToImageDto(image, links.Customer(files[image.FileId]), labels.GetValueOrDefault(image.JobId)));
    }
}
