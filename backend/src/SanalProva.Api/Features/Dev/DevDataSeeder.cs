using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SanalProva.Domain.Catalog;
using SanalProva.Domain.Common;
using SanalProva.Domain.Files;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Identity;
using SanalProva.Infrastructure.Images;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Storage;
using SanalProva.Infrastructure.Tenancy;
using SkiaSharp;

namespace SanalProva.Api.Features.Dev;

/// <summary>
/// Yalnızca Development ortamında, migration'lar uygulanmış ve ilgili tablolar boşken demo veri oluşturur:
/// firma, şube, kategoriler, 3 personel hesabı ve "Örnek" etiketli stilize ürün görselleriyle demo katalog.
/// Gerçek müşteri verisi veya gerçek ürün fotoğrafı içermez. Demo parola kaynak kodda değil, user-secrets'taki Seed:DevPassword değerindedir.
/// </summary>
public static partial class DevDataSeeder
{
    private sealed record VariantSpec(string Code, string Name, string Hex);

    private sealed record ProductSpec(
        string Code, string Name, string Tagline, DemoGarmentKind Kind, string CategorySlug, ProductType Type, string Description,
        VariantSpec[] Variants, string[]? Components = null);

    private static readonly VariantSpec Bordo = new("BRD", "Bordo", "#7A1F3A");
    private static readonly VariantSpec Zumrut = new("ZMR", "Zümrüt", "#1F6B4F");
    private static readonly VariantSpec Gece = new("GCE", "Gece mavisi", "#1D2B53");
    private static readonly VariantSpec Ekru = new("EKR", "Ekru", "#E7DDC9");
    private static readonly VariantSpec KirikBeyaz = new("KBY", "Kırık beyaz", "#F2EDE3");
    private static readonly VariantSpec Altin = new("ALT", "Altın", "#B8913A");
    private static readonly VariantSpec Siyah = new("SYH", "Siyah", "#2A2220");
    private static readonly VariantSpec Pudra = new("PDR", "Pudra", "#D9B8B0");

    private static readonly ProductSpec[] Catalog =
    [
        new("BD-014", "Sultan Bindallı", "Altın işleme", DemoGarmentKind.Bindalli, "bindalli", ProductType.OutfitSet,
            "Kadife üzerine altın sırma işlemeli, uzun etekli klasik bindallı.", [Bordo, Gece]),
        new("ŞT-008", "Lale Şalvar Takımı", "3 parça", DemoGarmentKind.SalvarTakimi, "salvar-takimi", ProductType.OutfitSet,
            "Gömlek, yelek ve şalvardan oluşan kına gecesi takımı.", [Zumrut, Bordo], ["ŞL-101", "GM-201", "YL-301"]),
        new("BD-021", "Hürrem Bindallı", "Kadife", DemoGarmentKind.Bindalli, "bindalli", ProductType.OutfitSet,
            "Geniş etekli, kadife dokulu gece mavisi bindallı.", [Gece, Bordo]),
        new("ŞT-012", "İnci Şalvar Takımı", "3 parça", DemoGarmentKind.SalvarTakimi, "salvar-takimi", ProductType.OutfitSet,
            "İnci detaylı, ekru tonlarında şalvar takımı.", [Ekru, Pudra], ["ŞL-101", "GM-202", "YL-302"]),
        new("BD-009", "Nigâr Bindallı", "İnci işleme", DemoGarmentKind.Bindalli, "bindalli", ProductType.OutfitSet,
            "Pudra tonunda, inci ve sırma işlemeli bindallı.", [Pudra, Ekru]),
        new("ŞT-015", "Gülnâr Şalvar Takımı", "Brokar", DemoGarmentKind.SalvarTakimi, "salvar-takimi", ProductType.OutfitSet,
            "Brokar kumaştan, bordo şalvar takımı.", [Bordo, Zumrut], ["ŞL-102", "GM-201", "YL-301"]),

        new("ŞL-101", "Lale Şalvar", "Büzgülü paça", DemoGarmentKind.Salvar, "salvar", ProductType.Single, "Bilekte büzgülü, geniş kesim şalvar.", [Bordo, Zumrut, Ekru]),
        new("ŞL-102", "Sırma Şalvar", "Sırma şerit", DemoGarmentKind.Salvar, "salvar", ProductType.Single, "Paçalarında sırma şerit detayı olan şalvar.", [Siyah, Gece]),
        new("GM-201", "İpek Gömlek", "İpek", DemoGarmentKind.Gomlek, "gomlek", ProductType.Single, "Şalvar altına uygun, bol kollu ipek gömlek.", [KirikBeyaz, Pudra]),
        new("GM-202", "Dantel Gömlek", "Dantel", DemoGarmentKind.Gomlek, "gomlek", ProductType.Single, "Yakası ve kolları dantelli gömlek.", [KirikBeyaz]),
        new("YL-301", "Kadife Yelek", "Altın şerit", DemoGarmentKind.Yelek, "yelek", ProductType.Single, "Kenarları altın şeritli kadife yelek.", [Bordo, Gece, Zumrut]),
        new("YL-302", "Sırma Yelek", "Sırma işleme", DemoGarmentKind.Yelek, "yelek", ProductType.Single, "Tamamı sırma işlemeli kısa yelek.", [Altin, Siyah]),
        new("KF-401", "Zümrüt Kaftan", "Uzun kesim", DemoGarmentKind.Kaftan, "kaftan", ProductType.Single, "Önü açık, uzun kesim kadife kaftan.", [Zumrut, Bordo]),
        new("KF-402", "Hünkâr Kaftan", "Altın yaka", DemoGarmentKind.Kaftan, "kaftan", ProductType.Single, "Altın yakalı, geniş kollu kaftan.", [Gece, Siyah]),
        new("KM-501", "Altın Kemer", "Tokalı", DemoGarmentKind.Kemer, "kemer", ProductType.Single, "Altın tokalı geniş bel kemeri.", [Altin]),
        new("KM-502", "Boncuk Kemer", "Boncuk işleme", DemoGarmentKind.Kemer, "kemer", ProductType.Single, "Boncuk işlemeli, kumaş bel kemeri.", [Bordo, Altin]),
    ];

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DevDataSeeder));
        var db = sp.GetRequiredService<AppDbContext>();

        try
        {
            if (!await db.Database.CanConnectAsync(ct) || (await db.Database.GetPendingMigrationsAsync(ct)).Any())
            {
                LogSkipped(logger, "veritabanı erişilemiyor veya bekleyen migration var");
                return;
            }
        }
        catch (System.Data.Common.DbException)
        {
            LogSkipped(logger, "veritabanı erişilemiyor");
            return;
        }

        if (!await db.Organizations.AnyAsync(ct))
        {
            await SeedBaseAsync(sp, db, logger, ct);
        }

        var organization = await db.Organizations.OrderBy(o => o.CreatedAt).FirstOrDefaultAsync(ct);
        if (organization is not null)
        {
            sp.GetRequiredService<TenantContext>().OrganizationId = organization.Id;
            if (!await db.Products.AnyAsync(ct))
            {
                await SeedCatalogAsync(sp, db, organization.Id, logger, ct);
            }
        }
    }

    private static async Task SeedBaseAsync(IServiceProvider sp, AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var organization = new Organization { Name = "Lâl Butik", Slug = "lal" };
        var store = new Store { OrganizationId = organization.Id, Name = "Nişantaşı Butik", Code = "NST" };
        db.AddRange(organization, store);

        db.Categories.AddRange(
            Category(organization.Id, "Bindallı", "bindalli", 10, BodyZone.FullBody, 50),
            Category(organization.Id, "Şalvar Takımı", "salvar-takimi", 20, BodyZone.FullBody, 50),
            Category(organization.Id, "Şalvar", "salvar", 30, BodyZone.Lower, 10, "Şalvar belden bileğe, paçalar bilekte toplanır."),
            Category(organization.Id, "Gömlek", "gomlek", 40, BodyZone.Upper, 10),
            Category(organization.Id, "Yelek", "yelek", 50, BodyZone.Upper, 20, "Yelek gömleğin üstüne giyilir."),
            Category(organization.Id, "Kaftan", "kaftan", 60, BodyZone.FullBody, 30, "Kaftan en dış katmandır, önü açık giyilir."),
            Category(organization.Id, "Kemer", "kemer", 70, BodyZone.Waist, 40, "Kemer belde, en üst katmanın üzerinde durur."));
        await db.SaveChangesAsync(ct);

        var password = sp.GetRequiredService<IConfiguration>()["Seed:DevPassword"];
        if (string.IsNullOrEmpty(password))
        {
            LogNoPassword(logger);
            return;
        }

        var users = sp.GetRequiredService<UserManager<AppUser>>();
        Guid? demoOwnerId = null;
        foreach (var (email, name, role) in new[]
                 {
                     ("elif@lal.local", "Elif K.", StoreRole.Staff),
                     ("yonetici@lal.local", "Mağaza Yöneticisi", StoreRole.Manager),
                     ("sahip@lal.local", "Firma Sahibi", StoreRole.Owner),
                 })
        {
            var user = new AppUser { OrganizationId = organization.Id, DisplayName = name, Email = email, UserName = email, EmailConfirmed = true };
            var result = await users.CreateAsync(user, password);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"Demo kullanıcı oluşturulamadı: {string.Join(", ", result.Errors.Select(e => e.Code))}");
            }

            db.UserStoreRoles.Add(new UserStoreRole { UserId = user.Id, StoreId = store.Id, OrganizationId = organization.Id, Role = role });
            if (role == StoreRole.Owner)
            {
                demoOwnerId = user.Id;
            }
        }

        // Demo firma (yalnızca Development + Seed:Enabled): sözleşme demo Owner adına kabul edilmiş sayılır; gerçek firmalar sözleşmeyi kendi Owner'larıyla kabul eder
        var agreement = SanalProva.Domain.Tenancy.ServiceAgreement.Document;
        db.OrganizationAgreementAcceptances.Add(new OrganizationAgreementAcceptance
        {
            OrganizationId = organization.Id, UserId = demoOwnerId!.Value, AgreementVersion = agreement.Version, TextSha256 = agreement.TextSha256, AcceptedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(ct);
        LogSeeded(logger);
    }

    private static async Task SeedCatalogAsync(IServiceProvider sp, AppDbContext db, Guid organizationId, ILogger logger, CancellationToken ct)
    {
        var images = sp.GetRequiredService<ImageStore>();
        (await db.Organizations.SingleAsync(o => o.Id == organizationId, ct)).Settings.IsDemoCatalog = true;
        var categories = await db.Categories.ToDictionaryAsync(c => c.Slug, ct);
        var products = new Dictionary<string, (Product Product, List<ProductVariant> Variants)>();

        foreach (var spec in Catalog)
        {
            var product = new Product
            {
                OrganizationId = organizationId, CategoryId = categories[spec.CategorySlug].Id, Code = spec.Code, Name = spec.Name,
                Tagline = spec.Tagline, Description = spec.Description, ProductType = spec.Type, GenderTarget = GenderTarget.Women,
            };
            db.Products.Add(product);
            var variants = spec.Variants.Select((v, i) => new ProductVariant
            {
                OrganizationId = organizationId, ProductId = product.Id, Code = v.Code, ColorName = v.Name, ColorHex = v.Hex, IsDefault = i == 0,
            }).ToList();
            db.ProductVariants.AddRange(variants);
            await db.SaveChangesAsync(ct);

            foreach (var (variant, index) in variants.Select((v, i) => (v, i)))
            {
                var color = SKColor.Parse(variant.ColorHex);
                await AddImageAsync(db, images, organizationId, product, variant.Id, ImageAngle.Front, spec.Type == ProductType.OutfitSet ? ProductImageKind.OnModel : ProductImageKind.Flat,
                    aiReference: true, DemoArt.Render(spec.Kind, color, "Örnek görsel"), index, ct);
            }

            if (spec.Type == ProductType.OutfitSet)
            {
                var first = variants[0];
                await AddImageAsync(db, images, organizationId, product, first.Id, ImageAngle.Back, ProductImageKind.OnModel,
                    aiReference: false, DemoArt.Render(spec.Kind, SKColor.Parse(first.ColorHex), "Örnek görsel · arka", back: true), 10, ct);
            }

            products[spec.Code] = (product, variants);
        }

        foreach (var spec in Catalog.Where(s => s.Components is not null))
        {
            var set = products[spec.Code].Product;
            foreach (var code in spec.Components!)
            {
                db.OutfitSetComponents.Add(new OutfitSetComponent
                {
                    OrganizationId = organizationId, SetProductId = set.Id, ComponentProductId = products[code].Product.Id, ComponentVariantId = products[code].Variants[0].Id,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        LogCatalogSeeded(logger, Catalog.Length);
    }

    private static async Task AddImageAsync(
        AppDbContext db, ImageStore images, Guid organizationId, Product product, Guid variantId, ImageAngle angle, ProductImageKind kind,
        bool aiReference, byte[] jpeg, int sortOrder, CancellationToken ct)
    {
        var saved = await images.SaveAsync(db, jpeg, ImageProcessingOptions.CatalogImage, organizationId, FilePurpose.ProductImage, null,
            fileId => StorageKeys.CatalogFile(organizationId, product.Id, fileId, "jpg"), ct);
        db.ProductImages.Add(new ProductImage
        {
            OrganizationId = organizationId, ProductId = product.Id, VariantId = variantId, FileId = saved.File.Id,
            Angle = angle, Kind = kind, IsAiReference = aiReference, SortOrder = sortOrder,
        });
        await db.SaveChangesAsync(ct);
    }

    private static Category Category(Guid organizationId, string name, string slug, int sort, BodyZone zone, int layer, string? hint = null) =>
        new() { OrganizationId = organizationId, Name = name, Slug = slug, SortOrder = sort, BodyZone = zone, LayerOrder = layer, AiHint = hint };

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo veri yükleme atlandı: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Seed:DevPassword tanımlı değil; demo kullanıcılar oluşturulmadı.")]
    private static partial void LogNoPassword(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo veriler yüklendi (1 firma, 1 şube, 7 kategori, 3 kullanıcı).")]
    private static partial void LogSeeded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo katalog yüklendi ({Count} ürün, 'Örnek' görselleriyle).")]
    private static partial void LogCatalogSeeded(ILogger logger, int count);
}
