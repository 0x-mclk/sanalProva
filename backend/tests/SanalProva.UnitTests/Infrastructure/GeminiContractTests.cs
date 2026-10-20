using System.Net;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Ai.Abstractions;
using SanalProva.Infrastructure.Ai.Gemini;

namespace SanalProva.UnitTests.Infrastructure;

/// <summary>
/// Gemini (Interactions API) adapter sözleşme testleri. İstek/yanıt şekilleri resmî dokümandan (ai.google.dev: image-generation,
/// interactions-api, interactions-breaking-changes-may-2026; 2026-10-20) türetilmiştir — gerçek API'den kaydedilmiş yanıt DEĞİLDİR.
/// Güvenlik/ret yanıtının tam şekli dokümanda tanımlı olmadığından o kısım "varsayım"dır ve canlı testte doğrulanacaktır (docs/11).
/// </summary>
public sealed class GeminiContractTests
{
    private static readonly ImageInput Person = new(AiTestImages.Jpeg(900, 1500), "image/jpeg");
    private static readonly byte[] Output = AiTestImages.Jpeg(900, 1500, seed: 5);

    private static GeminiProvider Create(StubHandler handler, string apiKey = "gem-key-456", int maxInlineBytes = 18 * 1024 * 1024) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") },
            Options.Create(new GeminiOptions { ApiKey = apiKey, MaxInlineBytes = maxInlineBytes, RequestTimeoutSeconds = 5 }),
            new CostEstimator(new StaticOptionsMonitor<GenerationSettings>(new GenerationSettings())));

    private static ProviderRequestOptions Request(string model = "gemini-3-pro-image", string size = "2K") =>
        new(model, new Dictionary<string, string> { ["image_size"] = size }, Seed: 11);

    private static GarmentReference Garment(string category, string name, int layer, byte seed) =>
        new(new ImageInput(AiTestImages.Jpeg(300, 400, seed), "image/jpeg"), category, name, "Bordo", layer, null);

    private static string Success(string imageBase64, string extraSteps = "") => $$"""
        {
          "id": "int_abc",
          "object": "interaction",
          "status": "completed",
          "steps": [
            {{extraSteps}}
            {"type":"model_output","content":[
              {"type":"text","text":"Hazır."},
              {"type":"image","mime_type":"image/png","data":"{{imageBase64}}"}
            ]}
          ],
          "usage": {
            "total_input_tokens": 8000,
            "total_output_tokens": 1400,
            "total_thought_tokens": 200,
            "output_tokens_by_modality": [{"modality":"image","tokens":1120},{"modality":"text","tokens":80}]
          }
        }
        """;

    private static readonly IReadOnlyList<GarmentReference> FivePieces =
    [
        Garment("Kemer", "Altın Kemer", 50, 5),
        Garment("Şalvar", "Lale Şalvar", 10, 1),
        Garment("Gömlek", "İpek Gömlek", 20, 2),
        Garment("Yelek", "Kadife Yelek", 30, 3),
        Garment("Kaftan", "Zümrüt Kaftan", 40, 4),
    ];

    [Fact]
    public async Task Besli_kombin_istegi_resmi_sozlesmeye_uygun_kurulur()
    {
        var handler = new StubHandler().Json(HttpStatusCode.OK, Success(Convert.ToBase64String(Output)));

        var result = await Create(handler).ComposeOutfitAsync(Person, FivePieces, "PROMPT-METNI", Request(), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1beta/interactions", request.Path);
        Assert.Equal("gem-key-456", request.Headers["x-goog-api-key"]);
        Assert.Equal("2026-05-20", request.Headers["Api-Revision"]); // yanıt şeması sabitlenir
        Assert.DoesNotContain("gem-key-456", request.Body, StringComparison.Ordinal); // anahtar gövdeye girmez

        using var body = request.BodyJson();
        var root = body.RootElement;
        Assert.Equal("gemini-3-pro-image", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("store").GetBoolean()); // müşteri görseli Google'da saklanmaz
        Assert.Equal("image", root.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("2K", root.GetProperty("response_format").GetProperty("image_size").GetString());
        Assert.Equal("9:16", root.GetProperty("response_format").GetProperty("aspect_ratio").GetString()); // 900×1500 (0,60) en yakın desteklenen oran
        Assert.Equal(11, root.GetProperty("generation_config").GetProperty("seed").GetInt32());

        var input = root.GetProperty("input").EnumerateArray().ToList();
        Assert.Equal(7, input.Count); // metin + kişi + 5 ürün
        Assert.Equal("text", input[0].GetProperty("type").GetString());
        Assert.Equal("PROMPT-METNI", input[0].GetProperty("text").GetString());
        Assert.All(input.Skip(1), part =>
        {
            Assert.Equal("image", part.GetProperty("type").GetString());
            Assert.Equal("image/jpeg", part.GetProperty("mime_type").GetString());
            Assert.True(part.GetProperty("data").GetString()!.Length > 100);
        });
        // Görsel 1 = kişi; ürünler katman sırasında (şalvar 10 → gömlek 20 → yelek 30 → kaftan 40 → kemer 50)
        Assert.Equal(Convert.ToBase64String(Person.Content.Span), input[1].GetProperty("data").GetString());
        var expectedOrder = FivePieces.OrderBy(g => g.LayerOrder).Select(g => Convert.ToBase64String(g.Image.Content.Span)).ToList();
        Assert.Equal(expectedOrder, input.Skip(2).Select(p => p.GetProperty("data").GetString()!).ToList());

        Assert.Equal(Output, result.Images[0].Content);
        Assert.Equal("Gemini", result.Provider);
        Assert.Equal("int_abc", result.ProviderRequestId);
    }

    [Fact]
    public async Task Maliyet_jetonlardan_hesaplanir_ve_gerceklesen_maliyet_bos_kalir()
    {
        var handler = new StubHandler().Json(HttpStatusCode.OK, Success(Convert.ToBase64String(Output)));

        var result = await Create(handler).ComposeOutfitAsync(Person, FivePieces, "p", Request(), CancellationToken.None);

        // giriş 8000×2/1M=0,016 + görsel 1120×120/1M=0,1344 + metin/düşünme (280+200)×12/1M=0,00576 = 0,15616
        Assert.Equal(0.1562m, result.Usage.EstimatedCostUsd);
        Assert.Null(result.Usage.ActualCostUsd);
        Assert.Equal(8000, result.Usage.InputTokens);
        Assert.Equal(1400 + 200, result.Usage.OutputTokens);
    }

    [Fact]
    public async Task Dusunme_adimindaki_ara_gorsel_sonuc_sayilmaz()
    {
        var interim = Convert.ToBase64String(AiTestImages.Jpeg(100, 160, seed: 9));
        var thought = $$"""{"type":"thought","summary":[{"type":"image","mime_type":"image/png","data":"{{interim}}"}]},""";
        var handler = new StubHandler().Json(HttpStatusCode.OK, Success(Convert.ToBase64String(Output), thought));

        var result = await Create(handler).ComposeOutfitAsync(Person, FivePieces, "p", Request(), CancellationToken.None);

        Assert.Equal(Output, result.Images[0].Content);
    }

    [Fact]
    public async Task Gorselsiz_yanit_icerik_reddi_sayilir_giris_maliyeti_yazilir_ve_tekrar_denenmez()
    {
        var handler = new StubHandler().Json(HttpStatusCode.OK, """
            {"id":"int_x","status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"Bunu üretemem."}]}],
             "usage":{"total_input_tokens":8000,"total_output_tokens":10}}
            """);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler).ComposeOutfitAsync(Person, FivePieces, "p", Request(), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.ContentRejected, ex.Code);
        Assert.False(ex.Retryable);
        Assert.True(ex.ChargedUsd > 0m); // giriş jetonları ücretlendirilmiş olabilir: kaydedilir
        Assert.Equal("int_x", ex.ProviderRequestId);
    }

    [Fact]
    public async Task Yarim_kalan_sonuc_gecersiz_cikti_sayilir()
    {
        var handler = new StubHandler().Json(HttpStatusCode.OK, """{"id":"i","status":"incomplete","steps":[],"usage":{"total_input_tokens":100}}""");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler).ComposeOutfitAsync(Person, FivePieces, "p", Request(), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.InvalidOutput, ex.Code);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task Basarisiz_etkilesim_guvenlik_kodunda_icerik_reddi_olur()
    {
        var handler = new StubHandler().Json(HttpStatusCode.OK, """{"id":"i","status":"failed","errors":[{"code":"https://errors.example/safety_blocked","message":"x"}]}""");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler).ComposeOutfitAsync(Person, FivePieces, "p", Request(), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.ContentRejected, ex.Code);
        Assert.False(ex.Retryable);
    }

    [Theory]
    [InlineData(400, "INVALID_ARGUMENT", GenerationErrorCode.InvalidInput, false)]
    [InlineData(400, "FAILED_PRECONDITION", GenerationErrorCode.ProviderNotConfigured, false)]
    [InlineData(401, "UNAUTHENTICATED", GenerationErrorCode.ProviderNotConfigured, false)]
    [InlineData(403, "PERMISSION_DENIED", GenerationErrorCode.ProviderNotConfigured, false)]
    [InlineData(404, "NOT_FOUND", GenerationErrorCode.ProviderNotConfigured, false)]
    [InlineData(429, "RESOURCE_EXHAUSTED", GenerationErrorCode.RateLimited, true)]
    [InlineData(500, "INTERNAL", GenerationErrorCode.ProviderUnavailable, true)]
    [InlineData(503, "UNAVAILABLE", GenerationErrorCode.ProviderUnavailable, true)]
    [InlineData(504, "DEADLINE_EXCEEDED", GenerationErrorCode.Timeout, true)]
    public async Task Http_hatalari_ortak_hata_koduna_eslenir(int status, string googleStatus, GenerationErrorCode code, bool retryable)
    {
        var handler = new StubHandler().Json((HttpStatusCode)status, $$$"""{"error":{"code":{{{status}}},"message":"gem-key-456 ayrıntı","status":"{{{googleStatus}}}"}}""");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler).ComposeOutfitAsync(Person, FivePieces, "p", Request(), CancellationToken.None));

        Assert.Equal(code, ex.Code);
        Assert.Equal(retryable, ex.Retryable);
        Assert.DoesNotContain("gem-key-456", ex.Message, StringComparison.Ordinal); // sağlayıcı iletisi (anahtar yansıtabilir) aktarılmaz
    }

    [Fact]
    public async Task Duzenleme_istegi_taban_gorsel_ve_referanslari_sirayla_gonderir()
    {
        var handler = new StubHandler().Json(HttpStatusCode.OK, Success(Convert.ToBase64String(Output)));
        var reference = new ImageInput(AiTestImages.Jpeg(300, 400, 8), "image/jpeg");

        await Create(handler).EditAsync(Person, "Replace only the belt.", [reference], Request("gemini-nano-banana-2.1"), CancellationToken.None);

        using var body = handler.Requests[0].BodyJson();
        Assert.Equal("gemini-nano-banana-2.1", body.RootElement.GetProperty("model").GetString());
        var input = body.RootElement.GetProperty("input").EnumerateArray().ToList();
        Assert.Equal("Replace only the belt.", input[0].GetProperty("text").GetString());
        Assert.Equal(Convert.ToBase64String(Person.Content.Span), input[1].GetProperty("data").GetString());
        Assert.Equal(Convert.ToBase64String(reference.Content.Span), input[2].GetProperty("data").GetString());
    }

    [Fact]
    public async Task Dogrulanmamis_model_kimligiyle_istek_gonderilmez()
    {
        var handler = new StubHandler();

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler).ComposeOutfitAsync(Person, FivePieces, "p", Request("gemini-uydurma-9"), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.ProviderNotConfigured, ex.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Lite_model_yalnizca_1k_destekler()
    {
        var handler = new StubHandler();

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler).EditAsync(Person, "x", [], Request("gemini-3.1-flash-lite-image", "2K"), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.InvalidInput, ex.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Api_anahtari_yoksa_veya_gorseller_cok_buyukse_istek_gonderilmez()
    {
        var handler = new StubHandler();
        var missing = await Assert.ThrowsAsync<ProviderException>(() => Create(handler, apiKey: "").ComposeOutfitAsync(Person, FivePieces, "p", Request(), CancellationToken.None));
        var tooBig = await Assert.ThrowsAsync<ProviderException>(() => Create(handler, maxInlineBytes: 1000).ComposeOutfitAsync(Person, FivePieces, "p", Request(), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.ProviderNotConfigured, missing.Code);
        Assert.Equal(GenerationErrorCode.InvalidInput, tooBig.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Kombin_parca_sayisi_sinirlari_denetlenir()
    {
        var handler = new StubHandler();
        var six = Enumerable.Range(0, 6).Select(i => Garment("Kategori", $"Ürün {i}", i, (byte)i)).ToList();

        var ex = await Assert.ThrowsAsync<ProviderException>(() => Create(handler).ComposeOutfitAsync(Person, six, "p", Request(), CancellationToken.None));
        var none = await Assert.ThrowsAsync<ProviderException>(() => Create(handler).ComposeOutfitAsync(Person, [], "p", Request(), CancellationToken.None));

        Assert.Equal(GenerationErrorCode.InvalidInput, ex.Code);
        Assert.Equal(GenerationErrorCode.InvalidInput, none.Code);
        Assert.Empty(handler.Requests);
    }
}
