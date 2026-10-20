// SkiaSharp 4: SKPath değiştirici yöntemleri SKPathBuilder lehine kullanımdan kalkıyor. Bu dosya yalnızca geliştirme ortamı demo görselleri
// çizer; yeni API'ye geçiş üretim kodunu etkilemez.
#pragma warning disable CS0618
using SkiaSharp;

namespace SanalProva.Api.Features.Dev;

public enum DemoGarmentKind
{
    Bindalli,
    SalvarTakimi,
    Salvar,
    Gomlek,
    Yelek,
    Kaftan,
    Kemer,
}

/// <summary>
/// Geliştirme ortamı için stilize ürün görselleri. Gerçek ürün fotoğrafı değildir: sahte katalog verisi ve ekran görüntüleri
/// için çizilir, görselin altında "Örnek görsel" yazar. Gerçek mağaza ürünleri yönetim ekranından yüklenir.
/// </summary>
public static class DemoArt
{
    private static readonly SKColor Cream = new(247, 241, 230);
    private static readonly SKColor Gold = new(184, 145, 58);
    private static readonly SKColor Skin = new(222, 190, 160);

    public static byte[] Render(DemoGarmentKind kind, SKColor main, string caption, bool back = false, int width = 900, int height = 1200)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        DrawBackground(c, width, height, main);

        var shade = Mix(main, SKColors.Black, 0.28f);
        var light = Mix(main, SKColors.White, 0.22f);
        using var fill = new SKPaint { Color = main, IsAntialias = true };
        using var dark = new SKPaint { Color = shade, IsAntialias = true };
        using var bright = new SKPaint { Color = light, IsAntialias = true };
        using var trim = new SKPaint { Color = Gold, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width * 0.006f, StrokeCap = SKStrokeCap.Round };
        using var skin = new SKPaint { Color = Skin, IsAntialias = true };
        float w = width, h = height;

        switch (kind)
        {
            case DemoGarmentKind.Bindalli:
                Mannequin(c, w, h, skin, hair: back);
                Skirt(c, w, h, fill, dark, trim, 0.40f, 0.93f, 0.20f);
                Bodice(c, w, h, fill, dark, trim, 0.25f, 0.42f);
                Sleeves(c, w, h, fill, dark, 0.26f, 0.50f);
                Ornaments(c, w, h, 0.42f, 0.92f, 0.05f, 0.25f);
                break;
            case DemoGarmentKind.SalvarTakimi:
                Mannequin(c, w, h, skin, hair: back);
                Trousers(c, w, h, fill, dark, trim, 0.50f, 0.94f, baggy: 1.0f);
                Tunic(c, w, h, fill, dark, bright, trim, 0.25f, 0.62f);
                Sleeves(c, w, h, fill, dark, 0.26f, 0.48f);
                Ornaments(c, w, h, 0.30f, 0.58f, 0.03f, 0.085f);
                break;
            case DemoGarmentKind.Salvar:
                Trousers(c, w, h, fill, dark, trim, 0.28f, 0.90f, baggy: 1.15f);
                break;
            case DemoGarmentKind.Gomlek:
                Mannequin(c, w, h, skin, hair: back, legs: false);
                Tunic(c, w, h, fill, dark, bright, trim, 0.23f, 0.58f);
                Sleeves(c, w, h, fill, dark, 0.25f, 0.56f);
                break;
            case DemoGarmentKind.Yelek:
                Mannequin(c, w, h, skin, hair: back, legs: false);
                Vest(c, w, h, fill, dark, trim, 0.24f, 0.60f);
                break;
            case DemoGarmentKind.Kaftan:
                Mannequin(c, w, h, skin, hair: back);
                Kaftan(c, w, h, fill, dark, bright, trim);
                break;
            case DemoGarmentKind.Kemer:
                Belt(c, w, h, fill, dark, trim);
                break;
        }

        DrawCaption(c, w, h, caption);
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return encoded.ToArray();
    }

    private static void DrawBackground(SKCanvas c, float w, float h, SKColor tint)
    {
        using var gradient = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h), [Mix(Cream, tint, 0.10f), Mix(Cream, tint, 0.34f)], SKShaderTileMode.Clamp),
        };
        c.DrawRect(0, 0, w, h, gradient);

        // Kemerli pencere (referans fotoğraflardaki alkov hissi)
        using var arch = new SKPath();
        arch.MoveTo(w * 0.10f, h);
        arch.LineTo(w * 0.10f, h * 0.30f);
        arch.ArcTo(new SKRect(w * 0.10f, h * 0.04f, w * 0.90f, h * 0.56f), 180, 180, false);
        arch.LineTo(w * 0.90f, h);
        arch.Close();
        using var archFill = new SKPaint { Color = new SKColor(255, 255, 255, 70), IsAntialias = true };
        using var archLine = new SKPaint { Color = new SKColor(184, 145, 58, 120), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
        c.DrawPath(arch, archFill);
        c.DrawPath(arch, archLine);

        using var floor = new SKPaint { Color = Mix(tint, SKColors.Black, 0.45f).WithAlpha(46) };
        c.DrawRect(0, h * 0.94f, w, h * 0.06f, floor);
    }

    private static void Mannequin(SKCanvas c, float w, float h, SKPaint skin, bool hair, bool legs = true)
    {
        // Yüzsüz, nötr manken: ürün odaklı, kişi taklidi yok
        c.DrawOval(w * 0.5f, h * 0.15f, w * 0.052f, h * 0.056f, skin);
        c.DrawRect(w * 0.475f, h * 0.195f, w * 0.05f, h * 0.05f, skin);
        if (hair)
        {
            using var hairPaint = new SKPaint { Color = new SKColor(70, 50, 44), IsAntialias = true };
            c.DrawOval(w * 0.5f, h * 0.14f, w * 0.056f, h * 0.056f, hairPaint);
        }

        if (legs)
        {
            c.DrawRect(w * 0.44f, h * 0.88f, w * 0.05f, h * 0.07f, skin);
            c.DrawRect(w * 0.51f, h * 0.88f, w * 0.05f, h * 0.07f, skin);
        }
    }

    private static void Bodice(SKCanvas c, float w, float h, SKPaint fill, SKPaint dark, SKPaint trim, float top, float bottom)
    {
        using var path = new SKPath();
        path.MoveTo(w * 0.40f, h * top);
        path.LineTo(w * 0.60f, h * top);
        path.LineTo(w * 0.575f, h * bottom);
        path.LineTo(w * 0.425f, h * bottom);
        path.Close();
        c.DrawPath(path, fill);
        using var neckline = new SKPath();
        neckline.MoveTo(w * 0.455f, h * top);
        neckline.QuadTo(w * 0.5f, h * (top + 0.06f), w * 0.545f, h * top);
        c.DrawPath(neckline, trim);
        c.DrawLine(w * 0.5f, h * (top + 0.04f), w * 0.5f, h * bottom, trim);
        c.DrawLine(w * 0.425f, h * bottom, w * 0.575f, h * bottom, dark);
    }

    private static void Skirt(SKCanvas c, float w, float h, SKPaint fill, SKPaint dark, SKPaint trim, float waist, float hem, float flare)
    {
        using var path = new SKPath();
        path.MoveTo(w * 0.425f, h * waist);
        path.LineTo(w * 0.575f, h * waist);
        path.CubicTo(w * 0.62f, h * 0.60f, w * (1 - flare) + 0.02f * w, h * 0.80f, w * (1 - flare), h * hem);
        path.QuadTo(w * 0.5f, h * (hem + 0.03f), w * flare, h * hem);
        path.CubicTo(w * flare - 0.02f * w, h * 0.80f, w * 0.38f, h * 0.60f, w * 0.425f, h * waist);
        path.Close();
        c.DrawPath(path, fill);

        // Kıvrım gölgeleri
        for (var i = -2; i <= 2; i++)
        {
            using var fold = new SKPath();
            fold.MoveTo(w * (0.5f + i * 0.018f), h * (waist + 0.04f));
            fold.QuadTo(w * (0.5f + i * 0.07f), h * 0.72f, w * (0.5f + i * 0.14f), h * (hem + 0.02f));
            using var foldPaint = new SKPaint { Color = dark.Color.WithAlpha(90), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w * 0.006f };
            c.DrawPath(fold, foldPaint);
        }

        using var hemLine = new SKPath();
        hemLine.MoveTo(w * flare + w * 0.004f, h * (hem - 0.015f));
        hemLine.QuadTo(w * 0.5f, h * (hem + 0.018f), w * (1 - flare) - w * 0.004f, h * (hem - 0.015f));
        c.DrawPath(hemLine, trim);
    }

    private static void Sleeves(SKCanvas c, float w, float h, SKPaint fill, SKPaint dark, float top, float bottom)
    {
        foreach (var side in new[] { -1, 1 })
        {
            using var sleeve = new SKPath();
            var x0 = w * (0.5f + side * 0.10f);
            sleeve.MoveTo(x0, h * top);
            sleeve.LineTo(x0 + side * w * 0.045f, h * top + h * 0.01f);
            sleeve.LineTo(x0 + side * w * 0.13f, h * bottom);
            sleeve.LineTo(x0 + side * w * 0.075f, h * (bottom + 0.012f));
            sleeve.Close();
            c.DrawPath(sleeve, fill);
            c.DrawLine(x0 + side * w * 0.075f, h * (bottom + 0.012f), x0 + side * w * 0.13f, h * bottom, dark);
        }
    }

    private static void Tunic(SKCanvas c, float w, float h, SKPaint fill, SKPaint dark, SKPaint bright, SKPaint trim, float top, float bottom)
    {
        using var path = new SKPath();
        path.MoveTo(w * 0.40f, h * top);
        path.LineTo(w * 0.60f, h * top);
        path.LineTo(w * 0.625f, h * bottom);
        path.QuadTo(w * 0.5f, h * (bottom + 0.025f), w * 0.375f, h * bottom);
        path.Close();
        c.DrawPath(path, fill);
        c.DrawRect(w * 0.375f, h * (bottom - 0.02f), w * 0.25f, h * 0.012f, bright);
        c.DrawLine(w * 0.5f, h * (top + 0.04f), w * 0.5f, h * (bottom - 0.02f), trim);
        c.DrawLine(w * 0.375f, h * bottom, w * 0.625f, h * bottom, dark);
    }

    private static void Vest(SKCanvas c, float w, float h, SKPaint fill, SKPaint dark, SKPaint trim, float top, float bottom)
    {
        foreach (var side in new[] { -1, 1 })
        {
            using var panel = new SKPath();
            panel.MoveTo(w * (0.5f + side * 0.095f), h * top);
            panel.LineTo(w * (0.5f + side * 0.015f), h * (top + 0.03f));
            panel.LineTo(w * (0.5f + side * 0.025f), h * bottom);
            panel.LineTo(w * (0.5f + side * 0.115f), h * bottom);
            panel.LineTo(w * (0.5f + side * 0.105f), h * (top + 0.14f));
            panel.Close();
            c.DrawPath(panel, fill);
            c.DrawPath(panel, trim);
        }

        c.DrawLine(w * 0.385f, h * (top + 0.14f), w * 0.395f, h * bottom, dark);
    }

    private static void Kaftan(SKCanvas c, float w, float h, SKPaint fill, SKPaint dark, SKPaint bright, SKPaint trim)
    {
        using var inner = new SKPaint { Color = new SKColor(40, 28, 26), IsAntialias = true };
        using var body = new SKPath();
        body.MoveTo(w * 0.40f, h * 0.24f);
        body.LineTo(w * 0.60f, h * 0.24f);
        body.LineTo(w * 0.66f, h * 0.92f);
        body.QuadTo(w * 0.5f, h * 0.95f, w * 0.34f, h * 0.92f);
        body.Close();
        c.DrawPath(body, inner);

        foreach (var side in new[] { -1, 1 })
        {
            using var panel = new SKPath();
            panel.MoveTo(w * (0.5f + side * 0.10f), h * 0.24f);
            panel.LineTo(w * (0.5f + side * 0.035f), h * 0.26f);
            panel.LineTo(w * (0.5f + side * 0.05f), h * 0.93f);
            panel.LineTo(w * (0.5f + side * 0.16f), h * 0.92f);
            panel.Close();
            c.DrawPath(panel, fill);
            c.DrawPath(panel, trim);
            using var sleeve = new SKPath();
            sleeve.MoveTo(w * (0.5f + side * 0.10f), h * 0.25f);
            sleeve.LineTo(w * (0.5f + side * 0.24f), h * 0.58f);
            sleeve.LineTo(w * (0.5f + side * 0.15f), h * 0.60f);
            sleeve.LineTo(w * (0.5f + side * 0.09f), h * 0.36f);
            sleeve.Close();
            c.DrawPath(sleeve, bright);
            c.DrawPath(sleeve, trim);
        }

        c.DrawLine(w * 0.34f, h * 0.92f, w * 0.66f, h * 0.92f, dark);
    }

    private static void Trousers(SKCanvas c, float w, float h, SKPaint fill, SKPaint dark, SKPaint trim, float waist, float ankle, float baggy)
    {
        foreach (var side in new[] { -1, 1 })
        {
            using var leg = new SKPath();
            leg.MoveTo(w * 0.5f, h * waist);
            leg.LineTo(w * (0.5f + side * 0.115f), h * waist);
            leg.CubicTo(w * (0.5f + side * 0.20f * baggy), h * (waist + 0.16f), w * (0.5f + side * 0.17f * baggy), h * (ankle - 0.08f), w * (0.5f + side * 0.075f), h * ankle);
            leg.LineTo(w * (0.5f + side * 0.005f), h * ankle);
            leg.CubicTo(w * (0.5f + side * 0.02f), h * (ankle - 0.1f), w * (0.5f + side * 0.01f), h * (waist + 0.18f), w * 0.5f, h * waist);
            leg.Close();
            c.DrawPath(leg, fill);
            c.DrawLine(w * (0.5f + side * 0.075f), h * ankle, w * (0.5f + side * 0.005f), h * ankle, trim);
            using var crease = new SKPaint { Color = dark.Color.WithAlpha(80), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w * 0.005f };
            c.DrawLine(w * (0.5f + side * 0.05f), h * (waist + 0.05f), w * (0.5f + side * 0.045f), h * (ankle - 0.03f), crease);
        }

        c.DrawRect(w * 0.385f, h * (waist - 0.012f), w * 0.23f, h * 0.022f, dark);
    }

    private static void Belt(SKCanvas c, float w, float h, SKPaint fill, SKPaint dark, SKPaint trim)
    {
        var y = h * 0.47f;
        var band = new SKRect(w * 0.08f, y - h * 0.028f, w * 0.92f, y + h * 0.028f);
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 40), IsAntialias = true, ImageFilter = SKImageFilter.CreateBlur(10, 10) };
        c.DrawRoundRect(new SKRect(band.Left, band.Top + 14, band.Right, band.Bottom + 14), 12, 12, shadow);
        c.DrawRoundRect(band, 12, 12, fill);
        c.DrawRoundRect(new SKRect(band.Left + 8, band.Top + 8, band.Right - 8, band.Bottom - 8), 8, 8, trim);

        // Toka
        using var buckle = new SKPaint { Color = Gold, IsAntialias = true };
        using var buckleDark = new SKPaint { Color = Mix(Gold, SKColors.Black, 0.35f), IsAntialias = true };
        var cx = w * 0.5f;
        c.DrawRoundRect(new SKRect(cx - w * 0.09f, y - h * 0.05f, cx + w * 0.09f, y + h * 0.05f), 14, 14, buckle);
        c.DrawRoundRect(new SKRect(cx - w * 0.06f, y - h * 0.028f, cx + w * 0.06f, y + h * 0.028f), 8, 8, buckleDark);
        c.DrawCircle(cx, y, w * 0.018f, buckle);
        for (var i = 0; i < 9; i++)
        {
            var x = w * (0.14f + i * 0.045f);
            if (Math.Abs(x - cx) < w * 0.11f)
            {
                continue;
            }

            c.DrawCircle(x, y, w * 0.009f, buckle);
            c.DrawCircle(w - x, y, w * 0.009f, buckle);
        }
    }

    /// <summary>Altın süs noktaları: satır başına genişlik <paramref name="spanTop"/>'tan <paramref name="spanBottom"/>'a büyür (kumaşın dışına taşmaz).</summary>
    private static void Ornaments(SKCanvas c, float w, float h, float top, float bottom, float spanTop, float spanBottom)
    {
        using var dot = new SKPaint { Color = Gold, IsAntialias = true };
        for (var row = 0; row < 6; row++)
        {
            var t = row / 5f;
            var y = h * (top + (bottom - top) * (0.10f + row * 0.15f));
            var count = 3 + row;
            var span = w * (spanTop + (spanBottom - spanTop) * t);
            for (var i = 0; i < count; i++)
            {
                var x = w * 0.5f - span + i * span * 2 / (count - 1);
                c.DrawCircle(x, y, w * 0.007f, dot);
            }
        }
    }

    private static void DrawCaption(SKCanvas c, float w, float h, string caption)
    {
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default, w * 0.026f);
        using var paint = new SKPaint { Color = new SKColor(42, 34, 32, 150), IsAntialias = true };
        c.DrawText(caption, w / 2, h * 0.975f, SKTextAlign.Center, font, paint);
    }

    private static SKColor Mix(SKColor a, SKColor b, float t) =>
        new((byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t), (byte)(a.Blue + (b.Blue - a.Blue) * t));
}
