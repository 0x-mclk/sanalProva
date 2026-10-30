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
using SanalProva.Infrastructure.Credits;
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
/// Kuyruktan bir işi alıp çalıştırır. Kuyruk SQL Server'dadır: <c>UPDLOCK + READPAST</c> ile birden fazla worker aynı işi almaz;
/// kilidi (<c>lease_until</c>) dolan işler (çöken worker) yeniden kuyruğa alınır.
/// Deneme numarası (<c>attempt</c>) çit belirtecidir (iyimser eşzamanlılık): iş başka worker'a geçtiyse eski worker'ın yazmaları reddedilir.
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

    /// <summary>Sonucu yazma adımının geçici kilit hatalarında (kilitlenme, kilit zaman aşımı) en fazla deneme sayısı. Sağlayıcı yeniden çağrılmaz.</summary>
    private const int FinalizeAttempts = 3;

    /// <summary>Oturum sürüm kilidini bekleme süresi. Kilit yalnızca sürüm hesabı + birkaç INSERT boyunca tutulur.</summary>
    private static readonly TimeSpan SessionVersionLockTimeout = TimeSpan.FromSeconds(10);

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

        await RunOwnedAsync(scope.ServiceProvider, db, job, ct);
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
        await RunOwnedAsync(scope.ServiceProvider, db, job, ct);
    }

    /// <summary>
    /// İşi çalıştırır; işin sahipliği kaybedildiyse (kilit süresi doldu, iş yeniden kuyruğa alınıp başka worker'a geçti → deneme numarası
    /// değişti) bu worker'ın yazmaları <see cref="DbUpdateConcurrencyException"/> ile reddedilir. Bu worker sonuç veya maliyet yazmaz;
    /// yeni sahip aynı sağlayıcı tahminini (<c>provider_request_id</c>) sorgular: çift sonuç, çift maliyet kaydı ve çift ücret olmaz.
    /// </summary>
    private async Task RunOwnedAsync(IServiceProvider services, AppDbContext db, GenerationJob job, CancellationToken ct)
    {
        try
        {
            await RunAsync(services, db, job, ct);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or JobOwnershipLostException)
        {
            logger.LogWarning("Üretim işinin sahipliği kaybedildi (kilit doldu veya iş kurtarıldı/iptal edildi): {JobId}, bu worker'ın denemesi {Attempt}", job.Id, job.Attempt);
        }
        finally
        {
            await SettleCreditsAsync(job.Id, ct);
        }
    }

    /// <summary>
    /// İş bu worker'dan çıktığında (başarı, kesin başarısızlık, iptal, belirsiz, sahiplik kaybı) kredi rezervasyonunu işin ŞU ANKİ durumuna göre kapatır (M5.4). Başarı → tüketim,
    /// kesin başarısızlık/iptal → iade, belirsiz/çalışıyor → bloke. Idempotenttir; hata işi bozmaz (süpürücü kendini onarır). Yeni kapsam: işin kendi bağlamı bozulmuş olabilir.
    /// </summary>
    private async Task SettleCreditsAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await new CreditLedgerService(clock).SettleJobAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), jobId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Kredi kapanışı başarısız oldu (süpürücü yeniden deneyecek): {JobId}", jobId);
        }
    }

    /// <summary>Kilit yenileme aralığı: kilit süresinin üçte biri (en az 1 sn). Canlı worker uzun sağlayıcı beklemesinde işini kaybetmez.</summary>
    private TimeSpan LeaseRenewInterval => TimeSpan.FromSeconds(Math.Max(1, settings.CurrentValue.LeaseSeconds / 3.0));

    /// <summary>
    /// Sahiplik koşullu kilit uzatma: iş hâlâ bu denemede ve "Running" ise <c>lease_until</c> uzatılır. Etkilenen satır yoksa sahiplik kaybedilmiştir
    /// (iş kurtarılıp yeniden kuyruğa alındı, başka worker'a geçti veya iptal edildi). Süresi dolan işleri kurtaran güncelleme de aynı satırda
    /// koşullu bir UPDATE'tir: ikisi yarışırsa satır kilidi sayesinde yalnızca biri etkili olur.
    /// </summary>
    private async Task<bool> TryRenewLeaseAsync(Guid jobId, int attempt, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lease = clock.GetUtcNow().AddSeconds(settings.CurrentValue.LeaseSeconds);
        var renewed = await db.GenerationJobs.IgnoreQueryFilters()
            .Where(j => j.Id == jobId && j.Attempt == attempt && j.Status == GenerationJobStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseUntil, lease), ct);
        return renewed == 1;
    }

    internal async Task RecoverExpiredLeasesAsync(AppDbContext db, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var expired = db.GenerationJobs.IgnoreQueryFilters().Where(j => j.Status == GenerationJobStatus.Running && j.LeaseUntil < now);

        try
        {
            await expired.Where(j => j.Attempt < j.MaxAttempts).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, GenerationJobStatus.Queued)
                .SetProperty(j => j.Stage, (GenerationStage?)null)
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.NextAttemptAt, now), ct);

            // Son denemede ücretli gönderim başlamışsa (niyet veya kimlik kayıtlı) iş BİR KEZ çözüm geçişi için kuyruğa alınır: işleyici bilinen
            // kimliği sorgular ya da sonucu belirsiz olarak en kötü durum maliyetiyle kaydeder. Bu geçişte yeni ücretli istek açılamaz (niyet varken
            // gönderim yapılmaz, kesin bitişte deneme hakkı kalmaz). Deneme sınırı bir artırılır (ck_generation_jobs_attempts korunur);
            // OutcomeUncertain kodu "çözüm geçişi verildi" işaretidir (mesaj yalnızca Failed işte gösterilir).
            // Deyimlerin koşulları birbirini dışlar: deyimler arasında kilidi dolan iş, bir öncekinin payına düşeni yanlış sınıfa almaz
            var lastAttempt = expired.Where(j => j.Attempt >= j.MaxAttempts);
            var uncertain = lastAttempt.Where(j => j.ProviderSubmitStartedAt != null || j.ProviderRequestId != null);
            await uncertain.Where(j => j.ErrorCode != GenerationErrorCode.OutcomeUncertain).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, GenerationJobStatus.Queued)
                .SetProperty(j => j.Stage, (GenerationStage?)null)
                .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.MaxAttempts, j => j.MaxAttempts + 1)
                .SetProperty(j => j.ErrorCode, GenerationErrorCode.OutcomeUncertain)
                .SetProperty(j => j.NextAttemptAt, now), ct);

            // Çözüm geçişinde de çökerse: sonuç bilinmiyor (maliyet kaydı yazılamadı, kimlik mutabakat için işte kalır)
            await FailUncertainWithCostAsync(db, now, ct);

            var timedOut = await lastAttempt.Where(j => j.ProviderSubmitStartedAt == null && j.ProviderRequestId == null).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, GenerationJobStatus.Failed)
                .SetProperty(j => j.ErrorCode, GenerationErrorCode.Timeout)
                .SetProperty(j => j.ErrorMessage, "İşlem zaman aşımına uğradı.")
                .SetProperty(j => j.FinishedAt, now), ct);
            if (timedOut > 0)
            {
                // Hiç gönderilmemiş zaman aşımı: kredi hemen serbest (kalan açık rezervasyonlar süpürücüyle de kapanır)
                await new CreditLedgerService(clock).SettleOpenAsync(db, 50, ct);
            }
        }
        catch (Exception ex) when (DbErrors.IsDeadlock(ex))
        {
            // Eşzamanlı worker'lar aynı süresi dolmuş işleri güncellerken kilitlenebilir; deyim geri alındı, sonraki döngü tekrar dener
            logger.LogDebug(ex, "Süresi dolan işleri kurtarma kilitlenmeye takıldı; sonraki döngüde tekrar denenecek.");
        }
    }

    /// <summary>
    /// En eski bekleyen işi tek bir atomik deyimle "Running" yapar ve kimliğini döndürür (EF'in alt sorguya sardığı SqlQuery yerine doğrudan komut).
    /// <list type="bullet">
    /// <item><c>UPDLOCK</c>: okunan aday satır güncelleme kilidiyle tutulur; iki worker aynı satırı alamaz.</item>
    /// <item><c>READPAST</c>: başka worker'ın kilitlediği satır beklenmeden atlanır (PostgreSQL'deki <c>SKIP LOCKED</c>).</item>
    /// <item><c>ROWLOCK</c>: kilit satır düzeyinde kalır, sayfa/tablo kilidine yükselip diğer worker'ları bekletmez.</item>
    /// <item><c>OUTPUT inserted.id</c>: güncellenen satırın kimliği aynı deyimde döner (PostgreSQL'deki <c>RETURNING</c>). Tabloda tetikleyici yoktur
    /// (tetikleyici olsaydı OUTPUT ... INTO gerekirdi).</item>
    /// </list>
    /// Kilitlenme kurbanı olursa deyim geri alınmıştır: iş alınmamış sayılır, sonraki döngü tekrar dener.
    /// </summary>
    /// <summary>Belirsiz maliyet kaydının olay anahtarı: iş başına tek. İşlemci, kurtarma SQL'i ve süpürücü aynı biçimi kullanır (SQL tarafı: <c>CONCAT(N'uncertain:', LOWER(CONVERT(nvarchar(36), id)))</c>).</summary>
    internal static string UncertainEventKey(Guid jobId) => $"uncertain:{jobId}";

    internal const string UncertainMessage = "Sağlayıcıdaki işlemin sonucu bilinmiyor; çift ücret olmaması için otomatik yeniden denenmedi.";

    /// <summary>
    /// Çözüm geçişi verilmiş ve yine kilidi dolmuş (ikinci çökme) işi <c>Failed/OutcomeUncertain</c> yapar <b>ve aynı transaction'da</b> en kötü durum
    /// maliyet satırını yazar: iş "belirsiz" ama bütçe harcamayı görmüyor durumu oluşamaz. Tek T-SQL toplu işi: <c>UPDATE … OUTPUT INTO</c> ile taşınan
    /// satırlar tablo değişkenine alınır, ardından <c>INSERT … SELECT</c> yapılır; <c>XACT_ABORT</c> hatada hepsini geri alır.
    /// Tekrar çalıştırılabilir: iş artık <c>Running</c> olmadığı için ikinci çalıştırma satır bulmaz; olay anahtarı filtreli benzersiz indeksle korunur.
    /// Tutar <c>reserved_cost_usd</c>'dir (niyetle birlikte yazıldı); eski kayıtlarda boşsa iş başı bütçe sınırı (güvenli üst sınır) kullanılır.
    /// </summary>
    private async Task FailUncertainWithCostAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var fallback = settings.CurrentValue.Budget.MaxJobCostUsd;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @moved TABLE (id uniqueidentifier, organization_id uniqueidentifier, store_id uniqueidentifier, job_type nvarchar(60),
                provider nvarchar(40), model nvarchar(80), quality nvarchar(20), attempt int, provider_request_id nvarchar(120), reserved decimal(10,4));
            UPDATE generation_jobs
               SET status = N'Failed', error_code = N'OutcomeUncertain', error_message = {UncertainMessage}, lease_until = NULL, finished_at = {now}, updated_at = {now}
            OUTPUT inserted.id, inserted.organization_id, inserted.store_id, inserted.job_type, inserted.provider, inserted.model, inserted.quality,
                   inserted.attempt, inserted.provider_request_id, COALESCE(inserted.reserved_cost_usd, {fallback})
              INTO @moved
             WHERE status = N'Running' AND lease_until < {now} AND attempt >= max_attempts AND error_code = N'OutcomeUncertain'
               AND (provider_submit_started_at IS NOT NULL OR provider_request_id IS NOT NULL);
            INSERT INTO ai_usage_logs (id, created_at, updated_at, organization_id, store_id, job_id, provider, model, operation, quality, attempt, success,
                                       duration_ms, estimated_cost_usd, provider_request_id, error_code, event_key, is_adjustment, reconciliation_state)
            SELECT NEWID(), {now}, {now}, m.organization_id, m.store_id, m.id, COALESCE(m.provider, N'unknown'), COALESCE(m.model, N'unknown'), m.job_type, m.quality,
                   m.attempt, 0, 0, m.reserved, m.provider_request_id, N'OutcomeUncertain', CONCAT(N'uncertain:', LOWER(CONVERT(nvarchar(36), m.id))), 0, N'Open'
              FROM @moved AS m
             WHERE NOT EXISTS (SELECT 1 FROM ai_usage_logs AS u WHERE u.event_key = CONCAT(N'uncertain:', LOWER(CONVERT(nvarchar(36), m.id))));
            COMMIT TRANSACTION;
            """, ct);
    }

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
                WITH next_job AS (
                    SELECT TOP (1) id, status, attempt, stage, started_at, lease_until, updated_at, reserved_cost_usd, provider_request_id, provider_submit_started_at
                      FROM generation_jobs WITH (UPDLOCK, READPAST, ROWLOCK)
                     WHERE status = N'Queued' AND next_attempt_at <= @now
                     ORDER BY next_attempt_at, created_at)
                UPDATE next_job
                   SET status = N'Running', attempt = attempt + 1, stage = N'Preparing',
                       -- önceki denemenin rezervasyonu: sağlayıcıda bekleyen istek (kimlik/niyet) yoksa serbest; yeni deneme kendi rezervasyonunu yapar
                       reserved_cost_usd = CASE WHEN provider_request_id IS NULL AND provider_submit_started_at IS NULL THEN NULL ELSE reserved_cost_usd END,
                       started_at = COALESCE(started_at, @now), lease_until = @lease, updated_at = @now
                OUTPUT inserted.id;
                """;
            AddParameter(command, "now", now);
            AddParameter(command, "lease", now.AddSeconds(settings.CurrentValue.LeaseSeconds));
            return await command.ExecuteScalarAsync(ct) as Guid?;
        }
        catch (Exception ex) when (DbErrors.IsDeadlock(ex))
        {
            logger.LogDebug(ex, "İş alma deyimi kilitlenmeye takıldı; sonraki döngüde tekrar denenecek.");
            return null;
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
        parameter.DbType = DbType.DateTimeOffset; // sütunlar datetimeoffset
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private async Task RunAsync(IServiceProvider services, AppDbContext db, GenerationJob job, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        ResolvedStrategy? resolved = null;
        ProviderResult? paid = null;
        var worstCase = 0m;
        var callStarted = false;

        // Personel çalışan bir işi iptal ederse veya bu worker işin sahipliğini kaybederse sağlayıcı bekleyişi hemen bırakılır
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ownership = new OwnershipState();
        var watcher = WatchJobAsync(job.Id, job.Attempt, jobCts, ownership);
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

                // Önceki denemede ücretli gönderim başlamış ama kimlik kaydedilememiş (yanıt kayboldu, süreç çöktü): istek sağlayıcıda işlenmiş
                // olabilir. Sorgulanacak kimlik yok ve sağlayıcılar idempotency anahtarı belgelemiyor: yeniden gönderilmez, sonuç belirsizdir
                if (resumeId is null && job.ProviderSubmitStartedAt is not null)
                {
                    await MarkOutcomeUncertainAsync(db, job, resolved, worstCase, stopwatch.ElapsedMilliseconds, null, ct);
                    return;
                }

                if (resumeId is null)
                {
                    // Atomik bütçe kapısı + rezervasyon (firma başına kilit, tek transaction): paralel worker/örnekler aynı bütçeyi kontrolsüz tüketemez
                    await budget.ReserveAsync(db, job, worstCase, ct);
                }
            }

            var options = resolved.Options with
            {
                Seed = SeedFor(job.Id),
                ResumeRequestId = resumeId,
                OnSubmitting = async () =>
                {
                    // Önce yaz, sonra gönder: niyet kalıcı olmadan ücretli istek gönderilmez. Kayıt çit belirteciyle korunur
                    // (iş elden çıktıysa DbUpdateConcurrencyException: gönderim yapılmaz)
                    job.ProviderSubmitStartedAt = clock.GetUtcNow();
                    job.ReservedCostUsd = worstCase; // tutar niyetle aynı kayıtta: çökme/kurtarma sırasında SQL tarafı da bu tutarı bilir
                    job.ProviderRequestId = null;
                    await db.SaveChangesAsync(CancellationToken.None);
                },
                OnSubmitted = async id =>
                {
                    job.ProviderRequestId = id;
                    try
                    {
                        await db.SaveChangesAsync(CancellationToken.None); // kimlik hemen kalıcı: çökmede aynı tahmin sorgulanır
                    }
                    catch (Exception ex) when (ex is not DbUpdateConcurrencyException and not OperationCanceledException)
                    {
                        // Kimlik yazılamadı (ör. veritabanı kesintisi). Bellekte tutulur, sonuç kaydıyla birlikte yazılır; bu sırada süreç çökerse
                        // gönderim niyeti kayıtlı olduğu için iş yeniden gönderilmez (sonucu belirsiz olur). Mutabakat için günlüğe yazılır
                        // (sağlayıcı istek kimliği kişisel veri değildir)
                        logger.LogWarning(ex, "Sağlayıcı istek kimliği kaydedilemedi: {JobId} → {ProviderRequestId}", job.Id, id);
                    }
                },
            };

            // Gönderim kapısı: ücretli çağrıdan hemen önce sahiplik doğrulanır ve kilit uzatılır. Kilit dolup iş kurtarıldıysa, başka worker'a
            // geçtiyse veya iptal edildiyse sağlayıcı hiç çağrılmaz (aynı iş için ikinci ücretli istek açılmaz)
            if (!await TryRenewLeaseAsync(job.Id, job.Attempt, ct))
            {
                throw new JobOwnershipLostException("İş, sağlayıcı çağrısından önce bu worker'ın elinden çıktı.");
            }

            callStarted = true;
            var result = await CallProviderAsync(job, resolved, options, inputs, jobCts.Token);
            paid = result;

            if (await JobGoneAsync(db, job.Id))
            {
                // Sağlayıcı yanıtlarken oturum KVKK silmesiyle silindi: sonuç atılır (müşteri verisi geri doğmaz), ücret maliyet kaydına işe bağlanmadan yazılır
                db.AiUsageLogs.Add(UsageLog(job, result, stopwatch.ElapsedMilliseconds, success: true, errorCode: null));
                DetachDeletedJob(db, job);
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }

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

            var imageId = await FinalizeAsync(db, job, session, inputs, result, output, stopwatch, ct);
            if (imageId is { } created)
            {
                await NotifyAsync(job, created, ct);
                await FollowOnDisplayAsync(services, job.SessionId, created, ct);
            }
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or JobOwnershipLostException)
        {
            throw; // sahiplik kaybı: hiçbir şey yazılmaz (RunOwnedAsync)
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Uygulama kapanıyor: iş "Running" kalır, kilit dolunca yeniden kuyruğa alınır (tahmin kimliği saklıdır).
            // SqlClient iptali OperationCanceledException değil SqlException ("işlem kullanıcı tarafından iptal edildi") olarak bildirir:
            // kapanırken gelen her hata kesinti sayılır, iş "başarısız" yazılmaz.
            throw;
        }
        catch (OperationCanceledException ex) when (jobCts.IsCancellationRequested && ownership.Lost)
        {
            // Kilit yenilenemedi (iş kurtarıldı / başka worker'a geçti): bekleyiş bırakıldı. Tahmin kimliği kayıtlıysa yeni sahip aynı tahmini
            // sorgular; bu worker maliyet veya sonuç yazmaz (yazarsa iki kez sayılırdı)
            throw new JobOwnershipLostException("Sağlayıcı beklenirken işin sahipliği kaybedildi.", ex);
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
                if (await JobGoneAsync(db, job.Id))
                {
                    DetachDeletedJob(db, job); // oturum silindi: iş de gitti; maliyet kaydı işe bağlanmadan yazılır
                }

                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (ProviderException ex)
        {
            await HandleProviderFailureAsync(db, job, resolved, ex, worstCase, stopwatch.ElapsedMilliseconds, ct);
        }
        catch (ImageRejectedException ex)
        {
            await FailPaidAsync(db, job, paid, stopwatch.ElapsedMilliseconds, ex, ct);
        }
        catch (Exception ex)
        {
            // Ücretli çağrı başladı ama sonuç alınamadan beklenmeyen hata: sağlayıcıdaki işlem sürüyor/ücretlenmiş olabilir → sonucu belirsiz
            if (callStarted && paid is null && resolved is not null && resolved.Provider != GenerationStrategy.MockProvider)
            {
                await MarkOutcomeUncertainAsync(db, job, resolved, worstCase, stopwatch.ElapsedMilliseconds, ex, ct);
                return;
            }

            // Sağlayıcı sonucu üretti (ücret doğdu) ama kaydedilemediyse maliyet yine yazılır: bütçe kapısı gerçek harcamayı görür
            if (paid is not null)
            {
                db.AiUsageLogs.Add(UsageLog(job, paid, stopwatch.ElapsedMilliseconds, success: false, GenerationErrorCode.Unknown));
            }

            await FailAsync(db, job, GenerationErrorCode.Unknown, "Beklenmeyen bir hata oluştu.", ex, ct);
        }
        finally
        {
            await jobCts.CancelAsync();
            await watcher;
        }
    }

    /// <summary>
    /// Sağlayıcı sonucunu kaydeder: sürüm numarası (oturum içinde 1, 2, 3…) + sonuç görseli + maliyet kaydı + iş durumu tek transaction'da.
    /// Sürüm numarası paralel worker'larda çakışmasın diye oturum başına uygulama kilidi (<see cref="AppLocks"/>) alınır.
    /// Kilitlenme / kilit zaman aşımında transaction geri alınır ve yalnızca bu adım yeniden denenir: sağlayıcı tekrar çağrılmaz (sonuç bellekte,
    /// ücret bir kez). Görsel dosyası depoya bir kez yazılır.
    /// </summary>
    /// <returns>Oluşan sonuç görselinin kimliği; iş beklerken iptal edildiyse null (sonuç atılır, maliyet yazılır).</returns>
    private async Task<Guid?> FinalizeAsync(
        AppDbContext db, GenerationJob job, TryOnSession session, Inputs inputs, ProviderResult result, GeneratedOutput output, Stopwatch stopwatch, CancellationToken ct)
    {
        SavedImage? saved = null;
        for (var attempt = 1; ; attempt++)
        {
            GeneratedImage? image = null;
            AiUsageLog? usage = null;
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await AppLocks.AcquireExclusiveAsync(db, AppLocks.SessionVersion(job.SessionId), SessionVersionLockTimeout, ct);

                usage = UsageLog(job, result, stopwatch.ElapsedMilliseconds, success: true, errorCode: null);
                db.AiUsageLogs.Add(usage);

                // İş beklerken iptal edildiyse sonuç atılır; ücret doğmuş olabileceğinden kullanım kaydı yine yazılır
                var currentStatus = await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking().Where(j => j.Id == job.Id).Select(j => (GenerationJobStatus?)j.Status).SingleOrDefaultAsync(ct);
                if (currentStatus is null)
                {
                    // Oturum KVKK silmesiyle (iş dahil) silindi: sonuç dosyası hiç yazılmaz, müşteri verisi geri doğmaz; ücret doğmuş olabilir: maliyet işe bağlanmadan kaydedilir
                    usage.JobId = null;
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    DetachDeletedJob(db, job);
                    return null;
                }

                if (currentStatus == GenerationJobStatus.Cancelled)
                {
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return null;
                }

                saved ??= await images.SaveAsync(db, output.Content, ImageProcessingOptions.GeneratedResult, job.OrganizationId, FilePurpose.GeneratedImage, session.RetentionUntil,
                    fileId => StorageKeys.SessionFile(job.OrganizationId, job.StoreId, job.SessionId, fileId, "jpg"), ct);
                image = await CreateResultImageAsync(db, job, inputs, result, saved.File, ct);
                db.GeneratedImages.Add(image);

                job.ProviderRequestId = result.ProviderRequestId ?? job.ProviderRequestId;
                job.Status = GenerationJobStatus.Succeeded;
                job.Stage = null;
                job.LeaseUntil = null;
                job.ErrorCode = null;
                job.ErrorMessage = null;
                job.FinishedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct); // iş satırı deneme numarası koşuluyla güncellenir (çit belirteci)
                await transaction.CommitAsync(ct);
                return image.Id;
            }
            catch (Exception ex)
            {
                // Bu denemenin eklediği kayıtlar izlemeden çıkarılır: yeniden denemede veya hata kaydında iki kez yazılmasın
                Detach(db, image);
                Detach(db, usage);
                if (attempt < FinalizeAttempts && DbErrors.IsTransientLockFailure(ex))
                {
                    logger.LogWarning(ex, "Sonuç kaydı geçici kilit hatasına takıldı, yeniden deneniyor: {JobId} ({Attempt}/{Max})", job.Id, attempt, FinalizeAttempts);
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct);
                    continue;
                }

                // Dosya depoya yazıldı ama kayıt işlenemedi (ör. oturum az önce silindi → yabancı anahtar ihlali): yetim müşteri verisi bırakılmaz
                if (saved is not null)
                {
                    try
                    {
                        await images.DeleteAsync(saved.File, CancellationToken.None);
                    }
                    catch (Exception cleanup)
                    {
                        logger.LogError(cleanup, "Yazılamayan sonuç dosyası silinemedi (yetim dosya süpürücüsü temizler): {JobId}", job.Id);
                    }
                }

                throw;
            }
        }
    }

    private static void Detach(AppDbContext db, object? entity)
    {
        if (entity is not null)
        {
            db.Entry(entity).State = EntityState.Detached;
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

    private sealed class OwnershipState
    {
        public bool Lost { get; set; }
    }

    /// <summary>
    /// İş çalışırken (1) personel iptal ederse ve (2) kilit yenilenemezse (sahiplik kaybı) bağlı jetonu iptal eder; sağlayıcı bekleyişi (yoklama/HTTP)
    /// bundan haberdar olur. Kilit, süresinin üçte birinde bir sahiplik koşuluyla yenilenir (heartbeat): canlı worker'ın işi başkasına geçmez.
    /// </summary>
    private async Task WatchJobAsync(Guid jobId, int attempt, CancellationTokenSource target, OwnershipState ownership)
    {
        try
        {
            var lastRenewal = clock.GetUtcNow();
            while (!target.IsCancellationRequested)
            {
                await Task.Delay(settings.CurrentValue.CancelPollMs, target.Token);
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var status = await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking().Where(j => j.Id == jobId).Select(j => (GenerationJobStatus?)j.Status).SingleOrDefaultAsync(target.Token);
                if (status is GenerationJobStatus.Cancelled or null)
                {
                    await target.CancelAsync();
                    return;
                }

                if (clock.GetUtcNow() - lastRenewal < LeaseRenewInterval)
                {
                    continue;
                }

                if (await TryRenewLeaseAsync(jobId, attempt, target.Token))
                {
                    lastRenewal = clock.GetUtcNow();
                    continue;
                }

                // Yenileme olmadı: iptal mi (personel), yoksa sahiplik kaybı mı? İptalse en kötü durum maliyeti yine bu worker yazar
                var now = await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking().Where(j => j.Id == jobId).Select(j => (GenerationJobStatus?)j.Status).SingleOrDefaultAsync(target.Token);
                ownership.Lost = now is not (GenerationJobStatus.Cancelled or null);
                await target.CancelAsync();
                return;
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

    /// <summary>Sonuç görseli kaydını oluşturur. Sürüm numarası okunurken oturum sürüm kilidi tutuluyor olmalıdır (<see cref="FinalizeAsync"/>).</summary>
    private static async Task<GeneratedImage> CreateResultImageAsync(AppDbContext db, GenerationJob job, Inputs inputs, ProviderResult result, StoredFile file, CancellationToken ct)
    {
        var nextVersion = (await db.GeneratedImages.Where(i => i.SessionId == job.SessionId).MaxAsync(i => (int?)i.VersionNo, ct) ?? 0) + 1;
        var names = inputs.Garments.Select(g => g.Product.Name).ToList();
        var label = job.JobType == GenerationJobType.OutfitSet
            ? $"{names[0]}{(inputs.Garments[0].ColorName is { } c ? $" — {c}" : string.Empty)}"
            : "Kombin: " + string.Join(" + ", inputs.Garments.Select(g => g.Category.Name));
        return new GeneratedImage
        {
            OrganizationId = job.OrganizationId, SessionId = job.SessionId, JobId = job.Id, ParentImageId = job.BaseImageId,
            VersionNo = nextVersion, FileId = file.Id, Label = label.Length > 200 ? label[..200] : label,
            IsDemo = result.Provider == GenerationStrategy.MockProvider,
        };
    }

    /// <summary>
    /// Sağlayıcı hatası üç sınıfa ayrılır:
    /// <list type="number">
    /// <item><b>Sonucu bilinmeyen gönderim</b> (<see cref="ProviderException.SubmissionUncertain"/>): istek kabul edilmiş olabilir, kimlik yok →
    /// yeniden gönderilmez, <see cref="GenerationErrorCode.OutcomeUncertain"/>.</item>
    /// <item><b>Kesin sonuç</b>: istek hiç kabul edilmedi (kimlik yok, yanıtlı ret) veya kimlikli istek sağlayıcıda kesin bitti
    /// (<see cref="ProviderException.RequestEnded"/>) → gönderim niyeti ve kimlik temizlenir; yeniden deneme yeni istek açabilir.</item>
    /// <item><b>Kimlikli istek sürüyor olabilir</b> (yoklamada zaman aşımı, ağ hatası, 5xx) → kimlik korunur, sonraki deneme aynı isteği sorgular.
    /// Deneme hakkı biterse sonuç bilinmediği için <see cref="GenerationErrorCode.OutcomeUncertain"/>.</item>
    /// </list>
    /// </summary>
    private async Task HandleProviderFailureAsync(
        AppDbContext db, GenerationJob job, ResolvedStrategy? resolved, ProviderException ex, decimal worstCase, long elapsedMs, CancellationToken ct)
    {
        if (ex.SubmissionUncertain)
        {
            await MarkOutcomeUncertainAsync(db, job, resolved, worstCase, elapsedMs, ex, ct);
            return;
        }

        var requestId = ex.ProviderRequestId ?? job.ProviderRequestId;
        if (ex.RequestEnded || job.ProviderRequestId is null)
        {
            // Sağlayıcıda süren istek yok: yeni gönderim güvenli
            job.ProviderRequestId = null;
            job.ProviderSubmitStartedAt = null;
            job.ReservedCostUsd = null; // kesin sonuç (ücret yok/yok sayıldı): rezervasyon serbest; maliyet kaydı (varsa) tutarı taşır
        }

        if (job.ProviderRequestId is not null && ex.Retryable && job.Attempt >= job.MaxAttempts)
        {
            // Deneme hakkı bitti ama kimliği bilinen istek sağlayıcıda hâlâ sürüyor/tamamlanmış olabilir: kesin başarısız değil, sonucu belirsiz
            await MarkOutcomeUncertainAsync(db, job, resolved, worstCase, elapsedMs, ex, ct);
            return;
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
            if (!await SaveOrKeepCostAsync(db, job, ct))
            {
                return;
            }

            logger.LogWarning(ex, "Üretim işi geçici hata verdi, yeniden denenecek: {JobId} (deneme {Attempt}/{Max})", job.Id, job.Attempt, job.MaxAttempts);
            await NotifyAsync(job, null, ct);
            return;
        }

        await FailAsync(db, job, ex.Code, ex.Retryable ? "Sağlayıcı yanıt vermedi." : ex.Message, ex, ct);
    }

    /// <summary>
    /// Sağlayıcıdaki işlemin sonucu bilinmiyor: gerçekleşmiş ve ücretlendirilmiş olabilir. Sağlayıcılar gönderim için idempotency anahtarı
    /// belgelemediğinden "tam olarak bir kez" garanti edilemez; bu yüzden otomatik yeniden gönderim YAPILMAZ (çift ücret riski) ve maliyet
    /// güvenli tarafta, en kötü durum tutarıyla kaydedilir (<see cref="GenerationJob.ReservedCostUsd"/>; bütçe bunu harcanmış sayar).
    /// Kayıt <c>uncertain:{jobId}</c> olay anahtarıyla tekildir ve mutabakat durumu <see cref="CostReconciliationState.Open"/> olarak açılır:
    /// platform yöneticisi sonucu sağlayıcı panelinden doğrulayıp karar verene kadar maliyet platformun üzerindedir (müşteri kredisi tüketilmez).
    /// İş "Failed / OutcomeUncertain" olur: aynı istek yeniden gönderildiğinde bu iş döner; personel bilinçli olarak "yeniden dene" diyebilir.
    /// Kimlik biliniyorsa (<see cref="GenerationJob.ProviderRequestId"/>) mutabakat için saklanır.
    /// </summary>
    private async Task MarkOutcomeUncertainAsync(
        AppDbContext db, GenerationJob job, ResolvedStrategy? resolved, decimal worstCase, long elapsedMs, Exception? exception, CancellationToken ct)
    {
        var key = UncertainEventKey(job.Id);
        var log = new AiUsageLog
        {
            OrganizationId = job.OrganizationId, StoreId = job.StoreId, JobId = job.Id,
            Provider = resolved?.Provider ?? job.Provider ?? "unknown", Model = resolved?.Options.Model ?? job.Model ?? "unknown",
            Operation = job.JobType.ToString(), Quality = job.Quality, Attempt = job.Attempt, Success = false, DurationMs = (int)elapsedMs,
            ErrorCode = GenerationErrorCode.OutcomeUncertain, EstimatedCostUsd = job.ReservedCostUsd ?? worstCase, ProviderRequestId = job.ProviderRequestId,
            EventKey = key, ReconciliationState = CostReconciliationState.Open,
        };

        // Tekrar çalıştırılabilir: kayıt zaten varsa (kurtarma SQL'i veya süpürücü yazmış) ikinci kayıt eklenmez
        var alreadyRecorded = await db.AiUsageLogs.IgnoreQueryFilters().AnyAsync(l => l.EventKey == key, CancellationToken.None);
        if (!alreadyRecorded)
        {
            db.AiUsageLogs.Add(log);
        }

        logger.LogWarning(exception, "Sağlayıcıdaki işlemin sonucu belirsiz; otomatik yeniden gönderilmedi: {JobId} (istek kimliği {ProviderRequestId})", job.Id, job.ProviderRequestId ?? "yok");
        try
        {
            await FailAsync(db, job, GenerationErrorCode.OutcomeUncertain, UncertainMessage, null, ct);
        }
        catch (DbUpdateException ex) when (!alreadyRecorded && DbErrors.IsUniqueViolation(ex))
        {
            // Denetim ile yazma arasında başka bir yol (kurtarma SQL'i) aynı kaydı yazdı: benzersiz indeks ikincisini reddetti, hepsi geri alındı.
            // Kayıt zaten var; yalnızca iş durumu yazılır.
            db.Entry(log).State = EntityState.Detached;
            await FailAsync(db, job, GenerationErrorCode.OutcomeUncertain, UncertainMessage, null, ct);
        }
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
        if (!await SaveOrKeepCostAsync(db, job, ct))
        {
            return; // iş (oturumuyla birlikte) silinmiş: yalnızca maliyet kaydı yazıldı
        }

        await NotifyAsync(job, null, ct);
    }

    /// <summary>
    /// İş satırını (ve bekleyen maliyet kayıtlarını) kaydeder. Oturum KVKK nedeniyle silindiyse iş de silinmiştir (zincirleme): iş güncellemesi
    /// <see cref="DbUpdateConcurrencyException"/> ile patlar ve aynı kayıttaki maliyet kaydı da kaybolurdu (ücret doğmuş olabilir). Bu durumda iş izlemeden çıkarılır,
    /// bekleyen maliyet kayıtlarının iş bağlantısı koparılır (muhasebe kaydı kişisel veri içermez, kalır) ve yalnızca onlar yazılır.
    /// </summary>
    /// <returns>İş hâlâ varsa true; silinmişse false.</returns>
    private static async Task<bool> SaveOrKeepCostAsync(AppDbContext db, GenerationJob job, CancellationToken ct)
    {
        if (!await JobGoneAsync(db, job.Id))
        {
            await db.SaveChangesAsync(ct);
            return true;
        }

        DetachDeletedJob(db, job);
        await db.SaveChangesAsync(CancellationToken.None);
        return false;
    }

    private static async Task<bool> JobGoneAsync(AppDbContext db, Guid jobId) =>
        !await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking().AnyAsync(j => j.Id == jobId, CancellationToken.None);

    private static void DetachDeletedJob(AppDbContext db, GenerationJob job)
    {
        db.Entry(job).State = EntityState.Detached;
        foreach (var entry in db.ChangeTracker.Entries<AiUsageLog>().Where(e => e.State == EntityState.Added))
        {
            entry.Entity.JobId = null;
        }
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

/// <summary>
/// Worker işin sahipliğini kaybetti (kilit doldu ve iş kurtarıldı/başka worker'a geçti ya da iptal edildi). Bu worker'ın hiçbir şey yazmaması gerekir.
/// </summary>
public sealed class JobOwnershipLostException : Exception
{
    public JobOwnershipLostException()
    {
    }

    public JobOwnershipLostException(string message)
        : base(message)
    {
    }

    public JobOwnershipLostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Kuyruğu işleyen arka plan servisi: <see cref="GenerationSettings.MaxParallelJobs"/> kadar eşzamanlı döngü.</summary>
public sealed class GenerationWorker(
    GenerationProcessor processor, CostReconciliationSweep sweep, IOptionsMonitor<GenerationSettings> settings, ILogger<GenerationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = Math.Max(1, settings.CurrentValue.MaxParallelJobs);
        await Task.WhenAll(Enumerable.Range(0, loops).Select(_ => LoopAsync(stoppingToken)).Append(SweepLoopAsync(stoppingToken)));
    }

    /// <summary>Belirsiz maliyet süpürücüsü: açılışta bir kez, sonra aralıkla. Her çalıştırma idempotenttir; hata sonraki turda yeniden denenir.</summary>
    private async Task SweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var minutes = settings.CurrentValue.ReconciliationSweepMinutes;
            if (minutes <= 0)
            {
                return;
            }

            try
            {
                await sweep.RunOnceAsync(ct);
                await Task.Delay(TimeSpan.FromMinutes(minutes), ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Belirsiz maliyet süpürücüsü hata verdi; sonraki turda yeniden denenecek.");
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, minutes)), ct).ConfigureAwait(false);
            }
        }
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
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return; // kapanış (SqlClient iptali SqlException olarak da gelebilir)
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Üretim worker döngüsünde hata.");
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
    }
}
