using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Files;
using SanalProva.Infrastructure.Images;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Storage;

namespace SanalProva.Api.Infrastructure;

/// <summary>Tutarlı Problem Details yanıtları. <c>code</c> alanını frontend Türkçe mesaja eşler.</summary>
public static class ApiProblems
{
    public static IResult Create(int status, string code, string title, IDictionary<string, object?>? extra = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                extensions[key] = value;
            }
        }

        return Results.Problem(title: title, statusCode: status, extensions: extensions);
    }

    public static IResult NotFound(string title = "Kayıt bulunamadı.", string code = "NOT_FOUND") => Create(StatusCodes.Status404NotFound, code, title);

    public static IResult BadRequest(string code, string title) => Create(StatusCodes.Status400BadRequest, code, title);

    public static IResult Conflict(string code, string title) => Create(StatusCodes.Status409Conflict, code, title);

    public static IResult Forbidden(string code, string title) => Create(StatusCodes.Status403Forbidden, code, title);

    public static IResult Unprocessable(string code, string title) => Create(StatusCodes.Status422UnprocessableEntity, code, title);

    public static IResult FromImage(ImageRejectedException ex) => ex.Code switch
    {
        "IMAGE_TOO_LARGE" => Create(StatusCodes.Status413PayloadTooLarge, ex.Code, ex.Message),
        "UNSUPPORTED_IMAGE_TYPE" => Create(StatusCodes.Status415UnsupportedMediaType, ex.Code, ex.Message),
        _ => Unprocessable(ex.Code, ex.Message),
    };
}

public sealed record ImageUrls(string Url, string ThumbUrl);

/// <summary>Dosya kayıtlarından kısa ömürlü, imzalı görsel URL'leri üretir. Katalog görselleri daha uzun, müşteri görselleri kısa yaşar.</summary>
public sealed class ImageLinks(ImageStore store, IOptions<StorageOptions> options)
{
    public ImageUrls Catalog(StoredFile file) => Create(file, options.Value.CatalogImageUrlTtlSeconds);

    public ImageUrls Customer(StoredFile file) => Create(file, options.Value.CustomerImageUrlTtlSeconds);

    private ImageUrls Create(StoredFile file, int ttlSeconds)
    {
        var ttl = TimeSpan.FromSeconds(ttlSeconds);
        return new ImageUrls(store.GetUrl(file, ttl), store.GetUrl(file, ttl, thumb: true));
    }

    public static async Task<Dictionary<Guid, StoredFile>> LoadFilesAsync(AppDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return list.Count == 0
            ? []
            : await db.StoredFiles.AsNoTracking().Where(f => list.Contains(f.Id) && f.DeletedAt == null).ToDictionaryAsync(f => f.Id, ct);
    }
}

public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int Total);

public static class UploadReader
{
    /// <summary>Yüklenen dosyayı sınır aşılmadan belleğe okur (Content-Length'e güvenmez).</summary>
    public static async Task<byte[]> ReadAsync(IFormFile file, int maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length > maxBytes)
        {
            throw new ImageRejectedException("IMAGE_TOO_LARGE", $"Dosya {maxBytes / (1024 * 1024)} MB sınırını aşıyor.");
        }

        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new ImageRejectedException("IMAGE_TOO_LARGE", $"Dosya {maxBytes / (1024 * 1024)} MB sınırını aşıyor.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

/// <summary>
/// İstek bağlama hataları (bozuk JSON, eksik zorunlu parametre, gövde sınırı) istemci hatasıdır: 500 değil, çerçevenin verdiği
/// 4xx kodu ve tutarlı ProblemDetails döner. Beklenmeyen hatalar bu işleyiciye takılmaz ve varsayılan 500 yanıtına gider.
/// </summary>
public sealed class BadRequestExceptionHandler : Microsoft.AspNetCore.Diagnostics.IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException bad)
        {
            return false;
        }

        httpContext.Response.StatusCode = bad.StatusCode;
        await httpContext.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = bad.StatusCode,
            Title = bad.StatusCode == StatusCodes.Status413PayloadTooLarge ? "İstek çok büyük." : "İstek geçersiz.",
            Extensions = { ["code"] = bad.StatusCode == StatusCodes.Status413PayloadTooLarge ? "PAYLOAD_TOO_LARGE" : "BAD_REQUEST" },
        }, cancellationToken);
        return true;
    }
}
