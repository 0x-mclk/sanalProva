using SanalProva.Domain.Tenancy;

namespace SanalProva.Infrastructure.Ai;

/// <summary>FASHN kalite ayarı: izin verilen değerler ve firma ayarının sunucu varsayılanının üzerine uygulanması. 4k bilinçli olarak sunulmaz (maliyet).</summary>
public static class FashnQuality
{
    public static readonly string[] Resolutions = ["1k", "2k"];
    public static readonly string[] Modes = ["fast", "balanced", "quality"];

    /// <summary>Firma ayarı varsa <c>resolution</c>/<c>generation_mode</c> üzerine yazar; yoksa yapılandırma varsayılanı kalır. Geçersiz değerler yok sayılır.</summary>
    public static IReadOnlyDictionary<string, string> Apply(IReadOnlyDictionary<string, string> parameters, OrganizationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(settings);
        var merged = new Dictionary<string, string>(parameters);
        if (settings.FashnResolution is { } resolution && Resolutions.Contains(resolution))
        {
            merged["resolution"] = resolution;
        }

        if (settings.FashnGenerationMode is { } mode && Modes.Contains(mode))
        {
            merged["generation_mode"] = mode;
        }

        return merged;
    }
}
