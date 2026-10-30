using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Identity;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.Platform;

public enum CreateOrganizationOutcome
{
    Created,
    Replayed,
    KeyReused,
    SlugTaken,
    EmailTaken,
}

public sealed record CreatedOrganization(Guid OrganizationId, Guid StoreId, Guid OwnerUserId, string? SetupLink, DateTimeOffset? SetupLinkExpiresAt);

public enum ReissueOutcome
{
    Issued,
    OrganizationNotFound,
    NoPendingOwner,
}

public sealed record ReissuedLink(Guid OrganizationId, Guid OwnerUserId, string SetupLink, DateTimeOffset ExpiresAt);

/// <summary>
/// Platform yöneticisinin firma + ilk şube + Owner oluşturması ve kurulum bağlantısını yeniden üretmesi (M5.1c). Platform işlemleridir: hedef firma
/// çağıranın kendi firması değildir, bu yüzden firma kapsamlı sorgular bilinçli olarak <c>IgnoreQueryFilters()</c> kullanır (yalnızca
/// <see cref="PlatformAdminFilter"/> arkasından çağrılır; envanter: <c>TenantArchitectureTests</c>). Hiçbir yanıt/kayıt düz token'ı saklamaz:
/// idempotency yalnızca firma satırındaki anahtar + istek özetidir; tekrarda bağlantı yeniden gösterilmez.
/// </summary>
public sealed class PlatformOnboardingService(
    AppDbContext db, UserManager<AppUser> users, AccountSetupTokenService tokens, AuditTrail audit, IOptions<SetupTokenOptions> options)
{
    public const string SetupPath = "/hesap/sifre-belirle";

    public async Task<(CreateOrganizationOutcome Outcome, CreatedOrganization? Result)> CreateOrganizationAsync(
        Guid adminUserId, string idempotencyKey, NormalizedOrganizationRequest request, CancellationToken ct)
    {
        var hash = Fingerprint(request, options.Value.SetupTokenKey);

        var replay = await TryReplayAsync(adminUserId, idempotencyKey, hash, ct);
        if (replay is not null)
        {
            return replay.Value;
        }

        if (await db.Organizations.AnyAsync(o => o.Slug == request.Slug, ct))
        {
            return (CreateOrganizationOutcome.SlugTaken, null);
        }

        if (await users.FindByEmailAsync(request.OwnerEmail) is not null)
        {
            return (CreateOrganizationOutcome.EmailTaken, null);
        }

        try
        {
            return (CreateOrganizationOutcome.Created, await InsertAsync(adminUserId, idempotencyKey, hash, request, ct));
        }
        catch (EmailTakenException)
        {
            return (CreateOrganizationOutcome.EmailTaken, null);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            // Eşzamanlı ikinci istek: benzersiz indeks kazananı belirledi. Neden anahtar/slug/e-posta olduğu yeniden okunarak belirlenir.
            db.ChangeTracker.Clear();
            var raced = await TryReplayAsync(adminUserId, idempotencyKey, hash, ct);
            if (raced is not null)
            {
                return raced.Value;
            }

            if (await db.Organizations.AnyAsync(o => o.Slug == request.Slug, ct))
            {
                return (CreateOrganizationOutcome.SlugTaken, null);
            }

            if (await users.FindByEmailAsync(request.OwnerEmail) is not null)
            {
                return (CreateOrganizationOutcome.EmailTaken, null);
            }

            throw;
        }
    }

    private async Task<(CreateOrganizationOutcome, CreatedOrganization?)?> TryReplayAsync(Guid adminUserId, string key, byte[] hash, CancellationToken ct)
    {
        var existing = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(o => o.CreatedByUserId == adminUserId && o.CreationRequestKey == key, ct);
        if (existing is null)
        {
            return null;
        }

        if (existing.CreationRequestHash is null || !CryptographicOperations.FixedTimeEquals(existing.CreationRequestHash, hash))
        {
            return (CreateOrganizationOutcome.KeyReused, null);
        }

        var storeId = await db.Stores.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == existing.Id).OrderBy(s => s.CreatedAt).Select(s => s.Id).FirstAsync(ct);
        var ownerId = await db.UserStoreRoles.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == existing.Id && r.StoreId == storeId).Select(r => r.UserId).FirstAsync(ct);
        // Düz token hiçbir yerde saklanmadığı için tekrarda bağlantı verilemez (yeniden üretim ayrı uç noktadır)
        return (CreateOrganizationOutcome.Replayed, new CreatedOrganization(existing.Id, storeId, ownerId, null, null));
    }

    private async Task<CreatedOrganization> InsertAsync(Guid adminUserId, string key, byte[] hash, NormalizedOrganizationRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var organization = new Organization { Name = request.Name, Slug = request.Slug, CreatedByUserId = adminUserId, CreationRequestKey = key, CreationRequestHash = hash };
        var store = new Store { OrganizationId = organization.Id, Name = request.StoreName, Code = request.StoreCode };
        db.Organizations.Add(organization);
        db.Stores.Add(store);
        await db.SaveChangesAsync(ct);

        // Parolasız hesap: giriş yapılamaz (PasswordHash null) ta ki kurulum bağlantısıyla şifre belirlenene kadar
        var owner = new AppUser { OrganizationId = organization.Id, DisplayName = request.OwnerDisplayName, Email = request.OwnerEmail, UserName = request.OwnerEmail };
        var created = await users.CreateAsync(owner);
        if (!created.Succeeded)
        {
            if (created.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName)))
            {
                throw new EmailTakenException();
            }

            throw new InvalidOperationException("Owner hesabı oluşturulamadı: " + string.Join(",", created.Errors.Select(e => e.Code)));
        }

        db.UserStoreRoles.Add(new UserStoreRole { UserId = owner.Id, StoreId = store.Id, OrganizationId = organization.Id, Role = StoreRole.Owner });
        var (token, entity) = await tokens.IssueAsync(
            owner.Id, organization.Id, AccountSetupTokenPurpose.InitialPassword, TimeSpan.FromHours(options.Value.InitialPasswordTtlHours), adminUserId, ct);

        audit.RecordFor(organization.Id, adminUserId, "Platform.OrganizationCreated", nameof(Organization), organization.Id);
        audit.RecordFor(organization.Id, adminUserId, "Platform.SetupLinkIssued", nameof(AccountSetupToken), entity.Id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new CreatedOrganization(organization.Id, store.Id, owner.Id, BuildLink(token), entity.ExpiresAt);
    }

    public async Task<(ReissueOutcome Outcome, ReissuedLink? Result)> ReissueOwnerLinkAsync(Guid adminUserId, Guid organizationId, CancellationToken ct)
    {
        if (!await db.Organizations.AnyAsync(o => o.Id == organizationId, ct))
        {
            return (ReissueOutcome.OrganizationNotFound, null);
        }

        // Yalnızca henüz şifresini belirlememiş Owner için bağlantı üretilir; şifre sıfırlama ayrı akıştır (M5.2)
        var ownerIds = db.UserStoreRoles.IgnoreQueryFilters().Where(r => r.OrganizationId == organizationId && r.Role == StoreRole.Owner).Select(r => r.UserId);
        var owner = await db.Users.AsNoTracking()
            .Where(u => ownerIds.Contains(u.Id) && u.OrganizationId == organizationId && u.IsActive && u.PasswordHash == null)
            .OrderBy(u => u.CreatedAt).FirstOrDefaultAsync(ct);
        if (owner is null)
        {
            return (ReissueOutcome.NoPendingOwner, null);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (token, entity) = await tokens.IssueAsync(
            owner.Id, organizationId, AccountSetupTokenPurpose.InitialPassword, TimeSpan.FromHours(options.Value.InitialPasswordTtlHours), adminUserId, ct);
        audit.RecordFor(organizationId, adminUserId, "Platform.SetupLinkIssued", nameof(AccountSetupToken), entity.Id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (ReissueOutcome.Issued, new ReissuedLink(organizationId, owner.Id, BuildLink(token), entity.ExpiresAt));
    }

    /// <summary>Bağlantıdaki token URL fragment'ındadır (<c>#t=</c>): tarayıcı fragment'ı sunucuya göndermez, istek günlüklerine/Referer'a girmez.</summary>
    public static string BuildLink(string token) => $"{SetupPath}#t={token}";

    /// <summary>İstek özeti: aynı anahtarla farklı gövdeyi ayırt eder. HMAC (anahtarlı): veritabanı sızsa bile e-posta/ad sözlükle doğrulanamaz.</summary>
    private static byte[] Fingerprint(NormalizedOrganizationRequest r, string key)
    {
        var canonical = new StringBuilder("platform.org.create.v1");
        foreach (var field in new[] { r.Name, r.Slug, r.StoreName, r.StoreCode, r.OwnerEmail.ToLowerInvariant(), r.OwnerDisplayName })
        {
            canonical.Append('\n').Append(field.Length).Append(':').Append(field);
        }

        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(canonical.ToString()));
    }

    private sealed class EmailTakenException : Exception;
}

public sealed record NormalizedOrganizationRequest(string Name, string Slug, string StoreName, string StoreCode, string OwnerEmail, string OwnerDisplayName);
