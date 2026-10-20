using SanalProva.Domain.Common;

namespace SanalProva.Domain.Files;

public enum FilePurpose
{
    CustomerPhoto,
    GeneratedImage,
    ProductImage,
    Mask,
}

/// <summary>Depolamadaki bir dosyanın metadata'sı. İkili içerik veritabanında tutulmaz.</summary>
public sealed class StoredFile : Entity, ITenantOwned
{
    public Guid OrganizationId { get; init; }
    public required string StorageKey { get; init; }
    public required string ContentType { get; init; }
    public long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public FilePurpose Purpose { get; init; }

    /// <summary>Katalog görselleri için null.</summary>
    public DateTimeOffset? RetentionUntil { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}
