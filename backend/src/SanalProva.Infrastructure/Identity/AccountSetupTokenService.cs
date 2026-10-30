using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Infrastructure.Identity;

public sealed class SetupTokenOptions
{
    public const string Section = "Security";

    /// <summary>HMAC-SHA256 anahtarı (en az 32 karakter). Yalnızca gizli yapılandırmadan gelir (user-secrets / ortam değişkeni); kaynağa yazılmaz.</summary>
    [Required]
    [MinLength(32)]
    public string SetupTokenKey { get; set; } = string.Empty;
}

/// <summary>Salt okunur durum (<see cref="AccountSetupTokenService.ValidateAsync"/>). Dış API'ye tek bir "geçersiz" olarak yansıtılmalıdır (M5.1 §2.2): hangi dal olduğu yalnızca iç kullanım/testler içindir.</summary>
public enum SetupTokenValidity
{
    Valid,
    NotFound,
    Expired,
    Used,
    Revoked,
}

public enum SetupTokenConsumeStatus
{
    Success,
    Invalid,
}

/// <summary><see cref="AccountSetupTokenService.ConsumeAsync"/> sonucu. <see cref="Invalid"/> iken <see cref="UserId"/>/<see cref="OrganizationId"/> boştur.</summary>
public sealed record SetupTokenConsumeResult(SetupTokenConsumeStatus Status, Guid UserId, Guid OrganizationId)
{
    public static SetupTokenConsumeResult Succeeded(Guid userId, Guid organizationId) => new(SetupTokenConsumeStatus.Success, userId, organizationId);

    public static readonly SetupTokenConsumeResult Invalid = new(SetupTokenConsumeStatus.Invalid, Guid.Empty, Guid.Empty);
}

/// <summary>
/// Tek kullanımlık hesap kurulum/parola belirleme token'larının üretimi, doğrulanması, tüketimi ve iptali (M5.1b).
/// Token'lar (platform yöneticisinin oluşturduğu firma/kullanıcı, veya tüketimi yapan anonim ziyaretçi) çoğu zaman
/// çağıranın kendi kiracı bağlamıyla EŞLEŞMEZ: bu yüzden sorgular bilinçli olarak <c>IgnoreQueryFilters()</c> kullanır
/// (bkz. <c>TenantArchitectureTests</c> envanteri). Satırların kendisi firmaya (<c>organization_id</c>) bileşik FK ile
/// bağlıdır; bu, veritabanı düzeyinde tutarlılık için, API düzeyinde yetkilendirme için değildir — yetkilendirme çağıran
/// uç noktanın işidir (M5.1c/d).
/// Düz token bu servisten SADECE dönüş değeri olarak çıkar; hiçbir yerde (günlük, denetim kaydı, veritabanı) saklanmaz.
/// </summary>
public sealed class AccountSetupTokenService(AppDbContext db, IOptions<SetupTokenOptions> options, TimeProvider clock)
{
    /// <summary>D1: ilk şifre bağlantısının üst sınırı. Çağıran daha kısa bir süre isteyebilir, bunu aşamaz.</summary>
    public const int MaxTtlHours = 72;

    private readonly string _key = options.Value.SetupTokenKey;

    /// <summary>
    /// Yeni token üretir. Aynı kullanıcı+amaç için önceki açık (tüketilmemiş, iptal edilmemiş) token'ları ÖNCE iptal eder
    /// (M5.1 §2.1: yeniden üretim önceki bağlantıları geçersiz kılar); ilk üretimde iptal edilecek bir şey yoktur, no-op'tur.
    /// Düz token yalnızca dönüş değerindedir; çağıran bunu kalıcı hiçbir yere yazmamalı, günlüğe geçirmemelidir.
    /// </summary>
    public async Task<(string Token, AccountSetupToken Entity)> IssueAsync(
        Guid userId, Guid organizationId, AccountSetupTokenPurpose purpose, TimeSpan ttl, Guid? createdByUserId, CancellationToken ct)
    {
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromHours(MaxTtlHours))
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl, $"Süre 0'dan büyük ve en fazla {MaxTtlHours} saat olmalı.");
        }

        await RevokeOpenAsync(userId, purpose, ct);

        var now = clock.GetUtcNow();
        var plainToken = AccountSetupTokenCredentials.NewToken();
        var entity = new AccountSetupToken
        {
            UserId = userId,
            OrganizationId = organizationId,
            Purpose = purpose,
            TokenHash = AccountSetupTokenCredentials.HashToken(plainToken, _key),
            CreatedAt = now,
            ExpiresAt = now.Add(ttl),
            CreatedByUserId = createdByUserId,
        };
        db.AccountSetupTokens.Add(entity);
        await db.SaveChangesAsync(ct);
        return (plainToken, entity);
    }

    /// <summary>Tüketmeden durumu okur (ör. sayfa yüklenirken ön bilgi için). Asıl geçerlilik kararı <see cref="ConsumeAsync"/>'in atomik denetimidir; bu metot araya giren bir yarışı ÖNLEMEZ.</summary>
    public async Task<SetupTokenValidity> ValidateAsync(string plainToken, CancellationToken ct)
    {
        var hash = AccountSetupTokenCredentials.HashToken(plainToken, _key);
        var token = await db.AccountSetupTokens.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null)
        {
            return SetupTokenValidity.NotFound;
        }

        if (token.RevokedAt is not null)
        {
            return SetupTokenValidity.Revoked;
        }

        if (token.UsedAt is not null)
        {
            return SetupTokenValidity.Used;
        }

        if (token.ExpiresAt <= clock.GetUtcNow())
        {
            return SetupTokenValidity.Expired;
        }

        return SetupTokenValidity.Valid;
    }

    /// <summary>
    /// Atomik tüketim (M5.1 §2.2): tek bir koşullu <c>UPDATE</c>. SQL Server eşleşen satırı günceller/kilitler; eşzamanlı ikinci
    /// istek birincinin commit'ini bekler, ardından <c>used_at</c> artık NULL olmadığından 0 satır etkiler → <see cref="SetupTokenConsumeStatus.Invalid"/>.
    /// Eşzamanlı iki istekten EN FAZLA biri <see cref="SetupTokenConsumeStatus.Success"/> döner. Şifre yazma ile aynı transaction'da
    /// çalıştırma (rollback güvencesi) M5.1d'nin işidir; bu metot kendi başına tek bir atomik adımdır ve ambient transaction içinde de doğru çalışır.
    /// </summary>
    public async Task<SetupTokenConsumeResult> ConsumeAsync(string plainToken, CancellationToken ct)
    {
        var hash = AccountSetupTokenCredentials.HashToken(plainToken, _key);
        var now = clock.GetUtcNow();
        var affected = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE dbo.account_setup_tokens SET used_at = {now} WHERE token_hash = {hash} AND used_at IS NULL AND revoked_at IS NULL AND expires_at > {now}", ct);
        if (affected != 1)
        {
            return SetupTokenConsumeResult.Invalid;
        }

        var token = await db.AccountSetupTokens.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.TokenHash == hash, ct);
        return SetupTokenConsumeResult.Succeeded(token.UserId, token.OrganizationId);
    }

    /// <summary>Belirli bir kullanıcının açık (tüketilmemiş, iptal edilmemiş) token'larını amaç bazında iptal eder; yeni token üretmez. Etkilenen satır sayısını döner.</summary>
    public async Task<int> RevokeOpenAsync(Guid userId, AccountSetupTokenPurpose purpose, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var purposeText = purpose.ToString();
        return await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE dbo.account_setup_tokens SET revoked_at = {now} WHERE user_id = {userId} AND purpose = {purposeText} AND used_at IS NULL AND revoked_at IS NULL", ct);
    }
}
