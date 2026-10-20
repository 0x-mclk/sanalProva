using System.Security.Cryptography;
using System.Text;

namespace SanalProva.Infrastructure.Displays;

/// <summary>
/// TV eşleştirme kimlik bilgileri. Kod 6 haneli sayıdır (TV'de okunup personel telefonunda yazılır) ve yalnızca kısa süre geçerlidir;
/// az sayıda olası değer olduğu için kod, sunucu anahtarıyla HMAC'lenerek saklanır (veritabanı sızsa bile kod geri çözülemez).
/// Gizli değer ve cihaz belirteci 256 bitlik rastgele değerdir; yalnızca SHA-256 özeti saklanır.
/// </summary>
public static class DisplayCredentials
{
    public const int CodeLength = 6;

    /// <summary>Rastgele 6 haneli kod (başında sıfır olabilir).</summary>
    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Boşluk ve tireleri atar; tam olarak 6 rakam değilse null döner.</summary>
    public static string? NormalizeCode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var digits = new StringBuilder(CodeLength);
        foreach (var c in input)
        {
            if (c is ' ' or '-')
            {
                continue;
            }

            if (c is < '0' or > '9')
            {
                return null;
            }

            digits.Append(c);
        }

        return digits.Length == CodeLength ? digits.ToString() : null;
    }

    public static string HashCode(string normalizedCode, string key) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(normalizedCode)));

    /// <summary>URL-güvenli, 256 bitlik rastgele gizli değer / cihaz belirteci.</summary>
    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashSecret(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
