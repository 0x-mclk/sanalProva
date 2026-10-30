using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SanalProva.Api.Features.Platform;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Identity;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.Management;

public sealed record StaffAssignment(Guid StoreId, StoreRole Role);

public sealed record StaffAssignmentDto(Guid StoreId, string StoreName, string StoreCode, bool StoreActive, StoreRole Role);

/// <param name="CanManage">Çağıran bu kişinin rolünü/durumunu değiştirebilir mi (arayüz düğmelerini buna göre gösterir; sunucu yine denetler).</param>
/// <param name="OpenLinkExpiresAt">Kullanılmamış, iptal edilmemiş, süresi dolmamış kurulum/sıfırlama bağlantısının bitişi (bağlantının kendisi asla gösterilmez).</param>
public sealed record StaffMemberDto(
    Guid Id, string DisplayName, string Email, bool IsActive, bool SetupPending, bool IsSelf, bool CanManage, DateTimeOffset? OpenLinkExpiresAt, IReadOnlyList<StaffAssignmentDto> Assignments);

public sealed record StaffSetupLinkDto(Guid UserId, string SetupLink, DateTimeOffset ExpiresAt, string Purpose);

public sealed record StaffInviteResult(StaffMemberDto Member, string SetupLink, DateTimeOffset ExpiresAt);

public enum StaffFailure
{
    None,
    NotFound,
    Forbidden,
    SelfChange,
    EmailTaken,
    StoreNotFound,
    StoreInactive,
    NoAssignment,
    ManagerLimit,
    LastOwner,
    UserInactive,
}

public sealed class StaffException(StaffFailure failure) : Exception(failure.ToString())
{
    public StaffFailure Failure { get; } = failure;
}

/// <summary>
/// Personel yönetimi iş kuralları (M5.2b/c). Çağıran her zaman bir Owner veya Manager'dır (<see cref="OrganizationRoleFilter"/>); asıl yetki sınırı burada, her işlemde veritabanındaki
/// güncel rollerden hesaplanır (istekteki iddiaya güvenilmez). Tüm yazmalar <see cref="AppLocks.OrganizationMembers"/> kilidi altında tek transaction'dadır: son Owner koruması ve
/// "Owner her şubede" değişmezi eşzamanlı isteklerle bozulamaz. Kişisel veri (e-posta, ad) denetim kaydına yazılmaz; düz token hiçbir yerde saklanmaz.
/// </summary>
public sealed class StaffManagementService(
    AppDbContext db, UserManager<AppUser> users, AccountSetupTokenService tokens, AuditTrail audit, IOptions<SetupTokenOptions> options, TimeProvider clock, SanalProva.Api.Hubs.StaffConnectionRegistry connections)
{
    /// <summary>Parola sıfırlama bağlantısının ömrü (saat): ilk kurulum bağlantısından kısa.</summary>
    public const int ResetLinkTtlHours = 24;

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private sealed record Actor(Guid UserId, Guid OrganizationId, bool IsOwner, HashSet<Guid> ManagedStores);

    public async Task<IReadOnlyList<StaffMemberDto>> ListAsync(Guid actorId, Guid organizationId, CancellationToken ct)
    {
        var actor = await LoadActorAsync(actorId, organizationId, ct);
        var roles = await db.UserStoreRoles.AsNoTracking().Where(r => r.OrganizationId == organizationId).ToListAsync(ct);
        var stores = await LoadStoresAsync(organizationId, ct);
        var visible = actor.IsOwner ? roles.Select(r => r.UserId).ToHashSet() : roles.Where(r => actor.ManagedStores.Contains(r.StoreId)).Select(r => r.UserId).ToHashSet();
        var members = await db.Users.AsNoTracking().Where(u => u.OrganizationId == organizationId && visible.Contains(u.Id)).OrderBy(u => u.DisplayName).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var links = (await db.AccountSetupTokens.AsNoTracking()
            .Where(t => t.OrganizationId == organizationId && t.UsedAt == null && t.RevokedAt == null && t.ExpiresAt > now)
            .Select(t => new { t.UserId, t.ExpiresAt }).ToListAsync(ct))
            .GroupBy(t => t.UserId).ToDictionary(g => g.Key, g => g.Max(t => t.ExpiresAt));
        var byUser = roles.GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.Select(r => new StaffAssignment(r.StoreId, r.Role)).ToList());

        return members.Select(u => ToDto(u, actor, byUser.GetValueOrDefault(u.Id) ?? [], stores, links.TryGetValue(u.Id, out var expiry) ? expiry : null)).ToList();
    }

    public async Task<StaffInviteResult> InviteAsync(Guid actorId, Guid organizationId, string email, string displayName, IReadOnlyList<StaffAssignment> requested, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AppLocks.AcquireExclusiveAsync(db, AppLocks.OrganizationMembers(organizationId), LockTimeout, ct);
        var actor = await LoadActorAsync(actorId, organizationId, ct);
        var stores = await LoadStoresAsync(organizationId, ct);
        var assignments = Normalize(requested, stores, existing: []);
        EnsureActorMayGrant(actor, assignments);

        var user = new AppUser { OrganizationId = organizationId, DisplayName = displayName, Email = email, UserName = email };
        var created = await users.CreateAsync(user);
        if (!created.Succeeded)
        {
            if (created.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName)))
            {
                throw new StaffException(StaffFailure.EmailTaken);
            }

            throw new InvalidOperationException("Personel hesabı oluşturulamadı: " + string.Join(",", created.Errors.Select(e => e.Code)));
        }

        foreach (var a in assignments)
        {
            db.UserStoreRoles.Add(new UserStoreRole { UserId = user.Id, StoreId = a.StoreId, OrganizationId = organizationId, Role = a.Role });
        }

        var (token, entity) = await tokens.IssueAsync(
            user.Id, organizationId, AccountSetupTokenPurpose.InitialPassword, TimeSpan.FromHours(options.Value.InitialPasswordTtlHours), actorId, ct);
        audit.RecordFor(organizationId, actorId, "User.Invited", nameof(AppUser), user.Id);
        audit.RecordFor(organizationId, actorId, "User.SetupLinkIssued", nameof(AccountSetupToken), entity.Id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new StaffInviteResult(ToDto(user, actor, assignments, stores, entity.ExpiresAt), PlatformOnboardingService.BuildLink(token), entity.ExpiresAt);
    }

    public async Task<StaffMemberDto> SetAssignmentsAsync(Guid actorId, Guid organizationId, Guid userId, IReadOnlyList<StaffAssignment> requested, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AppLocks.AcquireExclusiveAsync(db, AppLocks.OrganizationMembers(organizationId), LockTimeout, ct);
        var actor = await LoadActorAsync(actorId, organizationId, ct);
        var (user, current) = await LoadTargetAsync(userId, organizationId, ct);
        if (user.Id == actor.UserId)
        {
            throw new StaffException(StaffFailure.SelfChange);
        }

        var stores = await LoadStoresAsync(organizationId, ct);
        var currentAssignments = current.Select(r => new StaffAssignment(r.StoreId, r.Role)).ToList();
        List<StaffAssignment> final;
        if (actor.IsOwner)
        {
            final = Normalize(requested, stores, currentAssignments);
        }
        else
        {
            // Manager yalnızca kendi şubelerindeki Staff atamasını değiştirir; diğer şubelerdeki roller aynen korunur
            if (currentAssignments.Any(a => actor.ManagedStores.Contains(a.StoreId) && a.Role != StoreRole.Staff))
            {
                throw new StaffException(StaffFailure.Forbidden);
            }

            var proposed = Normalize(requested, stores, currentAssignments, allowEmpty: true);
            if (proposed.Any(a => a.Role != StoreRole.Staff || !actor.ManagedStores.Contains(a.StoreId)))
            {
                throw new StaffException(StaffFailure.ManagerLimit);
            }

            final = currentAssignments.Where(a => !actor.ManagedStores.Contains(a.StoreId)).Concat(proposed).ToList();
        }

        if (final.Count == 0)
        {
            throw new StaffException(StaffFailure.NoAssignment);
        }

        await EnsureNotLastOwnerAsync(user, organizationId, losesOwner: current.Any(r => r.Role == StoreRole.Owner) && !final.Any(a => a.Role == StoreRole.Owner), ct);

        var finalByStore = final.ToDictionary(a => a.StoreId);
        foreach (var row in current)
        {
            if (finalByStore.TryGetValue(row.StoreId, out var kept))
            {
                row.Role = kept.Role;
            }
            else
            {
                db.UserStoreRoles.Remove(row);
            }
        }

        foreach (var added in final.Where(a => current.All(r => r.StoreId != a.StoreId)))
        {
            db.UserStoreRoles.Add(new UserStoreRole { UserId = user.Id, StoreId = added.StoreId, OrganizationId = organizationId, Role = added.Role });
        }

        audit.RecordFor(organizationId, actorId, "User.RolesChanged", nameof(AppUser), user.Id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToDto(user, actor, final, stores, null);
    }

    public async Task<StaffMemberDto> SetActiveAsync(Guid actorId, Guid organizationId, Guid userId, bool isActive, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AppLocks.AcquireExclusiveAsync(db, AppLocks.OrganizationMembers(organizationId), LockTimeout, ct);
        var actor = await LoadActorAsync(actorId, organizationId, ct);
        var (user, current) = await LoadTargetAsync(userId, organizationId, ct);
        var assignments = current.Select(r => new StaffAssignment(r.StoreId, r.Role)).ToList();
        if (user.Id == actor.UserId)
        {
            throw new StaffException(StaffFailure.SelfChange);
        }

        EnsureActorMayManage(actor, assignments);
        if (user.IsActive != isActive)
        {
            if (!isActive)
            {
                await EnsureNotLastOwnerAsync(user, organizationId, losesOwner: current.Any(r => r.Role == StoreRole.Owner), ct);
            }

            user.IsActive = isActive;
            user.UpdatedAt = clock.GetUtcNow();
            // Açık oturumlar bir sonraki istekte düşer (ActiveUserCookieValidator); bekleyen bağlantılar da kullanılamaz hale gelir
            await users.UpdateSecurityStampAsync(user);
            if (!isActive)
            {
                await tokens.RevokeOpenAsync(user.Id, AccountSetupTokenPurpose.InitialPassword, ct);
                await tokens.RevokeOpenAsync(user.Id, AccountSetupTokenPurpose.PasswordReset, ct);
            }

            audit.RecordFor(organizationId, actorId, isActive ? "User.Activated" : "User.Deactivated", nameof(AppUser), user.Id);
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        if (!isActive)
        {
            connections.AbortUser(user.Id); // açık SignalR bağlantıları da hemen kesilir
        }

        var stores = await LoadStoresAsync(organizationId, ct);
        return ToDto(user, actor, assignments, stores, null);
    }

    /// <summary>Şifresini hiç belirlememiş hesap için yeni davet bağlantısı, şifresi olan hesap için parola sıfırlama bağlantısı üretir (önceki açık bağlantılar iptal olur).</summary>
    public async Task<StaffSetupLinkDto> IssueLinkAsync(Guid actorId, Guid organizationId, Guid userId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AppLocks.AcquireExclusiveAsync(db, AppLocks.OrganizationMembers(organizationId), LockTimeout, ct);
        var actor = await LoadActorAsync(actorId, organizationId, ct);
        var (user, current) = await LoadTargetAsync(userId, organizationId, ct);
        if (user.Id == actor.UserId)
        {
            throw new StaffException(StaffFailure.SelfChange);
        }

        EnsureActorMayManage(actor, current.Select(r => new StaffAssignment(r.StoreId, r.Role)).ToList());
        if (!user.IsActive)
        {
            throw new StaffException(StaffFailure.UserInactive);
        }

        var pending = user.PasswordHash is null;
        var purpose = pending ? AccountSetupTokenPurpose.InitialPassword : AccountSetupTokenPurpose.PasswordReset;
        var ttl = pending ? TimeSpan.FromHours(options.Value.InitialPasswordTtlHours) : TimeSpan.FromHours(ResetLinkTtlHours);
        var (token, entity) = await tokens.IssueAsync(user.Id, organizationId, purpose, ttl, actorId, ct);
        audit.RecordFor(organizationId, actorId, "User.SetupLinkIssued", nameof(AccountSetupToken), entity.Id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new StaffSetupLinkDto(user.Id, PlatformOnboardingService.BuildLink(token), entity.ExpiresAt, purpose.ToString());
    }

    public async Task RevokeLinksAsync(Guid actorId, Guid organizationId, Guid userId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AppLocks.AcquireExclusiveAsync(db, AppLocks.OrganizationMembers(organizationId), LockTimeout, ct);
        var actor = await LoadActorAsync(actorId, organizationId, ct);
        var (user, current) = await LoadTargetAsync(userId, organizationId, ct);
        EnsureActorMayManage(actor, current.Select(r => new StaffAssignment(r.StoreId, r.Role)).ToList());
        var revoked = await tokens.RevokeOpenAsync(user.Id, AccountSetupTokenPurpose.InitialPassword, ct) + await tokens.RevokeOpenAsync(user.Id, AccountSetupTokenPurpose.PasswordReset, ct);
        if (revoked > 0)
        {
            audit.RecordFor(organizationId, actorId, "User.SetupLinkRevoked", nameof(AppUser), user.Id);
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    // ---- Yetki ve kurallar ----

    private async Task<Actor> LoadActorAsync(Guid actorId, Guid organizationId, CancellationToken ct)
    {
        var rows = await db.UserStoreRoles.AsNoTracking().Where(r => r.UserId == actorId && r.OrganizationId == organizationId).ToListAsync(ct);
        var isOwner = rows.Any(r => r.Role == StoreRole.Owner);
        // Roller metin olarak saklanır: karşılaştırma bellekte yapılır
        var managed = rows.Where(r => r.Role >= StoreRole.Manager).Select(r => r.StoreId).ToHashSet();
        if (rows.Count == 0 || (!isOwner && managed.Count == 0))
        {
            throw new StaffException(StaffFailure.Forbidden);
        }

        return new Actor(actorId, organizationId, isOwner, managed);
    }

    private async Task<Dictionary<Guid, Store>> LoadStoresAsync(Guid organizationId, CancellationToken ct) =>
        await db.Stores.AsNoTracking().Where(s => s.OrganizationId == organizationId).ToDictionaryAsync(s => s.Id, ct);

    private async Task<(AppUser User, List<UserStoreRole> Roles)> LoadTargetAsync(Guid userId, Guid organizationId, CancellationToken ct)
    {
        // Kullanıcı tablosunda firma filtresi yoktur: başka firmanın kullanıcısı burada "yok" sayılır (ayrım sızdırılmaz)
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId && u.OrganizationId == organizationId, ct) ?? throw new StaffException(StaffFailure.NotFound);
        var roles = await db.UserStoreRoles.Where(r => r.UserId == userId && r.OrganizationId == organizationId).ToListAsync(ct);
        return (user, roles);
    }

    /// <summary>Owner her şeyi yönetir. Manager yalnızca TÜM rolleri Staff olan ve TÜM şubeleri kendi yönettiği şubeler olan kişiyi yönetir.</summary>
    private static void EnsureActorMayManage(Actor actor, List<StaffAssignment> target)
    {
        if (actor.IsOwner)
        {
            return;
        }

        if (target.Count == 0 || target.Any(a => a.Role != StoreRole.Staff || !actor.ManagedStores.Contains(a.StoreId)))
        {
            throw new StaffException(StaffFailure.Forbidden);
        }
    }

    private static void EnsureActorMayGrant(Actor actor, IReadOnlyList<StaffAssignment> assignments)
    {
        if (!actor.IsOwner && assignments.Any(a => a.Role != StoreRole.Staff || !actor.ManagedStores.Contains(a.StoreId)))
        {
            throw new StaffException(StaffFailure.ManagerLimit);
        }
    }

    /// <summary>Owner rolü verilirse firmadaki TÜM şubelerde Owner olur (değişmez). Şube bulunamaz/pasifse (yeni atamada) reddedilir; en az bir atama gerekir.</summary>
    private static List<StaffAssignment> Normalize(IReadOnlyList<StaffAssignment> requested, Dictionary<Guid, Store> stores, IReadOnlyList<StaffAssignment> existing, bool allowEmpty = false)
    {
        if (requested.Count == 0 && !allowEmpty)
        {
            throw new StaffException(StaffFailure.NoAssignment);
        }

        if (requested.Any(a => a.Role == StoreRole.Owner))
        {
            return stores.Keys.Select(id => new StaffAssignment(id, StoreRole.Owner)).ToList();
        }

        var result = new Dictionary<Guid, StaffAssignment>();
        foreach (var a in requested)
        {
            if (!stores.TryGetValue(a.StoreId, out var store))
            {
                throw new StaffException(StaffFailure.StoreNotFound);
            }

            if (!store.IsActive && !existing.Any(e => e.StoreId == a.StoreId && e.Role == a.Role))
            {
                throw new StaffException(StaffFailure.StoreInactive);
            }

            result[a.StoreId] = a; // aynı şube iki kez gelirse sonuncusu geçerli
        }

        return result.Values.ToList();
    }

    private async Task EnsureNotLastOwnerAsync(AppUser target, Guid organizationId, bool losesOwner, CancellationToken ct)
    {
        if (!losesOwner || !target.IsActive)
        {
            return;
        }

        var ownerIds = await db.UserStoreRoles.AsNoTracking().Where(r => r.OrganizationId == organizationId && r.Role == StoreRole.Owner).Select(r => r.UserId).Distinct().ToListAsync(ct);
        var otherActiveOwners = await db.Users.AsNoTracking().CountAsync(u => u.OrganizationId == organizationId && u.IsActive && u.Id != target.Id && ownerIds.Contains(u.Id), ct);
        if (otherActiveOwners == 0)
        {
            throw new StaffException(StaffFailure.LastOwner);
        }
    }

    private static StaffMemberDto ToDto(AppUser user, Actor actor, List<StaffAssignment> assignments, Dictionary<Guid, Store> stores, DateTimeOffset? linkExpiry)
    {
        var self = user.Id == actor.UserId;
        var canManage = !self && (actor.IsOwner || (assignments.Count > 0 && assignments.All(a => a.Role == StoreRole.Staff && actor.ManagedStores.Contains(a.StoreId))));
        var dtos = assignments
            .Where(a => stores.ContainsKey(a.StoreId))
            .OrderBy(a => stores[a.StoreId].Name, StringComparer.CurrentCulture)
            .Select(a => new StaffAssignmentDto(a.StoreId, stores[a.StoreId].Name, stores[a.StoreId].Code, stores[a.StoreId].IsActive, a.Role)).ToList();
        return new StaffMemberDto(user.Id, user.DisplayName, user.Email!, user.IsActive, user.PasswordHash is null, self, canManage, linkExpiry, dtos);
    }
}
