using SanalProva.Domain.Common;

namespace SanalProva.Infrastructure.Identity;

/// <summary>Bir <see cref="AccountSetupToken"/>'ın amacı.</summary>
public enum AccountSetupTokenPurpose
{
    /// <summary>Platform yöneticisinin oluşturduğu firmanın Owner'ı ilk şifresini belirler (M5.1).</summary>
    InitialPassword,

    /// <summary>Parola sıfırlama (M5.2+).</summary>
    PasswordReset,
}

/// <summary>
/// Tek kullanımlık, süreli hesap kurulum/parola belirleme bağlantısı (M5.1). Düz token hiçbir yerde saklanmaz: yalnızca
/// <see cref="TokenHash"/> (HMAC-SHA256, <c>Security:SetupTokenKey</c> ile) tutulur. Tüketim atomik olmalıdır: tek bir
/// transaction'da koşullu <c>UPDATE ... WHERE used_at IS NULL AND revoked_at IS NULL AND expires_at > @now</c> ile işaretlenir;
/// etkilenen satır sayısı 1 değilse işlem reddedilir (hata halinde tüm transaction geri alınır, token kullanılmamış kalır).
/// </summary>
public sealed class AccountSetupToken : ITenantOwned
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Token'ın sahibi (şifresini bu token ile belirleyecek kullanıcı). Her zaman <see cref="OrganizationId"/> ile aynı firmada.</summary>
    public Guid UserId { get; init; }

    public Guid OrganizationId { get; init; }

    public AccountSetupTokenPurpose Purpose { get; init; }

    /// <summary>HMAC-SHA256(Security:SetupTokenKey, token). Düz token asla saklanmaz.</summary>
    public required byte[] TokenHash { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? UsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Bağlantıyı üreten platform yöneticisi (bilgi amaçlı, denetim için). Platform yöneticisi hedef firmayla aynı firmada
    /// olmak zorunda değildir (genellikle ayrı "Platform" firmasındadır): bilinçli olarak FK yok (bkz. <c>AiCostReconciliation.DecidedByUserId</c>
    /// — aynı çapraz-firma gerekçesiyle FK'sız bırakılan mevcut örnek).
    /// </summary>
    public Guid? CreatedByUserId { get; init; }
}
