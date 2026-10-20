using System.Security.Cryptography;
using SanalProva.Domain.Files;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Storage;

namespace SanalProva.Infrastructure.Images;

public sealed record SavedImage(StoredFile File, ProcessedImage Image);

/// <summary>
/// Görseli işler (<see cref="ImageProcessor"/>), ana görsel + küçük resmi depoya yazar ve <see cref="StoredFile"/>
/// kaydını bağlama ekler. Çağıran <c>SaveChanges</c> yapar. İkili içerik veritabanına yazılmaz.
/// </summary>
public sealed class ImageStore(IImageStorageService storage)
{
    /// <param name="keyFor">Dosya kimliğinden depolama anahtarı üretir (ör. <c>StorageKeys.SessionFile</c>).</param>
    public async Task<SavedImage> SaveAsync(
        AppDbContext db,
        byte[] raw,
        ImageProcessingOptions options,
        Guid organizationId,
        FilePurpose purpose,
        DateTimeOffset? retentionUntil,
        Func<Guid, string> keyFor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(keyFor);

        var processed = ImageProcessor.Process(raw, options);
        var fileId = Guid.CreateVersion7();
        var key = keyFor(fileId);

        await storage.SaveAsync(key, new MemoryStream(processed.Main), "image/jpeg", ct);
        await storage.SaveAsync(StorageKeys.ThumbKey(key), new MemoryStream(processed.Thumb), "image/jpeg", ct);

        var file = new StoredFile
        {
            Id = fileId,
            OrganizationId = organizationId,
            StorageKey = key,
            ContentType = "image/jpeg",
            SizeBytes = processed.Main.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(processed.Main)),
            Width = processed.Width,
            Height = processed.Height,
            Purpose = purpose,
            RetentionUntil = retentionUntil,
        };
        db.StoredFiles.Add(file);
        return new SavedImage(file, processed);
    }

    public async Task DeleteAsync(StoredFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        await storage.DeleteAsync(file.StorageKey, ct);
        await storage.DeleteAsync(StorageKeys.ThumbKey(file.StorageKey), ct);
    }

    public Task<Stream?> OpenAsync(StoredFile file, CancellationToken ct) => storage.OpenReadAsync(file.StorageKey, ct);

    public string GetUrl(StoredFile file, TimeSpan ttl, bool thumb = false) =>
        storage.GetReadUrl(thumb ? StorageKeys.ThumbKey(file.StorageKey) : file.StorageKey, ttl);
}
