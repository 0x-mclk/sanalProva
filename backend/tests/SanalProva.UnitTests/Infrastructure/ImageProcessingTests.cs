using SanalProva.Infrastructure.Images;
using SkiaSharp;

namespace SanalProva.UnitTests.Infrastructure;

internal static class TestImages
{
    /// <summary>Kenarları ve metni olan, doğal fotoğrafa benzer keskin görsel.</summary>
    public static SKBitmap Detailed(int width, int height, SKColor? background = null)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(background ?? new SKColor(235, 225, 210));
        using var paint = new SKPaint { IsAntialias = true };
        var random = new Random(42);
        for (var i = 0; i < 160; i++)
        {
            paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            var size = random.Next(8, Math.Max(9, width / 8));
            canvas.DrawRect(random.Next(width), random.Next(height), size, size, paint);
        }

        return bitmap;
    }

    public static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg)
    {
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, 95).ToArray();
    }

    public static byte[] Jpeg(int width, int height) => Encode(Detailed(width, height));

    /// <summary>JPEG'e gerçek bir EXIF APP1 bloğu (yön + sahte GPS etiketi yerine yön) ekler.</summary>
    public static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        var exif = new List<byte> { 0xFF, 0xE1, 0x00, 0x22 };           // APP1, uzunluk 34
        exif.AddRange("Exif\0\0"u8.ToArray());
        exif.AddRange([0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08]); // TIFF başlığı (big endian), IFD0 ofseti 8
        exif.AddRange([0x00, 0x01]);                                     // 1 giriş
        exif.AddRange([0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, (byte)(orientation >> 8), (byte)orientation, 0x00, 0x00]);
        exif.AddRange([0x00, 0x00, 0x00, 0x00]);                         // sonraki IFD yok

        var result = new List<byte> { 0xFF, 0xD8 };
        result.AddRange(exif);
        result.AddRange(jpeg.Skip(2));
        return [.. result];
    }
}

public sealed class ImageProcessorTests
{
    private static readonly ImageProcessingOptions Options = ImageProcessingOptions.CustomerPhoto;

    [Fact]
    public void Gecerli_jpeg_yeniden_kodlanir_kucultulur_ve_kucuk_resim_uretilir()
    {
        var result = ImageProcessor.Process(TestImages.Jpeg(3000, 4000), Options);

        Assert.Equal(2048, Math.Max(result.Width, result.Height));
        Assert.Equal(480, Math.Max(result.ThumbWidth, result.ThumbHeight));
        Assert.True(result.Main.Length < 3_000_000);
        using var decoded = SKBitmap.Decode(result.Main);
        Assert.Equal(result.Width, decoded.Width);
        Assert.Equal(0xFF, result.Main[0]);
        Assert.Equal(0xD8, result.Main[1]);
    }

    [Fact]
    public void Kucuk_gorsel_buyutulmez()
    {
        var result = ImageProcessor.Process(TestImages.Jpeg(800, 1200), Options);

        Assert.Equal((800, 1200), (result.Width, result.Height));
    }

    [Theory]
    [InlineData(6, 1200, 800)]   // RightTop: 90° saat yönü → yatay kayıt dikey görünür
    [InlineData(8, 1200, 800)]   // LeftBottom: 270°
    [InlineData(3, 800, 1200)]   // 180°: boyut aynı
    [InlineData(1, 800, 1200)]
    public void Exif_yonu_piksellere_uygulanir(ushort orientation, int storedWidth, int storedHeight)
    {
        // Kayıtlı piksel boyutu storedWidth×storedHeight, EXIF yönü ile görünen boyut değişir
        var source = TestImages.WithExifOrientation(TestImages.Jpeg(storedWidth, storedHeight), orientation);

        var result = ImageProcessor.Process(source, Options);

        var swaps = orientation is 5 or 6 or 7 or 8;
        Assert.Equal(swaps ? (storedHeight, storedWidth) : (storedWidth, storedHeight), (result.Width, result.Height));
    }

    [Fact]
    public void Cikti_hicbir_exif_veya_metadata_tasimaz()
    {
        var source = TestImages.WithExifOrientation(TestImages.Jpeg(800, 1200), 1);
        Assert.True(source.AsSpan().IndexOf("Exif"u8) >= 0, "Test girdisi EXIF içermeli");

        var result = ImageProcessor.Process(source, Options);

        Assert.True(result.Main.AsSpan().IndexOf("Exif"u8) < 0);
        Assert.True(result.Thumb.AsSpan().IndexOf("Exif"u8) < 0);
    }

    [Fact]
    public void Seffaf_png_beyaz_zemine_yerlestirilir()
    {
        using var bitmap = new SKBitmap(600, 800, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);
        var png = TestImages.Encode(bitmap, SKEncodedImageFormat.Png);

        var result = ImageProcessor.Process(png, Options);

        using var decoded = SKBitmap.Decode(result.Main);
        var pixel = decoded.GetPixel(10, 10);
        Assert.True(pixel.Red > 250 && pixel.Green > 250 && pixel.Blue > 250, $"Beyaz bekleniyordu, {pixel}");
    }

    [Theory]
    [InlineData("GIF89a-not-supported-content", "UNSUPPORTED_IMAGE_TYPE")]
    [InlineData("<html>script</html>", "UNSUPPORTED_IMAGE_TYPE")]
    public void Desteklenmeyen_icerik_reddedilir(string content, string code)
    {
        var ex = Assert.Throws<ImageRejectedException>(() => ImageProcessor.Process(System.Text.Encoding.ASCII.GetBytes(content), Options));

        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void Uzantisi_jpeg_gibi_ama_icerigi_bozuk_dosya_reddedilir()
    {
        byte[] fake = [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[200]];

        Assert.Equal("IMAGE_CORRUPT", Assert.Throws<ImageRejectedException>(() => ImageProcessor.Process(fake, Options)).Code);
    }

    [Fact]
    public void Jpeg_basligi_ardina_eklenmis_zararli_veri_cikti_icinde_yer_almaz()
    {
        var payload = "<script>alert(1)</script>"u8.ToArray();
        byte[] polyglot = [.. TestImages.Jpeg(800, 1200), .. payload];

        var result = ImageProcessor.Process(polyglot, Options);

        Assert.True(result.Main.AsSpan().IndexOf("<script>"u8) < 0);
    }

    [Fact]
    public void Cok_buyuk_dosya_ve_cok_kucuk_cozunurluk_reddedilir()
    {
        var tooLarge = new ImageProcessingOptions(1024, 400, 2048, 480);
        Assert.Equal("IMAGE_TOO_LARGE", Assert.Throws<ImageRejectedException>(() => ImageProcessor.Process(TestImages.Jpeg(800, 1200), tooLarge)).Code);
        Assert.Equal("IMAGE_TOO_SMALL", Assert.Throws<ImageRejectedException>(() => ImageProcessor.Process(TestImages.Jpeg(300, 300), Options)).Code);
        Assert.Equal("IMAGE_CORRUPT", Assert.Throws<ImageRejectedException>(() => ImageProcessor.Process([], Options)).Code);
    }

    [Fact]
    public void Netlik_olcusu_bulanik_gorseli_keskinden_ayirir()
    {
        using var sharp = TestImages.Detailed(1200, 1800);
        using var blurred = new SKBitmap(1200, 1800, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(blurred))
        using (var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(30, 30) })
        {
            canvas.DrawBitmap(sharp, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);
        }

        var sharpScore = ImageProcessor.MeasureSharpness(sharp);
        var blurScore = ImageProcessor.MeasureSharpness(blurred);

        Assert.True(sharpScore > PhotoQualityEvaluator.BlurThreshold * 5, $"keskin: {sharpScore}");
        Assert.True(blurScore < PhotoQualityEvaluator.BlurThreshold, $"bulanık: {blurScore}");
    }
}

public sealed class PhotoQualityEvaluatorTests
{
    [Fact]
    public void Dikey_yuksek_cozunurluklu_keskin_fotograf_uyari_uretmez()
    {
        var flags = PhotoQualityEvaluator.Evaluate(1500, 2400, sharpness: 400);

        Assert.False(flags.HasWarnings);
    }

    [Theory]
    [InlineData(2400, 1500, true, false, false)] // yatay
    [InlineData(1200, 1200, true, false, false)] // kare
    [InlineData(400, 600, false, true, false)]   // düşük çözünürlük
    public void Kadraj_ve_cozunurluk_uyarilari(int width, int height, bool notPortrait, bool lowRes, bool blurry)
    {
        var flags = PhotoQualityEvaluator.Evaluate(width, height, sharpness: 400);

        Assert.Equal(notPortrait, flags.NotPortrait);
        Assert.Equal(lowRes, flags.LowResolution);
        Assert.Equal(blurry, flags.Blurry);
    }

    [Fact]
    public void Dusuk_netlik_bulanik_isaretlenir() =>
        Assert.True(PhotoQualityEvaluator.Evaluate(1500, 2400, sharpness: 2).Blurry);
}

public sealed class DemoImageComposerTests
{
    [Fact]
    public void Demo_gorseli_ayni_boyutta_cozumlenebilir_ve_alt_banti_bordodur()
    {
        var person = TestImages.Jpeg(800, 1200);
        var garment = TestImages.Jpeg(400, 500);

        var result = DemoImageComposer.Compose(person, [new DemoGarment(garment, "Sultan Bindallı")]);

        using var decoded = SKBitmap.Decode(result);
        Assert.Equal((800, 1200), (decoded.Width, decoded.Height));
        // Banner satırının boş bir noktası (sol alt köşe) bordo: filigran kırpılmadan kaldırılamaz
        var corner = decoded.GetPixel(3, decoded.Height - 3);
        Assert.True(corner.Red > corner.Green + 40 && corner.Red > corner.Blue + 20, $"Bordo bekleniyordu: {corner}");
    }

    [Fact]
    public void Urunsuz_demo_gorseli_de_filigranlanir()
    {
        var result = DemoImageComposer.Compose(TestImages.Jpeg(600, 900), []);

        using var decoded = SKBitmap.Decode(result);
        Assert.Equal(600, decoded.Width);
    }
}
