using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Generation;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.Admin;

public sealed record QualityOption(string Resolution, string Mode, decimal Credits, decimal EstimatedUsdPerImage, string Label);

/// <summary>Hazır takım (FASHN Try-On Max) kalite ayarı. <see cref="IsOverride"/> false ise sunucu yapılandırmasındaki varsayılan geçerlidir.</summary>
public sealed record AiQualityDto(string Resolution, string Mode, bool IsOverride, string DefaultResolution, string DefaultMode, IReadOnlyList<QualityOption> Options);

public sealed record AiQualityRequest(string Resolution, string Mode);

/// <summary>
/// Yönetici kalite/çözünürlük ayarı (firma düzeyinde). Seçim, sonraki hazır takım üretimlerinde FASHN parametrelerini ve maliyet tahminini/bütçe
/// kapısını belirler. Fiyat/performans için varsayılan <b>balanced + 1k</b> (2 kredi); 2k (3 kredi) alternatiftir. 4k sunulmaz.
/// Kalite farkı (yüz, kumaş, nakış, ürün sadakati) canlı testte ölçülecek — ucuz seçenek kalite kanıtlanmadan "yeterli" varsayılmaz (docs/11).
/// </summary>
public static class AiQualityEndpoints
{
    public static RouteGroupBuilder MapAiQualityEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/admin/ai-quality").WithTags("Admin").RequireOrganizationRole(StoreRole.Manager);
        group.MapGet("", GetAsync).Produces<AiQualityDto>();
        group.MapPut("", PutAsync).Produces<AiQualityDto>();
        return api;
    }

    private static async Task<IResult> GetAsync(AppDbContext db, CurrentUser user, IOptionsMonitor<GenerationSettings> generation, CostEstimator costs, CancellationToken ct) =>
        TypedResults.Ok(Build(await LoadSettingsAsync(db, user, ct), generation.CurrentValue, costs));

    private static async Task<IResult> PutAsync(AiQualityRequest request, AppDbContext db, CurrentUser user, IOptionsMonitor<GenerationSettings> generation, CostEstimator costs, AuditTrail audit, CancellationToken ct)
    {
        var resolution = request.Resolution?.Trim().ToLowerInvariant();
        var mode = request.Mode?.Trim().ToLowerInvariant();
        if (resolution is null || mode is null || !FashnQuality.Resolutions.Contains(resolution) || !FashnQuality.Modes.Contains(mode))
        {
            return ApiProblems.BadRequest("INVALID_QUALITY", "Çözünürlük 1k veya 2k, mod fast, balanced veya quality olmalı.");
        }

        var organization = await db.Organizations.SingleAsync(o => o.Id == user.OrganizationId, ct);
        organization.Settings.FashnResolution = resolution;
        organization.Settings.FashnGenerationMode = mode;
        audit.Record("Ai.QualityChanged", nameof(Organization), organization.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(Build(organization.Settings, generation.CurrentValue, costs));
    }

    private static async Task<OrganizationSettings> LoadSettingsAsync(AppDbContext db, CurrentUser user, CancellationToken ct) =>
        (await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == user.OrganizationId, ct)).Settings;

    internal static AiQualityDto Build(OrganizationSettings settings, GenerationSettings generation, CostEstimator costs)
    {
        var defaults = generation.Strategies.TryGetValue(GenerationJobType.OutfitSet, out var strategy) ? strategy.Options : [];
        var (defaultMode, defaultResolution) = CostEstimator.FashnKey(defaults);
        var resolution = settings.FashnResolution ?? defaultResolution;
        var mode = settings.FashnGenerationMode ?? defaultMode;
        var options = FashnQuality.Modes.SelectMany(m => FashnQuality.Resolutions.Select(r =>
        {
            var credits = costs.FashnCredits(new Dictionary<string, string> { ["generation_mode"] = m, ["resolution"] = r });
            return new QualityOption(r, m, credits, costs.FashnUsd(credits), $"{m} · {r.ToUpperInvariant()}");
        })).ToList();
        return new AiQualityDto(resolution, mode, settings.FashnResolution is not null || settings.FashnGenerationMode is not null, defaultResolution, defaultMode, options);
    }
}
