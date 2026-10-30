using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Audit;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Infrastructure.Generation;

/// <summary>
/// Sonucu belirsiz AI maliyetleri için tekrar çalıştırılabilir "kendini onaran" süpürücü. Normal yolda gerek duyulmaz (işlemci ve kurtarma SQL'i maliyet
/// satırını iş durumuyla aynı transaction'da yazar); süpürücü şu durumlara karşı ikinci savunma hattıdır: eski sürümde yazılmış kayıtlar, elle müdahale,
/// beklenmeyen kısmi yazım. Her adım idempotenttir: olay anahtarı (<c>uncertain:{jobId}</c>) filtreli benzersiz indeksle korunur, aynı adımı ikinci kez
/// çalıştırmak ikinci kayıt üretmez. Süpürücü tutar uydurmaz: yalnızca <c>reserved_cost_usd</c> (yoksa iş başı bütçe sınırı) kullanır; gerçek tutarı bulmak
/// platform yöneticisinin mutabakat kararıdır.
/// </summary>
public sealed class CostReconciliationSweep(
    IServiceScopeFactory scopes, IOptionsMonitor<GenerationSettings> settings, TimeProvider clock, ILogger<CostReconciliationSweep> logger)
{
    /// <param name="MissingCostRows">İş "belirsiz" ama maliyet satırı yokken eklenen satır sayısı.</param>
    /// <param name="LegacyKeyed">Olay anahtarı/mutabakat durumu olmayan eski belirsiz satırlardan düzeltilenler.</param>
    /// <param name="StaleOpen">Süresi aşan açık vaka sayısı (yalnızca uyarı; veri değişmez).</param>
    /// <param name="ReleasedReservations">İptal edilmiş ve hiç gönderilmemiş işlerden serbest bırakılan rezervasyon sayısı (M5.3).</param>
    /// <param name="CancelledToUncertain">İptal edilmiş, gönderilmiş ve kurtarma süresi dolmuş işlerden belirsiz maliyet kaydına çevrilen rezervasyon sayısı (M5.3).</param>
    public sealed record Result(int MissingCostRows, int LegacyKeyed, int StaleOpen, decimal StaleOpenUsd, int ReleasedReservations = 0, int CancelledToUncertain = 0);

    public async Task<Result> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await RunOnceAsync(db, ct);
    }

    /// <summary>
    /// Süpürücü deyimini çalıştırır. Olay anahtarı benzersiz indeksi (<c>ux_ai_usage_logs_event_key</c>) eşzamanlı ikinci süpürücüye karşı koruma hattıdır: aynı işin kaydını başka bir
    /// süpürücü az önce yazdıysa deyim geri alınır ve bu tur 0 sayılır (ikinci kayıt yok; kaybeden tur sonraki çalıştırmada zaten kayıtlıyı görür).
    /// </summary>
    private static async Task<int> TryExecuteAsync(AppDbContext db, FormattableString sql, CancellationToken ct)
    {
        try
        {
            return await db.Database.ExecuteSqlInterpolatedAsync(sql, ct);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return 0;
        }
    }

    internal async Task<Result> RunOnceAsync(AppDbContext db, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var fallback = settings.CurrentValue.Budget.MaxJobCostUsd;

        // (a) İş Failed/OutcomeUncertain ama hiçbir belirsiz maliyet satırı yok → en kötü durum satırı ekle (bütçe harcamayı görsün)
        var missing = await TryExecuteAsync(db, $"""
            INSERT INTO ai_usage_logs (id, created_at, updated_at, organization_id, store_id, job_id, provider, model, operation, quality, attempt, success,
                                       duration_ms, estimated_cost_usd, provider_request_id, error_code, event_key, is_adjustment, reconciliation_state)
            SELECT NEWID(), {now}, {now}, j.organization_id, j.store_id, j.id, COALESCE(j.provider, N'unknown'), COALESCE(j.model, N'unknown'), j.job_type, j.quality,
                   j.attempt, 0, 0, COALESCE(j.reserved_cost_usd, {fallback}), j.provider_request_id, N'OutcomeUncertain',
                   CONCAT(N'uncertain:', LOWER(CONVERT(nvarchar(36), j.id))), 0, N'Open'
              FROM generation_jobs AS j
             WHERE j.status = N'Failed' AND j.error_code = N'OutcomeUncertain'
               AND NOT EXISTS (SELECT 1 FROM ai_usage_logs AS u WHERE u.job_id = j.id AND u.error_code = N'OutcomeUncertain' AND u.is_adjustment = 0)
               AND NOT EXISTS (SELECT 1 FROM ai_usage_logs AS u WHERE u.event_key = CONCAT(N'uncertain:', LOWER(CONVERT(nvarchar(36), j.id))));
            """, ct);

        // (b) Eski sürümde yazılmış belirsiz satırlar: olay anahtarı ve "Open" durumu eklenir (iş başına en eski satır; başka satırda aynı anahtar yoksa)
        var keyed = await TryExecuteAsync(db, $"""
            WITH legacy AS (
                SELECT id, job_id, ROW_NUMBER() OVER (PARTITION BY job_id ORDER BY created_at, id) AS rn
                  FROM ai_usage_logs
                 WHERE error_code = N'OutcomeUncertain' AND is_adjustment = 0 AND event_key IS NULL AND job_id IS NOT NULL)
            UPDATE u
               SET event_key = CONCAT(N'uncertain:', LOWER(CONVERT(nvarchar(36), u.job_id))), reconciliation_state = COALESCE(u.reconciliation_state, N'Open')
              FROM ai_usage_logs AS u
              JOIN legacy AS l ON l.id = u.id AND l.rn = 1
             WHERE NOT EXISTS (SELECT 1 FROM ai_usage_logs AS o WHERE o.event_key = CONCAT(N'uncertain:', LOWER(CONVERT(nvarchar(36), u.job_id))));
            """, ct);

        // (c) İş bağlantısı kopmuş (oturum silinmiş) eski satırlar: anahtarsız kalır ama mutabakata girer
        keyed += await TryExecuteAsync(db, $"""
            UPDATE ai_usage_logs SET reconciliation_state = N'Open'
             WHERE error_code = N'OutcomeUncertain' AND is_adjustment = 0 AND reconciliation_state IS NULL AND job_id IS NULL;
            """, ct);

        // (d) İptal edilmiş + HİÇ gönderilmemiş (niyet ve kimlik yok) iş: sağlayıcıya hiçbir şey gitmediği KESİN → rezervasyon serbest (yalnızca ayrılmış sayılmaz).
        var released = await TryExecuteAsync(db, $"""
            UPDATE generation_jobs SET reserved_cost_usd = NULL, updated_at = {now}
             WHERE status = N'Cancelled' AND reserved_cost_usd IS NOT NULL AND provider_submit_started_at IS NULL AND provider_request_id IS NULL;
            """, ct);

        // (e) İptal edilmiş + GÖNDERİLMİŞ (veya niyet/kimlik var) + maliyet kaydı yok + kurtarma süresi doldu: doğrudan serbest bırakılmaz; en kötü durumla BELİRSİZ maliyet kaydına
        // çevrilir (mutabakata açık). Olay anahtarı iş başına tektir: ikinci çalıştırma/yarış ikinci kayıt üretmez (filtreli benzersiz indeks).
        var recoveryCutoff = now.AddSeconds(-Math.Max(60, settings.CurrentValue.CancelledReservationRecoverySeconds));
        var converted = await TryExecuteAsync(db, $"""
            INSERT INTO ai_usage_logs (id, created_at, updated_at, organization_id, store_id, job_id, provider, model, operation, quality, attempt, success,
                                       duration_ms, estimated_cost_usd, provider_request_id, error_code, event_key, is_adjustment, reconciliation_state)
            SELECT NEWID(), {now}, {now}, j.organization_id, j.store_id, j.id, COALESCE(j.provider, N'unknown'), COALESCE(j.model, N'unknown'), j.job_type, j.quality,
                   j.attempt, 0, 0, j.reserved_cost_usd, j.provider_request_id, N'OutcomeUncertain',
                   CONCAT(N'uncertain:', LOWER(CONVERT(nvarchar(36), j.id))), 0, N'Open'
              FROM generation_jobs AS j
             WHERE j.status = N'Cancelled' AND j.reserved_cost_usd IS NOT NULL AND (j.provider_submit_started_at IS NOT NULL OR j.provider_request_id IS NOT NULL)
               AND COALESCE(j.finished_at, j.updated_at) < {recoveryCutoff}
               AND NOT EXISTS (SELECT 1 FROM ai_usage_logs AS u WHERE u.job_id = j.id AND u.attempt = j.attempt AND u.is_adjustment = 0)
               AND NOT EXISTS (SELECT 1 FROM ai_usage_logs AS u WHERE u.event_key = CONCAT(N'uncertain:', LOWER(CONVERT(nvarchar(36), j.id))));
            """, ct);

        var threshold = now.AddDays(-Math.Max(1, settings.CurrentValue.ReconciliationStaleDays));
        var stale = await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.ReconciliationState == CostReconciliationState.Open && l.CreatedAt < threshold)
            .Select(l => l.EstimatedCostUsd).ToListAsync(ct);
        var result = new Result(missing, keyed, stale.Count, stale.Sum(), released, converted);

        if (released > 0 || converted > 0)
        {
            logger.LogWarning("İptal edilmiş işlerin rezervasyonları kapatıldı: serbest {Released}, belirsiz maliyete çevrilen {Converted}.", released, converted);
        }

        if (missing > 0 || keyed > 0)
        {
            logger.LogWarning("Belirsiz maliyet süpürücüsü kayıt onardı: eksik satır {Missing}, eski kayıt {Keyed}.", missing, keyed);
        }

        if (stale.Count > 0)
        {
            logger.LogWarning("{Count} belirsiz maliyet vakası {Days} günü aşkın süredir mutabakat bekliyor (toplam {Usd} USD bütçede tutuluyor).",
                stale.Count, settings.CurrentValue.ReconciliationStaleDays, result.StaleOpenUsd);
        }

        return result;
    }
}

public enum DecisionOutcome
{
    /// <summary>Karar kaydedildi.</summary>
    Recorded,

    /// <summary>Aynı Idempotency-Key ve aynı gövde daha önce işlenmişti: aynı karar döndürüldü, ikinci kayıt yok.</summary>
    Replayed,

    /// <summary>Vaka bulunamadı veya mutabakat gerektiren belirsiz bir maliyet değil.</summary>
    NotFound,

    /// <summary>Tutar kuralı ihlali (ConfirmedCharged için tutar zorunlu, diğerlerinde yasak, üst sınır).</summary>
    InvalidAmount,

    /// <summary>Başka bir karar araya girdi (ExpectedSequence eski): yönetici güncel durumu görüp yeniden karar vermeli.</summary>
    SequenceMismatch,

    /// <summary>Aynı Idempotency-Key farklı bir gövdeyle kullanıldı.</summary>
    KeyReused,
}

public sealed record DecideCommand(CostDecision Decision, decimal? AmountUsd, string? Note, int ExpectedSequence);

public sealed record DecisionResult(DecisionOutcome Outcome, AiCostReconciliation? Decision, CostReconciliationState? State, int CurrentSequence);

/// <summary>
/// Platform yöneticisinin belirsiz maliyet kararı. Yetki denetimi çağıranındır (API filtresi); bu sınıf veri bütünlüğünden sorumludur:
/// <list type="bullet">
/// <item>Karar, düzeltme satırı (±fark), vakanın durumu ve denetim kaydı <b>tek transaction</b>'da yazılır.</item>
/// <item>İki katmanlı idempotency: (vaka, Idempotency-Key) benzersiz → aynı istek tekrarlanınca ikinci kayıt yok; (vaka, sıra) benzersiz → iki yönetici
/// aynı sırayı yazamaz. Düzeltme kararları (sonraki sıra) engellenmez.</item>
/// <item>Kayıtlar silinmez/değiştirilmez: efektif maliyet = ilk satır + düzeltme satırlarının toplamı; her karar bir düzeltme satırı ekler.</item>
/// </list>
/// </summary>
public sealed class CostReconciliationService(TimeProvider clock)
{
    public const decimal MaxConfirmedAmountUsd = 1000m;
    public const int MaxNoteLength = 200;

    public async Task<DecisionResult> DecideAsync(
        AppDbContext db, Guid usageLogId, Guid adminUserId, string idempotencyKey, DecideCommand command, string? ipHash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        // Platform yöneticisi başka firmaların kaydını işler: filtre bilinçli atlanır, her sorgu açıkça vaka kimliğine bağlıdır
        var uncertain = await db.AiUsageLogs.IgnoreQueryFilters()
            .SingleOrDefaultAsync(l => l.Id == usageLogId && !l.IsAdjustment && l.ReconciliationState != null, ct);
        if (uncertain is null)
        {
            return new DecisionResult(DecisionOutcome.NotFound, null, null, 0);
        }

        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        var hash = Hash(command, note);
        var decisions = await db.AiCostReconciliations.IgnoreQueryFilters().AsNoTracking().Where(d => d.UsageLogId == usageLogId).OrderBy(d => d.Sequence).ToListAsync(ct);

        var replay = decisions.FirstOrDefault(d => d.IdempotencyKey == idempotencyKey);
        if (replay is not null)
        {
            return replay.RequestHash == hash
                ? new DecisionResult(DecisionOutcome.Replayed, replay, StateOf(replay.Decision), decisions.Count)
                : new DecisionResult(DecisionOutcome.KeyReused, null, null, decisions.Count);
        }

        if (!AmountIsValid(command) || (note is not null && note.Length > MaxNoteLength))
        {
            return new DecisionResult(DecisionOutcome.InvalidAmount, null, null, decisions.Count);
        }

        if (command.ExpectedSequence != decisions.Count)
        {
            return new DecisionResult(DecisionOutcome.SequenceMismatch, null, null, decisions.Count);
        }

        var reserved = uncertain.EstimatedCostUsd;
        var previousEffective = decisions.Count == 0 ? reserved : decisions[^1].EffectiveCostUsd;
        var previousActual = await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.AdjustsLogId == usageLogId).SumAsync(l => (decimal?)l.ActualCostUsd, ct) ?? 0m;
        previousActual += uncertain.ActualCostUsd ?? 0m;

        var (effective, actual) = command.Decision switch
        {
            CostDecision.ConfirmedCharged => (command.AmountUsd!.Value, command.AmountUsd.Value),
            CostDecision.ConfirmedNotCharged => (0m, 0m),
            _ => (reserved, 0m), // WrittenOff: kanıtlanamadı, en kötü durum tutarı platformun zararı olarak kalır; gerçek tutar bilinmiyor
        };

        var decision = new AiCostReconciliation
        {
            OrganizationId = uncertain.OrganizationId, UsageLogId = usageLogId, JobId = uncertain.JobId, Sequence = decisions.Count + 1,
            Decision = command.Decision, EffectiveCostUsd = effective, ConfirmedAmountUsd = command.Decision == CostDecision.ConfirmedCharged ? command.AmountUsd : null,
            Note = note, DecidedByUserId = adminUserId, IdempotencyKey = idempotencyKey, RequestHash = hash,
        };
        var adjustment = new AiUsageLog
        {
            OrganizationId = uncertain.OrganizationId, StoreId = uncertain.StoreId, JobId = uncertain.JobId, Provider = uncertain.Provider, Model = uncertain.Model,
            Operation = uncertain.Operation, Quality = uncertain.Quality, Attempt = uncertain.Attempt, Success = false, DurationMs = 0,
            EstimatedCostUsd = effective - previousEffective, ActualCostUsd = actual - previousActual, ProviderRequestId = uncertain.ProviderRequestId,
            ErrorCode = GenerationErrorCode.OutcomeUncertain, IsAdjustment = true, AdjustsLogId = usageLogId, EventKey = $"reconcile:{decision.Id}",
            OccurredAt = uncertain.OccurredAt ?? uncertain.CreatedAt, // harcama doğduğu güne yazılır (bütçe penceresi)
        };

        // Açık Add: gezinti olmayan yeni varlıklar (bilinen EF tuzağı) — durum Added olarak kesinleşir
        db.AiCostReconciliations.Add(decision);
        db.AiUsageLogs.Add(adjustment);
        uncertain.ReconciliationState = StateOf(command.Decision);
        db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = uncertain.OrganizationId, StoreId = uncertain.StoreId, UserId = adminUserId,
            Action = $"Cost.{command.Decision}", EntityType = "AiUsageLog", EntityId = usageLogId, IpHash = ipHash, CreatedAt = clock.GetUtcNow(),
        });

        try
        {
            await db.SaveChangesAsync(ct); // karar + düzeltme satırı + durum + denetim: tek transaction
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // Eşzamanlı yarış: aynı anahtar (tekrar eden istek) veya aynı sıra (başka yönetici). Yeniden okuyup hangisi olduğunu ayırırız.
            db.ChangeTracker.Clear();
            var after = await db.AiCostReconciliations.IgnoreQueryFilters().AsNoTracking().Where(d => d.UsageLogId == usageLogId).OrderBy(d => d.Sequence).ToListAsync(ct);
            var same = after.FirstOrDefault(d => d.IdempotencyKey == idempotencyKey);
            if (same is not null)
            {
                return same.RequestHash == hash
                    ? new DecisionResult(DecisionOutcome.Replayed, same, StateOf(same.Decision), after.Count)
                    : new DecisionResult(DecisionOutcome.KeyReused, null, null, after.Count);
            }

            return new DecisionResult(DecisionOutcome.SequenceMismatch, null, null, after.Count);
        }

        return new DecisionResult(DecisionOutcome.Recorded, decision, StateOf(command.Decision), decision.Sequence);
    }

    internal static CostReconciliationState StateOf(CostDecision decision) => decision switch
    {
        CostDecision.ConfirmedCharged => CostReconciliationState.ConfirmedCharged,
        CostDecision.ConfirmedNotCharged => CostReconciliationState.ConfirmedNotCharged,
        _ => CostReconciliationState.WrittenOff,
    };

    private static bool AmountIsValid(DecideCommand command) => command.Decision == CostDecision.ConfirmedCharged
        ? command.AmountUsd is > 0 and <= MaxConfirmedAmountUsd && decimal.Round(command.AmountUsd.Value, 4) == command.AmountUsd
        : command.AmountUsd is null;

    private static string Hash(DecideCommand command, string? note)
    {
        var text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{command.Decision}|{command.AmountUsd:0.0000}|{note}|{command.ExpectedSequence}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }
}
