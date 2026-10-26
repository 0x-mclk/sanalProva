using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Generation;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.Admin;

/// <param name="Id">Vaka kimliği (belirsiz maliyet satırının kimliği; oturum silinse de kalır).</param>
/// <param name="ReservedCostUsd">Belirsizlik anında bütçeye yazılan en kötü durum tutarı.</param>
/// <param name="EffectiveCostUsd">Kararlar uygulandıktan sonra geçerli maliyet (ilk tutar + düzeltmeler).</param>
/// <param name="DecisionCount">Verilmiş karar sayısı: yeni karar bu değeri <c>expectedSequence</c> olarak göndermelidir.</param>
public sealed record CostCaseDto(
    Guid Id, Guid OrganizationId, string OrganizationName, Guid StoreId, Guid? JobId, string Provider, string Model, string? Quality, string Operation,
    int Attempt, string? ProviderRequestId, decimal ReservedCostUsd, decimal EffectiveCostUsd, CostReconciliationState State, int DecisionCount, DateTimeOffset OccurredAt);

public sealed record CostDecisionDto(int Sequence, CostDecision Decision, decimal EffectiveCostUsd, decimal? ConfirmedAmountUsd, string? Note, Guid DecidedByUserId, DateTimeOffset DecidedAt);

public sealed record CostCaseDetail(CostCaseDto Case, IReadOnlyList<CostDecisionDto> Decisions);

/// <param name="AmountUsd">Sağlayıcının gerçek tutarı: yalnızca <c>ConfirmedCharged</c> için zorunlu, diğerlerinde gönderilmez.</param>
/// <param name="Note">Kısa açıklama (≤ 200 karakter). Kişisel veri yazmayın.</param>
/// <param name="ExpectedSequence">Yöneticinin gördüğü karar sayısı (ilk karar için 0). Araya başka karar girdiyse 409 döner.</param>
public sealed record CostDecisionRequest(CostDecision Decision, decimal? AmountUsd, [property: System.ComponentModel.DataAnnotations.StringLength(200)] string? Note, int ExpectedSequence);

/// <summary>
/// Belirsiz AI maliyetlerinin mutabakatı. <b>Yalnızca platform yöneticisi</b> (SaaS işletmecisi): mağaza Owner/Manager rolleri bu uç noktalara erişemez ve
/// hiçbir API platform yöneticisi yetkisi veremez. Firma sınırını bilinçli olarak aşar (tüm firmaların vakaları görünür): her okuma/yazma bu sınıftadır
/// ve her karar denetim kaydı bırakır. Kayıtlar kişisel veri içermez.
/// </summary>
public static class PlatformCostEndpoints
{
    private const int MaxPage = 200;

    public static RouteGroupBuilder MapPlatformCostEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/platform/cost-cases").WithTags("Platform").RequirePlatformAdmin();
        group.MapGet("", ListAsync).Produces<IReadOnlyList<CostCaseDto>>();
        group.MapGet("/{id:guid}", GetAsync).Produces<CostCaseDetail>();
        group.MapPost("/{id:guid}/decisions", DecideAsync).Produces<CostCaseDetail>(StatusCodes.Status201Created).Produces<CostCaseDetail>();
        return api;
    }

    private static async Task<IResult> ListAsync(AppDbContext db, CancellationToken ct, CostReconciliationState? state = null, int take = 50)
    {
        take = Math.Clamp(take, 1, MaxPage);
        var query = db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().Where(l => l.ReconciliationState != null && !l.IsAdjustment);
        if (state is { } wanted)
        {
            query = query.Where(l => l.ReconciliationState == wanted);
        }

        var cases = await query.OrderByDescending(l => l.CreatedAt).Take(take).ToListAsync(ct);
        return TypedResults.Ok(await ToDtosAsync(db, cases, ct));
    }

    private static async Task<IResult> GetAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var found = await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(l => l.Id == id && l.ReconciliationState != null && !l.IsAdjustment, ct);
        return found is null ? ApiProblems.NotFound("Vaka bulunamadı.") : TypedResults.Ok(await DetailAsync(db, found, ct));
    }

    private static async Task<IResult> DecideAsync(
        Guid id, CostDecisionRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        AppDbContext db, CostReconciliationService service, CurrentUser user, AuditTrail audit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length is < 8 or > 64)
        {
            return ApiProblems.BadRequest("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key başlığı (8–64 karakter) gerekli.");
        }

        var result = await service.DecideAsync(
            db, id, user.UserId, idempotencyKey, new DecideCommand(request.Decision, request.AmountUsd, request.Note, request.ExpectedSequence), audit.RequestIpHash(), ct);

        switch (result.Outcome)
        {
            case DecisionOutcome.NotFound:
                return ApiProblems.NotFound("Vaka bulunamadı.");
            case DecisionOutcome.InvalidAmount:
                return ApiProblems.BadRequest("INVALID_AMOUNT", "Tutar geçersiz: yalnızca ConfirmedCharged için 0–1000 USD arası pozitif tutar (en çok 4 ondalık) gerekir; not en fazla 200 karakter.");
            case DecisionOutcome.SequenceMismatch:
                return ApiProblems.Create(StatusCodes.Status409Conflict, "DECISION_CONFLICT", "Bu vakaya başka bir karar girildi. Güncel durumu yenileyip tekrar karar verin.",
                    new Dictionary<string, object?> { ["currentSequence"] = result.CurrentSequence });
            case DecisionOutcome.KeyReused:
                return ApiProblems.Unprocessable("IDEMPOTENCY_KEY_REUSED", "Bu Idempotency-Key farklı bir karar için kullanılmış.");
        }

        var found = await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == id, ct);
        var detail = await DetailAsync(db, found, ct);
        return result.Outcome == DecisionOutcome.Recorded
            ? TypedResults.Created($"/api/v1/platform/cost-cases/{id}", detail)
            : TypedResults.Ok(detail);
    }

    private static async Task<CostCaseDetail> DetailAsync(AppDbContext db, AiUsageLog found, CancellationToken ct)
    {
        var dto = (await ToDtosAsync(db, [found], ct))[0];
        var decisions = await db.AiCostReconciliations.IgnoreQueryFilters().AsNoTracking().Where(d => d.UsageLogId == found.Id).OrderBy(d => d.Sequence).ToListAsync(ct);
        return new CostCaseDetail(dto, decisions.Select(d => new CostDecisionDto(d.Sequence, d.Decision, d.EffectiveCostUsd, d.ConfirmedAmountUsd, d.Note, d.DecidedByUserId, d.CreatedAt)).ToList());
    }

    private static async Task<IReadOnlyList<CostCaseDto>> ToDtosAsync(AppDbContext db, IReadOnlyList<AiUsageLog> cases, CancellationToken ct)
    {
        var ids = cases.Select(c => c.Id).ToList();
        var organizationIds = cases.Select(c => c.OrganizationId).Distinct().ToList();
        var adjustments = (await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().Where(a => a.IsAdjustment && ids.Contains(a.AdjustsLogId!.Value))
            .GroupBy(a => a.AdjustsLogId!.Value).Select(g => new { Id = g.Key, Sum = g.Sum(a => a.EstimatedCostUsd) }).ToListAsync(ct)).ToDictionary(a => a.Id, a => a.Sum);
        var counts = (await db.AiCostReconciliations.IgnoreQueryFilters().AsNoTracking().Where(d => ids.Contains(d.UsageLogId))
            .GroupBy(d => d.UsageLogId).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync(ct)).ToDictionary(a => a.Id, a => a.Count);
        var names = await db.Organizations.IgnoreQueryFilters().AsNoTracking().Where(o => organizationIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Name, ct);

        return cases.Select(c => new CostCaseDto(
            c.Id, c.OrganizationId, names.GetValueOrDefault(c.OrganizationId, "?"), c.StoreId, c.JobId, c.Provider, c.Model, c.Quality, c.Operation, c.Attempt, c.ProviderRequestId,
            c.EstimatedCostUsd, c.EstimatedCostUsd + adjustments.GetValueOrDefault(c.Id), c.ReconciliationState!.Value, counts.GetValueOrDefault(c.Id), c.OccurredAt ?? c.CreatedAt)).ToList();
    }
}
