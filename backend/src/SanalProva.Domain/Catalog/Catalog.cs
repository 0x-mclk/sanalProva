using SanalProva.Domain.Common;

namespace SanalProva.Domain.Catalog;

public enum BodyZone
{
    Upper,
    Lower,
    FullBody,
    Waist,
    Accessory,
}

public enum ProductType
{
    /// <summary>Bağımsız ürün (şalvar, gömlek, kemer...). Serbest kombinde kullanılır.</summary>
    Single,

    /// <summary>Hazır takım. Tek ürün gibi seçilir; FASHN için takımın tamamını gösteren tek AI referans görseli gerekir.</summary>
    OutfitSet,
}

public enum GenderTarget
{
    Women,
    Men,
    Unisex,
    Kids,
}

public enum ProductImageKind
{
    /// <summary>Düz çekim.</summary>
    Flat,

    /// <summary>Manken üzerinde.</summary>
    OnModel,

    /// <summary>Nakış/kumaş detayı.</summary>
    Detail,
}

public sealed class Category : Entity, ITenantOwned
{
    public Guid OrganizationId { get; init; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public int SortOrder { get; set; }
    public BodyZone BodyZone { get; set; }

    /// <summary>Giyiliş katmanı (içten dışa): gömlek 10, yelek 20, kaftan 30, kemer 40.</summary>
    public int LayerOrder { get; set; }

    public int MaxSelection { get; set; } = 1;

    /// <summary>Aynı kombinde birlikte seçilemeyecek kategoriler.</summary>
    public List<Guid> ConflictsWith { get; set; } = [];

    /// <summary>Prompt'a eklenen giyim tarifi. Yalnızca yönetici görür.</summary>
    public string? AiHint { get; set; }

    public bool IsActive { get; set; } = true;
}

public sealed class Product : Entity, ITenantOwned
{
    public Guid OrganizationId { get; init; }
    public Guid CategoryId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Kartta renk adının yanında görünen kısa ifade ("Altın işleme", "3 parça").</summary>
    public string? Tagline { get; set; }

    public ProductType ProductType { get; set; }
    public GenderTarget? GenderTarget { get; set; }
    public int? LayerOrderOverride { get; set; }
    public string? AiNotes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? ArchivedAt { get; set; }

    public Category? Category { get; set; }
    public List<ProductVariant> Variants { get; } = [];
    public List<ProductImage> Images { get; } = [];
    public List<OutfitSetComponent> Components { get; } = [];

    /// <summary>
    /// Ürünün verilen varyantla AI üretiminde kullanılabilir olup olmadığı.
    /// Ön cephe (Front) için tam olarak bir AI referans görseli gerekir; varyanta özel görsel yoksa
    /// tüm varyantlar için geçerli (VariantId = null) görsel kullanılır.
    /// Hazır takımda bu görsel takımın tamamını göstermelidir (FASHN tek ürün görseli alır).
    /// </summary>
    public bool IsReadyForAi(Guid? variantId)
    {
        if (!IsActive || ArchivedAt is not null)
        {
            return false;
        }

        if (variantId is not null && !Variants.Any(v => v.Id == variantId && v.IsActive))
        {
            return false;
        }

        return FindAiReference(variantId) is not null;
    }

    public ProductImage? FindAiReference(Guid? variantId, ImageAngle angle = ImageAngle.Front)
    {
        var candidates = Images.Where(i => i.IsAiReference && i.Angle == angle).ToList();
        return candidates.SingleOrDefault(i => i.VariantId == variantId && variantId is not null)
            ?? candidates.SingleOrDefault(i => i.VariantId is null);
    }
}

public sealed class ProductVariant : Entity, ITenantOwned
{
    public Guid OrganizationId { get; init; }
    public Guid ProductId { get; init; }
    public required string Code { get; set; }
    public required string ColorName { get; set; }

    /// <summary>#RRGGBB</summary>
    public string? ColorHex { get; set; }

    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ProductImage : Entity, ITenantOwned
{
    public Guid OrganizationId { get; init; }
    public Guid ProductId { get; init; }

    /// <summary>Null: tüm varyantlar için geçerli.</summary>
    public Guid? VariantId { get; set; }

    public Guid FileId { get; set; }
    public ImageAngle Angle { get; set; } = ImageAngle.Front;
    public ProductImageKind Kind { get; set; }

    /// <summary>AI üretiminde referans olarak kullanılır. Ürün + varyant + açı başına en fazla bir tane (DB kısıtı).</summary>
    public bool IsAiReference { get; set; }

    public int SortOrder { get; set; }
}

/// <summary>Hazır takımın bileşen parçaları ("takımdaki kemeri değiştir" revizyonu için).</summary>
public sealed class OutfitSetComponent : ITenantOwned
{
    public Guid OrganizationId { get; init; }
    public Guid SetProductId { get; init; }
    public Guid ComponentProductId { get; init; }
    public Guid? ComponentVariantId { get; set; }
}
