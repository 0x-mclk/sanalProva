using System.Security.Cryptography;
using System.Text;

namespace SanalProva.Infrastructure.Identity;

/// <summary>
/// Hesap kurulum/parola belirleme bağlantısının kriptografik temeli (M5.1b). Düz token 256 bit CSPRNG'dir, URL fragment'ında
/// taşınır (sunucuya hiç gitmez) ve hiçbir yerde saklanmaz; veritabanında yalnızca <c>HMAC-SHA256(Security:SetupTokenKey, token)</c>
/// tutulur (bkz. <see cref="AccountSetupToken.TokenHash"/>). HMAC (yalnızca SHA-256 değil) kullanılır: anahtar olmadan veritabanı
/// sızsa bile token'lar geri çözülemez/doğrulanamaz.
/// </summary>
public static class AccountSetupTokenCredentials
{
    public const int TokenBytesLength = 32; // 256 bit

    /// <summary>URL-güvenli (base64url), 256 bitlik rastgele token.</summary>
    public static string NewToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytesLength));

    /// <summary>HMAC-SHA256(key, token). Sabit 32 bayt döner (<c>account_setup_tokens.token_hash</c> ile eşleşir).</summary>
    public static byte[] HashToken(string token, string key) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(token));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
