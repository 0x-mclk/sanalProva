using Microsoft.Extensions.Configuration;
using SanalProva.Domain.Catalog;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai.Abstractions;
using SanalProva.Infrastructure.ImageWorker;

namespace SanalProva.IntegrationTests;

/// <summary>
/// C# istemcisi ile Python image-worker arasındaki sözleşme testi (JSON alan adları, hata eşleme).
/// Servis 127.0.0.1:8090'da çalışmıyorsa atlanır: ./scripts/image-worker.ps1
/// </summary>
public sealed class ImageWorkerContractTests : IDisposable
{
    // 4×8 piksel gri PNG (OpenCV ile üretildi ve doğrulandı)
    private static readonly byte[] SmallPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAICAAAAAD7WAHZAAAAG0lEQVQIHQXBAQEAAACCIHze9KChoaGhoaGh4VbcBAm5aZ0ZAAAAAElFTkSuQmCC");

    private readonly HttpClient _http;
    private readonly ImageWorkerClient _client;

    public ImageWorkerContractTests()
    {
        var secrets = new ConfigurationBuilder().AddUserSecrets(typeof(Program).Assembly, optional: true).Build();
        _http = new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("SANALPROVA_TEST_WORKER") ?? "http://127.0.0.1:8090/") };
        _http.DefaultRequestHeaders.Add(ImageWorkerClient.InternalKeyHeader, secrets["ImageWorker:InternalKey"] ?? string.Empty);
        _client = new ImageWorkerClient(_http);
    }

    private async Task RequireWorkerAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _client.GetHealthAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            Assert.Skip("image-worker çalışmıyor (./scripts/image-worker.ps1).");
        }
    }

    [Fact]
    public async Task Health_sozlesmesi_eslesir()
    {
        await RequireWorkerAsync();

        var health = await _client.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ok", health.Status);
        Assert.Equal("mock", health.Segmentation);
        Assert.StartsWith("4.", health.OpenCvVersion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Segmentasyon_istegi_ve_yaniti_sozlesmeyle_eslesir()
    {
        await RequireWorkerAsync();

        var result = await _client.SegmentAsync(
            new ImageInput(SmallPng, "image/png"),
            new SegmentationTarget(BodyZone.Lower, "salvar", [new PointPrompt(0.5, 0.7)]),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.MaskPng);
        Assert.Equal(0.0, result.Confidence); // mock maske asla güvenilir sayılmaz
        Assert.InRange(result.AreaRatio, 0.0, 1.0);
    }

    [Fact]
    public async Task Henuz_uygulanmayan_renk_degisimi_tekrar_denenmeyen_hataya_esler()
    {
        await RequireWorkerAsync();

        var ex = await Assert.ThrowsAsync<ProviderException>(() => _client.RecolorAsync(
            new ImageInput(SmallPng, "image/png"), SmallPng, "#F5F0E6", TestContext.Current.CancellationToken));

        Assert.Equal(GenerationErrorCode.ProviderUnavailable, ex.Code);
        Assert.False(ex.Retryable);
    }

    public void Dispose() => _http.Dispose();
}
