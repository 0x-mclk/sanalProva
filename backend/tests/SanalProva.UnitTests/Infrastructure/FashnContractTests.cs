using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Ai.Abstractions;
using SanalProva.Infrastructure.Ai.Fashn;

namespace SanalProva.UnitTests.Infrastructure;

/// <summary>
/// FASHN adapter sözleşme testleri. Yanıt gövdeleri resmî dokümandaki şekillerden (docs.fashn.ai: api-fundamentals, error-handling,
/// tryon-max; 2026-10-20) türetilmiştir — gerçek API'den kaydedilmiş yanıt DEĞİLDİR. Gerçek çağrı yapılmaz, ücret doğmaz.
/// </summary>
public sealed class FashnContractTests
{
    private static readonly ImageInput Person = new(AiTestImages.Jpeg(300, 500), "image/jpeg");
    private static readonly ImageInput Garment = new(AiTestImages.Jpeg(300, 400, seed: 2), "image/jpeg");
    private static readonly byte[] Output = AiTestImages.Jpeg(300, 500, seed: 3);

    private static string OutputDataUri => $"data:image/jpeg;base64,{Convert.ToBase64String(Output)}";

    private static FashnTryOnProvider Create(StubHandler handler, string apiKey = "test-key-123", int maxWaitSeconds = 5) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.fashn.ai/") },
            Options.Create(new FashnOptions { ApiKey = apiKey, PollIntervalMs = 1, MaxWaitSeconds = maxWaitSeconds, RequestTimeoutSeconds = 5 }),
            new CostEstimator(new StaticOptionsMonitor<GenerationSettings>(new GenerationSettings())));

    private static ProviderRequestOptions Request(string? resume = null, Func<string, Task>? onSubmitted = null) =>
        new("tryon-max", new Dictionary<string, string> { ["resolution"] = "2k", ["generation_mode"] = "balanced", ["return_base64"] = "false" }, Seed: 77, ResumeRequestId: resume, OnSubmitted: onSubmitted);

    private static string Completed(string id) => $$"""{"id":"{{id}}","status":"completed","output":["{{OutputDataUri}}"],"error":null}""";

    [Fact]
    public async Task Istek_resmi_sozlesmeye_uygun_kurulur_ve_sonuc_okunur()
    {
        var handler = new StubHandler()
            .Json(HttpStatusCode.OK, """{"id":"pred-1","error":null}""")
            .Json(HttpStatusCode.OK, """{"id":"pred-1","status":"processing","error":null}""")
            .Json(HttpStatusCode.OK, Completed("pred-1"), ("x-fashn-credits-used", "3"));
        var submitted = new List<string>();

        var result = await Create(handler).TryOnAsync(Person, Garment, Request(onSubmitted: id => { submitted.Add(id); return Task.CompletedTask; }), CancellationToken.None);

        var run = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, run.Method);
        Assert.Equal("/v1/run", run.Path);
        Assert.Equal("Bearer test-key-123", run.Headers["Authorization"]);
        using var body = run.BodyJson();
        Assert.Equal("tryon-max", body.RootElement.GetProperty("model_name").GetString());
        var inputs = body.RootElement.GetProperty("inputs");
        Assert.StartsWith("data:image/jpeg;base64,", inputs.GetProperty("model_image").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("data:image/jpeg;base64,", inputs.GetProperty("product_image").GetString(), StringComparison.Ordinal);
        Assert.Equal("2k", inputs.GetProperty("resolution").GetString());
        Assert.Equal("balanced", inputs.GetProperty("generation_mode").GetString());
        Assert.Equal(1, inputs.GetProperty("num_images").GetInt32());
        Assert.Equal("jpeg", inputs.GetProperty("output_format").GetString());
        Assert.Equal(77, inputs.GetProperty("seed").GetInt32());
        // KVKK: çıktı FASHN CDN'inde kalmasın — yapılandırmada "false" olsa bile adapter base64'ü zorlar
        Assert.True(inputs.GetProperty("return_base64").GetBoolean());

        Assert.Equal(["pred-1"], submitted);
        Assert.All(handler.Requests.Skip(1), r => Assert.Equal("/v1/status/pred-1", r.Path));
        Assert.Equal(Output, result.Images[0].Content);
        Assert.Equal("image/jpeg", result.Images[0].ContentType);
        Assert.Equal("Fashn", result.Provider);
        Assert.Equal("pred-1", result.ProviderRequestId);
        Assert.Equal(3m, result.Usage.Credits);
        Assert.Equal(0.225m, result.Usage.EstimatedCostUsd); // 3 kredi × 0,075 USD
        Assert.Equal(0.225m, result.Usage.ActualCostUsd); // x-fashn-credits-used başlığından
    }

    [Fact]
    public async Task Kredi_basligi_yoksa_tablodan_tahmin_edilir_gerceklesen_bos_kalir()
    {
        var handler = new StubHandler().Json(HttpStatusCode.OK, """{"id":"p","error":null}""").Json(HttpStatusCode.OK, Completed("p"));

        var result = await Create(handler).TryOnAsync(Person, Garment, Request(), CancellationToken.None);

        Assert.Equal(3m, result.Usage.Credits);
        Assert.Equal(0.225m, result.Usage.EstimatedCostUsd);
        Assert.Null(result.Usage.ActualCostUsd);
    }

    [Fact]
    public async Task Devam_etme_modunda_yeni_istek_acilmaz_ayni_tahmin_sorgulanir()
    {
        var handler = new StubHandler().Json(HttpStatusCode.OK, Completed("pred-9"));

        var result = await Create(handler).TryOnAsync(Person, Garment, Request(resume: "pred-9"), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method); // POST /v1/run yok → ikinci ücret yok
        Assert.Equal("/v1/status/pred-9", request.Path);
        Assert.Equal("pred-9", result.ProviderRequestId);
    }

    [Fact]
    public async Task Devam_ederken_tahmin_bulunamazsa_otomatik_yeniden_gonderilmez()
    {
        var handler = new StubHandler().Json(HttpStatusCode.NotFound, """{"error":"NotFound","message":"x"}""");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler).TryOnAsync(Person, Garment, Request(resume: "gone"), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.InvalidOutput, ex.Code);
        Assert.False(ex.Retryable);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Zaman_asiminda_tahmin_kimligi_bildirilmis_olur_ve_hata_yeniden_denenebilir()
    {
        var handler = new StubHandler().Json(HttpStatusCode.OK, """{"id":"slow","error":null}""").Json(HttpStatusCode.OK, """{"id":"slow","status":"processing","error":null}""");
        string? submitted = null;

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            Create(handler, maxWaitSeconds: 0).TryOnAsync(Person, Garment, Request(onSubmitted: id => { submitted = id; return Task.CompletedTask; }), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.Timeout, ex.Code);
        Assert.True(ex.Retryable);
        Assert.Equal("slow", submitted); // çağıran kimliği saklar → yeniden denemede aynı tahmin sorgulanır
    }

    [Theory]
    [InlineData(401, "UnauthorizedAccess", GenerationErrorCode.ProviderNotConfigured, false)]
    [InlineData(400, "BadRequest", GenerationErrorCode.InvalidInput, false)]
    [InlineData(429, "RateLimitExceeded", GenerationErrorCode.RateLimited, true)]
    [InlineData(429, "ConcurrencyLimitExceeded", GenerationErrorCode.RateLimited, true)]
    [InlineData(429, "OutOfCredits", GenerationErrorCode.QuotaExceeded, false)]
    [InlineData(500, "InternalServerError", GenerationErrorCode.ProviderUnavailable, true)]
    public async Task Http_hatalari_ortak_hata_koduna_eslenir(int status, string name, GenerationErrorCode code, bool retryable)
    {
        var handler = new StubHandler().Json((HttpStatusCode)status, $$"""{"error":"{{name}}","message":"ayrıntı"}""");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler).TryOnAsync(Person, Garment, Request(), CancellationToken.None));

        Assert.Equal(code, ex.Code);
        Assert.Equal(retryable, ex.Retryable);
        Assert.Equal(0m, ex.ChargedUsd);
    }

    [Theory]
    [InlineData("ContentModerationError", GenerationErrorCode.ContentRejected, false)]
    [InlineData("ImageLoadError", GenerationErrorCode.InvalidInput, false)]
    [InlineData("InputValidationError", GenerationErrorCode.InvalidInput, false)]
    [InlineData("ThirdPartyError", GenerationErrorCode.ProviderUnavailable, true)]
    [InlineData("UnavailableError", GenerationErrorCode.ProviderUnavailable, true)]
    [InlineData("PipelineError", GenerationErrorCode.ProviderUnavailable, true)]
    [InlineData("BeklenmeyenYeniHata", GenerationErrorCode.Unknown, true)]
    public async Task Basarisiz_tahmin_hata_adina_gore_eslenir_ve_ucret_yazilmaz(string name, GenerationErrorCode code, bool retryable)
    {
        var handler = new StubHandler().Json(HttpStatusCode.OK, """{"id":"f","error":null}""")
            .Json(HttpStatusCode.OK, $$$"""{"id":"f","status":"failed","error":{"name":"{{{name}}}","message":"ayrıntı"}}""");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler).TryOnAsync(Person, Garment, Request(), CancellationToken.None));

        Assert.Equal(code, ex.Code);
        Assert.Equal(retryable, ex.Retryable);
        Assert.Equal(0m, ex.ChargedUsd); // başarısız tahmin kredi harcamaz
    }

    [Fact]
    public async Task Api_anahtari_yoksa_hicbir_istek_gonderilmez()
    {
        var handler = new StubHandler();

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler, apiKey: " ").TryOnAsync(Person, Garment, Request(), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.ProviderNotConfigured, ex.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cdn_disindaki_adresten_cikti_indirilmez()
    {
        var handler = new StubHandler().Json(HttpStatusCode.OK, """{"id":"u","error":null}""")
            .Json(HttpStatusCode.OK, """{"id":"u","status":"completed","output":["https://evil.example/x.jpg"],"error":null}""");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler).TryOnAsync(Person, Garment, Request(), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.InvalidOutput, ex.Code);
        Assert.Equal(2, handler.Requests.Count); // kötü adrese istek atılmadı
    }

    [Fact]
    public async Task Hata_iletileri_anahtar_veya_gorsel_icermez()
    {
        var handler = new StubHandler().Json(HttpStatusCode.OK, """{"id":"f","error":null}""")
            .Json(HttpStatusCode.OK, """{"id":"f","status":"failed","error":{"name":"ContentModerationError","message":"test-key-123 data:image/jpeg;base64,AAAA"}}""");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler).TryOnAsync(Person, Garment, Request(), CancellationToken.None));

        Assert.DoesNotContain("test-key-123", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bozuk_yanit_gecici_hata_sayilir()
    {
        var handler = new StubHandler().Json(HttpStatusCode.OK, "<html>bakım</html>");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler).TryOnAsync(Person, Garment, Request(), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.ProviderUnavailable, ex.Code);
        Assert.True(ex.Retryable);
        _ = JsonSerializer.Serialize(ex.Message);
    }
}
