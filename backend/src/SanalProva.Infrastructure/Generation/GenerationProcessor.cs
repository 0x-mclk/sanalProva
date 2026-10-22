using System.Data;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Catalog;
using SanalProva.Domain.Common;
using SanalProva.Domain.Files;
using SanalProva.Domain.Generation;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Ai.Abstractions;
using SanalProva.Infrastructure.Ai.TryOn;
using SanalProva.Infrastructure.Displays;
using SanalProva.Infrastructure.Images;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Storage;
using SanalProva.Infrastructure.Tenancy;

namespace SanalProva.Infrastructure.Generation;

/// <summary>Bir işin durumu değişince istemcilere haber verir (SignalR). Gönderim hatası işi etkilemez.</summary>
public interface IJobNotifier
{
    Task NotifyAsync(JobUpdate update, CancellationToken ct);
}

public sealed record JobUpdate(
    Guid JobId, Guid SessionId, Guid StoreId, GenerationJobStatus Status, GenerationStage? Stage,
    Guid? ResultImageId, GenerationErrorCode? ErrorCode);

public sealed class NullJobNotifier : IJobNotifier
{
    public Task NotifyAsync(JobUpdate update, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Kuyruktan bir işi alıp çalıştırır. Kuyruk PostgreSQL'dedir: <c>FOR UPDATE SKIP LOCKED</c> ile birden fazla worker aynı işi
/// almaz; kilidi (<c>lease_until</c>) dolan işler (çöken worker) yeniden kuyruğa alınır.
/// Sağlayıcı seçimi yalnızca <see cref="GenerationStrategy"/>'dedir.
/// </summary>
public sealed class GenerationProcessor(
    IServiceScopeFactory scopes,
    GenerationStrategy strategy,
    ImageStore images,
    IJobNotifier notifier,
    CostEstimator costs,
    BudgetGuard budget,
    TimeProvider clock,
    IOptionsMonitor<GenerationSettings> settings,
    ILogger<GenerationProcessor> logger)
{
    private const string DemoPromptVersion = "demo.v1";

    /// <returns>Bir iş işlendiyse true, kuyruk boşsa false.</returns>
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await RecoverExpiredLeasesAsync(db, ct);
        var jobId = await ClaimAsync(db, ct);
        if (jobId is null)
        {
            return false;
        }

        var job = await db.GenerationJobs.IgnoreQueryFilters().SingleAsync(j => j.Id == jobId, ct);
        // Bu noktadan sonra sorgular (filtreli) yalnızca işin firmasını görür
        scope.ServiceProvider.GetRequiredService<TenantContext>().OrganizationId = job.OrganizationId;

        await RunAsync(scope.ServiceProvider, db, job, ct);
        return true;
    }

    /// <summary>
    /// Zaten "Running" olarak ayrılmış (claim edilmiş) tek bir işi çalıştırır. Üretim worker'ı kullanmaz; entegrasyon testleri, paralel
    /// çalışan diğer worker'lara karışmadan bir işi belirli sağlayıcı yapılandırmasıyla uçtan uca sınamak için kullanır.
    /// </summary>
    public async Task ProcessClaimedAsync(Guid jobId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.GenerationJobs.IgnoreQueryFilters().SingleAsync(j => j.Id == jobId, ct);
        scope.ServiceProvider.GetRequiredService<TenantContext>().OrganizationId = job.OrganizationId;
        await RunAsync(scope.ServiceProvider, db, job, ct);
    }

    private async Task RecoverExpiredLeasesAsync(AppDbContext db, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var expired = db.GenerationJobs.IgnoreQueryFilters().Where(j => j.Status == GenerationJobStatus.Running && j.LeaseUntil < now);

        await expired.Where(j => j.Attempt < j.MaxAttempts).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, GenerationJobStatus.Queued)
            .SetProperty(j => j.Stage, (GenerationStage?)null)
            .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
            .SetProperty(j => j.NextAttemptAt, now), ct);

        await expired.Where(j => j.Attempt >= j.MaxAttempts).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, GenerationJobStatus.Failed)
            .SetProperty(j => j.ErrorCode, GenerationErrorCode.Timeout)
            .SetProperty(j => j.ErrorMessage, "İşlem zaman aşımına uğradı.")
            .SetProperty(j => j.FinishedAt, now), ct);
    }

    /// <summary>SKIP LOCKED ile en eski bekleyen işi atomik olarak "Running" yapar. EF'in alt sorguya sardığı SqlQuery yerine doğrudan komut.</summary>
    private async Task<Guid?> ClaimAsync(AppDbContext db, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE generation_jobs
                   SET status = 'Running', attempt = attempt + 1, stage = 'Preparing',
                       started_at = COALESCE(started_at, @now), lease_until = @lease, updated_at = @now
                 WHERE id = (SELECT id FROM generation_jobs
                              WHERE status = 'Queued' AND next_attempt_at <= @now
                              ORDER BY next_attempt_at, created_at
                              FOR UPDATE SKIP LOCKED LIMIT 1)
                RETURNING id
                """;
            AddParameter(command, "now", now);
            AddParameter(command, "lease", now.AddSeconds(settings.CurrentValue.LeaseSeconds));
            return await command.ExecuteScalarAsync(ct) as Guid?;
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static void AddParameter(IDbCommand command, string name, DateTimeOffset value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value.UtcDateTime;
        parameter.DbType = DbType.DateTime;
        // Npgsql: timestamptz için UTC DateTime kabul edilir
        if (parameter is Npgsql.NpgsqlParameter npgsql)
        {
            npgsql.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz;
            npgsql.Value = value.UtcDateTime;
        }

        command.Parameters.Add(parameter);
    }

    private async Task RunAsync(IServiceProvider services, AppDbContext db, GenerationJob job, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        ResolvedStrategy? resolved = null;
        ProviderResult? paid = null;
        var worstCase = 0m;
        var callStarted = false;

        // Personel çalışan bir işi iptal ederse sağlayıcı bekleyişi hemen bırakılır
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = WatchForCancellationAsync(job.Id, jobCts);
        try
        {
            await NotifyAsync(job, null, ct);

            var session = await db.TryOnSessions.IgnoreQueryFilters().SingleOrDefaultAsync(s => s.Id == job.SessionId, ct);
            if (session is not { Status: SessionStatus.Open })
            {
                await FailAsync(db, job, GenerationErrorCode.InvalidInput, "Oturum kapalı veya silinmiş.", null, ct);
                return;
            }

            resolved = strategy.Resolve(job.JobType);
            if (job.JobType == GenerationJobType.OutfitSet && resolved.ConfiguredProvider == "Fashn")
            {
                // Yönetici kalite/çözünürlük ayarı (firma düzeyi) yapılandırma varsayılanının üzerine yazılır; maliyet tahmini ve bütçe kapısı da bunu kullanır
                var organizationSettings = (await db.Organizations.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.Id == job.OrganizationId, ct)).Settings;
                resolved = resolved with { Options = resolved.Options with { Parameters = FashnQuality.Apply(resolved.Options.Parameters, organizationSettings) } };
            }

            var isMock = resolved.Provider == GenerationStrategy.MockProvider;

            // Tek ürün giydirme: model ve kalite istek anında sabitlendi (job.ModelKey/Quality); yapılandırma stratejisinin yerine geçer
            var tryOn = job.JobType == GenerationJobType.OutfitSet && TryOnModels.Find(job.ModelKey) is { } selectedModel
                && selectedModel.Quality(job.Quality ?? selectedModel.DefaultQuality) is { } selectedQuality
                ? (Model: selectedModel, Quality: selectedQuality)
                : ((TryOnModel Model, TryOnQuality Quality)?)null;
            if (tryOn is { } t)
            {
                var parameters = new Dictionary<string, string>(t.Quality.Parameters) { ["model_key"] = t.Model.Key, ["tier"] = t.Quality.Key };
                resolved = resolved with
                {
                    Provider = isMock ? GenerationStrategy.MockProvider : t.Model.Provider,
                    ConfiguredProvider = t.Model.Provider,
                    Options = new ProviderRequestOptions(t.Model.ModelId, parameters),
                };
            }

            if (job.JobType == GenerationJobType.FreeCombination && !isMock && !settings.CurrentValue.LiveFreeCombinationEnabled)
            {
                // Çok parçalı kombin gerçek modelle doğrulanmadı: gerçek modda çalışıyormuş gibi gösterilmez (API de reddeder; bu ikinci savunma hattı)
                throw new ProviderException(GenerationErrorCode.ProviderNotConfigured, "Beş parçalı kombin gerçek modda henüz etkin değil.", retryable: false);
            }

            var previousProvider = job.Provider;
            job.Provider = resolved.Provider;
            job.Model = resolved.Options.Model;
            job.PromptTemplateVersion = isMock ? DemoPromptVersion
                : tryOn is { } v ? (v.Model.Provider == "Pruna" ? PromptBuilder.PrunaTryOnVersion : v.Model.Provider == "Fashn" ? PromptBuilder.OutfitSetVersion : PromptBuilder.SingleGarmentVersion)
                : job.JobType == GenerationJobType.FreeCombination ? PromptBuilder.FreeCombinationVersion : PromptBuilder.OutfitSetVersion;
            job.Stage = GenerationStage.Generating;
            await db.SaveChangesAsync(ct);
            await NotifyAsync(job, null, ct);

            var inputs = await LoadInputsAsync(db, job, ct);

            // Önceki denemede sağlayıcıya gönderilmiş ve sonucu alınamamış tahmin varsa yenisi açılmaz (çift ücret yok)
            var resumeId = !isMock && previousProvider == resolved.Provider ? job.ProviderRequestId : null;
            if (tryOn is { } prompted && prompted.Model.Provider is "FalSeedream" or "FalGptImage")
            {
                // Görsel düzenleme modelleri talimatla çalışır: talimat katalog verisinden sunucuda üretilir
                var garment = inputs.Garments[0];
                var parameters = new Dictionary<string, string>(resolved.Options.Parameters)
                {
                    ["prompt"] = PromptBuilder.BuildSingleGarment(garment.Category.Name, garment.Product.Name, garment.ColorName, garment.Product.ProductType == ProductType.OutfitSet),
                };
                resolved = resolved with { Options = resolved.Options with { Parameters = parameters } };
            }

            if (!isMock)
            {
                if (tryOn is null)
                {
                    var capability = ModelCatalog.CheckCapability(resolved.ConfiguredProvider, resolved.Options.Model, job.JobType);
                    if (capability is not null)
                    {
                        throw new ProviderException(GenerationErrorCode.ProviderNotConfigured, capability, retryable: false);
                    }
                }

                worstCase = tryOn is { } priced
                    ? TryOnModels.Price(priced.Model, priced.Quality, settings.CurrentValue.Pricing, ProviderHttp.CostInput(resolved.Options, inputs.Person, inputs.Garments[0].Image)).WorstCase
                    : costs.WorstCasePerCallUsd(resolved.ConfiguredProvider, resolved.Options.Model, resolved.Options.Parameters, inputs.Garments.Count + 1);
                if (resumeId is null)
                {
                    await budget.EnsureAllowedAsync(db, job.OrganizationId, job.Id, worstCase, ct);
                }
            }

            var options = resolved.Options with
            {
                Seed = SeedFor(job.Id),
                ResumeRequestId = resumeId,
                OnSubmitted = async id =>
                {
                    job.ProviderRequestId = id;
                    await db.SaveChangesAsync(CancellationToken.None); // kimlik hemen kalıcı: çökmede aynı tahmin sorgulanır
                },
            };

            callStarted = true;
            var result = await CallProviderAsync(job, resolved, options, inputs, jobCts.Token);
            paid = result;

            job.Stage = GenerationStage.Validating;
            await db.SaveChangesAsync(ct);
            await NotifyAsync(job, null, ct);

            var output = result.Images.Count > 0 ? result.Images[0] : throw new ProviderException(GenerationErrorCode.InvalidOutput, "Sağlayıcı görsel döndürmedi.", retryable: false);
            try
            {
                OutputValidator.Validate(output, inputs.Person);
            }
            catch (ProviderException invalid)
            {
                // Sağlayıcı sonucu üretti (ücret doğdu) ama çıktı kullanılamaz: maliyet kaydedilir, yeniden denenmez
                throw new ProviderException(invalid.Code, invalid.Message, invalid.Retryable) { ChargedUsd = result.Usage.EstimatedCostUsd, ProviderRequestId = result.ProviderRequestId };
            }

            // Sürüm numarası (oturum içinde 1, 2, 3...) paralel worker'larda çakışmasın: oturum başına advisory kilit,
            // sürüm hesabı + sonuç + iş durumu tek işlemde yazılır
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({job.SessionId.ToString("N")}, 0))", ct);

            // İş beklerken iptal edildiyse sonuç atılır; ücret doğmuş olabileceğinden kullanım kaydı yine yazılır
            var currentStatus = await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking().Where(j => j.Id == job.Id).Select(j => j.Status).SingleAsync(ct);
            if (currentStatus == GenerationJobStatus.Cancelled)
            {
                db.AiUsageLogs.Add(UsageLog(job, result, stopwatch.ElapsedMilliseconds, success: true, errorCode: null));
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return;
            }

            var imageId = await SaveResultAsync(db, job, session, inputs, result, output, ct);
            db.AiUsageLogs.Add(UsageLog(job, result, stopwatch.ElapsedMilliseconds, success: true, errorCode: null));
            job.ProviderRequestId = result.ProviderRequestId ?? job.ProviderRequestId;
            job.Status = GenerationJobStatus.Succeeded;
            job.Stage = null;
            job.LeaseUntil = null;
            job.ErrorCode = null;
            job.ErrorMessage = null;
            job.FinishedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            await NotifyAsync(job, imageId, ct);
            await FollowOnDisplayAsync(services, job.SessionId, imageId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Uygulama kapanıyor: iş "Running" kalır, kilit dolunca yeniden kuyruğa alınır (tahmin kimliği saklıdır)
        }
        catch (OperationCanceledException) when (jobCts.IsCancellationRequested)
        {
            // Personel işi iptal etti. Çağrı başlamışsa sağlayıcı yine de ücretlendirebilir: en kötü durum tahmini kaydedilir (bütçe güvenli tarafta kalır)
            if (callStarted && resolved is not null && resolved.Provider != GenerationStrategy.MockProvider)
            {
                db.AiUsageLogs.Add(new AiUsageLog
                {
                    OrganizationId = job.OrganizationId, StoreId = job.StoreId, JobId = job.Id, Provider = resolved.Provider, Model = resolved.Options.Model,
                    Operation = job.JobType.ToString(), Quality = job.Quality, Attempt = job.Attempt, Success = false, DurationMs = (int)stopwatch.ElapsedMilliseconds,
                    EstimatedCostUsd = worstCase, ProviderRequestId = job.ProviderRequestId,
                });
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (ProviderException ex)
        {
            await HandleProviderFailureAsync(db, job, resolved, ex, stopwatch.ElapsedMilliseconds, ct);
        }
        catch (ImageRejectedException ex)
        {
            await FailPaidAsync(db, job, paid, stopwatch.ElapsedMilliseconds, ex, ct);
        }
        catch (Exception ex)
        {
            await FailAsync(db, job, GenerationErrorCode.Unknown, "Beklenmeyen bir hata oluştu.", ex, ct);
        }
        finally
        {
            await jobCts.CancelAsync();
            await watcher;
        }
    }

    private static AiUsageLog UsageLog(GenerationJob job, ProviderResult result, long elapsedMs, bool success, GenerationErrorCode? errorCode) => new()
    {
        OrganizationId = job.OrganizationId, StoreId = job.StoreId, JobId = job.Id,
        Provider = result.Provider, Model = result.Model, Operation = job.JobType.ToString(), Quality = job.Quality, Attempt = job.Attempt,
        Success = success, DurationMs = (int)elapsedMs, CreditsUsed = result.Usage.Credits,
        InputTokens = result.Usage.InputTokens, OutputTokens = result.Usage.OutputTokens,
        EstimatedCostUsd = result.Usage.EstimatedCostUsd, ActualCostUsd = result.Usage.ActualCostUsd,
        ProviderRequestId = result.ProviderRequestId, ErrorCode = errorCode,
    };

    /// <summary>Üretilen çıktı doğrulamadan geçemedi ama sağlayıcı ücreti doğmuştu: maliyet kaydedilir, iş yeniden denenmez (çift ücret olmasın).</summary>
    private async Task FailPaidAsync(AppDbContext db, GenerationJob job, ProviderResult? paid, long elapsedMs, Exception exception, CancellationToken ct)
    {
        if (paid is not null)
        {
            db.AiUsageLogs.Add(UsageLog(job, paid, elapsedMs, success: false, GenerationErrorCode.InvalidOutput));
        }

        await FailAsync(db, job, GenerationErrorCode.InvalidOutput, "Üretilen görsel doğrulanamadı.", exception, ct);
    }

    /// <summary>Aynı iş çalışırken personel iptal ederse bağlı jetonu iptal eder. Sağlayıcı bekleyişi (yoklama/HTTP) bundan haberdar olur.</summary>
    private async Task WatchForCancellationAsync(Guid jobId, CancellationTokenSource target)
    {
        try
        {
            while (!target.IsCancellationRequested)
            {
                await Task.Delay(settings.CurrentValue.CancelPollMs, target.Token);
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var status = await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking().Where(j => j.Id == jobId).Select(j => (GenerationJobStatus?)j.Status).SingleOrDefaultAsync(target.Token);
                if (status is GenerationJobStatus.Cancelled or null)
                {
                    await target.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // iş bitti: gözcü sonlanır
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "İptal gözcüsü hata verdi: {JobId}", jobId);
        }
    }

    /// <summary>Aynı oturumda TV, bu oturumun bir sonucunu tekli gösteriyorsa en yeni sonuca geçer (personel sonucu görmek için yenilemek zorunda kalmaz).</summary>
    private async Task FollowOnDisplayAsync(IServiceProvider services, Guid sessionId, Guid imageId, CancellationToken ct)
    {
        try
        {
            await services.GetRequiredService<DisplayStateService>().FollowLatestResultAsync(sessionId, imageId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TV'ye yeni sonuç yansıtılamadı: {SessionId}", sessionId);
        }
    }

    /// <summary>Aynı işin yeniden denemeleri aynı tohumu kullanır; farklı işler farklı varyasyon üretir.</summary>
    internal static int SeedFor(Guid jobId) => BitConverter.ToInt32(jobId.ToByteArray(), 12) & 0x7FFFFFFF;

    private sealed record Inputs(
        ImageInput Person, SelectionData Selection, IReadOnlyList<(Product Product, Guid? VariantId, Category Category, ImageInput Image, string? ColorName)> Garments);

    private sealed record SelectionData(Guid? SelectionId, OutfitMode? Mode);

    private async Task<Inputs> LoadInputsAsync(AppDbContext db, GenerationJob job, CancellationToken ct)
    {
        var photo = await db.SessionPhotos.SingleAsync(p => p.Id == job.SourcePhotoId, ct);
        var photoFile = await db.StoredFiles.SingleAsync(f => f.Id == photo.FileId, ct);
        var person = new ImageInput(await ReadAsync(photoFile, ct), "image/jpeg");

        var selection = await db.OutfitSelections.Include(s => s.Items).SingleAsync(s => s.Id == job.SelectionId, ct);
        var productIds = selection.Items.Select(i => i.ProductId).ToList();
        var products = await db.Products.Include(p => p.Images).Include(p => p.Variants).Include(p => p.Category)
            .Where(p => productIds.Contains(p.Id)).AsSplitQuery().ToDictionaryAsync(p => p.Id, ct);

        var garments = new List<(Product, Guid?, Category, ImageInput, string?)>();
        foreach (var item in selection.Items.OrderBy(i => i.LayerOrder).ThenBy(i => products[i.ProductId].Category!.SortOrder))
        {
            var product = products[item.ProductId];
            var reference = product.FindAiReference(item.VariantId)
                ?? throw new ProviderException(GenerationErrorCode.InvalidInput, $"'{product.Code}' ürününün AI referans görseli yok.", retryable: false);
            var file = await db.StoredFiles.SingleAsync(f => f.Id == reference.FileId, ct);
            var variant = product.Variants.FirstOrDefault(v => v.Id == item.VariantId);
            garments.Add((product, item.VariantId, product.Category!, new ImageInput(await ReadAsync(file, ct), "image/jpeg"), variant?.ColorName));
        }

        return new Inputs(person, new SelectionData(selection.Id, selection.Mode), garments);
    }

    private async Task<byte[]> ReadAsync(StoredFile file, CancellationToken ct)
    {
        await using var stream = await images.OpenAsync(file, ct)
            ?? throw new ProviderException(GenerationErrorCode.InvalidInput, "Girdi görseli bulunamadı (silinmiş olabilir).", retryable: false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private Task<ProviderResult> CallProviderAsync(GenerationJob job, ResolvedStrategy resolved, ProviderRequestOptions options, Inputs inputs, CancellationToken ct)
    {
        switch (job.JobType)
        {
            case GenerationJobType.OutfitSet:
                return strategy.GetTryOnProvider(resolved).TryOnAsync(inputs.Person, inputs.Garments[0].Image, options, ct);

            case GenerationJobType.FreeCombination:
                var references = inputs.Garments.Select(g => new GarmentReference(
                    g.Image, g.Category.Name, g.Product.Name, g.ColorName, g.Product.LayerOrderOverride ?? g.Category.LayerOrder,
                    string.Join(' ', new[] { g.Category.AiHint, g.Product.AiNotes }.Where(x => !string.IsNullOrWhiteSpace(x))))).ToList();
                return strategy.GetGenerationProvider(resolved).ComposeOutfitAsync(inputs.Person, references, PromptBuilder.BuildFreeCombination(references), options, ct);

            default:
                // Revizyon motorları (renk değişimi, ürün değiştirme) Aşama 5'te
                throw new ProviderException(GenerationErrorCode.NotImplemented, "Bu işlem tipi henüz desteklenmiyor.", retryable: false);
        }
    }

    private async Task<Guid> SaveResultAsync(AppDbContext db, GenerationJob job, TryOnSession session, Inputs inputs, ProviderResult result, GeneratedOutput output, CancellationToken ct)
    {
        var saved = await images.SaveAsync(db, output.Content, ImageProcessingOptions.GeneratedResult, job.OrganizationId, FilePurpose.GeneratedImage, session.RetentionUntil,
            fileId => StorageKeys.SessionFile(job.OrganizationId, job.StoreId, job.SessionId, fileId, "jpg"), ct);

        var nextVersion = (await db.GeneratedImages.Where(i => i.SessionId == job.SessionId).MaxAsync(i => (int?)i.VersionNo, ct) ?? 0) + 1;
        var names = inputs.Garments.Select(g => g.Product.Name).ToList();
        var label = job.JobType == GenerationJobType.OutfitSet
            ? $"{names[0]}{(inputs.Garments[0].ColorName is { } c ? $" — {c}" : string.Empty)}"
            : "Kombin: " + string.Join(" + ", inputs.Garments.Select(g => g.Category.Name));
        var image = new GeneratedImage
        {
            OrganizationId = job.OrganizationId, SessionId = job.SessionId, JobId = job.Id, ParentImageId = job.BaseImageId,
            VersionNo = nextVersion, FileId = saved.File.Id, Label = label.Length > 200 ? label[..200] : label,
            IsDemo = result.Provider == GenerationStrategy.MockProvider,
        };
        db.GeneratedImages.Add(image);
        return image.Id;
    }

    private async Task HandleProviderFailureAsync(AppDbContext db, GenerationJob job, ResolvedStrategy? resolved, ProviderException ex, long elapsedMs, CancellationToken ct)
    {
        // Yalnızca zaman aşımında tahmin kimliği korunur (aynı tahmin sorgulanır); diğer hatalarda yeni deneme yeni tahmin açar (başarısız tahmin ücretsizdir)
        var requestId = ex.ProviderRequestId ?? job.ProviderRequestId;
        if (ex.Code != GenerationErrorCode.Timeout)
        {
            job.ProviderRequestId = null;
        }

        db.AiUsageLogs.Add(new AiUsageLog
        {
            OrganizationId = job.OrganizationId, StoreId = job.StoreId, JobId = job.Id,
            Provider = resolved?.Provider ?? "unknown", Model = resolved?.Options.Model ?? "unknown", Operation = job.JobType.ToString(), Quality = job.Quality,
            Attempt = job.Attempt, Success = false, DurationMs = (int)elapsedMs, ErrorCode = ex.Code,
            EstimatedCostUsd = ex.ChargedUsd, ProviderRequestId = requestId,
        });

        if (ex.Retryable && job.Attempt < job.MaxAttempts)
        {
            var backoff = TimeSpan.FromSeconds(Math.Pow(2, job.Attempt) * settings.CurrentValue.RetryBackoffBaseSeconds);
            job.Status = GenerationJobStatus.Queued;
            job.Stage = null;
            job.LeaseUntil = null;
            job.NextAttemptAt = clock.GetUtcNow() + backoff;
            job.ErrorCode = ex.Code;
            job.ErrorMessage = "Geçici bir hata oluştu, yeniden denenecek.";
            await db.SaveChangesAsync(ct);
            logger.LogWarning(ex, "Üretim işi geçici hata verdi, yeniden denenecek: {JobId} (deneme {Attempt}/{Max})", job.Id, job.Attempt, job.MaxAttempts);
            await NotifyAsync(job, null, ct);
            return;
        }

        await FailAsync(db, job, ex.Code, ex.Retryable ? "Sağlayıcı yanıt vermedi." : ex.Message, ex, ct);
    }

    private async Task FailAsync(AppDbContext db, GenerationJob job, GenerationErrorCode code, string message, Exception? exception, CancellationToken ct)
    {
        if (exception is not null)
        {
            logger.LogError(exception, "Üretim işi başarısız: {JobId} ({Code})", job.Id, code);
        }

        job.Status = GenerationJobStatus.Failed;
        job.Stage = null;
        job.LeaseUntil = null;
        job.ErrorCode = code;
        job.ErrorMessage = message.Length > 500 ? message[..500] : message; // kişisel veri içermez: yalnızca sabit/ProviderException mesajları
        job.FinishedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await NotifyAsync(job, null, ct);
    }

    private async Task NotifyAsync(GenerationJob job, Guid? imageId, CancellationToken ct)
    {
        try
        {
            await notifier.NotifyAsync(new JobUpdate(job.Id, job.SessionId, job.StoreId, job.Status, job.Stage, imageId, job.ErrorCode), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "İş bildirimi gönderilemedi: {JobId}", job.Id);
        }
    }
}

/// <summary>Kuyruğu işleyen arka plan servisi: <see cref="GenerationSettings.MaxParallelJobs"/> kadar eşzamanlı döngü.</summary>
public sealed class GenerationWorker(GenerationProcessor processor, IOptionsMonitor<GenerationSettings> settings, ILogger<GenerationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = Math.Max(1, settings.CurrentValue.MaxParallelJobs);
        await Task.WhenAll(Enumerable.Range(0, loops).Select(_ => LoopAsync(stoppingToken)));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await processor.ProcessNextAsync(ct))
                {
                    await Task.Delay(settings.CurrentValue.PollIntervalMs, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Üretim worker döngüsünde hata.");
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
    }
}
