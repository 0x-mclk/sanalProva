using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai.Abstractions;
using SkiaSharp;

namespace SanalProva.Infrastructure.Ai;

/// <summary>
/// Sağlayıcı çıktısının temel makullük denetimi (docs/07 §6): çözümlenebilir, düz/siyah değil, çerçeve girdi fotoğrafıyla uyumlu.
/// Bu denetim yüz/poz/nakış doğruluğunu <b>ölçmez</b> ve garanti etmez; o kalite değerlendirmesi insan gözüyle yapılır (docs/11).
/// Başarısız çıktı yeniden denenmez: sağlayıcı ücreti zaten doğmuş olabilir, yeniden deneme çift ücrettir. Personel isterse elle tekrar başlatır.
/// </summary>
public static class OutputValidator
{
    private const double MaxAspectDeviation = 0.25;
    private const double MinLuminanceDeviation = 4.0;

    public static void Validate(GeneratedOutput output, ImageInput person)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(person);
        if (output.Content.Length == 0)
        {
            throw Invalid("Sağlayıcı boş görsel döndürdü.");
        }

        SKBitmap? decoded;
        try
        {
            decoded = SKBitmap.Decode(output.Content);
        }
        catch (ArgumentException)
        {
            decoded = null; // bozuk/kesik veri
        }

        using var bitmap = decoded;
        if (decoded is null || decoded.Width < 64 || decoded.Height < 64)
        {
            throw Invalid("Üretilen görsel çözümlenemedi.");
        }

        if (IsFlat(decoded))
        {
            throw Invalid("Üretilen görsel boş veya tek renk.");
        }

        using var personData = SKData.CreateCopy(person.Content.Span);
        using var codec = SKCodec.Create(personData);
        if (codec is { Info.Height: > 0 })
        {
            var expected = (double)codec.Info.Width / codec.Info.Height;
            var actual = (double)decoded.Width / decoded.Height;
            if (Math.Abs(actual - expected) / expected > MaxAspectDeviation)
            {
                throw Invalid("Üretilen görselin çerçevesi fotoğrafla uyuşmuyor.");
            }
        }
    }

    /// <summary>Izgara örneklemesiyle parlaklık standart sapması: neredeyse sabit görsel (siyah/beyaz/düz) reddedilir.</summary>
    internal static bool IsFlat(SKBitmap bitmap)
    {
        const int grid = 24;
        double sum = 0, sumSquares = 0;
        var count = 0;
        for (var gy = 0; gy < grid; gy++)
        {
            for (var gx = 0; gx < grid; gx++)
            {
                var c = bitmap.GetPixel((gx * bitmap.Width / grid) + bitmap.Width / (2 * grid), (gy * bitmap.Height / grid) + bitmap.Height / (2 * grid));
                var luminance = (0.299 * c.Red) + (0.587 * c.Green) + (0.114 * c.Blue);
                sum += luminance;
                sumSquares += luminance * luminance;
                count++;
            }
        }

        var mean = sum / count;
        return Math.Sqrt(Math.Max(0, (sumSquares / count) - (mean * mean))) < MinLuminanceDeviation;
    }

    private static ProviderException Invalid(string message) => new(GenerationErrorCode.InvalidOutput, message, retryable: false);
}
