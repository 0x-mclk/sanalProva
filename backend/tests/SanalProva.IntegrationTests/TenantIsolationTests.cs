using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SanalProva.Api.Features.Catalog;
using SanalProva.Api.Infrastructure;
using SanalProva.Api.Features.TryOn;
using SanalProva.Domain.Generation;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.IntegrationTests;

/// <summary>
/// M3.4: firma ve şube izolasyonunun gerçek HTTP yığını üzerinden (IDOR/BOLA) sınanması. İki firma (A: iki şube, B: bir şube) ve her birinin kataloğu, A'nın şube-1'inde
/// tam bir oturum (onay, fotoğraf, taslak, tamamlanmış iş ve sonuç görseli) kurulur. Saldırganlar (başka firmanın personeli/yöneticisi, aynı firmanın başka şubesi)
/// A'nın <b>gerçek kimlikleriyle</b> her uç noktayı dener. Kanıtlananlar: hiçbir istek 2xx/5xx dönmez, A'nın verisi hiçbir tabloda değişmez (tablo bazında sağlama toplamı),
/// saldırı sonrası veritabanında hiçbir çapraz-firma/çapraz-şube referans oluşmaz (<see cref="TenantScan"/>).
/// </summary>
public sealed class TenantIsolationTests(ApiFactory factory) : ApiTestBase(factory)
{
    private sealed record World(
        TestTenant A, Guid StoreA2, ApiClient AManager1, ApiClient AStaff1, ApiClient AStaff2, ApiClient AManager2,
        TestTenant B, ApiClient BManager, ApiClient BStaff, CatalogIds CatA, CatalogIds CatB,
        Guid SessionA, Guid PhotoA, Guid JobA, Guid ImageA, Guid ProductImageA, Guid SessionB, Guid PhotoB, Guid ImageB);

    private async Task<World> BuildWorldAsync()
    {
        ApiFactory.RequireDatabase(Factory);
        var a = await TestData.CreateTenantAsync(Services, StoreRole.Manager);
        var storeA2 = await TestData.AddStoreAsync(Services, a.OrganizationId, "A2");
        var aStaff1 = await TestData.AddUserAsync(Services, a.OrganizationId, [(a.StoreId, StoreRole.Staff)]);
        var aStaff2 = await TestData.AddUserAsync(Services, a.OrganizationId, [(storeA2, StoreRole.Staff)]);
        var aManager2 = await TestData.AddUserAsync(Services, a.OrganizationId, [(storeA2, StoreRole.Manager)]);
        var b = await TestData.CreateTenantAsync(Services, StoreRole.Manager);
        var bStaff = await TestData.AddUserAsync(Services, b.OrganizationId, [(b.StoreId, StoreRole.Staff)]);

        var catA = await TestData.CreateCatalogAsync(Services, a.OrganizationId);
        var catB = await TestData.CreateCatalogAsync(Services, b.OrganizationId);
        var aManager1Client = await ApiClient.LoginAsync(Factory, a.Email, a.StoreId);
        var aStaff1Client = await ApiClient.LoginAsync(Factory, aStaff1, a.StoreId);
        var bManagerClient = await ApiClient.LoginAsync(Factory, b.Email, b.StoreId);
        var bStaffClient = await ApiClient.LoginAsync(Factory, bStaff, b.StoreId);

        async Task<(Guid Session, Guid Photo, Guid Job, Guid Image)> Complete(ApiClient client, Guid setProduct)
        {
            var (session, photo) = await PreparedSessionAsync(client);
            await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, setProduct);
            var job = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id)).Id);
            Assert.NotNull(job.ResultImageId);
            return (session.Id, photo.Id, job.Id, job.ResultImageId!.Value);
        }

        var (sessionA, photoA, jobA, imageA) = await Complete(aStaff1Client, catA.SetProduct);
        var (sessionB, photoB, _, imageB) = await Complete(bStaffClient, catB.SetProduct);
        var productImageA = (await aManager1Client.GetAsync<ProductDetail>($"/api/v1/products/{catA.SetProduct}")).Images[0].Id;

        return new World(
            a, storeA2, aManager1Client, aStaff1Client, await ApiClient.LoginAsync(Factory, aStaff2, storeA2), await ApiClient.LoginAsync(Factory, aManager2, storeA2),
            b, bManagerClient, bStaffClient, catA, catB, sessionA, photoA, jobA, imageA, productImageA, sessionB, photoB, imageB);
    }

    /// <summary>Firmanın tüm tablolarındaki satırların parmak izi: değişiklik (ekleme, güncelleme, silme) hangi tabloda olursa olsun farklı çıkar.</summary>
    private async Task<Dictionary<string, string>> SnapshotAsync(Guid organizationId)
    {
        var ct = TestContext.Current.CancellationToken;
        var rows = await RowFactory.LoadAsync(Factory.MigratorConnectionString!, ct);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = new SqlConnection(Factory.MigratorConnectionString);
        await connection.OpenAsync(ct);
        foreach (var table in rows.Columns.Keys.Where(t => rows.Columns[t].Any(c => c.Name == "organization_id")).Order(StringComparer.Ordinal))
        {
            // organizations.id ve users.organization_id da kapsanır; oturum çerezi/kimlik tabloları (user_*) dışarıda: giriş denetim verisi değişebilir
            await using var command = new SqlCommand($"SELECT CONCAT(COUNT_BIG(*), '|', CHECKSUM_AGG(CHECKSUM(*))) FROM [{table}] WHERE organization_id = @org", connection);
            command.Parameters.AddWithValue("@org", organizationId);
            result[table] = Convert.ToString(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture)!;
        }

        return result;
    }

    private sealed record Attack(string Name, Func<ApiClient, Task<HttpResponseMessage>> Send, bool OrgWideCatalog = false);

    private static Attack Get(string name, string url, bool catalog = false) => new(name, c => c.GetAsync(url), catalog);

    private static Attack Send(string name, HttpMethod method, string url, object? body = null, bool catalog = false) =>
        new(name, c => c.SendAsync(method, url, body ?? (method == HttpMethod.Get ? null : new { })), catalog);

    private static IEnumerable<Attack> Attacks(World w)
    {
        const string api = "/api/v1";
        var s = w.SessionA;
        var category = new { name = "Saldiri", slug = "saldiri", sortOrder = 1, bodyZone = "Upper", layerOrder = 1, maxSelection = 1, conflictsWith = Array.Empty<Guid>(), aiHint = (string?)null, isActive = true };
        var variant = new { code = "HACK", colorName = "Kirmizi", colorHex = "#FF0000", isDefault = false, isActive = true };

        // --- Oturum ve alt kaynakları (şube düzeyi)
        yield return Get("GET oturum", $"{api}/sessions/{s}");
        yield return Send("DELETE oturum", HttpMethod.Delete, $"{api}/sessions/{s}");
        yield return Send("POST kapat", HttpMethod.Post, $"{api}/sessions/{s}/close");
        yield return Send("POST onay", HttpMethod.Post, $"{api}/sessions/{s}/consent", FullConsent());
        yield return Send("DELETE onay", HttpMethod.Delete, $"{api}/sessions/{s}/consent");
        yield return Send("PUT taslak", HttpMethod.Put, $"{api}/sessions/{s}/draft", new { mode = "OutfitSet", items = new[] { new { productId = w.CatA.SetProduct, variantId = (Guid?)null } } });
        yield return Get("GET görseller", $"{api}/sessions/{s}/images");
        yield return new("POST üretim", c => c.SendAsync(HttpMethod.Post, $"{api}/sessions/{s}/generations", new { photoId = w.PhotoA }, configure: r => r.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"))));
        yield return Send("POST revizyon", HttpMethod.Post, $"{api}/sessions/{s}/revisions", new { baseImageId = w.ImageA, type = "Recolor", color = "#FF0000" });
        yield return new("POST fotoğraf", c => c.UploadAsync($"{api}/sessions/{s}/photos", TestImages.Portrait(9)));
        yield return Send("DELETE fotoğraf", HttpMethod.Delete, $"{api}/sessions/{s}/photos/{w.PhotoA}");
        yield return Get("GET iş", $"{api}/jobs/{w.JobA}");
        yield return Send("POST iş iptal", HttpMethod.Post, $"{api}/jobs/{w.JobA}/cancel");
        yield return new("POST iş yeniden dene", c => c.SendAsync(HttpMethod.Post, $"{api}/jobs/{w.JobA}/retry", new { }));
        yield return Send("PATCH görsel favori", HttpMethod.Patch, $"{api}/images/{w.ImageA}/favorite", new { isFavorite = true });

        // --- Katalog (firma düzeyi: aynı firmanın başka şubesi meşru olarak erişir, başka firma asla)
        yield return Get("GET ürün", $"{api}/products/{w.CatA.SetProduct}", catalog: true);
        yield return Send("PUT ürün", HttpMethod.Put, $"{api}/products/{w.CatA.SetProduct}", new { code = "HACK", name = "Hack", categoryId = w.CatA.Takim, productType = "OutfitSet", isActive = true }, catalog: true);
        yield return Send("PATCH ürün durumu", HttpMethod.Patch, $"{api}/products/{w.CatA.SetProduct}/status", new { isActive = false }, catalog: true);
        yield return Send("POST varyant", HttpMethod.Post, $"{api}/products/{w.CatA.SalvarProduct}/variants", variant, catalog: true);
        yield return Send("PUT varyant", HttpMethod.Put, $"{api}/products/{w.CatA.SetProduct}/variants/{w.CatA.SetVariant}", variant, catalog: true);
        yield return Send("PUT bileşenler", HttpMethod.Put, $"{api}/products/{w.CatA.SetProduct}/components", new[] { new { componentProductId = w.CatA.SalvarProduct, componentVariantId = (Guid?)null } }, catalog: true);
        yield return new("POST ürün görseli", c => c.UploadAsync($"{api}/products/{w.CatA.SetProduct}/images", TestImages.Jpeg(800, 1000)), true);
        yield return Send("PATCH ürün görseli", HttpMethod.Patch, $"{api}/products/{w.CatA.SetProduct}/images/{w.ProductImageA}", new { sortOrder = 7 }, catalog: true);
        yield return Send("DELETE ürün görseli", HttpMethod.Delete, $"{api}/products/{w.CatA.SetProduct}/images/{w.ProductImageA}", catalog: true);
        yield return Send("PUT kategori", HttpMethod.Put, $"{api}/categories/{w.CatA.Kemer}", category, catalog: true);

        // --- Kimliği gövdede taşıyan saldırılar: saldırgan KENDİ kaynağına A'nın kimliklerini bağlamaya çalışır
        yield return Send("B taslağına A ürünü", HttpMethod.Put, $"{api}/sessions/{w.SessionB}/draft", new { mode = "OutfitSet", items = new[] { new { productId = w.CatA.SetProduct, variantId = (Guid?)w.CatA.SetVariant } } });
        yield return new("B üretimine A fotoğrafı", c => c.SendAsync(HttpMethod.Post, $"{api}/sessions/{w.SessionB}/generations", new { photoId = w.PhotoA }, configure: r => r.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"))));
        yield return Send("B ürününe A bileşeni", HttpMethod.Put, $"{api}/products/{w.CatB.SetProduct}/components", new[] { new { componentProductId = w.CatA.SalvarProduct, componentVariantId = (Guid?)w.CatA.SalvarVariant } }, catalog: true);
        yield return Send("B ürünü A kategorisinde", HttpMethod.Post, $"{api}/products", new { code = "ZZ-1", name = "Hack", categoryId = w.CatA.Gomlek, productType = "Single", isActive = true }, catalog: true);
        yield return Send("B ürünü A kategorisine taşı", HttpMethod.Put, $"{api}/products/{w.CatB.SalvarProduct}", new { code = "SL-001", name = "Hack", categoryId = w.CatA.Gomlek, productType = "Single", isActive = true }, catalog: true);
        yield return Send("B varyantına A varyantı", HttpMethod.Put, $"{api}/products/{w.CatB.SalvarProduct}/variants/{w.CatA.SalvarVariant}", variant, catalog: true);
        yield return Send("B ürün görseli yolunda A görseli", HttpMethod.Patch, $"{api}/products/{w.CatB.SetProduct}/images/{w.ProductImageA}", new { sortOrder = 9 }, catalog: true);
        yield return Send("B iş yolunda A iş kimliği", HttpMethod.Post, $"{api}/jobs/{w.JobA}/retry");
    }

    private static readonly HttpStatusCode[] Denied = [HttpStatusCode.BadRequest, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity];

    private static async Task<List<string>> RunAsync(IEnumerable<Attack> attacks, ApiClient attacker, string who, Func<Attack, bool> applicable)
    {
        var failures = new List<string>();
        foreach (var attack in attacks.Where(applicable))
        {
            using var response = await attack.Send(attacker);

            // Revizyon motoru yok (Aşama 5): uç nokta sahiplik denetiminden önce sabit 501 döner. Bu bir sızıntı değildir (yanıt oturumun varlığına/sahibine bağlı değil);
            // motor yazıldığında 2xx dönerse bu test kırılır ve sahiplik denetimi zorunlu hale gelir.
            if (!Denied.Contains(response.StatusCode) && !(attack.Name == "POST revizyon" && response.StatusCode == HttpStatusCode.NotImplemented))
            {
                failures.Add($"{who}: {attack.Name} → {(int)response.StatusCode} {response.StatusCode}");
            }
        }

        return failures;
    }

    // ------------------------------------------------------------------ saldırı matrisi

    [Fact]
    public async Task Baska_firmanin_personeli_ve_yoneticisi_A_nin_hicbir_kaynagina_dokunamaz_veri_degismez_capraz_referans_olusmaz()
    {
        var w = await BuildWorldAsync();
        var before = await SnapshotAsync(w.A.OrganizationId);

        var failures = new List<string>();
        failures.AddRange(await RunAsync(Attacks(w), w.BStaff, "B personeli", _ => true));
        failures.AddRange(await RunAsync(Attacks(w), w.BManager, "B yöneticisi", _ => true));

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        var after = await SnapshotAsync(w.A.OrganizationId);
        Assert.True(before.All(p => after[p.Key] == p.Value), "A'nın değişen tabloları: " + string.Join(", ", before.Where(p => after[p.Key] != p.Value).Select(p => p.Key))); // A'nın hiçbir tablosu değişmedi

        // Saldırgan firmanın verisinde A'ya ait hiçbir referans oluşmadı (tüm FK'lar için firma/şube tutarlılığı)
        var findings = await TenantScan.ScanAsync(Factory.MigratorConnectionString!, TestContext.Current.CancellationToken, [w.A.OrganizationId, w.B.OrganizationId]);
        Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings.Select(f => $"{f.Kind}: {f.Relation} ({f.Count})")));

        // Liste uç noktaları da sızdırmaz: B'nin listesinde A'nın hiçbir kimliği yok
        var products = await w.BManager.GetAsync<Page<ProductListItem>>("/api/v1/products?pageSize=200");
        Assert.DoesNotContain(products.Items, p => p.Id == w.CatA.SetProduct || p.Id == w.CatA.SalvarProduct);
        var categories = await w.BManager.GetAsync<List<CategoryDto>>("/api/v1/categories?includeInactive=true");
        Assert.DoesNotContain(categories, c => c.Id == w.CatA.Kemer);
        var sessions = await w.BStaff.GetAsync<Page<SessionSummary>>("/api/v1/sessions?pageSize=200");
        Assert.DoesNotContain(sessions.Items, x => x.Id == w.SessionA);
    }

    [Fact]
    public async Task Ayni_firmanin_baska_subesindeki_personel_ve_yonetici_A1_in_oturum_is_ve_gorsellerine_dokunamaz_katalog_meşru_olarak_paylasilir()
    {
        var w = await BuildWorldAsync();
        var before = await SnapshotAsync(w.A.OrganizationId);

        // Şube-2 personeli ve yöneticisi: şube-1'in oturum/iş/görsel/fotoğrafı reddedilir; katalog saldırıları firma düzeyidir (A2 yöneticisi katalogu yönetebilir) ve burada çıkarılır
        var failures = new List<string>();
        failures.AddRange(await RunAsync(Attacks(w), w.AStaff2, "A2 personeli", a => !a.OrgWideCatalog));
        failures.AddRange(await RunAsync(Attacks(w), w.AManager2, "A2 yöneticisi", a => !a.OrgWideCatalog));
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        var after = await SnapshotAsync(w.A.OrganizationId);
        Assert.True(before.All(p => after[p.Key] == p.Value), "A'nın değişen tabloları: " + string.Join(", ", before.Where(p => after[p.Key] != p.Value).Select(p => p.Key)));

        // Şube-2 oturum listesinde şube-1 oturumu yok; A1 personeli kendi oturumunu hâlâ görür
        var sessions = await w.AStaff2.GetAsync<Page<SessionSummary>>("/api/v1/sessions?pageSize=200");
        Assert.DoesNotContain(sessions.Items, x => x.Id == w.SessionA);
        Assert.Equal(w.SessionA, (await w.AStaff1.GetAsync<SessionDetail>($"/api/v1/sessions/{w.SessionA}")).Id);

        // Aynı firmanın başka şubesine ait fotoğraf, kendi şubesindeki oturumun üretiminde kullanılamaz
        var (sessionA2, _) = await PreparedSessionAsync(w.AStaff2);
        await SaveDraftAsync(w.AStaff2, sessionA2.Id, OutfitMode.OutfitSet, w.CatA.SetProduct);
        using var response = await w.AStaff2.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionA2.Id}/generations", new { photoId = w.PhotoA }, configure: r => r.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N")));
        Assert.Contains(response.StatusCode, Denied);
        Assert.Empty(await TenantScan.ScanAsync(Factory.MigratorConnectionString!, TestContext.Current.CancellationToken, [w.A.OrganizationId]));
    }

    [Fact]
    public async Task Kimliksiz_ve_yetkisiz_istekler_hicbir_kaynak_kimligi_icin_veri_dondurmez_dosya_baglantilari_sahte_olamaz()
    {
        var w = await BuildWorldAsync();

        // Kimliksiz: tüm uç noktalar 401
        var anonymous = new ApiClient(Factory.CreateClient());
        foreach (var attack in Attacks(w))
        {
            using var response = await attack.Send(anonymous);
            Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest, $"Kimliksiz {attack.Name} → {(int)response.StatusCode}");
        }

        // İmzalı dosya bağlantısı: A'nın görsel adresi sahibine çalışır ama kurcalanırsa (imza, süre, yol) çalışmaz; B'nin oturumuyla da A'nın dosyası alınamaz
        var images = await w.AStaff1.GetAsync<List<GeneratedImageDto>>($"/api/v1/sessions/{w.SessionA}/images");
        var url = images.Single(i => i.Id == w.ImageA).Urls.Url;
        Assert.Equal(HttpStatusCode.OK, (await w.AStaff1.GetAsync(url)).StatusCode);

        var uri = new Uri(new Uri("http://localhost"), url);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        string With(Action<System.Collections.Specialized.NameValueCollection> edit)
        {
            var copy = System.Web.HttpUtility.ParseQueryString(uri.Query);
            edit(copy);
            return uri.AbsolutePath + "?" + copy;
        }

        foreach (var key in query.AllKeys.Where(k => k is not null))
        {
            var tampered = With(q => q[key] = q[key] + "0");
            using var response = await w.BStaff.GetAsync(tampered);
            Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest, $"Kurcalanmış '{key}' → {(int)response.StatusCode}");
        }

        using var noSignature = await w.BStaff.GetAsync(uri.AbsolutePath);
        Assert.True(noSignature.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest, $"İmzasız → {(int)noSignature.StatusCode}");
    }

    [Fact]
    public async Task Veritabani_baska_firmanin_secimini_oturumunu_veya_fotografini_kullanan_isi_reddeder_isleyiciye_hic_ulasmaz()
    {
        var w = await BuildWorldAsync();
        var ct = TestContext.Current.CancellationToken;
        Guid foreignSelection;
        var (scope, db) = OpenDb(w.A.OrganizationId);
        using (scope)
        {
            foreignSelection = (await db.GenerationJobs.AsNoTracking().SingleAsync(j => j.Id == w.JobA, ct)).SelectionId!.Value;
        }

        // Önceki sürümde bu satır yazılabiliyor ve yalnızca işleyicinin kiracı filtresi güvenli biçimde başarısız ediyordu. Artık bileşik yabancı anahtarlar yazmayı baştan reddeder.
        (scope, db) = OpenDb(w.B.OrganizationId);
        using (scope)
        {
            var user = await db.Users.SingleAsync(u => u.Email == w.B.Email, ct);
            GenerationJob Job(Guid session, Guid? selection, Guid? photo) => new()
            {
                OrganizationId = w.B.OrganizationId, StoreId = w.B.StoreId, SessionId = session, JobType = GenerationJobType.OutfitSet, SelectionId = selection,
                SourcePhotoId = photo, IdempotencyKey = Guid.NewGuid().ToString("N"), RequestedByUserId = user.Id, Status = GenerationJobStatus.Failed, NextAttemptAt = DateTimeOffset.UtcNow,
            };

            foreach (var (name, job) in new[]
                     {
                         ("başka firmanın seçimi", Job(w.SessionB, foreignSelection, w.PhotoB)),
                         ("başka firmanın oturumu", Job(w.SessionA, null, null)),
                         ("başka firmanın fotoğrafı", Job(w.SessionB, null, w.PhotoA)),
                     })
            {
                db.GenerationJobs.Add(job);
                var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
                Assert.True(DbErrors.IsForeignKeyViolation(rejected), $"{name}: beklenen FK ihlali değil: {rejected.InnerException?.Message}");
                db.ChangeTracker.Clear();
            }
        }

        // Hiçbir çapraz referans oluşmadı
        Assert.Empty(await TenantScan.ScanAsync(Factory.MigratorConnectionString!, ct, [w.A.OrganizationId, w.B.OrganizationId]));
    }

    [Fact]
    public async Task Sube_kapsami_oturum_uzerinden_yapisal_uygulanir_fotograf_onay_secim_kalem_ve_gorsel_baska_subede_gorunmez_firma_kapsamli_isleyici_hepsini_gorur()
    {
        var w = await BuildWorldAsync();
        var ct = TestContext.Current.CancellationToken;

        Guid selectionA;
        var (selectionScope, selectionDb) = OpenDb(w.A.OrganizationId);
        using (selectionScope)
        {
            selectionA = await selectionDb.OutfitSelections.Where(x => x.SessionId == w.SessionA).Select(x => x.Id).SingleAsync(ct);
        }

        async Task<(int Photos, int Consents, int Selections, int Items, int Images)> CountsAsync(Guid organization, Guid? store)
        {
            var (scope, db) = OpenDb(organization);
            using (scope)
            {
                scope.ServiceProvider.GetRequiredService<SanalProva.Infrastructure.Tenancy.TenantContext>().StoreId = store;
                return (
                    await db.SessionPhotos.CountAsync(p => p.SessionId == w.SessionA, ct), await db.ConsentRecords.CountAsync(c => c.SessionId == w.SessionA, ct),
                    await db.OutfitSelections.CountAsync(s => s.SessionId == w.SessionA, ct), await db.OutfitSelectionItems.CountAsync(i => i.SelectionId == selectionA, ct), await db.GeneratedImages.CountAsync(i => i.SessionId == w.SessionA, ct));
            }
        }

        // Sahibi şubesi: hepsi görünür
        var own = await CountsAsync(w.A.OrganizationId, w.A.StoreId);
        Assert.True(own.Photos > 0 && own.Consents > 0 && own.Selections > 0 && own.Items > 0 && own.Images > 0, $"Sahibi şubesi hiçbir şey görmedi: {own}");

        // Aynı firmanın başka şubesi: HİÇBİRİ görünmez (bu tabloların kendi şube sütunu yok; kapsam oturum üzerinden)
        Assert.Equal((0, 0, 0, 0, 0), await CountsAsync(w.A.OrganizationId, w.StoreA2));

        // Başka firma: hiçbiri
        Assert.Equal((0, 0, 0, 0, 0), await CountsAsync(w.B.OrganizationId, w.B.StoreId));
        Assert.Equal((0, 0, 0, 0, 0), await CountsAsync(w.B.OrganizationId, null));

        // Yalnızca firma bağlamı (arka plan işleyicisi, şube seçili değil): firmanın tüm kayıtlarını görür
        Assert.Equal(own, await CountsAsync(w.A.OrganizationId, null));
    }
}
