using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

namespace SanalProva.IntegrationTests;

internal sealed record TestTenant(Guid OrganizationId, Guid StoreId, string Email, string Password);

internal sealed record CatalogIds(Guid Salvar, Guid Gomlek, Guid Yelek, Guid Kaftan, Guid Kemer, Guid Takim, Guid SetProduct, Guid SalvarProduct, Guid GomlekProduct, Guid YelekProduct, Guid KemerProduct, Guid KaftanProduct, Guid SetVariant, Guid SalvarVariant);

/// <summary>Her test kendi benzersiz firmasını oluşturur; testler birbirinin verisine dokunmaz.</summary>
internal static class TestData
{
    public const string Password = "Test-Parola-12345";

    public static async Task<TestTenant> CreateTenantAsync(IServiceProvider services, StoreRole role = StoreRole.Staff, bool acceptAgreement = true, bool paidAiEnabled = true)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..10];
        // Fixture varsayılanı: ücretli AI AÇIK (gerçek-mod sağlayıcı testleri firma kapısını geçsin). Üretimde yeni firmalar KAPALI başlar; negatif testler paidAiEnabled: false verir.
        var organization = new Organization { Name = $"Test Firma {suffix}", Slug = $"test-{suffix}", Settings = new OrganizationSettings { PaidAiEnabled = paidAiEnabled } };
        var store = new Store { OrganizationId = organization.Id, Name = "Test Şube", Code = "TST" };
        db.AddRange(organization, store);
        await db.SaveChangesAsync();

        var email = await AddUserAsync(services, organization.Id, [(store.Id, role)]);
        if (acceptAgreement)
        {
            await AcceptAgreementAsync(services, organization.Id, email);
        }

        return new TestTenant(organization.Id, store.Id, email, Password);
    }

    /// <summary>Firma adına güncel hizmet sözleşmesini (veya verilen sürümü) kabul edilmiş yazar. Fixture: gerçek akışta yalnızca Owner uç noktasıyla kabul edilir.</summary>
    public static async Task AcceptAgreementAsync(IServiceProvider services, Guid organizationId, string userEmail, string? version = null)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users.Where(u => u.Email == userEmail).Select(u => u.Id).SingleAsync();
        var agreement = ServiceAgreement.Document;
        db.OrganizationAgreementAcceptances.Add(new OrganizationAgreementAcceptance
        {
            OrganizationId = organizationId, UserId = userId, AgreementVersion = version ?? agreement.Version, TextSha256 = agreement.TextSha256, AcceptedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    public static async Task<Guid> AddStoreAsync(IServiceProvider services, Guid organizationId, string code = "ST2")
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = new Store { OrganizationId = organizationId, Name = $"Şube {code}", Code = code };
        db.Stores.Add(store);
        await db.SaveChangesAsync();
        return store.Id;
    }

    public static async Task<string> AddUserAsync(IServiceProvider services, Guid organizationId, IEnumerable<(Guid StoreId, StoreRole Role)> roles)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var email = $"personel-{Guid.NewGuid().ToString("N")[..10]}@test.local";
        var user = new AppUser { OrganizationId = organizationId, DisplayName = "Test Personel", Email = email, UserName = email };
        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Code)));
        foreach (var (storeId, role) in roles)
        {
            db.UserStoreRoles.Add(new UserStoreRole { UserId = user.Id, StoreId = storeId, OrganizationId = organizationId, Role = role });
        }

        await db.SaveChangesAsync();
        return email;
    }

    /// <summary>Firmaya 6 kategori (şalvar, gömlek, yelek, kaftan, kemer, takım), bir hazır takım ve her kategoriden bir ürün ekler; hepsinin AI referans görseli vardır.</summary>
    public static async Task<CatalogIds> CreateCatalogAsync(IServiceProvider services, Guid organizationId)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().OrganizationId = organizationId;
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var images = scope.ServiceProvider.GetRequiredService<ImageStore>();

        Category Cat(string name, int sort, BodyZone zone, int layer) =>
            new() { OrganizationId = organizationId, Name = name, Slug = name.ToLowerInvariant(), SortOrder = sort, BodyZone = zone, LayerOrder = layer };
        var salvar = Cat("Salvar", 10, BodyZone.Lower, 10);
        var gomlek = Cat("Gomlek", 20, BodyZone.Upper, 10);
        var yelek = Cat("Yelek", 30, BodyZone.Upper, 20);
        var kaftan = Cat("Kaftan", 40, BodyZone.FullBody, 30);
        var kemer = Cat("Kemer", 50, BodyZone.Waist, 40);
        var takim = Cat("Takim", 60, BodyZone.FullBody, 50);
        // Kaftan ve hazır takım birlikte seçilemez (örnek çakışma kuralı)
        kaftan.ConflictsWith = [takim.Id];
        db.Categories.AddRange(salvar, gomlek, yelek, kaftan, kemer, takim);
        await db.SaveChangesAsync();

        async Task<(Product Product, ProductVariant Variant)> AddProductAsync(Category category, string code, string name, ProductType type)
        {
            var product = new Product { OrganizationId = organizationId, CategoryId = category.Id, Code = code, Name = name, ProductType = type, Tagline = "Altın işleme" };
            var variant = new ProductVariant { OrganizationId = organizationId, ProductId = product.Id, Code = "BRD", ColorName = "Bordo", ColorHex = "#7A1F3A", IsDefault = true };
            db.Products.Add(product);
            db.ProductVariants.Add(variant);
            await db.SaveChangesAsync();

            var saved = await images.SaveAsync(db, TestImages.Jpeg(800, 1000), ImageProcessingOptions.CatalogImage, organizationId, FilePurpose.ProductImage, null,
                fileId => StorageKeys.CatalogFile(organizationId, product.Id, fileId, "jpg"), CancellationToken.None);
            db.ProductImages.Add(new ProductImage
            {
                OrganizationId = organizationId, ProductId = product.Id, FileId = saved.File.Id, Angle = ImageAngle.Front,
                Kind = ProductImageKind.OnModel, IsAiReference = true,
            });
            await db.SaveChangesAsync();
            return (product, variant);
        }

        var set = await AddProductAsync(takim, "BD-014", "Sultan Bindallı", ProductType.OutfitSet);
        var sal = await AddProductAsync(salvar, "SL-001", "Lale Şalvar", ProductType.Single);
        var gom = await AddProductAsync(gomlek, "GM-001", "İpek Gömlek", ProductType.Single);
        var yel = await AddProductAsync(yelek, "YL-001", "Kadife Yelek", ProductType.Single);
        var kem = await AddProductAsync(kemer, "KM-001", "Altın Kemer", ProductType.Single);
        var kaf = await AddProductAsync(kaftan, "KF-001", "Zümrüt Kaftan", ProductType.Single);
        return new CatalogIds(salvar.Id, gomlek.Id, yelek.Id, kaftan.Id, kemer.Id, takim.Id, set.Product.Id, sal.Product.Id, gom.Product.Id, yel.Product.Id, kem.Product.Id, kaf.Product.Id, set.Variant.Id, sal.Variant.Id);
    }
}

internal static class TestImages
{
    public static byte[] Jpeg(int width, int height, int seed = 1)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(235, 225, 210));
            using var paint = new SKPaint { IsAntialias = true };
            var random = new Random(seed);
            for (var i = 0; i < 200; i++)
            {
                paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                var size = random.Next(8, Math.Max(9, width / 8));
                canvas.DrawRect(random.Next(width), random.Next(height), size, size, paint);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 92).ToArray();
    }

    /// <summary>Boydan çekime uygun dikey fotoğraf.</summary>
    public static byte[] Portrait(int seed = 1) => Jpeg(900, 1500, seed);
}

/// <summary>Çerez ve CSRF'i yöneten, test için ince bir API istemcisi.</summary>
internal sealed class ApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private string? _csrf;

    public Guid? StoreId { get; set; }
    public string AuthCookie { get; private set; } = string.Empty;

    public static async Task<ApiClient> LoginAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string email, Guid? storeId = null, string password = TestData.Password)
    {
        var client = new ApiClient(factory.CreateClient()) { StoreId = storeId };
        var response = await client.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password, rememberMe = false });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.AuthCookie = response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).Single(c => c.StartsWith("sp_auth=", StringComparison.Ordinal));
        client._csrf = null; // antiforgery token kullanıcı kimliğine bağlıdır
        return client;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? json = null, HttpContent? content = null, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (json is not null)
        {
            request.Content = JsonContent.Create(json, options: Json);
        }
        else if (content is not null)
        {
            request.Content = content;
        }

        if (StoreId is { } storeId)
        {
            request.Headers.Add("X-Store-Id", storeId.ToString());
        }

        if (method != HttpMethod.Get)
        {
            request.Headers.Add("X-CSRF-TOKEN", _csrf ??= (await http.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf", TestContext.Current.CancellationToken)).GetProperty("token").GetString());
        }

        configure?.Invoke(request);
        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(HttpMethod.Get, url);

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == expected, $"Beklenen {expected}, gelen {(int)response.StatusCode} {response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json)!;
    }

    public async Task<T> GetAsync<T>(string url) => await ReadAsync<T>(await GetAsync(url));

    public async Task<T> PostAsync<T>(string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK, Action<HttpRequestMessage>? configure = null) =>
        await ReadAsync<T>(await SendAsync(HttpMethod.Post, url, body ?? new { }, configure: configure), expected);

    public async Task<T> PutAsync<T>(string url, object body, HttpStatusCode expected = HttpStatusCode.OK) =>
        await ReadAsync<T>(await SendAsync(HttpMethod.Put, url, body), expected);

    public async Task<T> PatchAsync<T>(string url, object body, HttpStatusCode expected = HttpStatusCode.OK) =>
        await ReadAsync<T>(await SendAsync(HttpMethod.Patch, url, body), expected);

    public Task<HttpResponseMessage> UploadAsync(string url, byte[] bytes, string fileName = "foto.jpg", string contentType = "image/jpeg", IDictionary<string, string>? fields = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);
        foreach (var (key, value) in fields ?? new Dictionary<string, string>())
        {
            form.Add(new StringContent(value), key);
        }

        return SendAsync(HttpMethod.Post, url, content: form);
    }

    /// <summary>Hata yanıtının durum kodunu ve ProblemDetails <c>code</c> alanını doğrular.</summary>
    public static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == status, $"Beklenen {status}, gelen {(int)response.StatusCode}: {body}");
        Assert.Contains($"\"code\":\"{code}\"", body, StringComparison.Ordinal);
    }

    public async Task<byte[]> DownloadAsync(string url)
    {
        var response = await http.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
    }
}
