using System.Diagnostics;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai.Abstractions;
using SanalProva.Infrastructure.Images;

namespace SanalProva.Infrastructure.Ai.Mock;

/// <summary>
/// Üç sağlayıcı arayüzünü de uygulayan mock. Gerçek API çağrısı yapmaz, maliyet üretmez.
/// Yapılandırılabilir gecikme ve hata oranıyla kuyruk, durum bildirimi ve TV akışının uçtan uca denenmesini sağlar.
/// Çıktı müşterinin giydirilmiş görüntüsü değildir: girdi fotoğrafı üzerine seçilen ürün kartları ve DEMO filigranı basılır.
/// </summary>
public sealed class MockImageProvider(IOptionsMonitor<GenerationSettings> settings) : ITryOnProvider, IImageGenerationProvider, IImageEditProvider
{
    public Task<ProviderResult> TryOnAsync(ImageInput person, ImageInput garment, ProviderRequestOptions options, CancellationToken ct) =>
        SimulateAsync(person, [new DemoGarment(garment.Content.ToArray(), "Takım")], options, "tryon", ct);

    public Task<ProviderResult> ComposeOutfitAsync(
        ImageInput person, IReadOnlyList<GarmentReference> garments, string prompt, ProviderRequestOptions options, CancellationToken ct) =>
        SimulateAsync(person, garments.Select(g => new DemoGarment(g.Image.Content.ToArray(), g.CategoryName)).ToList(), options, "compose", ct);

    public Task<ProviderResult> EditAsync(
        ImageInput baseImage, string instruction, IReadOnlyList<ImageInput> references, ProviderRequestOptions options, CancellationToken ct) =>
        SimulateAsync(baseImage, [], options, "edit", ct);

    private async Task<ProviderResult> SimulateAsync(ImageInput input, IReadOnlyList<DemoGarment> garments, ProviderRequestOptions options, string operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        var mock = settings.CurrentValue.Mock;
        var stopwatch = Stopwatch.StartNew();

        await Task.Delay(mock.DelayMs, ct);

#pragma warning disable CA5394 // Güvenlik amaçlı değil: yalnızca yapay hata simülasyonu
        if (mock.FailureRate > 0 && Random.Shared.NextDouble() < mock.FailureRate)
#pragma warning restore CA5394
        {
            throw new ProviderException(GenerationErrorCode.ProviderUnavailable, "Mock sağlayıcı yapay hata üretti.", retryable: true);
        }

        // Çıktı gerçek bir giydirme DEĞİLDİR: girdi fotoğrafı + ürün kartları + belirgin DEMO filigranı
        var output = new GeneratedOutput(DemoImageComposer.Compose(input.Content.ToArray(), garments), "image/jpeg");
        return new ProviderResult(
            [output],
            GenerationStrategy.MockProvider,
            options.Model,
            ProviderRequestId: $"mock-{operation}-{Guid.NewGuid():N}",
            new ProviderUsage((int)stopwatch.ElapsedMilliseconds));
    }
}
