using Microsoft.Extensions.DependencyInjection;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Ai.Abstractions;
using SanalProva.Infrastructure.Ai.Mock;

namespace SanalProva.UnitTests.Infrastructure;

public sealed class GenerationStrategyTests
{
    private static GenerationSettings Settings(bool useMock) => new()
    {
        UseMockProviders = useMock,
        Mock = new MockSettings { DelayMs = 0 },
        Strategies =
        {
            [GenerationJobType.OutfitSet] = new StrategySettings { Provider = "Fashn", Model = "tryon-max", Options = { ["resolution"] = "2k" } },
            [GenerationJobType.FreeCombination] = new StrategySettings { Provider = "Gemini", Model = "gemini-3-pro-image" },
            [GenerationJobType.Recolor] = new StrategySettings { Provider = "ImageWorker", Model = "opencv-lab-v1", Fallback = GenerationJobType.ProductSwap },
        },
    };

    private static GenerationStrategy Create(GenerationSettings settings)
    {
        var monitor = new StaticOptionsMonitor<GenerationSettings>(settings);
        var services = new ServiceCollection();
        services.AddSingleton(new MockImageProvider(monitor));
        services.AddKeyedSingleton<ITryOnProvider>(GenerationStrategy.MockProvider, (sp, _) => sp.GetRequiredService<MockImageProvider>());
        services.AddKeyedSingleton<IImageGenerationProvider>(GenerationStrategy.MockProvider, (sp, _) => sp.GetRequiredService<MockImageProvider>());
        return new GenerationStrategy(monitor, services.BuildServiceProvider());
    }

    [Fact]
    public void Mock_modda_yapilandirilmis_saglayici_ve_model_izlenebilirlik_icin_korunur()
    {
        var resolved = Create(Settings(useMock: true)).Resolve(GenerationJobType.OutfitSet);

        Assert.Equal(GenerationStrategy.MockProvider, resolved.Provider);
        Assert.Equal("Fashn", resolved.ConfiguredProvider);
        Assert.Equal("tryon-max", resolved.Options.Model);
        Assert.Equal("2k", resolved.Options.Parameters["resolution"]);
    }

    [Fact]
    public void Yerel_goruntu_servisi_mock_modda_da_kendi_saglayicisini_kullanir()
    {
        var resolved = Create(Settings(useMock: true)).Resolve(GenerationJobType.Recolor);

        Assert.Equal(GenerationStrategy.ImageWorkerProvider, resolved.Provider);
        Assert.Equal(GenerationJobType.ProductSwap, resolved.Fallback);
    }

    [Fact]
    public void Gercek_modda_kayitsiz_saglayici_acik_hata_verir()
    {
        var strategy = Create(Settings(useMock: false));
        var resolved = strategy.Resolve(GenerationJobType.OutfitSet);

        Assert.Equal("Fashn", resolved.Provider);
        var ex = Assert.Throws<InvalidOperationException>(() => strategy.GetTryOnProvider(resolved));
        Assert.Contains("Fashn", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Yapilandirilmamis_is_tipi_hata_verir() =>
        Assert.Throws<InvalidOperationException>(() => Create(Settings(useMock: true)).Resolve(GenerationJobType.VariantPreview));

    [Fact]
    public async Task Mock_saglayici_demo_filigranli_gorsel_uretir_ve_model_bilgisini_kaydeder()
    {
        var strategy = Create(Settings(useMock: true));
        var resolved = strategy.Resolve(GenerationJobType.FreeCombination);
        var personBytes = TestImages.Jpeg(600, 900);
        var garment = new GarmentReference(new ImageInput(TestImages.Jpeg(300, 400), "image/jpeg"), "Şalvar", "Lale Şalvar", "Bordo", 10, null);

        var result = await strategy.GetGenerationProvider(resolved)
            .ComposeOutfitAsync(new ImageInput(personBytes, "image/jpeg"), [garment], "prompt", resolved.Options, TestContext.Current.CancellationToken);

        Assert.Equal(GenerationStrategy.MockProvider, result.Provider);
        Assert.Equal("gemini-3-pro-image", result.Model);
        Assert.StartsWith("mock-compose-", result.ProviderRequestId, StringComparison.Ordinal);
        var output = result.Images.Single();
        Assert.NotEqual(personBytes, output.Content); // gerçek giydirme değil ama girdi de aynen dönmez: DEMO filigranı basılı
        using var decoded = SkiaSharp.SKBitmap.Decode(output.Content);
        Assert.Equal((600, 900), (decoded.Width, decoded.Height));
    }

    [Fact]
    public async Task Mock_hata_orani_ortak_hata_koduyla_yansir()
    {
        var settings = Settings(useMock: true);
        settings.Mock.FailureRate = 1;
        var strategy = Create(settings);
        var resolved = strategy.Resolve(GenerationJobType.OutfitSet);
        var image = new ImageInput(new byte[] { 1 }, "image/png");

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            strategy.GetTryOnProvider(resolved).TryOnAsync(image, image, resolved.Options, TestContext.Current.CancellationToken));

        Assert.Equal(GenerationErrorCode.ProviderUnavailable, ex.Code);
        Assert.True(ex.Retryable);
    }
}
