namespace SanalProva.Domain.Catalog;

public enum CombinationError
{
    Empty,
    OutfitSetMustBeAlone,
    SingleProductRequired,
    TooManyInCategory,
    ConflictingCategories,
}

/// <summary>Serbest kombin ve hazır takım seçim kuralları. Kurallar kategori metadata'sından gelir.</summary>
public static class CombinationRules
{
    public sealed record SelectedItem(Product Product, Category Category);

    public static IReadOnlyList<CombinationError> ValidateFreeCombination(IReadOnlyCollection<SelectedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var errors = new List<CombinationError>();

        if (items.Count == 0)
        {
            errors.Add(CombinationError.Empty);
            return errors;
        }

        if (items.Any(i => i.Product.ProductType != ProductType.Single))
        {
            errors.Add(CombinationError.SingleProductRequired);
        }

        var byCategory = items.GroupBy(i => i.Category.Id).ToList();
        if (byCategory.Any(g => g.Count() > g.First().Category.MaxSelection))
        {
            errors.Add(CombinationError.TooManyInCategory);
        }

        var selectedCategoryIds = byCategory.Select(g => g.Key).ToHashSet();
        if (byCategory.Any(g => g.First().Category.ConflictsWith.Any(selectedCategoryIds.Contains)))
        {
            errors.Add(CombinationError.ConflictingCategories);
        }

        return errors;
    }

    public static IReadOnlyList<CombinationError> ValidateOutfitSet(IReadOnlyCollection<SelectedItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return [CombinationError.Empty];
        }

        return items.Count == 1 && IsSingleTryOnItem(items.First())
            ? []
            : [CombinationError.OutfitSetMustBeAlone];
    }

    /// <summary>
    /// Tek görselle giydirilebilen ürün: hazır takım veya tüm vücudu kaplayan tekil ürün (ör. kaftan). Tek ürün giydirme modelleri yalnızca
    /// bunları alır; çok parçalı kombin ayrı akıştır.
    /// </summary>
    public static bool IsSingleTryOnItem(SelectedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Product.ProductType == ProductType.OutfitSet
            || (item.Product.ProductType == ProductType.Single && item.Category.BodyZone == BodyZone.FullBody);
    }

    /// <summary>Prompt ve görselleştirme için içten dışa katman sırası.</summary>
    public static IReadOnlyList<SelectedItem> OrderByLayer(IEnumerable<SelectedItem> items) =>
        items.OrderBy(i => i.Product.LayerOrderOverride ?? i.Category.LayerOrder)
             .ThenBy(i => i.Category.SortOrder)
             .ToList();
}
