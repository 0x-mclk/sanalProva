using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Catalog;
using SanalProva.Domain.Common;
using SanalProva.Domain.Files;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Images;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Storage;

namespace SanalProva.Api.Features.Catalog;

/// <summary>
/// Katalog firma seviyesindedir: aynı firmanın tüm şubeleri aynı ürünleri görür.
/// Okuma: firmadaki her personel. Yazma: firmadaki herhangi bir şubede en az Manager rolü.
/// </summary>
public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder api)
    {
        var read = api.MapGroup("").WithTags("Catalog").RequireOrganizationRole(StoreRole.Staff);
        read.MapGet("/categories", ListCategoriesAsync).Produces<IReadOnlyList<CategoryDto>>();
        read.MapGet("/products", ListProductsAsync).Produces<Page<ProductListItem>>();
        read.MapGet("/products/{id:guid}", GetProductAsync).Produces<ProductDetail>();

        var write = api.MapGroup("").WithTags("Catalog").RequireOrganizationRole(StoreRole.Manager);
        write.MapPost("/categories", CreateCategoryAsync).Produces<CategoryDto>(StatusCodes.Status201Created);
        write.MapPut("/categories/{id:guid}", UpdateCategoryAsync).Produces<CategoryDto>();
        write.MapPost("/products", CreateProductAsync).Produces<ProductDetail>(StatusCodes.Status201Created);
        write.MapPut("/products/{id:guid}", UpdateProductAsync).Produces<ProductDetail>();
        write.MapPatch("/products/{id:guid}/status", SetProductStatusAsync).Produces<ProductDetail>();
        write.MapPost("/products/{id:guid}/variants", CreateVariantAsync).Produces<ProductDetail>(StatusCodes.Status201Created);
        write.MapPut("/products/{id:guid}/variants/{variantId:guid}", UpdateVariantAsync).Produces<ProductDetail>();
        write.MapPut("/products/{id:guid}/components", SetComponentsAsync).Produces<ProductDetail>();
        write.MapPost("/products/{id:guid}/images", UploadImageAsync).DisableAntiforgery().Produces<ProductDetail>(StatusCodes.Status201Created);
        write.MapPatch("/products/{id:guid}/images/{imageId:guid}", UpdateImageAsync).Produces<ProductDetail>();
        write.MapDelete("/products/{id:guid}/images/{imageId:guid}", DeleteImageAsync).Produces<ProductDetail>();
        return api;
    }

    // ---- Kategoriler ----

    private static async Task<IResult> ListCategoriesAsync(AppDbContext db, CurrentUser user, CancellationToken ct, bool includeInactive = false)
    {
        var canManage = user.Role >= StoreRole.Manager;
        var query = db.Categories.AsNoTracking();
        if (!(includeInactive && canManage))
        {
            query = query.Where(c => c.IsActive);
        }

        var items = await query.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
        return TypedResults.Ok(items.Select(c => c.ToDto(canManage)).ToList());
    }

    private static async Task<IResult> CreateCategoryAsync(CategoryWriteRequest request, AppDbContext db, CurrentUser user, CancellationToken ct)
    {
        if (await db.Categories.AnyAsync(c => c.Slug == request.Slug, ct))
        {
            return ApiProblems.Conflict("DUPLICATE_SLUG", "Bu kısa ad başka bir kategoride kullanılıyor.");
        }

        var category = new Category { OrganizationId = user.OrganizationId, Name = request.Name.Trim(), Slug = request.Slug };
        if (await Apply(category, request, db, ct) is { } problem)
        {
            return problem;
        }

        db.Categories.Add(category);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/categories/{category.Id}", category.ToDto(includeAiHint: true));
    }

    private static async Task<IResult> UpdateCategoryAsync(Guid id, CategoryWriteRequest request, AppDbContext db, CancellationToken ct)
    {
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (category is null)
        {
            return ApiProblems.NotFound();
        }

        if (await db.Categories.AnyAsync(c => c.Slug == request.Slug && c.Id != id, ct))
        {
            return ApiProblems.Conflict("DUPLICATE_SLUG", "Bu kısa ad başka bir kategoride kullanılıyor.");
        }

        category.Slug = request.Slug;
        category.Name = request.Name.Trim();
        if (await Apply(category, request, db, ct) is { } problem)
        {
            return problem;
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(category.ToDto(includeAiHint: true));
    }

    private static async Task<IResult?> Apply(Category category, CategoryWriteRequest request, AppDbContext db, CancellationToken ct)
    {
        var conflicts = (request.ConflictsWith ?? []).Distinct().Where(c => c != category.Id).ToList();
        if (conflicts.Count > 0 && await db.Categories.CountAsync(c => conflicts.Contains(c.Id), ct) != conflicts.Count)
        {
            return ApiProblems.BadRequest("UNKNOWN_CATEGORY", "Çakışan kategorilerden biri bulunamadı.");
        }

        category.SortOrder = request.SortOrder;
        category.BodyZone = request.BodyZone;
        category.LayerOrder = request.LayerOrder;
        category.MaxSelection = request.MaxSelection;
        category.ConflictsWith = conflicts;
        category.AiHint = string.IsNullOrWhiteSpace(request.AiHint) ? null : request.AiHint.Trim();
        category.IsActive = request.IsActive;
        return null;
    }

    // ---- Ürünler: okuma ----

    private static async Task<IResult> ListProductsAsync(
        AppDbContext db, CurrentUser user, ImageLinks links, CancellationToken ct,
        ProductType? type = null, Guid? categoryId = null, string? q = null,
        string status = "active", bool selectable = false, string sort = "recommended", int page = 1, int pageSize = 24, bool tryOn = false)
    {
        var canManage = user.Role >= StoreRole.Manager;
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Products.AsNoTracking().AsQueryable();
        // Yalnızca yönetici pasif/arşivli ürünleri görebilir
        var effectiveStatus = canManage ? status : "active";
        query = effectiveStatus switch
        {
            "inactive" => query.Where(p => !p.IsActive || p.ArchivedAt != null),
            "all" => query,
            _ => query.Where(p => p.IsActive && p.ArchivedAt == null),
        };

        if (type is not null)
        {
            query = query.Where(p => p.ProductType == type);
        }

        if (categoryId is not null)
        {
            query = query.Where(p => p.CategoryId == categoryId);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            // LIKE joker karakterleri (T-SQL'de "[" de karakter sınıfı başlatır) kaçırılır; büyük/küçük harf duyarsızlığını
            // sütun collation'ı sağlar (ad: Turkish_100_CI_AS, kod: Latin1_General_100_CI_AS_SC)
            var pattern = $"%{q.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal)}%";
            query = query.Where(p => EF.Functions.Like(p.Name, pattern, "\\") || EF.Functions.Like(p.Code, pattern, "\\"));
        }

        if (tryOn)
        {
            // Tek ürün giydirme: hazır takımlar + tüm vücut tekil ürünler (kaftan)
            query = query.Where(p => p.ProductType == ProductType.OutfitSet || (p.ProductType == ProductType.Single && p.Category!.BodyZone == BodyZone.FullBody));
        }

        if (selectable)
        {
            // Seçim ekranı: AI referans görseli olmayan ürünler hiç listelenmez (kesin hazırlık kontrolü aşağıda bellekte)
            query = query.Where(p => p.IsActive && p.Images.Any(i => i.IsAiReference && i.Angle == ImageAngle.Front));
        }

        query = sort switch
        {
            "name" => query.OrderBy(p => p.Name),
            "code" => query.OrderBy(p => p.Code),
            "newest" => query.OrderByDescending(p => p.CreatedAt),
            _ => query.OrderBy(p => p.Category!.SortOrder).ThenBy(p => p.Code),
        };

        var total = await query.CountAsync(ct);
        var products = await query
            .Include(p => p.Category).Include(p => p.Variants).Include(p => p.Images)
            .AsSplitQuery().Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var files = await ImageLinks.LoadFilesAsync(db, products.SelectMany(p => p.Images.Select(i => i.FileId)), ct);
        var items = products.Select(p => ToListItem(p, files, links)).ToList();
        if (selectable)
        {
            items = items.Where(i => i.IsReadyForAi).ToList();
        }

        return TypedResults.Ok(new Page<ProductListItem>(items, page, pageSize, total));
    }

    private static async Task<IResult> GetProductAsync(Guid id, AppDbContext db, CurrentUser user, ImageLinks links, CancellationToken ct)
    {
        var detail = await LoadDetailAsync(db, id, user.Role >= StoreRole.Manager, links, ct);
        return detail is null ? ApiProblems.NotFound("Ürün bulunamadı.") : TypedResults.Ok(detail);
    }

    private static ProductListItem ToListItem(Product p, Dictionary<Guid, StoredFile> files, ImageLinks links) =>
        new(p.Id, p.Code, p.Name, p.ProductType, p.CategoryId, p.Category?.Name ?? string.Empty, p.Tagline, p.Subtitle(),
            p.IsActive && p.ArchivedAt is null, p.IsReady(), CoverUrls(p, null, files, links), VariantDtos(p, files, links));

    private static ImageUrls? CoverUrls(Product p, Guid? variantId, Dictionary<Guid, StoredFile> files, ImageLinks links) =>
        p.PickCover(variantId) is { } image && files.TryGetValue(image.FileId, out var file) ? links.Catalog(file) : null;

    private static List<VariantDto> VariantDtos(Product p, Dictionary<Guid, StoredFile> files, ImageLinks links) =>
        p.Variants.OrderByDescending(v => v.IsDefault).ThenBy(v => v.ColorName).Select(v =>
        {
            // Yalnızca bu varyanta özel görsel varsa gösterilir; yoksa istemci ürün kapağını kullanır
            var own = p.Images.Where(i => i.VariantId == v.Id).OrderByDescending(i => i.Angle == ImageAngle.Front).ThenByDescending(i => i.IsAiReference).ThenBy(i => i.SortOrder).FirstOrDefault();
            return new VariantDto(v.Id, v.Code, v.ColorName, v.ColorHex, v.IsDefault, v.IsActive, own is not null && files.TryGetValue(own.FileId, out var f) ? links.Catalog(f) : null);
        }).ToList();

    internal static async Task<ProductDetail?> LoadDetailAsync(AppDbContext db, Guid id, bool includeAiNotes, ImageLinks links, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking()
            .Include(p => p.Category).Include(p => p.Variants).Include(p => p.Images).Include(p => p.Components)
            .AsSplitQuery().SingleOrDefaultAsync(p => p.Id == id, ct);
        if (product is null)
        {
            return null;
        }

        var componentIds = product.Components.Select(c => c.ComponentProductId).ToList();
        var componentProducts = componentIds.Count == 0
            ? []
            : await db.Products.AsNoTracking().Include(p => p.Category).Include(p => p.Variants).Include(p => p.Images)
                .Where(p => componentIds.Contains(p.Id)).AsSplitQuery().ToListAsync(ct);

        var fileIds = product.Images.Select(i => i.FileId).Concat(componentProducts.SelectMany(p => p.Images.Select(i => i.FileId)));
        var files = await ImageLinks.LoadFilesAsync(db, fileIds, ct);

        var images = product.Images.OrderBy(i => i.SortOrder).ThenBy(i => i.CreatedAt)
            .Where(i => files.ContainsKey(i.FileId))
            .Select(i => new ProductImageDto(i.Id, i.VariantId, i.Angle, i.Kind, i.IsAiReference, i.SortOrder, links.Catalog(files[i.FileId])))
            .ToList();

        var components = product.Components.Select(c =>
        {
            var cp = componentProducts.Single(p => p.Id == c.ComponentProductId);
            return new SetComponentDto(cp.Id, cp.Code, cp.Name, cp.Category?.Name ?? string.Empty, c.ComponentVariantId, CoverUrls(cp, c.ComponentVariantId, files, links));
        }).ToList();

        return new ProductDetail(
            product.Id, product.Code, product.Name, product.ProductType, product.CategoryId, product.Category?.Name ?? string.Empty,
            product.Description, product.Tagline, product.GenderTarget, product.LayerOrderOverride, includeAiNotes ? product.AiNotes : null,
            product.IsActive && product.ArchivedAt is null, product.IsReady(), CoverUrls(product, null, files, links),
            VariantDtos(product, files, links), images, components);
    }

    // ---- Ürünler: yazma ----

    private static async Task<IResult> CreateProductAsync(ProductWriteRequest request, AppDbContext db, CurrentUser user, ImageLinks links, CancellationToken ct)
    {
        var code = request.Code.Trim();
        if (await db.Products.AnyAsync(p => p.Code == code, ct))
        {
            return ApiProblems.Conflict("DUPLICATE_CODE", "Bu ürün kodu zaten kullanılıyor.");
        }

        if (!await db.Categories.AnyAsync(c => c.Id == request.CategoryId && c.IsActive, ct))
        {
            return ApiProblems.BadRequest("UNKNOWN_CATEGORY", "Kategori bulunamadı.");
        }

        var product = new Product { OrganizationId = user.OrganizationId, CategoryId = request.CategoryId, Code = code, Name = request.Name.Trim() };
        ApplyProduct(product, request);
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/products/{product.Id}", await LoadDetailAsync(db, product.Id, true, links, ct));
    }

    private static async Task<IResult> UpdateProductAsync(Guid id, ProductWriteRequest request, AppDbContext db, ImageLinks links, CancellationToken ct)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (product is null)
        {
            return ApiProblems.NotFound("Ürün bulunamadı.");
        }

        var code = request.Code.Trim();
        if (await db.Products.AnyAsync(p => p.Code == code && p.Id != id, ct))
        {
            return ApiProblems.Conflict("DUPLICATE_CODE", "Bu ürün kodu zaten kullanılıyor.");
        }

        if (!await db.Categories.AnyAsync(c => c.Id == request.CategoryId, ct))
        {
            return ApiProblems.BadRequest("UNKNOWN_CATEGORY", "Kategori bulunamadı.");
        }

        if (request.ProductType != product.ProductType && await db.OutfitSetComponents.AnyAsync(c => c.SetProductId == id || c.ComponentProductId == id, ct))
        {
            return ApiProblems.Conflict("PRODUCT_TYPE_LOCKED", "Takım bileşeni olan ürünün tipi değiştirilemez.");
        }

        product.Code = code;
        product.Name = request.Name.Trim();
        product.CategoryId = request.CategoryId;
        ApplyProduct(product, request);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await LoadDetailAsync(db, id, true, links, ct));
    }

    private static void ApplyProduct(Product product, ProductWriteRequest request)
    {
        product.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        product.Tagline = string.IsNullOrWhiteSpace(request.Tagline) ? null : request.Tagline.Trim();
        product.ProductType = request.ProductType;
        product.GenderTarget = request.GenderTarget;
        product.LayerOrderOverride = request.LayerOrderOverride;
        product.AiNotes = string.IsNullOrWhiteSpace(request.AiNotes) ? null : request.AiNotes.Trim();
        product.IsActive = request.IsActive;
        product.ArchivedAt = request.IsActive ? null : product.ArchivedAt;
    }

    private static async Task<IResult> SetProductStatusAsync(Guid id, ProductStatusRequest request, AppDbContext db, ImageLinks links, CancellationToken ct)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (product is null)
        {
            return ApiProblems.NotFound("Ürün bulunamadı.");
        }

        product.IsActive = request.IsActive;
        product.ArchivedAt = request.IsActive ? null : DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await LoadDetailAsync(db, id, true, links, ct));
    }

    // ---- Varyantlar ----

    private static async Task<IResult> CreateVariantAsync(Guid id, VariantWriteRequest request, AppDbContext db, CurrentUser user, ImageLinks links, CancellationToken ct)
    {
        var product = await db.Products.Include(p => p.Variants).SingleOrDefaultAsync(p => p.Id == id, ct);
        if (product is null)
        {
            return ApiProblems.NotFound("Ürün bulunamadı.");
        }

        var code = request.Code.Trim();
        if (product.Variants.Any(v => string.Equals(v.Code, code, StringComparison.OrdinalIgnoreCase)))
        {
            return ApiProblems.Conflict("DUPLICATE_CODE", "Bu varyant kodu zaten kullanılıyor.");
        }

        var isFirst = product.Variants.Count == 0;
        var variant = new ProductVariant
        {
            OrganizationId = user.OrganizationId, ProductId = product.Id, Code = code,
            ColorName = request.ColorName.Trim(), ColorHex = request.ColorHex?.ToUpperInvariant(), IsActive = request.IsActive,
        };
        db.ProductVariants.Add(variant);
        await db.SaveChangesAsync(ct);
        await SetDefaultVariantAsync(db, product, variant, request.IsDefault || isFirst, ct);
        return TypedResults.Created($"/api/v1/products/{id}", await LoadDetailAsync(db, id, true, links, ct));
    }

    private static async Task<IResult> UpdateVariantAsync(Guid id, Guid variantId, VariantWriteRequest request, AppDbContext db, ImageLinks links, CancellationToken ct)
    {
        var product = await db.Products.Include(p => p.Variants).SingleOrDefaultAsync(p => p.Id == id, ct);
        var variant = product?.Variants.SingleOrDefault(v => v.Id == variantId);
        if (product is null || variant is null)
        {
            return ApiProblems.NotFound("Varyant bulunamadı.");
        }

        var code = request.Code.Trim();
        if (product.Variants.Any(v => v.Id != variantId && string.Equals(v.Code, code, StringComparison.OrdinalIgnoreCase)))
        {
            return ApiProblems.Conflict("DUPLICATE_CODE", "Bu varyant kodu zaten kullanılıyor.");
        }

        variant.Code = code;
        variant.ColorName = request.ColorName.Trim();
        variant.ColorHex = request.ColorHex?.ToUpperInvariant();
        variant.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        await SetDefaultVariantAsync(db, product, variant, request.IsDefault, ct);
        return TypedResults.Ok(await LoadDetailAsync(db, id, true, links, ct));
    }

    /// <summary>Ürün başına tek varsayılan varyant (kısmi benzersiz indeks). Önce eskiler kaldırılır, sonra yenisi atanır.</summary>
    private static async Task SetDefaultVariantAsync(AppDbContext db, Product product, ProductVariant variant, bool makeDefault, CancellationToken ct)
    {
        if (!makeDefault || variant.IsDefault && product.Variants.Count(v => v.IsDefault) == 1)
        {
            return;
        }

        foreach (var other in product.Variants.Where(v => v.IsDefault && v.Id != variant.Id))
        {
            other.IsDefault = false;
        }

        await db.SaveChangesAsync(ct);
        variant.IsDefault = true;
        await db.SaveChangesAsync(ct);
    }

    // ---- Hazır takım bileşenleri ----

    private static async Task<IResult> SetComponentsAsync(Guid id, IReadOnlyList<SetComponentRequest> request, AppDbContext db, CurrentUser user, ImageLinks links, CancellationToken ct)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (product is null)
        {
            return ApiProblems.NotFound("Ürün bulunamadı.");
        }

        if (product.ProductType != ProductType.OutfitSet)
        {
            return ApiProblems.BadRequest("NOT_AN_OUTFIT_SET", "Yalnızca hazır takımların bileşenleri olur.");
        }

        var ids = request.Select(c => c.ComponentProductId).Distinct().ToList();
        if (ids.Count != request.Count || ids.Contains(id))
        {
            return ApiProblems.BadRequest("INVALID_COMPONENTS", "Bileşen listesi geçersiz.");
        }

        var singles = await db.Products.Include(p => p.Variants).Where(p => ids.Contains(p.Id) && p.ProductType == ProductType.Single).ToListAsync(ct);
        if (singles.Count != ids.Count)
        {
            return ApiProblems.BadRequest("INVALID_COMPONENTS", "Bileşenler mevcut, bağımsız ürünler olmalı.");
        }

        if (request.Any(c => c.ComponentVariantId is { } v && !singles.Single(s => s.Id == c.ComponentProductId).Variants.Any(x => x.Id == v)))
        {
            return ApiProblems.BadRequest("INVALID_COMPONENTS", "Varyant ürüne ait değil.");
        }

        db.OutfitSetComponents.RemoveRange(await db.OutfitSetComponents.Where(c => c.SetProductId == id).ToListAsync(ct));
        db.OutfitSetComponents.AddRange(request.Select(c => new OutfitSetComponent
        {
            OrganizationId = user.OrganizationId, SetProductId = id, ComponentProductId = c.ComponentProductId, ComponentVariantId = c.ComponentVariantId,
        }));
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await LoadDetailAsync(db, id, true, links, ct));
    }

    // ---- Görseller ----

    private static async Task<IResult> UploadImageAsync(
        Guid id, [FromForm] ProductImageForm form, AppDbContext db, CurrentUser user, ImageStore images, ImageLinks links, CancellationToken ct)
    {
        var product = await db.Products.Include(p => p.Variants).Include(p => p.Images).SingleOrDefaultAsync(p => p.Id == id, ct);
        if (product is null)
        {
            return ApiProblems.NotFound("Ürün bulunamadı.");
        }

        if (form.VariantId is { } variantId && product.Variants.All(v => v.Id != variantId))
        {
            return ApiProblems.BadRequest("UNKNOWN_VARIANT", "Varyant bu ürüne ait değil.");
        }

        SavedImage saved;
        try
        {
            var raw = await UploadReader.ReadAsync(form.File, ImageProcessingOptions.CatalogImage.MaxBytes, ct);
            saved = await images.SaveAsync(db, raw, ImageProcessingOptions.CatalogImage, user.OrganizationId, FilePurpose.ProductImage, null,
                fileId => StorageKeys.CatalogFile(user.OrganizationId, id, fileId, "jpg"), ct);
        }
        catch (ImageRejectedException ex)
        {
            return ApiProblems.FromImage(ex);
        }

        if (form.IsAiReference)
        {
            await DemoteAiReferencesAsync(db, product, form.VariantId, form.Angle, exceptImageId: null, ct);
        }

        db.ProductImages.Add(new ProductImage
        {
            OrganizationId = user.OrganizationId, ProductId = id, VariantId = form.VariantId, FileId = saved.File.Id,
            Angle = form.Angle, Kind = form.Kind, IsAiReference = form.IsAiReference, SortOrder = product.Images.Count,
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/products/{id}", await LoadDetailAsync(db, id, true, links, ct));
    }

    private static async Task<IResult> UpdateImageAsync(Guid id, Guid imageId, ProductImageUpdateRequest request, AppDbContext db, ImageLinks links, CancellationToken ct)
    {
        var product = await db.Products.Include(p => p.Images).SingleOrDefaultAsync(p => p.Id == id, ct);
        var image = product?.Images.SingleOrDefault(i => i.Id == imageId);
        if (product is null || image is null)
        {
            return ApiProblems.NotFound("Görsel bulunamadı.");
        }

        if (request.Angle is { } angle)
        {
            image.Angle = angle;
        }

        if (request.Kind is { } kind)
        {
            image.Kind = kind;
        }

        if (request.SortOrder is { } sort)
        {
            image.SortOrder = sort;
        }

        if (request.IsAiReference is { } isAi)
        {
            if (isAi)
            {
                await DemoteAiReferencesAsync(db, product, image.VariantId, image.Angle, image.Id, ct);
            }

            image.IsAiReference = isAi;
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await LoadDetailAsync(db, id, true, links, ct));
    }

    /// <summary>Aynı ürün + varyant + açı için tek AI referansı kuralı (DB kısmi benzersiz indeksi): eski referans önce kaldırılır.</summary>
    private static async Task DemoteAiReferencesAsync(AppDbContext db, Product product, Guid? variantId, ImageAngle angle, Guid? exceptImageId, CancellationToken ct)
    {
        var existing = product.Images.Where(i => i.IsAiReference && i.VariantId == variantId && i.Angle == angle && i.Id != exceptImageId).ToList();
        if (existing.Count == 0)
        {
            return;
        }

        existing.ForEach(i => i.IsAiReference = false);
        await db.SaveChangesAsync(ct);
    }

    private static async Task<IResult> DeleteImageAsync(Guid id, Guid imageId, AppDbContext db, ImageStore images, ImageLinks links, CancellationToken ct)
    {
        var image = await db.ProductImages.SingleOrDefaultAsync(i => i.Id == imageId && i.ProductId == id, ct);
        if (image is null)
        {
            return ApiProblems.NotFound("Görsel bulunamadı.");
        }

        var file = await db.StoredFiles.SingleOrDefaultAsync(f => f.Id == image.FileId, ct);
        db.ProductImages.Remove(image);
        if (file is not null)
        {
            db.StoredFiles.Remove(file);
        }

        await db.SaveChangesAsync(ct);
        if (file is not null)
        {
            await images.DeleteAsync(file, ct);
        }

        return TypedResults.Ok(await LoadDetailAsync(db, id, true, links, ct));
    }
}
