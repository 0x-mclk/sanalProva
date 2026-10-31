using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Features.Agreement;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.Platform;

/// <param name="Slug">Küçük harf, rakam ve tire (3–60). Firma adresi için benzersizdir.</param>
/// <param name="StoreCode">İlk şubenin kısa kodu (harf, rakam, tire; büyük harfe çevrilir).</param>
public sealed record CreateOrganizationRequest(
    [property: Required, StringLength(120, MinimumLength = 2)] string Name,
    [property: Required, StringLength(60, MinimumLength = 3), RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$")] string Slug,
    [property: Required, StringLength(120, MinimumLength = 2)] string StoreName,
    [property: Required, StringLength(30, MinimumLength = 1), RegularExpression("^[A-Za-z0-9]+(-[A-Za-z0-9]+)*$")] string StoreCode,
    [property: Required, StringLength(254), EmailAddress] string OwnerEmail,
    [property: Required, StringLength(80, MinimumLength = 2)] string OwnerDisplayName);

/// <param name="SetupLink">Göreli yol + fragment (<c>/hesap/sifre-belirle#t=…</c>); istemci kendi kökünü ekler. <b>Yalnızca ilk yanıtta</b> dolu; idempotency tekrarında ve yeniden üretim dışında asla yeniden gösterilmez.</param>
/// <param name="SetupLinkShown">İlk yanıtta <c>true</c>; tekrarda <c>false</c> (bağlantı gerekiyorsa yeniden üretilir).</param>
public sealed record CreateOrganizationResponse(
    Guid OrganizationId, Guid StoreId, Guid OwnerUserId, string? SetupLink, DateTimeOffset? SetupLinkExpiresAt, bool SetupLinkShown);

/// <param name="AgreementAccepted">Firma GÜNCEL hizmet sözleşmesi sürümünü kabul etti mi (müşteri KVKK onayından ayrıdır).</param>
/// <param name="SettingsCorrupt">Firma ayar JSON'u okunamıyor (bozuk): ücretli AI kapalı sayılır, bütçe alanları boştur. Diğer alanlar güvenle döner.</param>
public sealed record OrganizationListItem(Guid Id, string Name, string Slug, bool IsActive, bool PaidAiEnabled, bool OwnerSetupPending, bool AgreementAccepted, DateTimeOffset CreatedAt, bool SettingsCorrupt = false);

public sealed record OrganizationStoreItem(Guid Id, string Name, string Code, bool IsActive);

public sealed record OrganizationOwnerItem(Guid UserId, string DisplayName, string Email, bool SetupPending);

public sealed record OrganizationDetail(
    Guid Id, string Name, string Slug, bool IsActive, bool PaidAiEnabled, DateTimeOffset CreatedAt, bool AgreementAccepted, DateTimeOffset? AgreementAcceptedAt, string AgreementVersion,
    IReadOnlyList<OrganizationStoreItem> Stores, IReadOnlyList<OrganizationOwnerItem> Owners, decimal? DailyBudgetUsd, decimal? MonthlyBudgetUsd, decimal? MaxJobCostUsd, int CreditBalance, int CreditReserved, bool SettingsCorrupt = false);

public sealed record SetupLinkResponse(Guid OrganizationId, Guid OwnerUserId, string SetupLink, DateTimeOffset SetupLinkExpiresAt);

/// <summary>
/// Platform yöneticisinin işletme (firma + ilk şube + Owner) oluşturması (M5.1c). Yalnızca platform yöneticisi; halka açık kayıt yoktur.
/// Platform yöneticisinin firmalarda rolü yoktur: yeni firmanın fotoğraf, oturum veya kataloğuna bu uç noktalarla erişemez (okuma yalnızca
/// özet/yönetim bilgisi döner). Her işlem hedef firmaya denetim kaydı bırakır; kayıtlarda kişisel veri ve token yoktur.
/// </summary>
public static class PlatformOrganizationEndpoints
{
    public static RouteGroupBuilder MapPlatformOrganizationEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/platform/organizations").WithTags("Platform").RequirePlatformAdmin();
        group.MapPost("", CreateAsync).Produces<CreateOrganizationResponse>(StatusCodes.Status201Created).Produces<CreateOrganizationResponse>();
        group.MapGet("", ListAsync).Produces<IReadOnlyList<OrganizationListItem>>();
        group.MapGet("/{id:guid}", GetAsync).Produces<OrganizationDetail>();
        group.MapPost("/{id:guid}/owner/setup-link", ReissueAsync).Produces<SetupLinkResponse>();
        return api;
    }

    private static async Task<IResult> CreateAsync(
        CreateOrganizationRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        PlatformOnboardingService service, CurrentUser user, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length is < 8 or > 64)
        {
            return ApiProblems.BadRequest("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key başlığı (8–64 karakter) gerekli.");
        }

        var normalized = new NormalizedOrganizationRequest(
            request.Name.Trim(), request.Slug.Trim(), request.StoreName.Trim(), request.StoreCode.Trim().ToUpperInvariant(), request.OwnerEmail.Trim(), request.OwnerDisplayName.Trim());
        var (outcome, result) = await service.CreateOrganizationAsync(user.UserId, idempotencyKey, normalized, ct);
        NoStore(http);

        switch (outcome)
        {
            case CreateOrganizationOutcome.KeyReused:
                return ApiProblems.Unprocessable("IDEMPOTENCY_KEY_REUSED", "Bu Idempotency-Key farklı bir istek için kullanılmış.");
            case CreateOrganizationOutcome.SlugTaken:
                return ApiProblems.Conflict("SLUG_TAKEN", "Bu firma adresi (slug) zaten kullanılıyor.");
            case CreateOrganizationOutcome.EmailTaken:
                return ApiProblems.Conflict("EMAIL_TAKEN", "Bu e-posta adresiyle zaten bir hesap var.");
        }

        var body = new CreateOrganizationResponse(
            result!.OrganizationId, result.StoreId, result.OwnerUserId, result.SetupLink, result.SetupLinkExpiresAt, SetupLinkShown: result.SetupLink is not null);
        return outcome == CreateOrganizationOutcome.Created
            ? TypedResults.Created($"/api/v1/platform/organizations/{result.OrganizationId}", body)
            : TypedResults.Ok(body);
    }

    private static async Task<IResult> ListAsync(AppDbContext db, AgreementGate gate, CancellationToken ct, int take = 50)
    {
        take = Math.Clamp(take, 1, 200);
        // Ayar JSON'u listeyi düşürmesin: skaler sütunlar toplu, ayar firma başına dayanıklı okunur (bozuksa SettingsCorrupt, fail-closed)
        var organizations = await db.Organizations.AsNoTracking().OrderByDescending(o => o.CreatedAt).Take(take).Select(o => new { o.Id, o.Name, o.Slug, o.IsActive, o.CreatedAt }).ToListAsync(ct);
        var ids = organizations.Select(o => o.Id).ToList();
        var pending = (await PendingOwnersAsync(db, ids, ct)).Select(p => p.OrganizationId).ToHashSet();
        var version = gate.Current.Version;
        var accepted = (await db.OrganizationAgreementAcceptances.IgnoreQueryFilters().AsNoTracking()
            .Where(a => ids.Contains(a.OrganizationId) && a.AgreementVersion == version).Select(a => a.OrganizationId).ToListAsync(ct)).ToHashSet();
        var items = new List<OrganizationListItem>(organizations.Count);
        foreach (var o in organizations)
        {
            var (settings, corrupt) = await SanalProva.Infrastructure.Monitoring.OrganizationSettingsReader.ReadAsync(db, o.Id, ct);
            items.Add(new OrganizationListItem(o.Id, o.Name, o.Slug, o.IsActive, !corrupt && settings!.PaidAiEnabled, pending.Contains(o.Id), accepted.Contains(o.Id), o.CreatedAt, corrupt));
        }

        return TypedResults.Ok(items);
    }

    private static async Task<IResult> GetAsync(Guid id, AppDbContext db, AgreementGate gate, CancellationToken ct)
    {
        var organization = await db.Organizations.AsNoTracking().Where(o => o.Id == id).Select(o => new { o.Id, o.Name, o.Slug, o.IsActive, o.CreatedAt }).SingleOrDefaultAsync(ct);
        if (organization is null)
        {
            return ApiProblems.NotFound("Firma bulunamadı.");
        }

        var (settings, corrupt) = await SanalProva.Infrastructure.Monitoring.OrganizationSettingsReader.ReadAsync(db, id, ct);

        var stores = await db.Stores.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == id).OrderBy(s => s.CreatedAt)
            .Select(s => new OrganizationStoreItem(s.Id, s.Name, s.Code, s.IsActive)).ToListAsync(ct);
        var ownerIds = db.UserStoreRoles.IgnoreQueryFilters().Where(r => r.OrganizationId == id && r.Role == StoreRole.Owner).Select(r => r.UserId).Distinct();
        var owners = await db.Users.AsNoTracking().Where(u => u.OrganizationId == id && ownerIds.Contains(u.Id)).OrderBy(u => u.CreatedAt)
            .Select(u => new OrganizationOwnerItem(u.Id, u.DisplayName, u.Email!, u.PasswordHash == null)).ToListAsync(ct);
        var credit = await db.CreditAccounts.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(a => a.OrganizationId == id, ct);
        var version = gate.Current.Version;
        var acceptedAt = await db.OrganizationAgreementAcceptances.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == id && a.AgreementVersion == version).Select(a => (DateTimeOffset?)a.AcceptedAt).SingleOrDefaultAsync(ct);
        return TypedResults.Ok(new OrganizationDetail(organization.Id, organization.Name, organization.Slug, organization.IsActive, !corrupt && settings!.PaidAiEnabled, organization.CreatedAt,
            acceptedAt is not null, acceptedAt, version, stores, owners,
            corrupt ? null : settings!.DailyBudgetUsd, corrupt ? null : settings!.MonthlyBudgetUsd, corrupt ? null : settings!.MaxJobCostUsd, credit?.BalanceCredits ?? 0, credit?.ReservedCredits ?? 0, corrupt));
    }

    private static async Task<IResult> ReissueAsync(
        Guid id, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, PlatformOnboardingService service, CurrentUser user, HttpContext http, CancellationToken ct)
    {
        if (idempotencyKey is not null && idempotencyKey.Length is < 8 or > 64)
        {
            return ApiProblems.BadRequest("IDEMPOTENCY_KEY_INVALID", "Idempotency-Key 8–64 karakter olmalı.");
        }

        var (outcome, result) = await service.ReissueOwnerLinkAsync(user.UserId, id, ct, idempotencyKey);
        NoStore(http);
        return outcome switch
        {
            ReissueOutcome.OrganizationNotFound => ApiProblems.NotFound("Firma bulunamadı."),
            ReissueOutcome.AlreadyIssued => ApiProblems.Conflict("SETUP_LINK_ALREADY_ISSUED", "Bu istekle bağlantı zaten üretildi ve bir daha gösterilemez. Yeni bir bağlantı gerekiyorsa yeniden üretin (öncekini iptal eder)."),
            ReissueOutcome.NoPendingOwner => ApiProblems.Conflict("OWNER_ALREADY_ACTIVATED", "Owner şifresini zaten belirlemiş (veya bekleyen Owner yok); yeni bağlantı üretilemez."),
            _ => TypedResults.Ok(new SetupLinkResponse(result!.OrganizationId, result.OwnerUserId, result.SetupLink, result.ExpiresAt)),
        };
    }

    private static async Task<List<(Guid OrganizationId, Guid UserId)>> PendingOwnersAsync(AppDbContext db, List<Guid> organizationIds, CancellationToken ct)
    {
        var rows = await db.UserStoreRoles.IgnoreQueryFilters().AsNoTracking()
            .Where(r => organizationIds.Contains(r.OrganizationId) && r.Role == StoreRole.Owner)
            .Join(db.Users.Where(u => u.PasswordHash == null), r => r.UserId, u => u.Id, (r, u) => new { r.OrganizationId, UserId = u.Id })
            .Distinct().ToListAsync(ct);
        return rows.Select(r => (r.OrganizationId, r.UserId)).ToList();
    }

    /// <summary>Yanıt kurulum bağlantısı içerebilir: hiçbir ara katman/tarayıcı önbelleğe almamalı.</summary>
    internal static void NoStore(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
    }
}
