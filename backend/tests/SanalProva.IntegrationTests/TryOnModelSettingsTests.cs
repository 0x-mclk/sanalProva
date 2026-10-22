using System.Net;
using Microsoft.EntityFrameworkCore;
using SanalProva.Api.Features.Admin;
using SanalProva.Api.Features.Catalog;
using SanalProva.Api.Features.TryOn;
using SanalProva.Api.Infrastructure;
using SanalProva.Domain.Generation;
using SanalProva.Domain.TryOn;

namespace SanalProva.IntegrationTests;

/// <summary>Yönetici model/kalite ayarları, personelin yalnızca izinli seçenekleri kullanabilmesi ve tek kaftan akışı (mock sağlayıcı; ücret yok).</summary>
public sealed class TryOnModelSettingsTests(ApiFactory factory) : ApiTestBase(factory)
{
    private static object Settings(string defaultModel, params (string Key, bool Enabled, string[] Allowed, string Default)[] models) => new
    {
        defaultModel,
        models = models.Select(m => new { key = m.Key, enabled = m.Enabled, allowedQualities = m.Allowed, defaultQuality = m.Default }).ToArray(),
    };

    private async Task<GenerationJob> LoadJobAsync(Guid jobId)
    {
        var (scope, db) = OpenDb();
        using (scope)
        {
            return await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(j => j.Id == jobId, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Varsayilanlar_seedream_ve_gpt_medium_eski_modeller_secilemez()
    {
        var (_, manager, _) = await ManagerAndStaffAsync();

        var settings = await manager.GetAsync<AiModelSettingsDto>("/api/v1/admin/ai-models");

        Assert.Equal("seedream-5-flash", settings.DefaultModel);
        var gpt = settings.Models.Single(m => m.Key == "gpt-image-2.5");
        Assert.True(gpt.Enabled);
        Assert.Equal("medium", gpt.DefaultQuality); // high varsayılan değil
        Assert.Equal(["low", "medium", "high"], gpt.AllowedQualities);
        Assert.Equal(["auto", "low", "medium", "high", "xhigh", "max"], gpt.Qualities.Select(q => q.Key).ToArray()); // fal şemasındaki altı değer
        var medium = gpt.Qualities.Single(q => q.Key == "medium");
        Assert.Equal(0.0173m, medium.MeasuredUsd);
        Assert.True(medium.EnabledByDefault);
        Assert.InRange(medium.EstimatedUsd, 0.01m, 0.05m);
        Assert.True(medium.WorstCaseUsd >= medium.EstimatedUsd);
        Assert.All(gpt.Qualities.Where(q => q.Key is "auto" or "xhigh" or "max"), q => Assert.True(q.HighCost && !q.EnabledByDefault, q.Key));
        Assert.All(gpt.Qualities, q => Assert.False(q.ExceedsJobLimit)); // mock modda ücret yok
        var byQuality = gpt.Qualities.ToDictionary(q => q.Key, q => q.EstimatedUsd);
        Assert.True(byQuality["low"] < byQuality["medium"] && byQuality["medium"] < byQuality["high"] && byQuality["high"] < byQuality["xhigh"] && byQuality["xhigh"] < byQuality["max"]);
        Assert.True(settings.Models.Single(m => m.Key == "pruna-tryon").Enabled);
        var fashn = settings.Models.Single(m => m.Key == "fashn-tryon-max");
        Assert.True(fashn.Legacy);
        Assert.False(fashn.Enabled);
        Assert.All(settings.Models, m => Assert.False(m.LiveVerified)); // hiçbir model canlı doğrulanmış gösterilmez
    }

    [Fact]
    public async Task Yonetici_izinleri_personelin_secimlerini_sinirlar()
    {
        var (tenant, manager, staff) = await ManagerAndStaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        await manager.PutAsync<AiModelSettingsDto>("/api/v1/admin/ai-models", Settings("gpt-image-2.5",
            ("seedream-5-flash", true, ["standard"], "standard"),
            ("gpt-image-2.5", true, ["low", "medium"], "medium"),
            ("pruna-tryon", false, ["standard"], "standard")));

        var options = await staff.GetAsync<TryOnOptionsDto>("/api/v1/ai/try-on-options");
        Assert.Equal("gpt-image-2.5", options.DefaultModel);
        Assert.Equal(["seedream-5-flash", "gpt-image-2.5"], options.Models.Select(m => m.Key).ToArray());
        Assert.Equal(["low", "medium"], options.Models.Single(m => m.Key == "gpt-image-2.5").Qualities.Select(q => q.Key).ToArray());
        Assert.False(options.Live); // test ortamı mock
        Assert.True(options.FreeCombinationEnabled); // mock modda DEMO olarak açık

        var (session, _) = await PreparedSessionAsync(staff);
        await SaveDraftAsync(staff, session.Id, OutfitMode.OutfitSet, catalog.KaftanProduct);

        await ApiClient.AssertProblemAsync(await StartRawAsync(staff, session.Id, "gpt-image-2.5", "high"), HttpStatusCode.BadRequest, "QUALITY_NOT_ALLOWED");
        await ApiClient.AssertProblemAsync(await StartRawAsync(staff, session.Id, "pruna-tryon", null), HttpStatusCode.BadRequest, "MODEL_NOT_ALLOWED");
        await ApiClient.AssertProblemAsync(await StartRawAsync(staff, session.Id, "fashn-tryon-max", null), HttpStatusCode.BadRequest, "MODEL_NOT_ALLOWED");
        await ApiClient.AssertProblemAsync(await StartRawAsync(staff, session.Id, "uydurma-model", null), HttpStatusCode.BadRequest, "MODEL_NOT_ALLOWED");

        // Seçim yapılmazsa firmanın varsayılanı (gpt-image-2.5 + medium) işe yazılır
        var job = await StartGenerationAsync(staff, session.Id);
        var stored = await LoadJobAsync(job.Id);
        Assert.Equal(("gpt-image-2.5", "medium"), (stored.ModelKey, stored.Quality));

        var low = await ApiClient.ReadAsync<JobDto>(await StartRawAsync(staff, session.Id, "gpt-image-2.5", "low"), HttpStatusCode.Accepted);
        Assert.Equal("low", (await LoadJobAsync(low.Id)).Quality);
    }

    [Fact]
    public async Task Yonetici_auto_xhigh_max_acinca_personel_secer_ve_islem_gecmisine_yazilir()
    {
        var (tenant, manager, staff) = await ManagerAndStaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
        var saved = await manager.PutAsync<AiModelSettingsDto>("/api/v1/admin/ai-models", Settings("gpt-image-2.5",
            ("seedream-5-flash", true, ["standard"], "standard"),
            ("gpt-image-2.5", true, ["auto", "low", "medium", "high", "xhigh", "max"], "medium"),
            ("pruna-tryon", true, ["standard", "turbo"], "standard")));
        Assert.Equal(["auto", "low", "medium", "high", "xhigh", "max"], saved.Models.Single(m => m.Key == "gpt-image-2.5").AllowedQualities);

        var options = await staff.GetAsync<TryOnOptionsDto>("/api/v1/ai/try-on-options");
        var gpt = options.Models.Single(m => m.Key == "gpt-image-2.5");
        Assert.Equal(["auto", "low", "medium", "high", "xhigh", "max"], gpt.Qualities.Select(q => q.Key).ToArray());
        Assert.Equal("medium", gpt.DefaultQuality);
        Assert.Equal("Maksimum", gpt.Qualities.Single(q => q.Key == "max").Label);

        var (session, _) = await PreparedSessionAsync(staff);
        await SaveDraftAsync(staff, session.Id, OutfitMode.OutfitSet, catalog.KaftanProduct);
        var max = await ApiClient.ReadAsync<JobDto>(await StartRawAsync(staff, session.Id, "gpt-image-2.5", "max"), HttpStatusCode.Accepted);
        Assert.Equal(("gpt-image-2.5", "max", "GPT Image 2.5 · Maksimum"), (max.ModelKey, max.Quality, max.ModelLabel));

        var done = await WaitForJobAsync(staff, max.Id);
        Assert.True(done.IsDemo); // mock: seçilen model kaydedilir ama sonuç DEMO'dur
        var detail = await staff.GetAsync<SessionDetail>($"/api/v1/sessions/{session.Id}");
        Assert.Equal("GPT Image 2.5 · Maksimum", Assert.Single(detail.Images).AiModel);
        Assert.Equal("GPT Image 2.5 · Maksimum", detail.Jobs.Single(j => j.Id == max.Id).ModelLabel);
        var images = await staff.GetAsync<List<GeneratedImageDto>>($"/api/v1/sessions/{session.Id}/images");
        Assert.Equal("GPT Image 2.5 · Maksimum", Assert.Single(images).AiModel);
    }

    [Fact]
    public async Task Gecersiz_model_ayarlari_reddedilir_ve_yalnizca_yonetici_degistirebilir()
    {
        var (_, manager, staff) = await ManagerAndStaffAsync();

        var invalid = new[]
        {
            Settings("pruna-tryon", ("seedream-5-flash", true, ["standard"], "standard"), ("pruna-tryon", false, ["standard"], "standard")), // varsayılan etkin değil
            Settings("gpt-image-2.5", ("gpt-image-2.5", true, ["low"], "medium")), // varsayılan kalite izinli değil
            Settings("gpt-image-2.5", ("gpt-image-2.5", true, [], "medium")), // kalite yok
            Settings("gpt-image-2.5", ("gpt-image-2.5", true, ["ultra"], "ultra")), // desteklenmeyen kalite
            Settings("gpt-image-2.5", ("gpt-image-2.5", true, ["low", "low"], "low")), // yinelenen kalite
            Settings("fashn-tryon-max", ("fashn-tryon-max", true, ["balanced-1k"], "balanced-1k")), // eski model
            Settings("seedream-5-flash"), // boş liste
        };
        foreach (var body in invalid)
        {
            await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Put, "/api/v1/admin/ai-models", body), HttpStatusCode.BadRequest, "INVALID_AI_MODELS");
        }

        await ApiClient.AssertProblemAsync(await staff.GetAsync("/api/v1/admin/ai-models"), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        await ApiClient.AssertProblemAsync(
            await staff.SendAsync(HttpMethod.Put, "/api/v1/admin/ai-models", Settings("seedream-5-flash", ("seedream-5-flash", true, ["standard"], "standard"))),
            HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
    }

    [Fact]
    public async Task Tek_kaftan_secilip_uretilebilir_parcali_urun_tek_basina_secilemez()
    {
        var (tenant, client) = await StaffAsync();
        var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);

        // Seçim ekranı: hazır takım + tüm vücut tekil ürün (kaftan); şalvar/gömlek gibi parçalar listelenmez
        var list = await client.GetAsync<Page<ProductListItem>>("/api/v1/products?tryOn=true&selectable=true&pageSize=50");
        Assert.Equal(new[] { catalog.KaftanProduct, catalog.SetProduct }.Order().ToArray(), list.Items.Select(i => i.Id).Order().ToArray());

        var (session, _) = await PreparedSessionAsync(client);
        var wrong = await client.SendAsync(HttpMethod.Put, $"/api/v1/sessions/{session.Id}/draft", new { mode = OutfitMode.OutfitSet, items = new[] { new { productId = catalog.SalvarProduct } } });
        await ApiClient.AssertProblemAsync(wrong, HttpStatusCode.BadRequest, "WRONG_PRODUCT_TYPE");

        await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.KaftanProduct);
        var job = await WaitForJobAsync(client, (await StartGenerationAsync(client, session.Id)).Id);

        Assert.Equal(GenerationJobStatus.Succeeded, job.Status);
        Assert.True(job.IsDemo); // mock: DEMO işaretli, gerçek AI çıktısı gibi sunulmaz
        Assert.Equal("bytedance/seedream/v5/flash/edit", job.Model);
        var detail = await client.GetAsync<SessionDetail>($"/api/v1/sessions/{session.Id}");
        Assert.Contains("Zümrüt Kaftan", Assert.Single(detail.Images).Label, StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> StartRawAsync(ApiClient client, Guid sessionId, string? modelKey, string? quality) =>
        client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/generations", new { modelKey, quality },
            configure: r => r.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N")));
}
