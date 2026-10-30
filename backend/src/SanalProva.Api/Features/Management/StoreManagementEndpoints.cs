using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Display;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Displays;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Tenancy;

namespace SanalProva.Api.Features.Management;

/// <param name="Code">Şube kısa kodu (harf, rakam, tire); büyük harfe çevrilir, firmada benzersizdir.</param>
/// <param name="DailyGenerationQuota">Şube günlük üretim sınırı; boşsa firma ayarı geçerlidir. Maliyet sınırı (BudgetGuard) bundan bağımsız çalışır.</param>
public sealed record CreateStoreRequest(
    [property: Required, StringLength(120, MinimumLength = 2)] string Name,
    [property: Required, StringLength(30, MinimumLength = 1), RegularExpression("^[A-Za-z0-9]+(-[A-Za-z0-9]+)*$")] string Code,
    [property: StringLength(60)] string? Timezone,
    [property: Range(1, StoreManagementEndpoints.MaxStoreQuota)] int? DailyGenerationQuota);

public sealed record UpdateStoreRequest(
    [property: Required, StringLength(120, MinimumLength = 2)] string Name,
    [property: Required, StringLength(30, MinimumLength = 1), RegularExpression("^[A-Za-z0-9]+(-[A-Za-z0-9]+)*$")] string Code,
    [property: Required, StringLength(60)] string Timezone,
    [property: Range(1, StoreManagementEndpoints.MaxStoreQuota)] int? DailyGenerationQuota);

public sealed record SetStoreStatusRequest(bool IsActive);

/// <param name="MemberCount">Şubede rolü olan etkin kullanıcı sayısı (Owner'lar dahil).</param>
public sealed record StoreAdminDto(Guid Id, string Name, string Code, string Timezone, int? DailyGenerationQuota, bool IsActive, int MemberCount, DateTimeOffset CreatedAt);

/// <summary>
/// Şube yönetimi (M5.2a): yalnızca Owner. Firma kapsamı EF filtresinden gelir; başka firmanın şubesi "yok" gibi davranır (404).
/// Owner, firmadaki TÜM şubelerde rol satırı taşır (tasarım değişmezi): yeni şube açılırken tüm Owner'lara satır eklenir ve bu
/// <see cref="AppLocks.OrganizationMembers"/> kilidi altında yapılır, böylece eşzamanlı Owner eklemesi bir şubeyi atlayamaz.
/// </summary>
public static class StoreManagementEndpoints
{
    public const int MaxStoreQuota = 100_000;

    public static RouteGroupBuilder MapStoreManagementEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/management/stores").WithTags("Yönetim").RequireOrganizationRole(StoreRole.Owner);
        group.MapGet("", ListAsync).Produces<IReadOnlyList<StoreAdminDto>>();
        group.MapPost("", CreateAsync).Produces<StoreAdminDto>(StatusCodes.Status201Created);
        group.MapPut("/{id:guid}", UpdateAsync).Produces<StoreAdminDto>();
        group.MapPatch("/{id:guid}/status", SetStatusAsync).Produces<StoreAdminDto>();
        return api;
    }

    private static async Task<IResult> ListAsync(AppDbContext db, CurrentUser user, CancellationToken ct)
    {
        var stores = await db.Stores.AsNoTracking().Where(s => s.OrganizationId == user.OrganizationId).OrderBy(s => s.CreatedAt).ToListAsync(ct);
        var counts = await MemberCountsAsync(db, user.OrganizationId, ct);
        return TypedResults.Ok(stores.Select(s => ToDto(s, counts.GetValueOrDefault(s.Id))).ToList());
    }

    private static async Task<IResult> CreateAsync(CreateStoreRequest request, AppDbContext db, CurrentUser user, AuditTrail audit, CancellationToken ct)
    {
        var timezone = string.IsNullOrWhiteSpace(request.Timezone) ? "Europe/Istanbul" : request.Timezone.Trim();
        if (!IsValidTimezone(timezone))
        {
            return ApiProblems.BadRequest("TIMEZONE_INVALID", "Saat dilimi tanınmıyor.");
        }

        var code = request.Code.Trim().ToUpperInvariant();
        Store store;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await AppLocks.AcquireExclusiveAsync(db, AppLocks.OrganizationMembers(user.OrganizationId), TimeSpan.FromSeconds(10), ct);
            if (await db.Stores.AnyAsync(s => s.OrganizationId == user.OrganizationId && s.Code == code, ct))
            {
                return ApiProblems.Conflict("STORE_CODE_TAKEN", "Bu şube kodu zaten kullanılıyor.");
            }

            store = new Store { OrganizationId = user.OrganizationId, Name = request.Name.Trim(), Code = code, Timezone = timezone, DailyGenerationQuota = request.DailyGenerationQuota };
            db.Stores.Add(store);
            var ownerIds = await db.UserStoreRoles.Where(r => r.OrganizationId == user.OrganizationId && r.Role == StoreRole.Owner).Select(r => r.UserId).Distinct().ToListAsync(ct);
            foreach (var ownerId in ownerIds)
            {
                db.UserStoreRoles.Add(new UserStoreRole { UserId = ownerId, StoreId = store.Id, OrganizationId = user.OrganizationId, Role = StoreRole.Owner });
            }

            audit.Record("Store.Created", nameof(Store), store.Id);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        return TypedResults.Created($"/api/v1/management/stores/{store.Id}", ToDto(store, await MemberCountAsync(db, store.Id, user.OrganizationId, ct)));
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateStoreRequest request, AppDbContext db, CurrentUser user, AuditTrail audit, CancellationToken ct)
    {
        var timezone = request.Timezone.Trim();
        if (!IsValidTimezone(timezone))
        {
            return ApiProblems.BadRequest("TIMEZONE_INVALID", "Saat dilimi tanınmıyor.");
        }

        var store = await db.Stores.SingleOrDefaultAsync(s => s.Id == id && s.OrganizationId == user.OrganizationId, ct);
        if (store is null)
        {
            return ApiProblems.NotFound("Şube bulunamadı.");
        }

        var code = request.Code.Trim().ToUpperInvariant();
        if (!string.Equals(store.Code, code, StringComparison.Ordinal) && await db.Stores.AnyAsync(s => s.OrganizationId == user.OrganizationId && s.Code == code && s.Id != id, ct))
        {
            return ApiProblems.Conflict("STORE_CODE_TAKEN", "Bu şube kodu zaten kullanılıyor.");
        }

        store.Name = request.Name.Trim();
        store.Code = code;
        store.Timezone = timezone;
        store.DailyGenerationQuota = request.DailyGenerationQuota;
        audit.Record("Store.Updated", nameof(Store), store.Id);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            return ApiProblems.Conflict("STORE_CODE_TAKEN", "Bu şube kodu zaten kullanılıyor.");
        }

        return TypedResults.Ok(ToDto(store, await MemberCountAsync(db, store.Id, user.OrganizationId, ct)));
    }

    /// <summary>
    /// Pasife alma: şubeye erişim hemen kesilir (<see cref="StoreAccessFilter"/> etkin şube arar) ve şubenin TV eşleştirmeleri iptal edilir (ekran boşalır).
    /// Firmadaki son etkin şube pasife alınamaz. Veri silinmez; yeniden etkinleştirince TV'ler yeniden eşleştirilmelidir.
    /// </summary>
    private static async Task<IResult> SetStatusAsync(
        Guid id, SetStoreStatusRequest request, AppDbContext db, CurrentUser user, AuditTrail audit, DisplayStateService display, TenantContext tenant, TimeProvider clock, CancellationToken ct)
    {
        List<Guid> deviceIds = [];
        Store store;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await AppLocks.AcquireExclusiveAsync(db, AppLocks.OrganizationMembers(user.OrganizationId), TimeSpan.FromSeconds(10), ct);
            var found = await db.Stores.SingleOrDefaultAsync(s => s.Id == id && s.OrganizationId == user.OrganizationId, ct);
            if (found is null)
            {
                return ApiProblems.NotFound("Şube bulunamadı.");
            }

            store = found;
            if (store.IsActive != request.IsActive)
            {
                if (!request.IsActive && !await db.Stores.AnyAsync(s => s.OrganizationId == user.OrganizationId && s.IsActive && s.Id != id, ct))
                {
                    return ApiProblems.Conflict("LAST_ACTIVE_STORE", "Firmadaki son etkin şube pasife alınamaz.");
                }

                store.IsActive = request.IsActive;
                audit.Record(request.IsActive ? "Store.Activated" : "Store.Deactivated", nameof(Store), store.Id);
                await db.SaveChangesAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }

        if (!request.IsActive)
        {
            // TV'ler şube kapsamıyla (IStoreOwned filtresi) bulunur; durum yalnızca DisplayStateService üzerinden değişir
            tenant.StoreId = id;
            deviceIds = await db.DisplayDevices.AsNoTracking().Where(d => d.StoreId == id && d.RevokedAt == null).Select(d => d.Id).ToListAsync(ct);
            foreach (var deviceId in deviceIds)
            {
                try
                {
                    await display.MutateAsync(deviceId, device =>
                    {
                        device.RevokedAt = clock.GetUtcNow();
                        device.State = new DisplayState();
                        device.CurrentSessionId = null;
                        return DisplayStateReason.DeviceRevoked;
                    }, null, ct);
                }
                catch (DisplayNotFoundException)
                {
                    // Aynı anda elle iptal edilmiş: hedeflenen durum zaten sağlandı
                }
            }

            tenant.StoreId = null;
        }

        return TypedResults.Ok(ToDto(store, await MemberCountAsync(db, store.Id, user.OrganizationId, ct)));
    }

    private static bool IsValidTimezone(string id) => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);

    private static StoreAdminDto ToDto(Store s, int members) => new(s.Id, s.Name, s.Code, s.Timezone, s.DailyGenerationQuota, s.IsActive, members, s.CreatedAt);

    private static async Task<int> MemberCountAsync(AppDbContext db, Guid storeId, Guid organizationId, CancellationToken ct) =>
        (await MemberCountsAsync(db, organizationId, ct)).GetValueOrDefault(storeId);

    private static async Task<Dictionary<Guid, int>> MemberCountsAsync(AppDbContext db, Guid organizationId, CancellationToken ct) =>
        await db.UserStoreRoles.AsNoTracking()
            .Where(r => r.OrganizationId == organizationId)
            .Join(db.Users.Where(u => u.OrganizationId == organizationId && u.IsActive), r => r.UserId, u => u.Id, (r, _) => r.StoreId)
            .GroupBy(storeId => storeId).Select(g => new { StoreId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.StoreId, x => x.Count, ct);
}
