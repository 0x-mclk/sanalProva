using System.Globalization;
using System.Text;
using SanalProva.Infrastructure.Ai.Abstractions;

namespace SanalProva.Infrastructure.Ai;

/// <summary>
/// Prompt'u katalog metadata'sından sunucuda üretir; personel hiçbir zaman prompt yazmaz. Şablon sürümü işe kaydedilir
/// (kalite karşılaştırması için). Müşteri adı/takma adı veya serbest metin prompt'a asla girmez; yalnızca ürün/kategori alanları girer.
/// İngilizce şablon: modellerde daha tutarlı sonuç verir; ürün adları olduğu gibi kalır.
/// <para>
/// Not: Hiçbir şablon "yüzü kesin korur" garantisi vermez; modeller yüzü/pozu değiştirebilir. Bu kısıtlar yalnızca olasılığı azaltır
/// ve kalite değerlendirmesinde ölçülür (docs/11).
/// </para>
/// </summary>
public static class PromptBuilder
{
    public const string FreeCombinationVersion = "free-combination.v2";

    /// <summary>FASHN Try-On Max'e serbest metin gönderilmez (yalnızca parametreler); sürüm kaydı izlenebilirlik içindir.</summary>
    public const string OutfitSetVersion = "fashn-tryon.v1";

    public const string ProductSwapVersion = "product-swap.v1";

    public static string BuildFreeCombination(IReadOnlyList<GarmentReference> garments)
    {
        ArgumentNullException.ThrowIfNull(garments);
        var ordered = garments.OrderBy(g => g.LayerOrder).ToList();
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Image 1 is a full-body photo of a real person. Images 2 to {ordered.Count + 1} are separate product photos of real garments from a store.");
        text.AppendLine("Dress the person in Image 1 with ALL of the following garments, layered from innermost to outermost:");
        for (var i = 0; i < ordered.Count; i++)
        {
            var g = ordered[i];
            var color = string.IsNullOrWhiteSpace(g.ColorName) ? string.Empty : $", color: {g.ColorName}";
            var hint = string.IsNullOrWhiteSpace(g.AiHint) ? string.Empty : $" {g.AiHint.Trim()}";
            text.AppendLine(CultureInfo.InvariantCulture, $"- Image {i + 2}: {g.CategoryName} — {g.ProductName}{color}.{hint}");
        }

        text.AppendLine("Strict requirements:");
        text.AppendLine("- Keep the person's face, hair, skin tone, body shape, pose, hands and the background exactly the same as Image 1.");
        text.AppendLine("- Each product photo shows ONE separate garment. Use every listed garment exactly once, each in its own place on the body. Never merge, blend or duplicate garments, and never swap one garment's design onto another.");
        text.AppendLine("- Reproduce each garment's pattern, gold embroidery, ornaments, color and fabric as faithfully as possible. Do not invent new ornaments or change colors.");
        text.AppendLine("- Do not add any garments, accessories, crowns, headpieces, jewelry, bags or shoes that are not listed. Keep any visible shoes or bare feet as in Image 1.");
        text.AppendLine("- Photorealistic, natural lighting matching Image 1. Output a single full-body image with the same framing and aspect ratio as Image 1.");
        return text.ToString();
    }

    /// <summary>Tek parçayı değiştiren revizyon talimatı (Aşama 5'te kullanılacak; adapter şimdiden destekler). Görsel 1 = önceki sonuç, Görsel 2 = yeni ürün.</summary>
    public static string BuildProductSwap(string categoryName, string newProductName, string? colorName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newProductName);
        var color = string.IsNullOrWhiteSpace(colorName) ? string.Empty : $", color: {colorName}";
        var text = new StringBuilder();
        text.AppendLine("Image 1 is a try-on result. Image 2 is a product photo of a real garment.");
        text.AppendLine(CultureInfo.InvariantCulture, $"Replace ONLY the {categoryName} worn in Image 1 with the garment in Image 2 ({newProductName}{color}).");
        text.AppendLine("Everything else must stay pixel-identical in meaning: the person's face, hair, skin, body, pose, all other garments and the background.");
        text.AppendLine("Reproduce the new garment's pattern, embroidery and color faithfully. Do not add accessories or other garments. Keep the same framing and aspect ratio.");
        return text.ToString();
    }
}
