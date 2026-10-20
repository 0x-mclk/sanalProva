using System.Net;
using SanalProva.Api.Features.Catalog;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Catalog;
using SanalProva.Domain.Common;

namespace SanalProva.IntegrationTests;

public sealed class CatalogApiTests(ApiFactory factory) : ApiTestBase(factory)
{
    private static object NewCategory(string slug, string name = "Kategori", string bodyZone = "Lower", int layer = 10, int max = 1) =>
        new { name, slug, sortOrder = 1, bodyZone, layerOrder = layer, maxSelection = max, isActive = true };

    private static object NewProduct(Guid categoryId, string code, string name = "Ürün", string type = "Single", bool active = true) =>
        new { code, name, categoryId, productType = type, isActive = active, tagline = "Altın işleme" };

    private static async Task<ProductDetail> UploadAsync(ApiClient client, Guid productId, Guid? variantId = null, bool ai = false, ImageAngle angle = ImageAngle.Front, byte[]? bytes = null)
    {
        var fields = new Dictionary<string, string> { ["angle"] = angle.ToString(), ["kind"] = "OnModel", ["isAiReference"] = ai ? "true" : "false" };
        if (variantId is { } v)
        {
            fields["variantId"] = v.ToString();
        }

        var response = await client.UploadAsync($"/api/v1/products/{productId}/images", bytes ?? TestImages.Jpeg(800, 1000), "urun.jpg", "image/jpeg", fields);
        return await ApiClient.ReadAsync<ProductDetail>(response, HttpStatusCode.Created);
    }

    [Fact]
    public async Task Urun_variyant_ve_AI_referans_gorseli_ile_hazir_hale_gelir()
    {
        var (_, manager, staff) = await ManagerAndStaffAsync();
        var category = await manager.PostAsync<CategoryDto>("/api/v1/categories", NewCategory("bindalli", "Bindallı", "FullBody", 50), HttpStatusCode.Created);

        var product = await manager.PostAsync<ProductDetail>("/api/v1/products", NewProduct(category.Id, "BD-014", "Sultan Bindallı", "OutfitSet"), HttpStatusCode.Created);
        Assert.False(product.IsReadyForAi);
        Assert.Equal("Altın işleme", product.Tagline);

        product = await manager.PostAsync<ProductDetail>($"/api/v1/products/{product.Id}/variants",
            new { code = "BRD", colorName = "Bordo", colorHex = "#7a1f3a", isDefault = false, isActive = true }, HttpStatusCode.Created);
        Assert.True(Assert.Single(product.Variants).IsDefault, "İlk varyant otomatik varsayılan olur");
        Assert.Equal("#7A1F3A", product.Variants[0].ColorHex);

        product = await UploadAsync(manager, product.Id, ai: false);
        Assert.False(product.IsReadyForAi, "AI referansı işaretlenmedikçe hazır sayılmaz");

        product = await UploadAsync(manager, product.Id, ai: true);
        Assert.True(product.IsReadyForAi);
        Assert.NotNull(product.Cover);

        // Staff aynı ürünü görür ama AI notlarını görmez
        var asStaff = await staff.GetAsync<ProductDetail>($"/api/v1/products/{product.Id}");
        Assert.Null(asStaff.AiNotes);
        var image = Assert.Single(asStaff.Images, i => i.IsAiReference);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync(image.Urls.ThumbUrl)).StatusCode);
    }

    [Fact]
    public async Task Ikinci_AI_referans_gorseli_oncekini_kaldirir_ve_her_zaman_tek_referans_kalir()
    {
        var (_, manager, _) = await ManagerAndStaffAsync();
        var category = await manager.PostAsync<CategoryDto>("/api/v1/categories", NewCategory("salvar", "Şalvar"), HttpStatusCode.Created);
        var product = await manager.PostAsync<ProductDetail>("/api/v1/products", NewProduct(category.Id, "SL-001"), HttpStatusCode.Created);

        product = await UploadAsync(manager, product.Id, ai: true);
        product = await UploadAsync(manager, product.Id, ai: true);
        Assert.Equal(2, product.Images.Count);
        Assert.Single(product.Images, i => i.IsAiReference);

        // Eski görseli yeniden referans yapmak diğerini düşürür
        var old = product.Images.Single(i => !i.IsAiReference);
        product = await manager.PatchAsync<ProductDetail>($"/api/v1/products/{product.Id}/images/{old.Id}", new { isAiReference = true });
        Assert.Equal(old.Id, Assert.Single(product.Images, i => i.IsAiReference).Id);
    }

    [Fact]
    public async Task Varsayilan_varyant_tekil_kalir()
    {
        var (_, manager, _) = await ManagerAndStaffAsync();
        var category = await manager.PostAsync<CategoryDto>("/api/v1/categories", NewCategory("kemer", "Kemer", "Waist", 40), HttpStatusCode.Created);
        var product = await manager.PostAsync<ProductDetail>("/api/v1/products", NewProduct(category.Id, "KM-001"), HttpStatusCode.Created);

        await manager.PostAsync<ProductDetail>($"/api/v1/products/{product.Id}/variants", new { code = "A", colorName = "Altın", isDefault = true, isActive = true }, HttpStatusCode.Created);
        product = await manager.PostAsync<ProductDetail>($"/api/v1/products/{product.Id}/variants", new { code = "B", colorName = "Gümüş", isDefault = true, isActive = true }, HttpStatusCode.Created);

        Assert.Equal("B", Assert.Single(product.Variants, v => v.IsDefault).Code);
        Assert.Equal(2, product.Variants.Count);
    }

    [Fact]
    public async Task Yinelenen_kod_gecersiz_kategori_ve_gecersiz_alanlar_reddedilir()
    {
        var (_, manager, _) = await ManagerAndStaffAsync();
        var category = await manager.PostAsync<CategoryDto>("/api/v1/categories", NewCategory("yelek", "Yelek"), HttpStatusCode.Created);
        await manager.PostAsync<ProductDetail>("/api/v1/products", NewProduct(category.Id, "YL-001"), HttpStatusCode.Created);

        await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Post, "/api/v1/products", NewProduct(category.Id, "YL-001")), HttpStatusCode.Conflict, "DUPLICATE_CODE");
        await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Post, "/api/v1/products", NewProduct(Guid.NewGuid(), "YL-002")), HttpStatusCode.BadRequest, "UNKNOWN_CATEGORY");
        await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Post, "/api/v1/categories", NewCategory("yelek")), HttpStatusCode.Conflict, "DUPLICATE_SLUG");

        // DataAnnotations doğrulaması: geçersiz kısa ad ve boş ürün adı
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.SendAsync(HttpMethod.Post, "/api/v1/categories", NewCategory("Geçersiz Slug!"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.SendAsync(HttpMethod.Post, "/api/v1/products", NewProduct(category.Id, "YL-003", name: ""))).StatusCode);
    }

    [Fact]
    public async Task Staff_yalnizca_aktif_urunleri_gorur_secilebilir_liste_AI_referanssiz_urunleri_gizler()
    {
        var (_, manager, staff) = await ManagerAndStaffAsync();
        var category = await manager.PostAsync<CategoryDto>("/api/v1/categories", NewCategory("kaftan", "Kaftan", "FullBody", 30), HttpStatusCode.Created);
        var ready = await manager.PostAsync<ProductDetail>("/api/v1/products", NewProduct(category.Id, "KF-001", "Hazır Kaftan"), HttpStatusCode.Created);
        await UploadAsync(manager, ready.Id, ai: true);
        var noReference = await manager.PostAsync<ProductDetail>("/api/v1/products", NewProduct(category.Id, "KF-002", "Referanssız Kaftan"), HttpStatusCode.Created);
        var inactive = await manager.PostAsync<ProductDetail>("/api/v1/products", NewProduct(category.Id, "KF-003", "Pasif Kaftan", active: false), HttpStatusCode.Created);

        var staffList = await staff.GetAsync<Page<ProductListItem>>("/api/v1/products?status=all");
        Assert.Equal(new[] { "KF-001", "KF-002" }, staffList.Items.Select(p => p.Code).Order());

        var selectable = await staff.GetAsync<Page<ProductListItem>>("/api/v1/products?selectable=true");
        Assert.Equal(["KF-001"], selectable.Items.Select(p => p.Code));
        Assert.True(selectable.Items[0].IsReadyForAi);

        var managerAll = await manager.GetAsync<Page<ProductListItem>>("/api/v1/products?status=all");
        Assert.Equal(3, managerAll.Total);
        var managerInactive = await manager.GetAsync<Page<ProductListItem>>("/api/v1/products?status=inactive");
        Assert.Equal(inactive.Id, Assert.Single(managerInactive.Items).Id);
        Assert.False(managerInactive.Items[0].IsActive);
        Assert.NotEqual(noReference.Id, ready.Id);
    }

    [Fact]
    public async Task Arama_ad_ve_kodda_calisir_ve_joker_karakterler_kacirilir()
    {
        var (_, manager, staff) = await ManagerAndStaffAsync();
        var category = await manager.PostAsync<CategoryDto>("/api/v1/categories", NewCategory("arama", "Arama"), HttpStatusCode.Created);
        await manager.PostAsync<ProductDetail>("/api/v1/products", NewProduct(category.Id, "ARM-001", "Lale Şalvar Takımı"), HttpStatusCode.Created);
        await manager.PostAsync<ProductDetail>("/api/v1/products", NewProduct(category.Id, "ARM-002", "İnci Takım"), HttpStatusCode.Created);

        Assert.Single((await staff.GetAsync<Page<ProductListItem>>("/api/v1/products?q=lale")).Items);
        Assert.Single((await staff.GetAsync<Page<ProductListItem>>("/api/v1/products?q=arm-002")).Items);
        Assert.Equal(2, (await staff.GetAsync<Page<ProductListItem>>("/api/v1/products?q=takım")).Total);
        Assert.Empty((await staff.GetAsync<Page<ProductListItem>>("/api/v1/products?q=%25")).Items); // "%" her şeyle eşleşmemeli
    }

    [Fact]
    public async Task Gecersiz_gorsel_yuklemeleri_reddedilir()
    {
        var (_, manager, _) = await ManagerAndStaffAsync();
        var category = await manager.PostAsync<CategoryDto>("/api/v1/categories", NewCategory("gorsel", "Görsel"), HttpStatusCode.Created);
        var product = await manager.PostAsync<ProductDetail>("/api/v1/products", NewProduct(category.Id, "GR-001"), HttpStatusCode.Created);
        var url = $"/api/v1/products/{product.Id}/images";
        var fields = new Dictionary<string, string> { ["angle"] = "Front", ["kind"] = "Flat" };

        await ApiClient.AssertProblemAsync(await manager.UploadAsync(url, "GIF89a....."u8.ToArray(), "a.gif", "image/gif", fields), HttpStatusCode.UnsupportedMediaType, "UNSUPPORTED_IMAGE_TYPE");
        await ApiClient.AssertProblemAsync(await manager.UploadAsync(url, "<script>alert(1)</script>"u8.ToArray(), "a.jpg", "image/jpeg", fields), HttpStatusCode.UnsupportedMediaType, "UNSUPPORTED_IMAGE_TYPE");
        await ApiClient.AssertProblemAsync(await manager.UploadAsync(url, TestImages.Jpeg(120, 120), "kucuk.jpg", "image/jpeg", fields), HttpStatusCode.UnprocessableEntity, "IMAGE_TOO_SMALL");
        byte[] huge = [0xFF, 0xD8, 0xFF, .. new byte[11 * 1024 * 1024]];
        await ApiClient.AssertProblemAsync(await manager.UploadAsync(url, huge, "buyuk.jpg", "image/jpeg", fields), HttpStatusCode.RequestEntityTooLarge, "IMAGE_TOO_LARGE");

        Assert.Empty((await manager.GetAsync<ProductDetail>($"/api/v1/products/{product.Id}")).Images);
    }

    [Fact]
    public async Task Gorsel_silinince_dosya_kalkar_ve_urun_hazir_olmaktan_cikar()
    {
        var (_, manager, _) = await ManagerAndStaffAsync();
        var category = await manager.PostAsync<CategoryDto>("/api/v1/categories", NewCategory("silme", "Silme"), HttpStatusCode.Created);
        var product = await manager.PostAsync<ProductDetail>("/api/v1/products", NewProduct(category.Id, "SL-009"), HttpStatusCode.Created);
        product = await UploadAsync(manager, product.Id, ai: true);
        var image = Assert.Single(product.Images);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(image.Urls.Url)).StatusCode);

        var after = await ApiClient.ReadAsync<ProductDetail>(await manager.SendAsync(HttpMethod.Delete, $"/api/v1/products/{product.Id}/images/{image.Id}"));

        Assert.Empty(after.Images);
        Assert.False(after.IsReadyForAi);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(image.Urls.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(image.Urls.ThumbUrl)).StatusCode);
    }

    [Fact]
    public async Task Hazir_takim_bilesenleri_yalnizca_takimlara_ve_bagimsiz_urunlere_atanir()
    {
        var (_, manager, _) = await ManagerAndStaffAsync();
        var setCategory = await manager.PostAsync<CategoryDto>("/api/v1/categories", NewCategory("takim", "Takım", "FullBody", 50), HttpStatusCode.Created);
        var partCategory = await manager.PostAsync<CategoryDto>("/api/v1/categories", NewCategory("parca", "Parça"), HttpStatusCode.Created);
        var set = await manager.PostAsync<ProductDetail>("/api/v1/products", NewProduct(setCategory.Id, "TK-001", "Takım", "OutfitSet"), HttpStatusCode.Created);
        var part = await manager.PostAsync<ProductDetail>("/api/v1/products", NewProduct(partCategory.Id, "PR-001", "Parça"), HttpStatusCode.Created);

        var withParts = await manager.PutAsync<ProductDetail>($"/api/v1/products/{set.Id}/components", new[] { new { componentProductId = part.Id } });
        Assert.Equal("PR-001", Assert.Single(withParts.Components).Code);

        await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Put, $"/api/v1/products/{part.Id}/components", new[] { new { componentProductId = set.Id } }), HttpStatusCode.BadRequest, "NOT_AN_OUTFIT_SET");
        await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Put, $"/api/v1/products/{set.Id}/components", new[] { new { componentProductId = set.Id } }), HttpStatusCode.BadRequest, "INVALID_COMPONENTS");
        await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Put, $"/api/v1/products/{set.Id}/components", new[] { new { componentProductId = set.Id }, new { componentProductId = part.Id } }), HttpStatusCode.BadRequest, "INVALID_COMPONENTS");

        // Bileşeni olan ürünün tipi kilitlidir
        await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Put, $"/api/v1/products/{set.Id}", NewProduct(setCategory.Id, "TK-001", "Takım", "Single")), HttpStatusCode.Conflict, "PRODUCT_TYPE_LOCKED");
    }

    [Fact]
    public async Task Pasife_alinan_urun_staff_listesinden_kalkar_ve_geri_alinabilir()
    {
        var (_, manager, staff) = await ManagerAndStaffAsync();
        var category = await manager.PostAsync<CategoryDto>("/api/v1/categories", NewCategory("pasif", "Pasif"), HttpStatusCode.Created);
        var product = await manager.PostAsync<ProductDetail>("/api/v1/products", NewProduct(category.Id, "PS-001"), HttpStatusCode.Created);

        var off = await manager.PatchAsync<ProductDetail>($"/api/v1/products/{product.Id}/status", new { isActive = false });
        Assert.False(off.IsActive);
        Assert.Empty((await staff.GetAsync<Page<ProductListItem>>("/api/v1/products")).Items);

        var on = await manager.PatchAsync<ProductDetail>($"/api/v1/products/{product.Id}/status", new { isActive = true });
        Assert.True(on.IsActive);
        Assert.Single((await staff.GetAsync<Page<ProductListItem>>("/api/v1/products")).Items);
    }

    [Fact]
    public async Task Katalog_firmalar_arasi_izoledir()
    {
        var (_, managerA, _) = await ManagerAndStaffAsync();
        var category = await managerA.PostAsync<CategoryDto>("/api/v1/categories", NewCategory("izole", "İzole"), HttpStatusCode.Created);
        var product = await managerA.PostAsync<ProductDetail>("/api/v1/products", NewProduct(category.Id, "IZ-001"), HttpStatusCode.Created);
        var (_, managerB, staffB) = await ManagerAndStaffAsync();

        Assert.Empty((await staffB.GetAsync<Page<ProductListItem>>("/api/v1/products?status=all")).Items);
        Assert.Empty(await staffB.GetAsync<List<CategoryDto>>("/api/v1/categories"));
        await ApiClient.AssertProblemAsync(await staffB.GetAsync($"/api/v1/products/{product.Id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(
            await managerB.SendAsync(HttpMethod.Patch, $"/api/v1/products/{product.Id}/status", new { isActive = false }), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await managerB.SendAsync(HttpMethod.Post, "/api/v1/products", NewProduct(category.Id, "IZ-777")), HttpStatusCode.BadRequest, "UNKNOWN_CATEGORY");
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.SendAsync(HttpMethod.Put, $"/api/v1/categories/{category.Id}", NewCategory("izole", "Ele geçirildi"))).StatusCode);
    }

    [Fact]
    public async Task Cakisan_kategori_listesi_ve_AI_ipucu_yalnizca_yoneticiye_gorunur()
    {
        var (_, manager, staff) = await ManagerAndStaffAsync();
        var other = await manager.PostAsync<CategoryDto>("/api/v1/categories", NewCategory("diger", "Diğer"), HttpStatusCode.Created);
        var category = await manager.PostAsync<CategoryDto>("/api/v1/categories",
            new { name = "Kaftan", slug = "kaftan2", sortOrder = 2, bodyZone = "FullBody", layerOrder = 30, maxSelection = 1, conflictsWith = new[] { other.Id }, aiHint = "Kaftan önü açık giyilir", isActive = true },
            HttpStatusCode.Created);

        var asManager = (await manager.GetAsync<List<CategoryDto>>("/api/v1/categories")).Single(c => c.Id == category.Id);
        var asStaff = (await staff.GetAsync<List<CategoryDto>>("/api/v1/categories")).Single(c => c.Id == category.Id);

        Assert.Equal("Kaftan önü açık giyilir", asManager.AiHint);
        Assert.Null(asStaff.AiHint);
        Assert.Equal([other.Id], asStaff.ConflictsWith);
    }
}
