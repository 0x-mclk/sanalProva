using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Ai.Abstractions;

namespace SanalProva.UnitTests.Infrastructure;

public sealed class CostEstimatorTests
{
    private static CostEstimator Create() => new(new StaticOptionsMonitor<GenerationSettings>(new GenerationSettings()));

    private static Dictionary<string, string> Fashn(string mode, string resolution) => new() { ["generation_mode"] = mode, ["resolution"] = resolution };

    [Theory]
    [InlineData("fast", "1k", 1, "0.0750")]
    [InlineData("balanced", "2k", 3, "0.2250")]
    [InlineData("quality", "4k", 5, "0.3750")]
    public void Fashn_kredi_tablosu_ve_birim_fiyat(string mode, string resolution, int credits, string usd)
    {
        var estimator = Create();
        var parameters = Fashn(mode, resolution);

        Assert.Equal(credits, estimator.FashnCredits(parameters));
        Assert.Equal(decimal.Parse(usd, System.Globalization.CultureInfo.InvariantCulture), estimator.FashnUsd(estimator.FashnCredits(parameters)));
    }

    [Fact]
    public void Fashn_bilinmeyen_kombinasyonda_en_pahali_deger_varsayilir()
    {
        var estimator = Create();

        Assert.Equal(5m, estimator.FashnCredits(Fashn("turbo", "8k")));
        Assert.Equal(6m, estimator.FashnCredits(Fashn("balanced", "2k"), numImages: 2));
    }

    [Theory]
    [InlineData("gemini-3-pro-image", "2K", "0.1340")]
    [InlineData("gemini-3-pro-image", "4K", "0.2400")]
    [InlineData("gemini-nano-banana-2.1", "2K", "0.0504")]
    [InlineData("gemini-3.1-flash-lite-image", "1K", "0.0336")]
    public void Gemini_jeton_bilgisi_yoksa_gorsel_basi_resmi_fiyat_kullanilir(string model, string size, string usd) =>
        Assert.Equal(decimal.Parse(usd, System.Globalization.CultureInfo.InvariantCulture), Create().GeminiUsd(model, size, usage: null));

    [Fact]
    public void Gemini_jetonlardan_hesap_resmi_birim_fiyatlarla_tutarlidir()
    {
        // Pro 2K: 1120 görsel jetonu × 120 USD/1M = 0,1344 ≈ görsel başı 0,134 (resmî fiyatla uyumlu)
        var cost = Create().GeminiUsd("gemini-3-pro-image", "2K", new GeminiUsageTokens(InputTokens: 0, OutputImageTokens: 1120, OutputOtherTokens: 0, ThoughtTokens: 0));

        Assert.Equal(0.1344m, cost);
    }

    [Fact]
    public void Cagri_oncesi_en_kotu_durum_tahmini_giris_gorselleri_ve_dusunmeyi_icerir()
    {
        var estimator = Create();
        var options = new Dictionary<string, string> { ["image_size"] = "2K" };

        var worst = estimator.WorstCasePerCallUsd("Gemini", "gemini-3-pro-image", options, inputImages: 6);

        // 0,134 + 6×1300×2/1M (0,0156) + 4000×12/1M (0,048) = 0,1976
        Assert.Equal(0.1976m, worst);
        Assert.Equal(0.225m, estimator.WorstCasePerCallUsd("Fashn", "tryon-max", Fashn("balanced", "2k"), 2));
        Assert.Equal(0m, estimator.WorstCasePerCallUsd("Mock", "x", options, 2));
    }

    [Fact]
    public void Varsayilan_butce_tek_cagri_tahminlerinden_buyuktur()
    {
        // MaxJobCostUsd varsayılanı (0,50), varsayılan strateji çağrılarının en kötü durumunu karşılar; aksi halde hiçbir iş çalışamazdı
        var estimator = Create();
        var budget = new BudgetSettings();

        Assert.True(estimator.WorstCasePerCallUsd("Fashn", "tryon-max", Fashn("balanced", "2k"), 2) < budget.MaxJobCostUsd);
        Assert.True(estimator.WorstCasePerCallUsd("Gemini", "gemini-3-pro-image", new Dictionary<string, string> { ["image_size"] = "2K" }, 6) < budget.MaxJobCostUsd);
        Assert.False(budget.AllowPaidCalls); // varsayılan: ücretli çağrı kapalı
    }
}

public sealed class FashnQualityTests
{
    private static readonly Dictionary<string, string> Defaults = new() { ["resolution"] = "1k", ["generation_mode"] = "balanced", ["return_base64"] = "true" };

    [Fact]
    public void Firma_ayari_yoksa_sunucu_varsayilani_kalir()
    {
        var merged = FashnQuality.Apply(Defaults, new SanalProva.Domain.Tenancy.OrganizationSettings());

        Assert.Equal("1k", merged["resolution"]);
        Assert.Equal("balanced", merged["generation_mode"]);
    }

    [Fact]
    public void Firma_ayari_varsayilanin_ustune_yazilir_gecersiz_deger_yok_sayilir()
    {
        var settings = new SanalProva.Domain.Tenancy.OrganizationSettings { FashnResolution = "2k", FashnGenerationMode = "ultra" };

        var merged = FashnQuality.Apply(Defaults, settings);

        Assert.Equal("2k", merged["resolution"]);
        Assert.Equal("balanced", merged["generation_mode"]); // geçersiz mod yok sayıldı
        Assert.Equal("true", merged["return_base64"]);
        Assert.Equal("1k", Defaults["resolution"]); // girdi sözlüğü değişmez
    }

    [Fact]
    public void Varsayilan_secenek_iki_kredi_alternatif_uc_kredidir_4k_sunulmaz()
    {
        var estimator = new CostEstimator(new StaticOptionsMonitor<GenerationSettings>(new GenerationSettings()));

        Assert.Equal(2m, estimator.FashnCredits(new Dictionary<string, string> { ["generation_mode"] = "balanced", ["resolution"] = "1k" }));
        Assert.Equal(0.15m, estimator.FashnUsd(2m));
        Assert.Equal(3m, estimator.FashnCredits(new Dictionary<string, string> { ["generation_mode"] = "balanced", ["resolution"] = "2k" }));
        Assert.DoesNotContain("4k", FashnQuality.Resolutions);
    }
}

public sealed class ModelCatalogTests
{
    [Fact]
    public void Hicbir_model_canli_dogrulanmis_gosterilmez()
    {
        Assert.All(ModelCatalog.Known, m => Assert.False(m.LiveVerified));
        Assert.All(ModelCatalog.Known, m => Assert.Equal("2026-10-20", m.DocsVerifiedOn));
    }

    [Theory]
    [InlineData("Fashn", "tryon-max")]
    [InlineData("Gemini", "gemini-3-pro-image")]
    [InlineData("Gemini", "gemini-nano-banana-2.1")]
    [InlineData("Gemini", "gemini-3.1-flash-image")]
    [InlineData("Gemini", "gemini-3.1-flash-lite-image")]
    public void Resmi_dokumandaki_model_kimlikleri_taninir(string provider, string model) => Assert.NotNull(ModelCatalog.Find(provider, model));

    [Fact]
    public void Bilinmeyen_model_taninmaz()
    {
        Assert.Null(ModelCatalog.Find("Gemini", "gemini-9-ultra"));
        Assert.NotNull(ModelCatalog.CheckCapability("Gemini", "gemini-9-ultra", GenerationJobType.FreeCombination));
    }

    [Fact]
    public void Lite_model_kombin_uretimi_icin_uygun_degildir_ama_basit_duzenleme_adayidir()
    {
        Assert.NotNull(ModelCatalog.CheckCapability("Gemini", "gemini-3.1-flash-lite-image", GenerationJobType.FreeCombination));
        Assert.Null(ModelCatalog.CheckCapability("Gemini", "gemini-3.1-flash-lite-image", GenerationJobType.ProductSwap));
        Assert.Null(ModelCatalog.CheckCapability("Gemini", "gemini-3-pro-image", GenerationJobType.FreeCombination));
        Assert.Null(ModelCatalog.CheckCapability("Fashn", "tryon-max", GenerationJobType.OutfitSet));
    }
}

public sealed class OutputValidatorTests
{
    private static readonly ImageInput Person = new(AiTestImages.Jpeg(900, 1500), "image/jpeg");

    [Fact]
    public void Normal_cikti_gecer() =>
        OutputValidator.Validate(new GeneratedOutput(AiTestImages.Jpeg(900, 1500, seed: 4), "image/jpeg"), Person);

    [Fact]
    public void Duz_veya_siyah_cikti_reddedilir()
    {
        var ex = Assert.Throws<ProviderException>(() => OutputValidator.Validate(new GeneratedOutput(AiTestImages.Flat(900, 1500), "image/jpeg"), Person));

        Assert.Equal(GenerationErrorCode.InvalidOutput, ex.Code);
        Assert.False(ex.Retryable); // yeniden deneme çift ücret olabilir
    }

    [Fact]
    public void Cercevesi_fotografla_uyusmayan_cikti_reddedilir()
    {
        var ex = Assert.Throws<ProviderException>(() => OutputValidator.Validate(new GeneratedOutput(AiTestImages.Jpeg(1500, 900, seed: 6), "image/jpeg"), Person));

        Assert.Equal(GenerationErrorCode.InvalidOutput, ex.Code);
    }

    [Fact]
    public void Bos_veya_bozuk_cikti_reddedilir()
    {
        Assert.Throws<ProviderException>(() => OutputValidator.Validate(new GeneratedOutput([], "image/jpeg"), Person));
        Assert.Throws<ProviderException>(() => OutputValidator.Validate(new GeneratedOutput([1, 2, 3, 4], "image/jpeg"), Person));
    }
}

public sealed class PromptTemplateTests
{
    private static GarmentReference Garment(string category, string name, int layer) =>
        new(new ImageInput(new byte[] { 1 }, "image/jpeg"), category, name, null, layer, null);

    [Fact]
    public void Kombin_sablonu_gereksiz_aksesuar_ve_karisikligi_yasaklar_ve_surumludur()
    {
        var prompt = PromptBuilder.BuildFreeCombination([Garment("Şalvar", "Lale Şalvar", 10), Garment("Gömlek", "İpek Gömlek", 20), Garment("Yelek", "Kadife Yelek", 30), Garment("Kaftan", "Zümrüt Kaftan", 40), Garment("Kemer", "Altın Kemer", 50)]);

        Assert.Equal("free-combination.v2", PromptBuilder.FreeCombinationVersion);
        Assert.Contains("Images 2 to 6", prompt, StringComparison.Ordinal);
        Assert.Contains("Never merge, blend or duplicate garments", prompt, StringComparison.Ordinal);
        Assert.Contains("crowns, headpieces, jewelry, bags or shoes", prompt, StringComparison.Ordinal);
        Assert.Contains("face, hair, skin tone, body shape, pose, hands", prompt, StringComparison.Ordinal);
        Assert.Contains("gold embroidery", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("customer", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Urun_degistirme_sablonu_yalnizca_hedef_parcayi_degistirir()
    {
        var prompt = PromptBuilder.BuildProductSwap("Kemer", "Altın Kemer", "Altın");

        Assert.Contains("Replace ONLY the Kemer", prompt, StringComparison.Ordinal);
        Assert.Contains("color: Altın", prompt, StringComparison.Ordinal);
        Assert.Contains("all other garments and the background", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{", prompt, StringComparison.Ordinal);
    }
}
