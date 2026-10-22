using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace SanalProva.Infrastructure.Storage;

/// <summary>Yerel depo için HMAC-SHA256 imzalı, süreli dosya URL'leri üretir ve doğrular.</summary>
public sealed class FileUrlSigner(IOptions<StorageOptions> options, TimeProvider clock)
{
    public const string RoutePrefix = "/files/";

    private readonly byte[] _key = Encoding.UTF8.GetBytes(options.Value.SigningKey);

    public string CreateUrl(string key, TimeSpan ttl)
    {
        var expires = clock.GetUtcNow().Add(ttl).ToUnixTimeSeconds();
        return $"{RoutePrefix}{key}?exp={expires}&sig={Sign(key, expires)}";
    }

    public bool Validate(string key, long expires, string? signature)
    {
        if (string.IsNullOrEmpty(signature) || clock.GetUtcNow().ToUnixTimeSeconds() > expires)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(key, expires));
        var actual = Encoding.ASCII.GetBytes(signature);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private string Sign(string key, long expires)
    {
        var payload = Encoding.UTF8.GetBytes($"{key}\n{expires}");
        return Base64UrlEncode(HMACSHA256.HashData(_key, payload));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
