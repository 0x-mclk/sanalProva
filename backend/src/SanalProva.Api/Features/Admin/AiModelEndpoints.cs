using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Ai.TryOn;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.Admin;

/// <param name="EstimatedUsd">Tipik istek tahmini (3:4 fotoğraf, iki referans görsel, tek çıktı).</param>
/// <param name="WorstCaseUsd">Yükleme sınırlarındaki en büyük girdilerle en kötü durum (bütçe kapısı bunu kullanır).</param>
/// <param name="MeasuredUsd">Proje sahibinin gerçek faturadan ölçtüğü tutar (varsa; bilgi amaçlı).</param>
/// <param name="EnabledByDefault">Ayar yapılmamışsa personele açık mı.</param>
/// <param name="HighCost">Yüksek maliyetli kalite (arayüzde uyarı).</param>
/// <param name="ExceedsJobLimit">Gerçek modda en kötü durum iş başı bütçe sınırını aşıyor: bu kaliteyle üretim reddedilir.</param>
public sealed record TryOnQualityDto(
    string Key, string Label, decimal EstimatedUsd, decimal WorstCaseUsd, decimal? MeasuredUsd, bool EnabledByDefault, bool HighCost, bool ExceedsJobLimit);

/// <summary>Yönetici görünümü: modelin tüm kaliteleri, hangileri izinli ve varsayılan.</summary>
public sealed record AdminTryOnModelDto(
    string Key, string DisplayName, string ProviderModelId, string Note, bool Legacy, bool LiveVerified, string PriceSource,
    bool Enabled, IReadOnlyList<string> AllowedQualities, string DefaultQuality, IReadOnlyList<TryOnQualityDto> Qualities);

/// <param name="MaxJobCostUsd">İş başı bütçe sınırı (null: sınır uygulanmıyor).</param>
public sealed record AiModelSettingsDto(string DefaultModel, bool Live, decimal? MaxJobCostUsd, IReadOnlyList<AdminTryOnModelDto> Models);

public sealed record AiModelSettingRequest(string Key, bool Enabled, IReadOnlyList<string> AllowedQualities, string DefaultQuality);

public sealed record AiModelSettingsRequest(string DefaultModel, IReadOnlyList<AiModelSettingRequest> Models);

/// <summary>Personel görünümü: yalnızca izin verilen modeller ve kaliteler. Mobil/web istemciler seçim ekranını buradan kurar.</summary>
public sealed record TryOnModelOptionDto(string Key, string DisplayName, string Note, bool LiveVerified, string DefaultQuality, IReadOnlyList<TryOnQualityDto> Qualities);

public sealed record TryOnOptionsDto(bool Live, bool FreeCombinationEnabled, string DefaultModel, IReadOnlyList<TryOnModelOptionDto> Models);

/// <summary>
/// Tek ürün giydirme modelleri: yönetici hangi modellerin ve kalitelerin kullanılabileceğini ve varsayılanları belirler (firma düzeyi);
/// personel yalnızca izin verilenleri görür ve kullanabilir (kontrol üretim isteğinde de sunucuda tekrarlanır).
/// </summary>
public static class AiModelEndpoints
{
    public static RouteGroupBuilder MapAiModelEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin/ai-models").WithTags("Admin").RequireOrganizationRole(StoreRole.Manager);
        admin.MapGet("", GetAsync).Produces<AiModelSettingsDto>();
        admin.MapPut("", PutAsync).Produces<AiModelSettingsDto>();

        api.MapGet("/ai/try-on-options", OptionsAsync).WithTags("Generation").RequireStore(StoreRole.Staff).Produces<TryOnOptionsDto>();
        return api;
    }

    private static TryOnQualityDto ToDto(TryOnModel model, TryOnQuality quality, GenerationSettings generation)
    {
        var (estimated, worst) = TryOnModels.DisplayPrice(model, quality, generation.Pricing);
        return new TryOnQualityDto(quality.Key, quality.Label, estimated, worst, quality.MeasuredUsd, quality.EnabledByDefault, quality.HighCost,
            ExceedsJobLimit(generation, worst));
    }

    /// <summary>Gerçek modda bütçe kapısı (iş başı sınır) bu en kötü durumu reddeder mi. Mock modda ücret yoktur.</summary>
    internal static bool ExceedsJobLimit(GenerationSettings generation, decimal worstCaseUsd) =>
        !generation.UseMockProviders && generation.Budget.Enforce && worstCaseUsd > generation.Budget.MaxJobCostUsd;

    private static async Task<OrganizationSettings> LoadSettingsAsync(AppDbContext db, Guid organizationId, CancellationToken ct) =>
        (await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == organizationId, ct)).Settings;

    private static AiModelSettingsDto Build(OrganizationSettings settings, GenerationSettings generation)
    {
        var policy = TryOnPolicyResolver.Resolve(settings);
        var models = TryOnModels.All.Select(model =>
        {
            var allowed = policy.Models.FirstOrDefault(p => p.Model.Key == model.Key);
            return new AdminTryOnModelDto(
                model.Key, model.DisplayName, model.ModelId, model.Note, model.Legacy, model.LiveVerified, model.PriceSource,
                allowed is not null, allowed?.AllowedQualities ?? [], allowed?.DefaultQuality ?? model.DefaultQuality,
                model.Qualities.Select(q => ToDto(model, q, generation)).ToList());
        }).ToList();
        return new AiModelSettingsDto(policy.DefaultModel, !generation.UseMockProviders, generation.Budget.Enforce ? generation.Budget.MaxJobCostUsd : null, models);
    }

    private static async Task<IResult> GetAsync(AppDbContext db, CurrentUser user, IOptionsMonitor<GenerationSettings> generation, CancellationToken ct) =>
        TypedResults.Ok(Build(await LoadSettingsAsync(db, user.OrganizationId, ct), generation.CurrentValue));

    private static async Task<IResult> PutAsync(AiModelSettingsRequest request, AppDbContext db, CurrentUser user, IOptionsMonitor<GenerationSettings> generation, AuditTrail audit, CancellationToken ct)
    {
        var problem = Validate(request);
        if (problem is not null)
        {
            return ApiProblems.BadRequest("INVALID_AI_MODELS", problem);
        }

        var enabled = request.Models.Where(m => m.Enabled).ToList();
        var organization = await db.Organizations.SingleAsync(o => o.Id == user.OrganizationId, ct);
        organization.Settings.DefaultTryOnModel = request.DefaultModel;
        organization.Settings.EnabledTryOnModels = enabled.Select(m => m.Key).ToList();
        organization.Settings.AllowedTryOnQualities = enabled.SelectMany(m => m.AllowedQualities.Distinct().Select(q => $"{m.Key}:{q}")).ToList();
        organization.Settings.DefaultTryOnQualities = enabled.Select(m => $"{m.Key}:{m.DefaultQuality}").ToList();
        audit.Record("Ai.ModelsChanged", nameof(Organization), organization.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(Build(organization.Settings, generation.CurrentValue));
    }

    /// <returns>Hata iletisi veya null.</returns>
    internal static string? Validate(AiModelSettingsRequest request)
    {
        if (request.Models is null || request.Models.Count == 0)
        {
            return "Model listesi boş.";
        }

        foreach (var item in request.Models)
        {
            var model = TryOnModels.Find(item.Key);
            if (model is null || model.Legacy)
            {
                return $"'{item.Key}' seçilebilir bir model değil.";
            }

            if (!item.Enabled)
            {
                continue;
            }

            if (item.AllowedQualities is null || item.AllowedQualities.Count == 0 || item.AllowedQualities.Any(q => model.Quality(q) is null))
            {
                return $"{model.DisplayName} için en az bir geçerli kalite seçilmeli.";
            }

            if (item.AllowedQualities.Count != item.AllowedQualities.Distinct().Count())
            {
                return $"{model.DisplayName} için aynı kalite birden fazla kez gönderildi.";
            }

            if (!item.AllowedQualities.Contains(item.DefaultQuality))
            {
                return $"{model.DisplayName} için varsayılan kalite izin verilenler arasında olmalı.";
            }
        }

        if (request.Models.Select(m => m.Key).Distinct().Count() != request.Models.Count)
        {
            return "Aynı model birden fazla kez gönderildi.";
        }

        return request.Models.Any(m => m.Enabled && m.Key == request.DefaultModel) ? null : "Varsayılan model etkin modellerden biri olmalı.";
    }

    private static async Task<IResult> OptionsAsync(AppDbContext db, CurrentUser user, IOptionsMonitor<GenerationSettings> generation, CancellationToken ct)
    {
        var settings = generation.CurrentValue;
        var policy = TryOnPolicyResolver.Resolve(await LoadSettingsAsync(db, user.OrganizationId, ct));
        var live = !settings.UseMockProviders;
        var models = policy.Models.Select(p => new TryOnModelOptionDto(
            p.Model.Key, p.Model.DisplayName, p.Model.Note, p.Model.LiveVerified, p.DefaultQuality,
            p.AllowedQualities.Select(q => ToDto(p.Model, p.Model.Quality(q)!, settings)).ToList())).ToList();
        return TypedResults.Ok(new TryOnOptionsDto(live, !live || settings.LiveFreeCombinationEnabled, policy.DefaultModel, models));
    }
}
