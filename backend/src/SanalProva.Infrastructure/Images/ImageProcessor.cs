using SanalProva.Domain.TryOn;
using SkiaSharp;

namespace SanalProva.Infrastructure.Images;

/// <param name="MaxBytes">Yüklenen dosyanın en büyük boyutu.</param>
/// <param name="MinShortSide">Kısa kenar bunun altındaysa görsel reddedilir.</param>
/// <param name="MaxLongSide">Uzun kenar bu değeri aşarsa küçültülür.</param>
/// <param name="ThumbLongSide">Küçük resmin uzun kenarı.</param>
public sealed record ImageProcessingOptions(int MaxBytes, int MinShortSide, int MaxLongSide, int ThumbLongSide, int JpegQuality = 90)
{
    /// <summary>Müşteri fotoğrafı: büyük, boydan çekim için yeterli çözünürlük.</summary>
    public static readonly ImageProcessingOptions CustomerPhoto = new(15 * 1024 * 1024, 400, 2048, 480);

    /// <summary>Katalog ürün görseli.</summary>
    public static readonly ImageProcessingOptions CatalogImage = new(10 * 1024 * 1024, 200, 1600, 480);

    /// <summary>Sağlayıcıdan (veya mock'tan) dönen sonuç. Boyut denetimi gevşek, yalnızca bozuk çıktıyı eler.</summary>
    public static readonly ImageProcessingOptions GeneratedResult = new(30 * 1024 * 1024, 64, 2048, 480);
}

public sealed record ProcessedImage(
    byte[] Main, int Width, int Height,
    byte[] Thumb, int ThumbWidth, int ThumbHeight,
    double Sharpness);

/// <summary>Reddedilen yükleme. <see cref="Code"/> API'de ProblemDetails kodu olarak döner.</summary>
public sealed class ImageRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Yüklenen görseli güvene alır: dosya türünü içeriğinden (magic byte) doğrular, boyut ve çözünürlük sınırlarını uygular,
/// EXIF yönünü uygulayıp TÜM metadata'yı (konum dahil) siler, JPEG olarak yeniden kodlar (polyglot/gömülü içerik etkisizleşir)
/// ve küçük resim üretir. Şeffaf görseller beyaz zemine yerleştirilir.
/// </summary>
public static class ImageProcessor
{
    private const int MaxSourceSide = 12_000;
    private const long MaxSourcePixels = 60_000_000;

    public static ProcessedImage Process(ReadOnlySpan<byte> data, ImageProcessingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (data.Length == 0)
        {
            throw new ImageRejectedException("IMAGE_CORRUPT", "Dosya boş.");
        }

        if (data.Length > options.MaxBytes)
        {
            throw new ImageRejectedException("IMAGE_TOO_LARGE", $"Dosya {options.MaxBytes / (1024 * 1024)} MB sınırını aşıyor.");
        }

        if (!HasSupportedSignature(data))
        {
            throw new ImageRejectedException("UNSUPPORTED_IMAGE_TYPE", "Yalnızca JPEG, PNG veya WebP görseller yüklenebilir.");
        }

        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData) ?? throw new ImageRejectedException("IMAGE_CORRUPT", "Görsel okunamadı.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || info.Width > MaxSourceSide || info.Height > MaxSourceSide || (long)info.Width * info.Height > MaxSourcePixels)
        {
            throw new ImageRejectedException("IMAGE_DIMENSIONS_TOO_LARGE", "Görselin çözünürlüğü çok yüksek.");
        }

        using var decoded = SKBitmap.Decode(codec) ?? throw new ImageRejectedException("IMAGE_CORRUPT", "Görsel çözümlenemedi.");
        using var oriented = ApplyOrigin(decoded, codec.EncodedOrigin);

        if (Math.Min(oriented.Width, oriented.Height) < options.MinShortSide)
        {
            throw new ImageRejectedException("IMAGE_TOO_SMALL", "Görselin çözünürlüğü çok düşük.");
        }

        using var flat = FlattenOnWhite(oriented);
        using var main = ResizeToLongSide(flat, options.MaxLongSide);
        using var thumb = ResizeToLongSide(main, options.ThumbLongSide);

        return new ProcessedImage(
            EncodeJpeg(main, options.JpegQuality), main.Width, main.Height,
            EncodeJpeg(thumb, 80), thumb.Width, thumb.Height,
            MeasureSharpness(main));
    }

    private static bool HasSupportedSignature(ReadOnlySpan<byte> d) =>
        (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) ||                                  // JPEG
        (d.Length > 8 && d[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) || // PNG
        (d.Length > 12 && d[..4].SequenceEqual("RIFF"u8) && d[8..12].SequenceEqual("WEBP"u8));            // WebP

    /// <summary>EXIF yönünü piksellere uygular (telefon fotoğrafları yan yatık gelmesin).</summary>
    private static SKBitmap ApplyOrigin(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return source.Copy();
        }

        float w = source.Width, h = source.Height;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var target = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height, source.ColorType, source.AlphaType);

        // Kaynak (x,y) → hedef (x',y'): x' = a·x + b·y + tx, y' = c·x + d·y + ty
        var m = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };

        using var canvas = new SKCanvas(target);
        canvas.SetMatrix(m);
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        return target;
    }

    private static SKBitmap FlattenOnWhite(SKBitmap source)
    {
        var target = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        return target;
    }

    private static SKBitmap ResizeToLongSide(SKBitmap source, int longSide)
    {
        var current = Math.Max(source.Width, source.Height);
        if (current <= longSide)
        {
            return source.Copy();
        }

        var scale = (double)longSide / current;
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)), SKColorType.Rgba8888, SKAlphaType.Premul);
        return source.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new ImageRejectedException("IMAGE_CORRUPT", "Görsel yeniden boyutlandırılamadı.");
    }

    internal static byte[] EncodeJpeg(SKBitmap bitmap, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality)
            ?? throw new ImageRejectedException("IMAGE_CORRUPT", "Görsel kodlanamadı.");
        return encoded.ToArray();
    }

    /// <summary>
    /// Netlik ölçüsü: 320 piksel genişliğe indirilmiş parlaklık görüntüsünde Laplace varyansı.
    /// Büyük değer = keskin. Küçültme sensör gürültüsünü bastırır, yalnızca gerçek kenar bilgisini ölçer.
    /// </summary>
    internal static double MeasureSharpness(SKBitmap source)
    {
        var width = Math.Min(320, source.Width);
        var height = Math.Max(3, (int)Math.Round((double)source.Height * width / source.Width));
        using var small = source.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (small is null || width < 3)
        {
            return 0;
        }

        var pixels = small.GetPixelSpan();
        var luma = new double[width * height];
        for (var i = 0; i < luma.Length; i++)
        {
            var p = i * 4;
            luma[i] = 0.299 * pixels[p] + 0.587 * pixels[p + 1] + 0.114 * pixels[p + 2];
        }

        double sum = 0, sumSquares = 0;
        var count = 0;
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var i = y * width + x;
                var laplacian = 4 * luma[i] - luma[i - 1] - luma[i + 1] - luma[i - width] - luma[i + width];
                sum += laplacian;
                sumSquares += laplacian * laplacian;
                count++;
            }
        }

        var mean = sum / count;
        return sumSquares / count - mean * mean;
    }
}

/// <summary>Boydan müşteri fotoğrafı için basit kadraj sezgileri. Kişi sayısı veya tam boy tespiti YAPMAZ.</summary>
public static class PhotoQualityEvaluator
{
    /// <summary>Bu değerin altı bulanık sayılır (320 px genişlikte Laplace varyansı; sentetik ve örnek görsellerle kalibre edildi).</summary>
    public const double BlurThreshold = 12;

    public static PhotoQualityFlags Evaluate(int width, int height, double sharpness) => new()
    {
        Blurry = sharpness < BlurThreshold,
        LowResolution = Math.Min(width, height) < 600 || Math.Max(width, height) < 1000,
        NotPortrait = height < width * 1.25,
    };
}
