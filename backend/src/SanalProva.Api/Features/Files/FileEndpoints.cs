using Microsoft.Extensions.Options;
using SanalProva.Infrastructure.Storage;

namespace SanalProva.Api.Features.Files;

public static class FileEndpoints
{
    /// <summary>
    /// Yerel depodaki dosyaları yalnızca geçerli ve süresi dolmamış HMAC imzasıyla servis eder.
    /// İmza, dosyaya erişim yetkisinin kendisidir: URL'i yalnızca yetkili API yanıtları üretir.
    /// </summary>
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(FileUrlSigner.RoutePrefix + "{**key}", async (
            string key, long exp, string? sig, FileUrlSigner signer, IImageStorageService storage,
            IOptions<StorageOptions> options, HttpContext http, CancellationToken ct) =>
        {
            if (!StorageKeys.IsValid(key) || !signer.Validate(key, exp, sig))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var stream = await storage.OpenReadAsync(key, ct);
            if (stream is null)
            {
                return Results.NotFound();
            }

            var isCatalog = key.Contains("/catalog/", StringComparison.Ordinal);
            var maxAge = isCatalog ? options.Value.CatalogImageUrlTtlSeconds : options.Value.CustomerImageUrlTtlSeconds;
            // Müşteri görselleri hiçbir tarayıcı/TV önbelleğine yazılmaz (KVKK); katalog görselleri kısa ömürlü özel önbellekte tutulabilir
            http.Response.Headers.CacheControl = isCatalog ? $"private, max-age={maxAge}" : "private, no-store";
            return Results.Stream(stream, ContentTypeFor(key));
        })
        .ExcludeFromDescription();

        return app;
    }

    private static string ContentTypeFor(string key) => Path.GetExtension(key).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };
}
