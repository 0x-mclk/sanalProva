using SanalProva.Domain.Generation;

namespace SanalProva.Infrastructure.Ai;

/// <summary>Bir modelin doğrulanmış özellikleri. Yalnızca <see cref="DocsVerifiedOn"/> tarihli resmî dokümana dayanır; <see cref="LiveVerified"/> gerçek çağrıyla doğrulanınca true yapılır.</summary>
public sealed record ModelInfo(
    string Provider,
    string ModelId,
    string DisplayName,
    string DocStatus,
    string DocsVerifiedOn,
    bool LiveVerified,
    bool SupportsCompose,
    bool SupportsEdit,
    string[] Resolutions,
    string Note);

/// <summary>
/// Kodun tanıdığı modeller. Listede olmayan bir model kimliği yapılandırılırsa sağlayıcı çağrılmaz (yanlış/uydurma kimlikle ücret
/// ve belirsizlik oluşmasın). <b>Hiçbir model henüz gerçek API çağrısıyla doğrulanmadı</b> (<c>LiveVerified=false</c>): arayüz ve raporlar
/// bunu çalıştığı kanıtlanmış gibi göstermez.
/// </summary>
public static class ModelCatalog
{
    private const string Verified = "2026-10-20";

    public static readonly IReadOnlyList<ModelInfo> Known =
    [
        new("Fashn", "tryon-max", "FASHN Try-On Max", "Preview (26 Ocak 2026)", Verified, false, SupportsCompose: false, SupportsEdit: false, ["1k", "2k", "4k"],
            "Tek ürün görseli + kişi fotoğrafı; hazır takım için. docs.fashn.ai/api-reference/tryon-max"),
        new("Gemini", "gemini-3-pro-image", "Nano Banana Pro", "Belirtilmemiş", Verified, false, SupportsCompose: true, SupportsEdit: true, ["1K", "2K", "4K"],
            "Çok parçalı kombin için varsayılan. 14 referansa kadar (6 nesne + 5 karakter)."),
        new("Gemini", "gemini-nano-banana-2.1", "Nano Banana 2.1", "Belirtilmemiş", Verified, false, SupportsCompose: true, SupportsEdit: true, ["1K", "2K", "4K"],
            "Revizyon için aday (K2 varsayılanı). Çok adımlı düzenleme için resmî olarak önerilen verimli model."),
        new("Gemini", "gemini-3.1-flash-image", "Nano Banana 2", "Önceki nesil", Verified, false, SupportsCompose: true, SupportsEdit: true, ["1K", "2K", "4K"],
            "Doküman yeni projelerde 2.1'i önerir; yalnızca karşılaştırma içindir."),
        new("Gemini", "gemini-3.1-flash-lite-image", "Nano Banana 2 Lite", "Belirtilmemiş", Verified, false, SupportsCompose: false, SupportsEdit: true, ["1K"],
            "En ucuz/hızlı; yalnızca 1K. Resmî doküman: çoklu referans ve çok adımlı düzenleme için optimize DEĞİL → kombin üretiminde kullanılmaz, yalnızca basit tek-referanslı revizyon adayı."),
    ];

    public static ModelInfo? Find(string provider, string modelId) =>
        Known.FirstOrDefault(m => string.Equals(m.Provider, provider, StringComparison.OrdinalIgnoreCase) && string.Equals(m.ModelId, modelId, StringComparison.Ordinal));

    /// <summary>Modelin iş tipini desteklediği doğrulanmış mı? Değilse (neden, null=uygun).</summary>
    public static string? CheckCapability(string provider, string modelId, GenerationJobType jobType)
    {
        var model = Find(provider, modelId);
        if (model is null)
        {
            return $"'{modelId}' modeli doğrulanmış model listesinde yok.";
        }

        return jobType switch
        {
            GenerationJobType.FreeCombination when !model.SupportsCompose => $"{model.DisplayName} çok parçalı kombin için uygun değil (resmî dokümana göre çoklu referans için optimize değil).",
            GenerationJobType.OutfitSet when model.Provider == "Gemini" && !model.SupportsCompose => $"{model.DisplayName} hazır takım üretimi için uygun değil.",
            _ => null,
        };
    }
}
