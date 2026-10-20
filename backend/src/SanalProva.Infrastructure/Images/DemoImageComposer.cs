using SkiaSharp;

namespace SanalProva.Infrastructure.Images;

public sealed record DemoGarment(byte[] Image, string Label);

/// <summary>
/// Mock sağlayıcının sonucu: müşterinin fotoğrafı üzerine seçilen ürünlerin küçük kartları ve belirgin DEMO filigranı.
/// Çıktı müşterinin giydirilmiş görüntüsü DEĞİLDİR ve öyle görünmemelidir; filigran kırpılarak kaldırılamayacak şekilde
/// çapraz ve alt bantta tekrarlanır.
/// </summary>
public static class DemoImageComposer
{
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public const string BannerText = "DEMO · Gerçek AI çıktısı değildir";

    public static byte[] Compose(byte[] personImage, IReadOnlyList<DemoGarment> garments)
    {
        ArgumentNullException.ThrowIfNull(personImage);
        ArgumentNullException.ThrowIfNull(garments);

        using var person = SKBitmap.Decode(personImage) ?? throw new InvalidOperationException("Mock girdi görseli çözümlenemedi.");
        using var surface = SKSurface.Create(new SKImageInfo(person.Width, person.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.DrawBitmap(person, 0, 0, Sampling);

        var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        DrawDiagonalWatermark(canvas, person.Width, person.Height, typeface);
        DrawGarmentTiles(canvas, person.Width, person.Height, garments, typeface);
        DrawBanner(canvas, person.Width, person.Height, typeface);

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return encoded.ToArray();
    }

    private static void DrawDiagonalWatermark(SKCanvas canvas, int width, int height, SKTypeface typeface)
    {
        using var font = new SKFont(typeface, Math.Max(width, height) * 0.11f);
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, 70), IsAntialias = true };
        using var outline = new SKPaint { Color = new SKColor(122, 31, 58, 60), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };

        canvas.Save();
        canvas.RotateDegrees(-28, width / 2f, height / 2f);
        var step = font.Size * 2.2f;
        for (var y = -height; y < height * 2; y += (int)step)
        {
            for (var x = -width; x < width * 2; x += (int)(step * 1.7f))
            {
                canvas.DrawText("DEMO", x, y, SKTextAlign.Left, font, paint);
                canvas.DrawText("DEMO", x, y, SKTextAlign.Left, font, outline);
            }
        }

        canvas.Restore();
    }

    private static void DrawGarmentTiles(SKCanvas canvas, int width, int height, IReadOnlyList<DemoGarment> garments, SKTypeface typeface)
    {
        if (garments.Count == 0)
        {
            return;
        }

        var tileWidth = width * 0.22f;
        var tileHeight = tileWidth * 1.25f;
        var gap = width * 0.015f;
        var captionSize = Math.Max(10f, width * 0.026f);
        using var captionFont = new SKFont(typeface, captionSize);
        using var border = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var captionPaint = new SKPaint { Color = new SKColor(42, 34, 32), IsAntialias = true };
        using var captionBg = new SKPaint { Color = new SKColor(255, 255, 255, 235), IsAntialias = true };

        var bannerHeight = height * 0.055f;
        var x = gap * 2;
        var y = height - bannerHeight - gap * 2 - tileHeight - captionSize * 1.6f;
        foreach (var garment in garments.Take(4))
        {
            using var bitmap = SKBitmap.Decode(garment.Image);
            if (bitmap is null)
            {
                continue;
            }

            var frame = new SKRect(x, y, x + tileWidth, y + tileHeight + captionSize * 1.6f);
            canvas.DrawRoundRect(frame, 8, 8, border);
            var imageRect = new SKRect(frame.Left + 4, frame.Top + 4, frame.Right - 4, frame.Top + tileHeight);
            canvas.Save();
            canvas.ClipRoundRect(new SKRoundRect(imageRect, 5, 5), antialias: true);
            canvas.DrawBitmap(bitmap, FitCover(bitmap, imageRect), Sampling);
            canvas.Restore();

            var label = Truncate(garment.Label, captionFont, tileWidth - 8);
            canvas.DrawText(label, frame.MidX, frame.Bottom - captionSize * 0.45f, SKTextAlign.Center, captionFont, captionPaint);
            x += tileWidth + gap;
            if (x + tileWidth > width)
            {
                break;
            }
        }
    }

    private static void DrawBanner(SKCanvas canvas, int width, int height, SKTypeface typeface)
    {
        var bannerHeight = height * 0.055f;
        using var bg = new SKPaint { Color = new SKColor(122, 31, 58, 235) };
        canvas.DrawRect(0, height - bannerHeight, width, bannerHeight, bg);
        using var font = new SKFont(typeface, bannerHeight * 0.5f);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawText(BannerText, width / 2f, height - bannerHeight * 0.28f, SKTextAlign.Center, font, text);
    }

    private static SKRect FitCover(SKBitmap bitmap, SKRect target)
    {
        var scale = Math.Max(target.Width / bitmap.Width, target.Height / bitmap.Height);
        var w = bitmap.Width * scale;
        var h = bitmap.Height * scale;
        return new SKRect(target.MidX - w / 2, target.MidY - h / 2, target.MidX + w / 2, target.MidY + h / 2);
    }

    private static string Truncate(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth)
        {
            return text;
        }

        while (text.Length > 1 && font.MeasureText(text + "…") > maxWidth)
        {
            text = text[..^1];
        }

        return text + "…";
    }
}
