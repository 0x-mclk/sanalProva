using System.Net;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Ai.Abstractions;
using SanalProva.Infrastructure.Ai.Fal;
using SanalProva.Infrastructure.Ai.Pruna;
using SanalProva.Infrastructure.Ai.TryOn;

namespace SanalProva.UnitTests.Infrastructure;

/// <summary>
/// fal.ai (Seedream 5 Flash Edit, GPT Image 2.5 Sunburst Edit) sözleşme testleri. İstek/yanıt şekilleri resmî kaynaklardan türetilmiştir:
/// fal Queue dokümanı ve <c>queue.fal.run</c> OpenAPI şemaları (2026-10-22 indirildi). Gerçek API'den kaydedilmiş yanıt DEĞİLDİR; ücret doğmaz.
/// fal'ın başarısız istek hata türü listesi bu projede doğrulanmadı (anahtar kelime eşlemesi varsayımdır).
/// </summary>
public sealed class FalContractTests
{
    private static readonly ImageInput Person = new(AiTestImages.Jpeg(300, 500), "image/jpeg");
    private static readonly ImageInput Garment = new(AiTestImages.Jpeg(300, 400, seed: 2), "image/jpeg");
    private static readonly byte[] Output = AiTestImages.Jpeg(300, 500, seed: 3);

    private static FalQueueClient Client(StubHandler handler, string apiKey = "fal-key-789", int maxWaitSeconds = 5) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://queue.fal.run/") },
            Options.Create(new FalOptions { ApiKey = apiKey, PollIntervalMs = 1, MaxWaitSeconds = maxWaitSeconds, RequestTimeoutSeconds = 5 }));

    private static readonly StaticOptionsMonitor<GenerationSettings> Settings = new(new GenerationSettings());

    private static ProviderRequestOptions Seedream(string? resume = null, Func<string, Task>? onSubmitted = null) =>
        new("bytedance/seedream/v5/flash/edit",
            new Dictionary<string, string> { ["image_size"] = "auto_2K", ["model_key"] = TryOnModels.Seedream, ["tier"] = "standard", ["prompt"] = "TALIMAT" },
            Seed: 5, ResumeRequestId: resume, OnSubmitted: onSubmitted);

    private static ProviderRequestOptions Gpt(string quality) =>
        new("openai/gpt-image-2.5/sunburst/edit",
            new Dictionary<string, string> { ["quality"] = quality, ["image_size"] = "auto", ["model_key"] = TryOnModels.GptImage, ["tier"] = quality, ["prompt"] = "TALIMAT" });

    private static string Submitted(string id) =>
        $$"""{"request_id":"{{id}}","status_url":"https://queue.fal.run/x/requests/{{id}}/status","response_url":"https://queue.fal.run/x/requests/{{id}}","queue_position":0}""";

    private static string Result(string url) => $$"""{"images":[{"url":"{{url}}","content_type":"image/jpeg","width":300,"height":500}]}""";

    private static string DataUri => $"data:image/jpeg;base64,{Convert.ToBase64String(Output)}";

    [Fact]
    public async Task Seedream_istegi_semaya_uygun_kurulur_ve_sonuc_okunur()
    {
        var handler = new StubHandler()
            .Json(HttpStatusCode.OK, Submitted("req-1"))
            .Json(HttpStatusCode.OK, """{"status":"IN_PROGRESS","request_id":"req-1"}""")
            .Json(HttpStatusCode.OK, """{"status":"COMPLETED","request_id":"req-1","metrics":{"inference_time":3.1}}""")
            .Json(HttpStatusCode.OK, Result(DataUri));
        var submitted = new List<string>();

        var result = await new FalSeedreamTryOnProvider(Client(handler), Settings)
            .TryOnAsync(Person, Garment, Seedream(onSubmitted: id => { submitted.Add(id); return Task.CompletedTask; }), CancellationToken.None);

        var submit = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, submit.Method);
        Assert.Equal("/bytedance/seedream/v5/flash/edit", submit.Path);
        Assert.Equal("Key fal-key-789", submit.Headers["Authorization"]);
        Assert.Equal("0", submit.Headers["X-Fal-Store-IO"]); // fal tarafında yük saklanmaz
        Assert.DoesNotContain("fal-key-789", submit.Body, StringComparison.Ordinal);
        using var body = submit.BodyJson();
        var root = body.RootElement;
        Assert.Equal("TALIMAT", root.GetProperty("prompt").GetString());
        var urls = root.GetProperty("image_urls").EnumerateArray().Select(u => u.GetString()!).ToList();
        Assert.Equal(2, urls.Count);
        Assert.Equal($"data:image/jpeg;base64,{Convert.ToBase64String(Person.Content.Span)}", urls[0]); // Görsel 1 = kişi (data URI; fal deposuna yükleme yok)
        Assert.Equal($"data:image/jpeg;base64,{Convert.ToBase64String(Garment.Content.Span)}", urls[1]);
        Assert.Equal("auto_2K", root.GetProperty("image_size").GetString());
        Assert.Equal(1, root.GetProperty("num_images").GetInt32());
        Assert.True(root.GetProperty("sync_mode").GetBoolean()); // çıktı geçmişte tutulmaz
        Assert.True(root.GetProperty("enable_safety_checker").GetBoolean());
        Assert.False(root.TryGetProperty("seed", out _)); // şemada seed girdisi yok
        Assert.False(root.TryGetProperty("model_key", out _)); // iç parametreler sağlayıcıya gitmez
        Assert.False(root.TryGetProperty("tier", out _));

        Assert.Equal(["req-1"], submitted);
        // Durum/sonuç adresleri sahip/uygulama yolundan (doküman: tam yol 405 döner)
        Assert.Equal("/bytedance/seedream/requests/req-1/status", handler.Requests[1].Path);
        Assert.Equal("/bytedance/seedream/requests/req-1", handler.Requests[3].Path);
        Assert.Equal(Output, result.Images[0].Content);
        Assert.Equal("FalSeedream", result.Provider);
        Assert.Equal("req-1", result.ProviderRequestId);
        Assert.Equal(0.027m, result.Usage.EstimatedCostUsd);
        Assert.Null(result.Usage.ActualCostUsd);
    }

    [Fact]
    public async Task Devam_etmede_yeni_istek_gonderilmez()
    {
        var handler = new StubHandler()
            .Json(HttpStatusCode.OK, """{"status":"COMPLETED","request_id":"old"}""")
            .Json(HttpStatusCode.OK, Result(DataUri));

        await new FalSeedreamTryOnProvider(Client(handler), Settings).TryOnAsync(Person, Garment, Seedream(resume: "old"), CancellationToken.None);

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Devam_ederken_istek_bulunamazsa_otomatik_yeniden_gonderilmez()
    {
        var handler = new StubHandler().Json(HttpStatusCode.NotFound, """{"detail":"not found"}""");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => new FalSeedreamTryOnProvider(Client(handler), Settings).TryOnAsync(Person, Garment, Seedream(resume: "gone"), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.InvalidOutput, ex.Code);
        Assert.False(ex.Retryable);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Zaman_asiminda_istek_kimligi_bildirilmis_olur()
    {
        var handler = new StubHandler().Json(HttpStatusCode.OK, Submitted("slow")).Json(HttpStatusCode.OK, """{"status":"IN_QUEUE","request_id":"slow","queue_position":3}""");
        string? submitted = null;

        var ex = await Assert.ThrowsAsync<ProviderException>(() => new FalSeedreamTryOnProvider(Client(handler, maxWaitSeconds: 0), Settings)
            .TryOnAsync(Person, Garment, Seedream(onSubmitted: id => { submitted = id; return Task.CompletedTask; }), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.Timeout, ex.Code);
        Assert.True(ex.Retryable);
        Assert.Equal("slow", submitted);
    }

    [Fact]
    public async Task Gpt_image_kaliteyi_acikca_gonderir_ve_sahip_uygulama_yolunu_kullanir()
    {
        var handler = new StubHandler()
            .Json(HttpStatusCode.OK, Submitted("g-1"))
            .Json(HttpStatusCode.OK, """{"status":"COMPLETED","request_id":"g-1"}""")
            .Json(HttpStatusCode.OK, Result(DataUri));

        var result = await new FalGptImageTryOnProvider(Client(handler), Settings).TryOnAsync(Person, Garment, Gpt("medium"), CancellationToken.None);

        Assert.Equal("/openai/gpt-image-2.5/sunburst/edit", handler.Requests[0].Path);
        using var body = handler.Requests[0].BodyJson();
        Assert.Equal("medium", body.RootElement.GetProperty("quality").GetString());
        Assert.Equal("auto", body.RootElement.GetProperty("image_size").GetString());
        Assert.Equal("jpeg", body.RootElement.GetProperty("output_format").GetString());
        Assert.True(body.RootElement.GetProperty("sync_mode").GetBoolean());
        Assert.Equal("/openai/gpt-image-2.5/requests/g-1/status", handler.Requests[1].Path);
        // Gerçek çıktı 300×500 → 1024×768 satırı (medium 0,00903) + iki küçük referans (2 × 0,008) + kısa prompt → 0,0251 (0,0001'e yukarı)
        Assert.Equal(0.0251m, result.Usage.EstimatedCostUsd);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public async Task Gpt_image_alti_kalitenin_tamamini_acikca_gonderir(string quality)
    {
        var handler = new StubHandler()
            .Json(HttpStatusCode.OK, Submitted("g-q"))
            .Json(HttpStatusCode.OK, """{"status":"COMPLETED","request_id":"g-q"}""")
            .Json(HttpStatusCode.OK, Result(DataUri));

        await new FalGptImageTryOnProvider(Client(handler), Settings).TryOnAsync(Person, Garment, Gpt(quality), CancellationToken.None);

        using var body = handler.Requests[0].BodyJson();
        Assert.Equal(quality, body.RootElement.GetProperty("quality").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("num_images").GetInt32());
    }

    [Fact]
    public async Task Gpt_image_bilinmeyen_kaliteyi_gondermez()
    {
        var handler = new StubHandler();

        var ex = await Assert.ThrowsAsync<ProviderException>(() => new FalGptImageTryOnProvider(Client(handler), Settings).TryOnAsync(Person, Garment, Gpt("ultra"), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.InvalidInput, ex.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cikti_adresi_yalnizca_fal_medya_alan_adlarindan_indirilir()
    {
        var bad = new StubHandler().Json(HttpStatusCode.OK, Submitted("u")).Json(HttpStatusCode.OK, """{"status":"COMPLETED","request_id":"u"}""").Json(HttpStatusCode.OK, Result("https://evil.example/x.jpg"));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => new FalSeedreamTryOnProvider(Client(bad), Settings).TryOnAsync(Person, Garment, Seedream(), CancellationToken.None));
        Assert.Equal(GenerationErrorCode.InvalidOutput, ex.Code);
        Assert.Equal(3, bad.Requests.Count); // kötü adrese istek atılmadı

        var good = new StubHandler().Json(HttpStatusCode.OK, Submitted("v")).Json(HttpStatusCode.OK, """{"status":"COMPLETED","request_id":"v"}""")
            .Json(HttpStatusCode.OK, Result("https://v3b.fal.media/files/abc/out.jpg"))
            .Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Output) { Headers = { ContentType = new("image/jpeg") } } });
        var result = await new FalSeedreamTryOnProvider(Client(good), Settings).TryOnAsync(Person, Garment, Seedream(), CancellationToken.None);
        Assert.Equal(Output, result.Images[0].Content);
        Assert.Equal("/files/abc/out.jpg", good.Requests[3].Path);
        Assert.False(good.Requests[3].Headers.ContainsKey("Authorization")); // anahtar medya sunucusuna gönderilmez
    }

    [Theory]
    [InlineData(401, "", GenerationErrorCode.ProviderNotConfigured, false)]
    [InlineData(403, "", GenerationErrorCode.ProviderNotConfigured, false)]
    [InlineData(404, "", GenerationErrorCode.ProviderNotConfigured, false)]
    [InlineData(422, "{\"detail\":[{\"type\":\"value_error\"}]}", GenerationErrorCode.InvalidInput, false)]
    [InlineData(422, "{\"detail\":[{\"type\":\"content_policy_violation\"}]}", GenerationErrorCode.ContentRejected, false)]
    [InlineData(429, "", GenerationErrorCode.RateLimited, true)]
    [InlineData(500, "", GenerationErrorCode.ProviderUnavailable, true)]
    [InlineData(504, "", GenerationErrorCode.Timeout, true)]
    public async Task Gonderim_http_hatalari_eslenir(int status, string body, GenerationErrorCode code, bool retryable)
    {
        var handler = new StubHandler().Json((HttpStatusCode)status, body.Length == 0 ? "{\"detail\":\"fal-key-789 x\"}" : body);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => new FalSeedreamTryOnProvider(Client(handler), Settings).TryOnAsync(Person, Garment, Seedream(), CancellationToken.None));

        Assert.Equal(code, ex.Code);
        Assert.Equal(retryable, ex.Retryable);
        Assert.DoesNotContain("fal-key-789", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("content_policy_violation", GenerationErrorCode.ContentRejected, false)]
    [InlineData("image_load_error", GenerationErrorCode.InvalidInput, false)]
    [InlineData("generation_timeout", GenerationErrorCode.Timeout, true)]
    [InlineData("internal_server_error", GenerationErrorCode.ProviderUnavailable, true)]
    public async Task Basarisiz_tamamlanan_istek_hata_turune_gore_eslenir(string errorType, GenerationErrorCode code, bool retryable)
    {
        var handler = new StubHandler().Json(HttpStatusCode.OK, Submitted("e"))
            .Json(HttpStatusCode.OK, $$$"""{"status":"COMPLETED","request_id":"e","error":"ayrıntı","error_type":"{{{errorType}}}"}""");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => new FalSeedreamTryOnProvider(Client(handler), Settings).TryOnAsync(Person, Garment, Seedream(), CancellationToken.None));

        Assert.Equal(code, ex.Code);
        Assert.Equal(retryable, ex.Retryable);
    }

    [Fact]
    public async Task Anahtar_veya_talimat_yoksa_hicbir_istek_gonderilmez()
    {
        var handler = new StubHandler();
        var noKey = await Assert.ThrowsAsync<ProviderException>(() => new FalSeedreamTryOnProvider(Client(handler, apiKey: ""), Settings).TryOnAsync(Person, Garment, Seedream(), CancellationToken.None));
        var noPrompt = await Assert.ThrowsAsync<ProviderException>(() => new FalSeedreamTryOnProvider(Client(handler), Settings)
            .TryOnAsync(Person, Garment, new ProviderRequestOptions("bytedance/seedream/v5/flash/edit", new Dictionary<string, string>()), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.ProviderNotConfigured, noKey.Code);
        Assert.Equal(GenerationErrorCode.InvalidInput, noPrompt.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Iptal_edilince_fal_tarafinda_iptal_denenir()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler()
            .Json(HttpStatusCode.OK, Submitted("c-1"))
            .Enqueue(_ =>
            {
                cts.Cancel(); // durum yoklaması sırasında personel iptal etti
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"status":"IN_PROGRESS","request_id":"c-1"}""") };
            })
            .Json(HttpStatusCode.Accepted, """{"status":"CANCELLATION_REQUESTED"}""");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FalSeedreamTryOnProvider(Client(handler), Settings).TryOnAsync(Person, Garment, Seedream(), cts.Token));

        var cancel = handler.Requests.Last();
        Assert.Equal(HttpMethod.Put, cancel.Method);
        Assert.Equal("/bytedance/seedream/requests/c-1/cancel", cancel.Path);
    }
}

/// <summary>
/// Pruna P-Image Try-On sözleşme testleri. Şekiller resmî OpenAPI şemasından (docs.api.pruna.ai/openapi.yaml, 2026-10-22) türetilmiştir;
/// gerçek API'den kaydedilmiş yanıt DEĞİLDİR.
/// </summary>
public sealed class PrunaContractTests
{
    private static readonly ImageInput Person = new(AiTestImages.Jpeg(300, 500), "image/jpeg");
    private static readonly ImageInput Garment = new(AiTestImages.Jpeg(300, 400, seed: 2), "image/jpeg");
    private static readonly byte[] Output = AiTestImages.Jpeg(300, 500, seed: 3);

    private static PrunaTryOnProvider Create(StubHandler handler, string apiKey = "pruna-key-321", int maxWaitSeconds = 5) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.pruna.ai/") },
            Options.Create(new PrunaOptions { ApiKey = apiKey, PollIntervalMs = 1, MaxWaitSeconds = maxWaitSeconds, RequestTimeoutSeconds = 5 }),
            new StaticOptionsMonitor<GenerationSettings>(new GenerationSettings()));

    private static ProviderRequestOptions Request(string tier = "standard", string? resume = null, Func<string, Task>? onSubmitted = null) =>
        new("p-image-try-on",
            new Dictionary<string, string> { ["turbo"] = tier == "turbo" ? "true" : "false", ["model_key"] = TryOnModels.Pruna, ["tier"] = tier },
            Seed: 9, ResumeRequestId: resume, OnSubmitted: onSubmitted);

    private static string Upload(string id) => $$$"""{"id":"{{{id}}}","name":"x.jpg","content_type":"image/jpeg","size":1,"created_at":"2026-10-22T00:00:00Z","expires_at":"2026-10-22T00:30:00Z","urls":{"get":"https://api.pruna.ai/v1/files/{{{id}}}"}}""";

    private static StubHandler Download(StubHandler handler) =>
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Output) { Headers = { ContentType = new("image/jpeg") } } });

    [Fact]
    public async Task Yukleme_tahmin_yoklama_ve_indirme_resmi_sozlesmeye_uygundur()
    {
        var handler = Download(new StubHandler()
            .Json(HttpStatusCode.Created, Upload("person-1"))
            .Json(HttpStatusCode.Created, Upload("garment-1"))
            .Json(HttpStatusCode.Created, """{"id":"pred-1","model":"p-image-try-on","get_url":"https://api.pruna.ai/v1/predictions/status/pred-1"}""")
            .Json(HttpStatusCode.OK, """{"status":"processing","message":"Generation in progress"}""")
            .Json(HttpStatusCode.OK, """{"status":"succeeded","generation_url":"https://api.pruna.ai/v1/predictions/delivery/xezq/abc/output.jpg","metadata":{"seed":9}}"""));
        var submitted = new List<string>();

        var result = await Create(handler).TryOnAsync(Person, Garment, Request(onSubmitted: id => { submitted.Add(id); return Task.CompletedTask; }), CancellationToken.None);

        // 1-2: base64 desteklenmediği için iki geçici dosya yüklemesi (multipart "content")
        Assert.All(handler.Requests.Take(2), r =>
        {
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.Equal("/v1/files", r.Path);
            Assert.Equal("pruna-key-321", r.Headers["apikey"]);
            // Pruna .NET varsayılan biçimini (name=content; filename*=...) 500 ile reddeder: standart tırnaklı biçim zorunlu
            Assert.Matches("Content-Disposition: form-data; name=\"content\"; filename=\"(person|garment)\\.jpg\"\r\n", r.Body);
            Assert.DoesNotContain("filename*", r.Body, StringComparison.Ordinal);
        });

        // 3: tahmin (asenkron: Try-Sync yok)
        var predict = handler.Requests[2];
        Assert.Equal("/v1/predictions", predict.Path);
        Assert.Equal("p-image-try-on", predict.Headers["Model"]);
        Assert.Equal("application/json", predict.ContentType); // Pruna "; charset=utf-8" ekini 400 ile reddeder
        Assert.False(predict.Headers.ContainsKey("Try-Sync"));
        using (var body = predict.BodyJson())
        {
            var input = body.RootElement.GetProperty("input");
            Assert.Equal("https://api.pruna.ai/v1/files/person-1", input.GetProperty("person_image").GetString());
            Assert.Equal(["https://api.pruna.ai/v1/files/garment-1"], input.GetProperty("garment_images").EnumerateArray().Select(g => g.GetString()).ToArray());
            Assert.False(input.GetProperty("turbo").GetBoolean());
            Assert.Equal(9, input.GetProperty("seed").GetInt32());
            Assert.Equal("jpg", input.GetProperty("output_format").GetString());
            Assert.True(input.GetProperty("preserve_input_size").GetBoolean());
            Assert.False(input.TryGetProperty("prompt", out _)); // deneysel prompt gönderilmez
            Assert.DoesNotContain("base64", predict.Body, StringComparison.Ordinal);
        }

        Assert.Equal(["pred-1"], submitted);
        Assert.Equal("/v1/predictions/status/pred-1", handler.Requests[3].Path);
        var download = handler.Requests.Last();
        Assert.Equal("/v1/predictions/delivery/xezq/abc/output.jpg", download.Path);
        Assert.Equal("pruna-key-321", download.Headers["apikey"]);
        Assert.Equal(Output, result.Images[0].Content);
        Assert.Equal("Pruna", result.Provider);
        Assert.Equal(0.015m, result.Usage.EstimatedCostUsd);
        Assert.Null(result.Usage.ActualCostUsd);
    }

    [Fact]
    public async Task Turbo_kalitesi_turbo_parametresini_ve_maliyetini_kullanir()
    {
        var handler = Download(new StubHandler().Json(HttpStatusCode.Created, Upload("p")).Json(HttpStatusCode.Created, Upload("g"))
            .Json(HttpStatusCode.Created, """{"id":"t"}""")
            .Json(HttpStatusCode.OK, """{"status":"succeeded","generation_url":"https://api.pruna.ai/v1/predictions/delivery/t/o.jpg"}"""));

        var result = await Create(handler).TryOnAsync(Person, Garment, Request("turbo"), CancellationToken.None);

        using var body = handler.Requests[2].BodyJson();
        Assert.True(body.RootElement.GetProperty("input").GetProperty("turbo").GetBoolean());
        Assert.Equal(0.008m, result.Usage.EstimatedCostUsd);
    }

    [Fact]
    public async Task Devam_etmede_yukleme_ve_yeni_tahmin_yapilmaz()
    {
        var handler = Download(new StubHandler().Json(HttpStatusCode.OK, """{"status":"succeeded","generation_url":"https://api.pruna.ai/v1/predictions/delivery/r/o.jpg"}"""));

        await Create(handler).TryOnAsync(Person, Garment, Request(resume: "old"), CancellationToken.None);

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("/v1/predictions/status/old", handler.Requests[0].Path);
    }

    [Fact]
    public async Task Tahmin_bulunamazsa_yeniden_gonderilmez()
    {
        var handler = new StubHandler().Json(HttpStatusCode.NotFound, """{"error":{"code":"NOT_FOUND","message":"x"}}""");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler).TryOnAsync(Person, Garment, Request(resume: "gone"), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.InvalidOutput, ex.Code);
        Assert.False(ex.Retryable);
    }

    [Theory]
    [InlineData("NSFW content detected", GenerationErrorCode.ContentRejected, false)]
    [InlineData("Number of samples, -5, must be non-negative.", GenerationErrorCode.InvalidInput, false)]
    [InlineData("worker crashed", GenerationErrorCode.ProviderUnavailable, true)]
    public async Task Basarisiz_tahmin_eslenir(string error, GenerationErrorCode code, bool retryable)
    {
        var handler = new StubHandler().Json(HttpStatusCode.Created, Upload("p")).Json(HttpStatusCode.Created, Upload("g"))
            .Json(HttpStatusCode.Created, """{"id":"f"}""")
            .Json(HttpStatusCode.OK, $$"""{"status":"failed","message":"Prediction failed","error":"{{error}}"}""");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler).TryOnAsync(Person, Garment, Request(), CancellationToken.None));

        Assert.Equal(code, ex.Code);
        Assert.Equal(retryable, ex.Retryable);
    }

    [Fact]
    public async Task Pruna_disindaki_cikti_adresi_reddedilir_ve_suresi_dolan_cikti_yeniden_denenmez()
    {
        var foreign = new StubHandler().Json(HttpStatusCode.Created, Upload("p")).Json(HttpStatusCode.Created, Upload("g")).Json(HttpStatusCode.Created, """{"id":"x"}""")
            .Json(HttpStatusCode.OK, """{"status":"succeeded","generation_url":"https://evil.example/o.jpg"}""");
        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(foreign).TryOnAsync(Person, Garment, Request(), CancellationToken.None));
        Assert.Equal(GenerationErrorCode.InvalidOutput, ex.Code);
        Assert.Equal(4, foreign.Requests.Count);

        var expired = new StubHandler().Json(HttpStatusCode.OK, """{"status":"succeeded","generation_url":"https://api.pruna.ai/v1/predictions/delivery/e/o.jpg"}""")
            .Json(HttpStatusCode.Gone, """{"error":{"code":"CONTENT_EXPIRED","message":"x"}}""");
        var gone = await Assert.ThrowsAsync<ProviderException>(() => Create(expired).TryOnAsync(Person, Garment, Request(resume: "e"), CancellationToken.None));
        Assert.Equal(GenerationErrorCode.InvalidOutput, gone.Code);
        Assert.False(gone.Retryable);
    }

    [Theory]
    [InlineData(401, GenerationErrorCode.ProviderNotConfigured, false)]
    [InlineData(400, GenerationErrorCode.InvalidInput, false)]
    [InlineData(413, GenerationErrorCode.InvalidInput, false)]
    [InlineData(422, GenerationErrorCode.InvalidInput, false)]
    [InlineData(429, GenerationErrorCode.RateLimited, true)]
    [InlineData(500, GenerationErrorCode.ProviderUnavailable, true)]
    public async Task Http_hatalari_eslenir(int status, GenerationErrorCode code, bool retryable)
    {
        var handler = new StubHandler().Json((HttpStatusCode)status, """{"error":{"code":"X","message":"pruna-key-321"}}""");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler).TryOnAsync(Person, Garment, Request(), CancellationToken.None));

        Assert.Equal(code, ex.Code);
        Assert.Equal(retryable, ex.Retryable);
        Assert.DoesNotContain("pruna-key-321", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anahtar_yoksa_hicbir_istek_gonderilmez()
    {
        var handler = new StubHandler();

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler, apiKey: " ").TryOnAsync(Person, Garment, Request(), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.ProviderNotConfigured, ex.Code);
        Assert.Empty(handler.Requests);
    }
}

public sealed class TryOnPolicyTests
{
    [Fact]
    public void Varsayilan_politika_eski_olmayan_tum_modeller_seedream_varsayilan_gpt_medium()
    {
        var policy = TryOnPolicyResolver.Resolve(new OrganizationSettings());

        Assert.Equal(TryOnModels.Seedream, policy.DefaultModel);
        Assert.Equal([TryOnModels.Seedream, TryOnModels.GptImage, TryOnModels.Pruna], policy.Models.Select(m => m.Model.Key).ToArray());
        var gpt = policy.Models.Single(m => m.Model.Key == TryOnModels.GptImage);
        Assert.Equal("medium", gpt.DefaultQuality);
        Assert.Equal(["low", "medium", "high"], gpt.AllowedQualities.ToArray()); // auto/xhigh/max yöneticinin açmasını bekler
        Assert.Equal(["auto", "low", "medium", "high", "xhigh", "max"], gpt.Model.Qualities.Select(q => q.Key).ToArray());
        Assert.Equal(TryOnSelectionError.QualityNotAllowed, Pick(policy, TryOnModels.GptImage, "xhigh").Error);
        Assert.DoesNotContain(policy.Models, m => m.Model.Legacy);
    }

    [Fact]
    public void Yonetici_pahali_kaliteleri_acabilir()
    {
        var policy = TryOnPolicyResolver.Resolve(new OrganizationSettings
        {
            AllowedTryOnQualities = ["gpt-image-2.5:medium", "gpt-image-2.5:max", "gpt-image-2.5:auto", "gpt-image-2.5:xhigh"],
            DefaultTryOnQualities = ["gpt-image-2.5:medium"],
        });

        var gpt = policy.Models.Single(m => m.Model.Key == TryOnModels.GptImage);
        Assert.Equal(["auto", "medium", "xhigh", "max"], gpt.AllowedQualities.ToArray()); // katalog sırası korunur
        Assert.Equal((TryOnModels.GptImage, "max", (TryOnSelectionError?)null), Pick(policy, TryOnModels.GptImage, "max"));
        Assert.Equal(TryOnSelectionError.QualityNotAllowed, Pick(policy, TryOnModels.GptImage, "low").Error);
    }

    [Fact]
    public void Kalite_kisitlari_ve_varsayilanlar_firma_ayarindan_gelir()
    {
        var settings = new OrganizationSettings
        {
            DefaultTryOnModel = TryOnModels.GptImage,
            EnabledTryOnModels = [TryOnModels.GptImage],
            AllowedTryOnQualities = ["gpt-image-2.5:low", "gpt-image-2.5:medium"],
            DefaultTryOnQualities = ["gpt-image-2.5:low"],
        };
        var policy = TryOnPolicyResolver.Resolve(settings);

        Assert.Equal((TryOnModels.GptImage, "low"), (policy.DefaultModel, policy.Models.Single().DefaultQuality));
        Assert.Equal((TryOnModels.GptImage, "low", (TryOnSelectionError?)null), Pick(policy, null, null));
        Assert.Equal((TryOnModels.GptImage, "medium", (TryOnSelectionError?)null), Pick(policy, TryOnModels.GptImage, "MEDIUM"));
        Assert.Equal(TryOnSelectionError.QualityNotAllowed, Pick(policy, TryOnModels.GptImage, "high").Error);
        Assert.Equal(TryOnSelectionError.QualityNotAllowed, Pick(policy, TryOnModels.GptImage, "max").Error);
        Assert.Equal(TryOnSelectionError.UnknownQuality, Pick(policy, TryOnModels.GptImage, "ultra").Error);
        Assert.Equal(TryOnSelectionError.ModelNotAllowed, Pick(policy, TryOnModels.Seedream, null).Error);
        Assert.Equal(TryOnSelectionError.ModelNotAllowed, Pick(policy, TryOnModels.Fashn, null).Error);
        Assert.Equal(TryOnSelectionError.UnknownModel, Pick(policy, "uydurma", null).Error);
    }

    [Fact]
    public void Gecersiz_kayitli_ayarlar_guvenli_varsayilana_duser()
    {
        var policy = TryOnPolicyResolver.Resolve(new OrganizationSettings
        {
            DefaultTryOnModel = "yok", EnabledTryOnModels = [TryOnModels.Fashn], DefaultTryOnQualities = ["seedream-5-flash:yok"],
        });

        Assert.Equal(TryOnModels.Seedream, policy.DefaultModel); // eski model etkinleştirilemez; sistem varsayılanına düşer
        Assert.Equal("standard", policy.Models.Single().DefaultQuality);
    }

    [Fact]
    public void Fiyat_ezmesi_yapilandirmadan_uygulanir_en_kotu_tahminden_kucuk_olamaz()
    {
        var model = TryOnModels.Find(TryOnModels.GptImage)!;
        var pricing = new PricingSettings { TryOnEstimatedUsd = { ["gpt-image-2.5:medium"] = 0.06m }, TryOnWorstCaseUsd = { ["gpt-image-2.5:medium"] = 0.04m } };

        var (estimated, worst) = TryOnModels.Price(model, model.Quality("medium")!, pricing);

        Assert.Equal(0.06m, estimated);
        Assert.Equal(0.06m, worst); // en kötü durum hiçbir zaman tahminin altında kalmaz
        Assert.Equal((0.027m, 0.027m), TryOnModels.Price(TryOnModels.Find(TryOnModels.Seedream)!, TryOnModels.Find(TryOnModels.Seedream)!.Quality("standard")!, new PricingSettings()));
    }

    private static TryOnCostInput Input(int outW, int outH, int refs = 1, int numImages = 1, int promptChars = 80) =>
        new(new(outW, outH), Enumerable.Repeat(new ImageDimensions(1024, 1024), refs).ToList(), numImages, promptChars, new(outW, outH));

    [Theory]
    [InlineData("low", 0.0131)]
    [InlineData("medium", 0.0186)]
    [InlineData("high", 0.0495)]
    public void Gpt_image_tahmini_fal_sayfasindaki_ornek_toplamlarla_uyusur(string quality, double falExample)
    {
        // fal "Example totals": 1024×1536 çıktı, tek 1024×1024 referans, kısa prompt
        var (estimated, _) = GptImagePricing.Estimate(quality, "auto", Input(1024, 1536));

        Assert.InRange((double)estimated, falExample - 0.0005, falExample + 0.0005);
    }

    [Fact]
    public void Gpt_image_maliyeti_kalite_cozunurluk_ve_gorsel_sayisiyla_artar()
    {
        static decimal Est(string q, TryOnCostInput i) => GptImagePricing.Estimate(q, "auto", i).Estimated;
        var typical = Input(1024, 1536, refs: 2);

        var ladder = GptImagePricing.Qualities.Where(q => q != "auto").Select(q => Est(q, typical)).ToArray();
        Assert.Equal(ladder.Order().ToArray(), ladder);
        Assert.True(Est("high", Input(3840, 2160, refs: 2)) > Est("high", typical)); // çözünürlük
        Assert.True(Est("medium", Input(1024, 1536, refs: 3)) > Est("medium", typical)); // referans görsel sayısı
        // görsel sayısı: çıktı ücreti görsel başına, referans/prompt istek başına bir kez
        Assert.Equal(0.04116m, Est("high", Input(1024, 1536, refs: 2, numImages: 2)) - Est("high", typical), 4);
        // 4K üstü boyutlar 4K satırından orantılı büyütülür
        Assert.True(Est("max", Input(4096, 4096, refs: 2)) > Est("max", Input(3840, 2160, refs: 2)));
    }

    [Fact]
    public void Gpt_image_auto_tahmini_high_en_kotu_durumu_max_sutunudur()
    {
        var input = Input(1024, 1536, refs: 2);

        var auto = GptImagePricing.Estimate("auto", "auto", input);
        var high = GptImagePricing.Estimate("high", "auto", input);
        var max = GptImagePricing.Estimate("max", "auto", input);

        Assert.Equal(high.Estimated, auto.Estimated);
        Assert.Equal(max.WorstCase, auto.WorstCase);
    }

    [Fact]
    public void Gpt_image_en_kotu_durum_tablodaki_daha_pahali_kucuk_boyutu_kapsar()
    {
        // Tablo boyutla tekdüze değildir: 1024×1024 (high 0,05268) 1024×1536'dan (0,04116) pahalıdır
        var (estimated, worst) = GptImagePricing.Estimate("high", "auto", Input(1024, 1536, refs: 0, promptChars: 0));

        Assert.Equal(0.0412m, estimated);
        Assert.Equal(0.0557m, worst); // 0,05268 + prompt üst sınırı 0,003
    }

    [Fact]
    public void Gpt_image_auto_boyutu_kisi_fotografindan_hazir_boyutlar_tablodan()
    {
        var person = new TryOnCostInput(new(1536, 2048), [], 1, 0);

        Assert.Equal(0.0144m, GptImagePricing.Estimate("medium", "auto", person).Estimated); // 3,1 MP → 2560×1440 satırı
        Assert.Equal(0.0091m, GptImagePricing.Estimate("medium", "portrait_4_3", person).Estimated); // 768×1024 → 1024×768 satırı
    }

    [Fact]
    public void Gosterim_fiyati_tipik_tahmin_ve_yukleme_sinirina_gore_en_kotu_durum()
    {
        var model = TryOnModels.Find(TryOnModels.GptImage)!;
        foreach (var quality in model.Qualities)
        {
            var (estimated, worst) = TryOnModels.DisplayPrice(model, quality, new PricingSettings());
            Assert.True(estimated > 0 && worst >= estimated, quality.Key);
        }

        var maxWorst = TryOnModels.DisplayPrice(model, model.Quality("max")!, new PricingSettings()).WorstCase;
        Assert.InRange(maxWorst, 0.40m, 0.45m); // 2048×2048 girdi → 4K satırına kadar en pahalı (0,40026) + referanslar + prompt
    }

    [Fact]
    public void Tek_urun_talimati_yuz_poz_ve_urun_sadakatini_ister_musteri_bilgisi_icermez()
    {
        var prompt = PromptBuilder.BuildSingleGarment("Kaftan", "Zümrüt Kaftan", "Zümrüt", isCompleteSet: false);
        var set = PromptBuilder.BuildSingleGarment("Takım", "Sultan Bindallı", null, isCompleteSet: true);

        Assert.Contains("the Kaftan shown in Image 2 (Zümrüt Kaftan, color: Zümrüt)", prompt, StringComparison.Ordinal);
        Assert.Contains("face, identity, hair", prompt, StringComparison.Ordinal);
        Assert.Contains("gold embroidery", prompt, StringComparison.Ordinal);
        Assert.Contains("crowns, headpieces, jewelry", prompt, StringComparison.Ordinal);
        Assert.Contains("the complete outfit (all pieces)", set, StringComparison.Ordinal);
        Assert.DoesNotContain("customer", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{", prompt, StringComparison.Ordinal);
    }

    private static (string? Model, string? Quality, TryOnSelectionError? Error) Pick(TryOnPolicy policy, string? model, string? quality)
    {
        var (selected, q, error) = TryOnPolicyResolver.Select(policy, model, quality);
        return (selected?.Model.Key, q, error);
    }
}
