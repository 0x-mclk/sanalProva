using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Ai.Abstractions;
using SanalProva.Infrastructure.Ai.Fal;
using SanalProva.Infrastructure.Ai.Fashn;
using SanalProva.Infrastructure.Ai.Gemini;
using SanalProva.Infrastructure.Ai.Pruna;
using SanalProva.Infrastructure.Ai.TryOn;

namespace SanalProva.UnitTests.Infrastructure;

/// <summary>
/// Ücretli gönderimin sonuç sınıflandırması (gerçek ağ çağrısı yok). Sağlayıcıların hiçbiri gönderim için idempotency anahtarı belgelemiyor:
/// <list type="bullet">
/// <item><b>Belirsiz</b> (<see cref="ProviderException.SubmissionUncertain"/>): yanıt yok (zaman aşımı, bağlantı kopması), kimliksiz/okunamayan başarılı
/// yanıt; fal/Pruna/Gemini'de 5xx (belgeleri isteğin işlenmediğini garanti etmiyor). Adapter yeniden göndermez.</item>
/// <item><b>Kesin ret</b>: yanıtlı 4xx; FASHN'da yanıtlı her API hatası (belge: "kimlik verilmez, çift işleme riski yok").</item>
/// <item><b>Kimlikli istek sürüyor olabilir</b>: yoklamada ağ hatası/5xx → kimlik korunur (RequestEnded = false).</item>
/// <item><b>Kesin bitti</b> (<see cref="ProviderException.RequestEnded"/>): sağlayıcı başarısız/iptal dedi veya kimlik bulunamadı.</item>
/// </list>
/// </summary>
public sealed class UncertainSubmissionContractTests
{
    private static readonly ImageInput Person = new(AiTestImages.Jpeg(300, 500), "image/jpeg");
    private static readonly ImageInput Garment = new(AiTestImages.Jpeg(300, 400, seed: 2), "image/jpeg");

    /// <summary>Gönderim çağrısının izi: niyet yazıldı mı, kimlik bildirildi mi.</summary>
    private sealed class Trace
    {
        public List<string> Events { get; } = [];

        public ProviderRequestOptions Apply(ProviderRequestOptions options) => options with
        {
            OnSubmitting = () => { Events.Add("niyet"); return Task.CompletedTask; },
            OnSubmitted = id => { Events.Add($"kimlik:{id}"); return Task.CompletedTask; },
        };
    }

    public static TheoryData<string> LostResponses => ["timeout", "connection", "no-id", "bad-json"];

    /// <summary>Gönderim yanıtının kaybolduğu veya kimliksiz geldiği durumlar.</summary>
    private static StubHandler Lost(StubHandler handler, string kind) => kind switch
    {
        "timeout" => handler.Enqueue(_ => throw new TaskCanceledException("istemci zaman aşımı")),
        "connection" => handler.Enqueue(_ => throw new HttpRequestException("bağlantı koptu")),
        "no-id" => handler.Json(HttpStatusCode.OK, "{}"),
        "bad-json" => handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>ağ geçidi</html>", Encoding.UTF8, "text/html") }),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // ------------------------------------------------------------------ fal

    private static FalSeedreamTryOnProvider Fal(StubHandler handler) =>
        new(new FalQueueClient(new HttpClient(handler) { BaseAddress = new Uri("https://queue.fal.run/") },
                Options.Create(new FalOptions { ApiKey = "fal-key", PollIntervalMs = 1, MaxWaitSeconds = 5, RequestTimeoutSeconds = 5 })),
            new StaticOptionsMonitor<GenerationSettings>(new GenerationSettings()));

    private static ProviderRequestOptions FalRequest(string? resume = null) =>
        new("bytedance/seedream/v5/flash/edit",
            new Dictionary<string, string> { ["image_size"] = "auto_2K", ["model_key"] = TryOnModels.Seedream, ["tier"] = "standard", ["prompt"] = "TALIMAT" },
            ResumeRequestId: resume);

    private static string FalSubmitted(string id) => $$"""{"request_id":"{{id}}"}""";

    [Theory]
    [MemberData(nameof(LostResponses))]
    [InlineData("502")]
    [InlineData("503")]
    [InlineData("504")]
    public async Task Fal_gonderim_yaniti_kaybolursa_sonuc_belirsizdir_ve_yeniden_gonderilmez(string kind)
    {
        var handler = int.TryParse(kind, out var status) ? new StubHandler().Json((HttpStatusCode)status, "{}") : Lost(new StubHandler(), kind);
        var trace = new Trace();

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Fal(handler).TryOnAsync(Person, Garment, trace.Apply(FalRequest()), CancellationToken.None));

        Assert.True(ex.SubmissionUncertain);
        Assert.False(ex.RequestEnded);
        Assert.Equal(["niyet"], trace.Events); // niyet gönderimden önce yazıldı, kimlik hiç gelmedi
        Assert.Single(handler.Requests); // adapter yeniden göndermedi
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(422)]
    [InlineData(429)]
    public async Task Fal_yanitli_4xx_kesin_rettir(int status)
    {
        var handler = new StubHandler().Json((HttpStatusCode)status, """{"detail":"x"}""");
        var trace = new Trace();

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Fal(handler).TryOnAsync(Person, Garment, trace.Apply(FalRequest()), CancellationToken.None));

        Assert.False(ex.SubmissionUncertain); // istek kuyruğa alınmadı: ücret yok, yeniden gönderim güvenli
        Assert.Equal(["niyet"], trace.Events);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("502")]
    public async Task Fal_yoklamada_ag_hatasi_kimligi_korur_istek_bitmis_sayilmaz(string kind)
    {
        var handler = new StubHandler().Json(HttpStatusCode.OK, FalSubmitted("r-1"));
        if (kind == "connection")
        {
            handler.Enqueue(_ => throw new HttpRequestException("bağlantı koptu"));
        }
        else
        {
            handler.Json(HttpStatusCode.BadGateway, "{}");
        }

        var trace = new Trace();

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Fal(handler).TryOnAsync(Person, Garment, trace.Apply(FalRequest()), CancellationToken.None));

        Assert.True(ex.Retryable);
        Assert.False(ex.SubmissionUncertain);
        Assert.False(ex.RequestEnded); // işleyici kimliği korur, sonraki deneme aynı isteği sorgular
        Assert.Equal(["niyet", "kimlik:r-1"], trace.Events);
    }

    [Fact]
    public async Task Fal_basarisiz_istek_ve_bulunamayan_kimlik_kesin_bitmistir()
    {
        var failed = new StubHandler().Json(HttpStatusCode.OK, FalSubmitted("f"))
            .Json(HttpStatusCode.OK, """{"status":"COMPLETED","error":"x","error_type":"internal_server_error"}""");
        var ended = await Assert.ThrowsAsync<ProviderException>(() => Fal(failed).TryOnAsync(Person, Garment, FalRequest(), CancellationToken.None));
        Assert.True(ended.RequestEnded);

        var gone = new StubHandler().Json(HttpStatusCode.NotFound, "{}");
        var trace = new Trace();
        var missing = await Assert.ThrowsAsync<ProviderException>(() => Fal(gone).TryOnAsync(Person, Garment, trace.Apply(FalRequest(resume: "eski")), CancellationToken.None));
        Assert.True(missing.RequestEnded);
        Assert.Empty(trace.Events); // devam ederken gönderim niyeti yazılmaz, yeni istek açılmaz
    }

    // ------------------------------------------------------------------ Pruna

    private static PrunaTryOnProvider Pruna(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.pruna.ai/") },
            Options.Create(new PrunaOptions { ApiKey = "pruna-key", PollIntervalMs = 1, MaxWaitSeconds = 5, RequestTimeoutSeconds = 5 }),
            new StaticOptionsMonitor<GenerationSettings>(new GenerationSettings()));

    private static ProviderRequestOptions PrunaRequest(string? resume = null) =>
        new("p-image-try-on", new Dictionary<string, string> { ["turbo"] = "false", ["model_key"] = TryOnModels.Pruna, ["tier"] = "standard" }, Seed: 9, ResumeRequestId: resume);

    private static string Upload(string id) => $$$"""{"id":"{{{id}}}","urls":{"get":"https://api.pruna.ai/v1/files/{{{id}}}"}}""";

    private static StubHandler Uploaded() => new StubHandler().Json(HttpStatusCode.Created, Upload("p")).Json(HttpStatusCode.Created, Upload("g"));

    [Theory]
    [MemberData(nameof(LostResponses))]
    [InlineData("500")]
    [InlineData("503")]
    public async Task Pruna_tahmin_gonderimi_yaniti_kaybolursa_sonuc_belirsizdir(string kind)
    {
        var handler = int.TryParse(kind, out var status) ? Uploaded().Json((HttpStatusCode)status, "{}") : Lost(Uploaded(), kind);
        var trace = new Trace();

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Pruna(handler).TryOnAsync(Person, Garment, trace.Apply(PrunaRequest()), CancellationToken.None));

        Assert.True(ex.SubmissionUncertain);
        Assert.Equal(["niyet"], trace.Events);
        Assert.Equal(3, handler.Requests.Count); // 2 ücretsiz yükleme + tek tahmin isteği
    }

    [Fact]
    public async Task Pruna_ucretsiz_yukleme_hatasi_belirsiz_degildir_ve_niyet_yazilmaz()
    {
        var handler = new StubHandler().Json(HttpStatusCode.InternalServerError, "{}");
        var trace = new Trace();

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Pruna(handler).TryOnAsync(Person, Garment, trace.Apply(PrunaRequest()), CancellationToken.None));

        Assert.False(ex.SubmissionUncertain);
        Assert.True(ex.Retryable);
        Assert.Empty(trace.Events); // ücretli adıma gelinmedi
    }

    [Fact]
    public async Task Pruna_yanitli_4xx_kesin_ret_basarisiz_tahmin_kesin_bitis_yoklama_hatasi_kimligi_korur()
    {
        var rejected = await Assert.ThrowsAsync<ProviderException>(() =>
            Pruna(Uploaded().Json(HttpStatusCode.UnprocessableEntity, "{}")).TryOnAsync(Person, Garment, PrunaRequest(), CancellationToken.None));
        Assert.False(rejected.SubmissionUncertain);

        var failed = await Assert.ThrowsAsync<ProviderException>(() =>
            Pruna(Uploaded().Json(HttpStatusCode.Created, """{"id":"t"}""").Json(HttpStatusCode.OK, """{"status":"failed","error":"worker crashed"}"""))
                .TryOnAsync(Person, Garment, PrunaRequest(), CancellationToken.None));
        Assert.True(failed.RequestEnded);

        var polling = await Assert.ThrowsAsync<ProviderException>(() =>
            Pruna(Uploaded().Json(HttpStatusCode.Created, """{"id":"t"}""").Json(HttpStatusCode.ServiceUnavailable, "{}"))
                .TryOnAsync(Person, Garment, PrunaRequest(), CancellationToken.None));
        Assert.False(polling.RequestEnded);
        Assert.False(polling.SubmissionUncertain);
    }

    // ------------------------------------------------------------------ FASHN

    private static FashnTryOnProvider Fashn(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.fashn.ai/") },
            Options.Create(new FashnOptions { ApiKey = "fashn-key", PollIntervalMs = 1, MaxWaitSeconds = 5, RequestTimeoutSeconds = 5 }),
            new CostEstimator(new StaticOptionsMonitor<GenerationSettings>(new GenerationSettings())));

    private static ProviderRequestOptions FashnRequest() =>
        new("tryon-max", new Dictionary<string, string> { ["resolution"] = "1k", ["generation_mode"] = "balanced" }, Seed: 7);

    [Theory]
    [MemberData(nameof(LostResponses))]
    public async Task Fashn_yanit_hic_gelmezse_veya_kimliksizse_sonuc_belirsizdir(string kind)
    {
        var handler = Lost(new StubHandler(), kind);
        var trace = new Trace();

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Fashn(handler).TryOnAsync(Person, Garment, trace.Apply(FashnRequest()), CancellationToken.None));

        Assert.True(ex.SubmissionUncertain);
        Assert.Equal(["niyet"], trace.Events);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(500, "InternalServerError")]
    [InlineData(429, "RateLimitExceeded")]
    [InlineData(400, "BadRequest")]
    public async Task Fashn_yanitli_api_hatasi_belgesine_gore_kesin_rettir(int status, string name)
    {
        var handler = new StubHandler().Json((HttpStatusCode)status, $$"""{"error":"{{name}}","message":"x"}""");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Fashn(handler).TryOnAsync(Person, Garment, FashnRequest(), CancellationToken.None));

        Assert.False(ex.SubmissionUncertain); // "kimlik verilmez, istek işlenmez" (FASHN Error Handling)
    }

    [Fact]
    public async Task Fashn_basarisiz_tahmin_kesin_bitmistir()
    {
        var handler = new StubHandler().Json(HttpStatusCode.OK, """{"id":"f","error":null}""")
            .Json(HttpStatusCode.OK, """{"id":"f","status":"failed","error":{"name":"PipelineError","message":"x"}}""");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Fashn(handler).TryOnAsync(Person, Garment, FashnRequest(), CancellationToken.None));

        Assert.True(ex.RequestEnded);
        Assert.True(ex.Retryable); // yeni tahmin güvenli: başarısız tahmin kredi harcamaz (FASHN belgesi)
    }

    // ------------------------------------------------------------------ Gemini (eski/pasif; senkron, kimlik yok)

    [Theory]
    [InlineData("timeout")]
    [InlineData("connection")]
    [InlineData("bad-json")]
    public async Task Gemini_yanit_kaybolursa_sonuc_belirsizdir(string kind)
    {
        var handler = Lost(new StubHandler(), kind);
        var provider = new GeminiProvider(new HttpClient(handler) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") },
            Options.Create(new GeminiOptions { ApiKey = "gem-key", RequestTimeoutSeconds = 5 }),
            new CostEstimator(new StaticOptionsMonitor<GenerationSettings>(new GenerationSettings())));
        var trace = new Trace();
        var garment = new GarmentReference(Garment, "Kaftan", "Zümrüt", "Bordo", 30, null);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.ComposeOutfitAsync(Person, [garment], "p",
            trace.Apply(new ProviderRequestOptions("gemini-3-pro-image", new Dictionary<string, string> { ["image_size"] = "2K" })), CancellationToken.None));

        Assert.True(ex.SubmissionUncertain);
        Assert.Equal(["niyet"], trace.Events);
    }
}
