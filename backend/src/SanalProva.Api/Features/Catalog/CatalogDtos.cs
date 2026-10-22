using System.ComponentModel.DataAnnotations;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Catalog;
using SanalProva.Domain.Common;

namespace SanalProva.Api.Features.Catalog;

public sealed record CategoryDto(
    Guid Id, string Name, string Slug, int SortOrder, BodyZone BodyZone, int LayerOrder, int MaxSelection,
    IReadOnlyList<Guid> ConflictsWith, string? AiHint, bool IsActive);

public sealed record CategoryWriteRequest(
    [property: Required, StringLength(80, MinimumLength = 2)] string Name,
    [property: Required, RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$"), StringLength(60)] string Slug,
    int SortOrder, BodyZone BodyZone, [property: Range(0, 1000)] int LayerOrder, [property: Range(1, 10)] int MaxSelection,
    IReadOnlyList<Guid>? ConflictsWith, [property: StringLength(500)] string? AiHint, bool IsActive);

public sealed record VariantDto(Guid Id, string Code, string ColorName, string? ColorHex, bool IsDefault, bool IsActive, ImageUrls? Image);

public sealed record VariantWriteRequest(
    [property: Required, StringLength(40, MinimumLength = 1)] string Code,
    [property: Required, StringLength(60, MinimumLength = 1)] string ColorName,
    [property: RegularExpression("^#[0-9A-Fa-f]{6}$")] string? ColorHex,
    bool IsDefault, bool IsActive);

public sealed record ProductImageDto(Guid Id, Guid? VariantId, ImageAngle Angle, ProductImageKind Kind, bool IsAiReference, int SortOrder, ImageUrls Urls);

public sealed record SetComponentDto(Guid ProductId, string Code, string Name, string CategoryName, Guid? VariantId, ImageUrls? Image);

public sealed record ProductListItem(
    Guid Id, string Code, string Name, ProductType ProductType, Guid CategoryId, string CategoryName,
    string? Tagline, string? Subtitle, bool IsActive, bool IsReadyForAi, ImageUrls? Cover, IReadOnlyList<VariantDto> Variants);

public sealed record ProductDetail(
    Guid Id, string Code, string Name, ProductType ProductType, Guid CategoryId, string CategoryName,
    string? Description, string? Tagline, GenderTarget? GenderTarget, int? LayerOrderOverride, string? AiNotes,
    bool IsActive, bool IsReadyForAi, ImageUrls? Cover,
    IReadOnlyList<VariantDto> Variants, IReadOnlyList<ProductImageDto> Images, IReadOnlyList<SetComponentDto> Components);

public sealed record ProductWriteRequest(
    [property: Required, StringLength(40, MinimumLength = 2)] string Code,
    [property: Required, StringLength(120, MinimumLength = 2)] string Name,
    Guid CategoryId,
    [property: StringLength(2000)] string? Description,
    [property: StringLength(60)] string? Tagline,
    ProductType ProductType, GenderTarget? GenderTarget, int? LayerOrderOverride,
    [property: StringLength(500)] string? AiNotes, bool IsActive);

public sealed record ProductStatusRequest(bool IsActive);

public sealed record SetComponentRequest(Guid ComponentProductId, Guid? ComponentVariantId);

public sealed record ProductImageUpdateRequest(bool? IsAiReference, ImageAngle? Angle, ProductImageKind? Kind, int? SortOrder);

public sealed class ProductImageForm
{
    public required IFormFile File { get; init; }
    public Guid? VariantId { get; init; }
    public ImageAngle Angle { get; init; } = ImageAngle.Front;
    public ProductImageKind Kind { get; init; } = ProductImageKind.Flat;
    public bool IsAiReference { get; init; }
}

internal static class CatalogMapping
{
    public static CategoryDto ToDto(this Category c, bool includeAiHint) =>
        new(c.Id, c.Name, c.Slug, c.SortOrder, c.BodyZone, c.LayerOrder, c.MaxSelection, c.ConflictsWith, includeAiHint ? c.AiHint : null, c.IsActive);

    /// <summary>
    /// Kapak görseli: varsayılan varyantın ön cephe görseli → ürün geneli ön cephe görseli → herhangi bir görsel.
    /// AI referansı olan görsel önceliklidir.
    /// </summary>
    public static ProductImage? PickCover(this Product product, Guid? variantId = null)
    {
        var defaultVariant = variantId ?? product.Variants.FirstOrDefault(v => v.IsDefault && v.IsActive)?.Id ?? product.Variants.FirstOrDefault(v => v.IsActive)?.Id;
        static IEnumerable<ProductImage> Ordered(IEnumerable<ProductImage> images) =>
            images.OrderByDescending(i => i.Angle == ImageAngle.Front).ThenByDescending(i => i.IsAiReference).ThenBy(i => i.SortOrder);

        return Ordered(product.Images.Where(i => i.VariantId == defaultVariant && defaultVariant != null)).FirstOrDefault()
            ?? Ordered(product.Images.Where(i => i.VariantId == null)).FirstOrDefault()
            ?? Ordered(product.Images).FirstOrDefault();
    }

    public static string? Subtitle(this Product product)
    {
        var color = product.Variants.FirstOrDefault(v => v.IsDefault && v.IsActive)?.ColorName ?? product.Variants.FirstOrDefault(v => v.IsActive)?.ColorName;
        return string.Join(" · ", new[] { color, product.Tagline }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } text ? text : null;
    }

    public static bool IsReady(this Product product)
    {
        var defaultVariant = product.Variants.FirstOrDefault(v => v.IsDefault && v.IsActive) ?? product.Variants.FirstOrDefault(v => v.IsActive);
        return product.IsReadyForAi(defaultVariant?.Id);
    }
}
