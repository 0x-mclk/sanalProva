using Microsoft.Extensions.Options;

namespace SanalProva.Infrastructure.Credits;

/// <summary>
/// Kredi tarifesi ve kapıları (M5.4). Müşteriye satılan kredi, sağlayıcı maliyetinden (USD) AYRI bir kavramdır: burada USD yoktur.
/// <b>Ticari karar bekleyen konu:</b> gerçek kredi fiyatları belirlenmedi. Güvenli varsayılan: her ücretli (gerçek sağlayıcı) üretim <see cref="DefaultCostPerJob"/> = 1 kredi.
/// Tarife yapılandırmayla (<c>Credits:Tariff</c>) değiştirilir; kod değişikliği gerekmez.
/// </summary>
public sealed class CreditSettings
{
    public const string Section = "Credits";

    /// <summary>
    /// true iken gerçek (mock olmayan) sağlayıcıya gidecek iş kabulünde kredi aranır ve ayrılır. Varsayılan true (güvenli yön: kredisiz ücretli üretim olmaz).
    /// Mock sağlayıcı hiçbir zaman kredi tüketmez (ücret yok).
    /// </summary>
    public bool Enforce { get; set; } = true;

    /// <summary>Tarifede karşılığı olmayan iş için kredi bedeli. 0 = ücretsiz (kayıt ve rezervasyon yapılmaz).</summary>
    public int DefaultCostPerJob { get; set; } = 1;

    /// <summary>
    /// Tarife satırları: anahtar <c>{İşTipi}</c>, <c>{İşTipi}:{modelAnahtarı}</c> veya <c>{İşTipi}:{modelAnahtarı}:{kalite}</c> (ör. <c>OutfitSet:gpt-image-2.5:high</c>),
    /// değer kredi. En özel eşleşme kazanır; hiçbiri yoksa <see cref="DefaultCostPerJob"/>. Anahtarlar büyük/küçük harf duyarsızdır.
    /// </summary>
    public Dictionary<string, int> Tariff { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public const int MaxCostPerJob = 1000;
}

/// <summary>Tarife doğrulaması: sınır dışı/negatif değer uygulama açılışında reddedilir.</summary>
public sealed class CreditSettingsValidator : IValidateOptions<CreditSettings>
{
    public ValidateOptionsResult Validate(string? name, CreditSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DefaultCostPerJob is < 0 or > CreditSettings.MaxCostPerJob)
        {
            return ValidateOptionsResult.Fail($"Credits:DefaultCostPerJob 0–{CreditSettings.MaxCostPerJob} olmalı.");
        }

        foreach (var (key, cost) in options.Tariff)
        {
            if (cost is < 0 or > CreditSettings.MaxCostPerJob)
            {
                return ValidateOptionsResult.Fail($"Credits:Tariff:{key} 0–{CreditSettings.MaxCostPerJob} olmalı.");
            }
        }

        return ValidateOptionsResult.Success;
    }
}
