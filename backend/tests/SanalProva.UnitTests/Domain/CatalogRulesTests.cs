using SanalProva.Domain.Catalog;
using SanalProva.Domain.Common;

namespace SanalProva.UnitTests.Domain;

public sealed class CombinationRulesTests
{
    private static readonly Guid Org = Guid.CreateVersion7();

    private static Category NewCategory(string name, int layer, int max = 1) =>
        new() { OrganizationId = Org, Name = name, Slug = name.ToLowerInvariant(), LayerOrder = layer, MaxSelection = max };

    private static CombinationRules.SelectedItem Item(Category category, ProductType type = ProductType.Single, int? layerOverride = null) =>
        new(new Product { OrganizationId = Org, CategoryId = category.Id, Code = Guid.NewGuid().ToString("N"), Name = "Ürün", ProductType = type, LayerOrderOverride = layerOverride }, category);

    [Fact]
    public void Gecerli_serbest_kombin_hatasiz_doner()
    {
        var salvar = NewCategory("Salvar", 10);
        var yelek = NewCategory("Yelek", 20);
        var kemer = NewCategory("Kemer", 40);

        var errors = CombinationRules.ValidateFreeCombination([Item(salvar), Item(yelek), Item(kemer)]);

        Assert.Empty(errors);
    }

    [Fact]
    public void Bos_kombin_reddedilir() =>
        Assert.Equal([CombinationError.Empty], CombinationRules.ValidateFreeCombination([]));

    [Fact]
    public void Kategori_sinirini_asan_secim_reddedilir()
    {
        var kemer = NewCategory("Kemer", 40);

        var errors = CombinationRules.ValidateFreeCombination([Item(kemer), Item(kemer)]);

        Assert.Contains(CombinationError.TooManyInCategory, errors);
    }

    [Fact]
    public void Cakisan_kategoriler_reddedilir()
    {
        var kaftan = NewCategory("Kaftan", 30);
        var bindalli = NewCategory("Bindalli", 50);
        kaftan.ConflictsWith.Add(bindalli.Id);

        var errors = CombinationRules.ValidateFreeCombination([Item(kaftan), Item(bindalli)]);

        Assert.Contains(CombinationError.ConflictingCategories, errors);
    }

    [Fact]
    public void Serbest_kombinde_hazir_takim_kullanilamaz()
    {
        var takim = NewCategory("Takim", 50);

        var errors = CombinationRules.ValidateFreeCombination([Item(takim, ProductType.OutfitSet)]);

        Assert.Contains(CombinationError.SingleProductRequired, errors);
    }

    [Fact]
    public void Hazir_takim_tek_basina_secilmelidir()
    {
        var takim = NewCategory("Takim", 50);
        var kemer = NewCategory("Kemer", 40);

        Assert.Empty(CombinationRules.ValidateOutfitSet([Item(takim, ProductType.OutfitSet)]));
        Assert.Equal([CombinationError.OutfitSetMustBeAlone], CombinationRules.ValidateOutfitSet([Item(takim, ProductType.OutfitSet), Item(kemer)]));
        Assert.Equal([CombinationError.OutfitSetMustBeAlone], CombinationRules.ValidateOutfitSet([Item(kemer)]));
    }

    [Fact]
    public void Tek_urun_giydirmede_kaftan_gibi_tum_vucut_tekil_urun_de_kabul_edilir()
    {
        var kaftan = NewCategory("Kaftan", 30);
        kaftan.BodyZone = BodyZone.FullBody;
        var salvar = NewCategory("Salvar", 10);
        salvar.BodyZone = BodyZone.Lower;

        Assert.Empty(CombinationRules.ValidateOutfitSet([Item(kaftan)]));
        Assert.True(CombinationRules.IsSingleTryOnItem(Item(kaftan)));
        Assert.False(CombinationRules.IsSingleTryOnItem(Item(salvar)));
        Assert.Equal([CombinationError.OutfitSetMustBeAlone], CombinationRules.ValidateOutfitSet([Item(salvar)]));
        Assert.Equal([CombinationError.OutfitSetMustBeAlone], CombinationRules.ValidateOutfitSet([Item(kaftan), Item(salvar)])); // tek ürün akışı tek parça
    }

    [Fact]
    public void Katman_sirasi_icten_disa_ve_urun_istisnasina_gore_siralanir()
    {
        var gomlek = NewCategory("Gomlek", 10);
        var yelek = NewCategory("Yelek", 20);
        var kemer = NewCategory("Kemer", 40);
        var kemerItem = Item(kemer);
        var yelekItem = Item(yelek, layerOverride: 5); // ürüne özel: yelek bu üründe içte

        var ordered = CombinationRules.OrderByLayer([kemerItem, Item(gomlek), yelekItem]);

        Assert.Same(yelekItem, ordered[0]);
        Assert.Same(kemerItem, ordered[^1]);
    }
}

public sealed class ProductAiReadinessTests
{
    private static Product NewProduct(ProductType type = ProductType.OutfitSet) =>
        new() { OrganizationId = Guid.CreateVersion7(), CategoryId = Guid.CreateVersion7(), Code = "BD-014", Name = "Sultan Bindallı", ProductType = type };

    private static ProductImage Image(Product product, Guid? variantId, bool aiReference, ImageAngle angle = ImageAngle.Front) =>
        new() { OrganizationId = product.OrganizationId, ProductId = product.Id, VariantId = variantId, FileId = Guid.CreateVersion7(), Angle = angle, IsAiReference = aiReference };

    [Fact]
    public void AI_referans_gorseli_olmayan_urun_hazir_degildir()
    {
        var product = NewProduct();
        product.Images.Add(Image(product, null, aiReference: false));

        Assert.False(product.IsReadyForAi(null));
    }

    [Fact]
    public void Tum_varyantlar_icin_gecerli_referans_her_varyanti_hazir_yapar()
    {
        var product = NewProduct();
        var bordo = new ProductVariant { OrganizationId = product.OrganizationId, ProductId = product.Id, Code = "BRD", ColorName = "Bordo" };
        product.Variants.Add(bordo);
        product.Images.Add(Image(product, null, aiReference: true));

        Assert.True(product.IsReadyForAi(null));
        Assert.True(product.IsReadyForAi(bordo.Id));
    }

    [Fact]
    public void Varyanta_ozel_referans_genel_referanstan_onceliklidir()
    {
        var product = NewProduct();
        var zumrut = new ProductVariant { OrganizationId = product.OrganizationId, ProductId = product.Id, Code = "ZMR", ColorName = "Zümrüt" };
        product.Variants.Add(zumrut);
        var general = Image(product, null, aiReference: true);
        var specific = Image(product, zumrut.Id, aiReference: true);
        product.Images.AddRange([general, specific]);

        Assert.Same(specific, product.FindAiReference(zumrut.Id));
        Assert.Same(general, product.FindAiReference(null));
    }

    [Fact]
    public void Arka_cephe_referansi_on_cephe_yerine_gecmez()
    {
        var product = NewProduct();
        product.Images.Add(Image(product, null, aiReference: true, ImageAngle.Back));

        Assert.False(product.IsReadyForAi(null));
    }

    [Fact]
    public void Pasif_veya_arsivlenmis_urun_ve_bilinmeyen_varyant_hazir_degildir()
    {
        var product = NewProduct();
        product.Images.Add(Image(product, null, aiReference: true));

        Assert.False(product.IsReadyForAi(Guid.CreateVersion7()));
        product.IsActive = false;
        Assert.False(product.IsReadyForAi(null));
        product.IsActive = true;
        product.ArchivedAt = DateTimeOffset.UtcNow;
        Assert.False(product.IsReadyForAi(null));
    }
}
