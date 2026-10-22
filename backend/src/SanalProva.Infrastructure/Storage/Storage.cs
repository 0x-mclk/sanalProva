using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace SanalProva.Infrastructure.Storage;

/// <summary>
/// Görsel deposu. İlk uygulama yerel disk; S3 uyumlu depoya geçişte yalnızca yeni bir uygulama eklenir.
/// Görseller asla public değildir: istemciye yalnızca kısa ömürlü imzalı URL verilir.
/// </summary>
public interface IImageStorageService
{
    Task SaveAsync(string key, Stream content, string contentType, CancellationToken ct);

    /// <returns>Dosya yoksa null.</returns>
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>Bir önek altındaki tüm dosyaları siler (ör. oturum klasörü, KVKK saklama süresi dolunca).</summary>
    Task DeletePrefixAsync(string prefix, CancellationToken ct);

    /// <summary>Kısa ömürlü okuma URL'i. Yerel depoda API'nin /files uç noktası, S3'te presigned URL.</summary>
    string GetReadUrl(string key, TimeSpan ttl);

    Task<bool> IsHealthyAsync(CancellationToken ct);
}

public sealed class StorageOptions
{
    public const string Section = "Storage";

    [Required]
    public string Provider { get; set; } = "Local";

    /// <summary>Yerel depo kök klasörü. Göreli ise içerik kökünden çözülür; web kökünün dışında olmalıdır.</summary>
    [Required]
    public string LocalRoot { get; set; } = "../../../storage";

    /// <summary>İmzalı URL için HMAC anahtarı (en az 32 karakter). Yalnızca gizli yapılandırmadan gelir.</summary>
    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    public int CustomerImageUrlTtlSeconds { get; set; } = 300;
    public int CatalogImageUrlTtlSeconds { get; set; } = 3600;
}

/// <summary>Depolama anahtarı şeması. Oturum dosyaları tek önek altında toplanır, böylece tek seferde silinebilir.</summary>
public static partial class StorageKeys
{
    public static string SessionPrefix(Guid organizationId, Guid storeId, Guid sessionId) =>
        $"{organizationId:N}/{storeId:N}/sessions/{sessionId:N}/";

    public static string SessionFile(Guid organizationId, Guid storeId, Guid sessionId, Guid fileId, string extension) =>
        $"{SessionPrefix(organizationId, storeId, sessionId)}{fileId:N}.{NormalizeExtension(extension)}";

    public static string CatalogFile(Guid organizationId, Guid productId, Guid fileId, string extension) =>
        $"{organizationId:N}/catalog/{productId:N}/{fileId:N}.{NormalizeExtension(extension)}";

    /// <summary>Ana görselin küçük resim anahtarı: <c>x.jpg</c> → <c>x-t.jpg</c>. İşlenen tüm görseller JPEG'dir.</summary>
    public static string ThumbKey(string mainKey) =>
        mainKey.EndsWith(".jpg", StringComparison.Ordinal) ? mainKey[..^4] + "-t.jpg" : throw new ArgumentException("Yalnızca .jpg anahtarlarının küçük resmi vardır.", nameof(mainKey));

    /// <summary>Yol geçişi (../) ve beklenmeyen karakterlere karşı anahtar doğrulaması.</summary>
    public static bool IsValid(string key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= 300 && !key.Contains("..", StringComparison.Ordinal) && KeyPattern().IsMatch(key);

    private static string NormalizeExtension(string extension)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return ext is "jpg" or "jpeg" or "png" or "webp" ? ext : throw new ArgumentException($"Desteklenmeyen uzantı: {extension}", nameof(extension));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9/_.-]*$")]
    private static partial Regex KeyPattern();
}
