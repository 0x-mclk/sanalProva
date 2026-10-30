using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;

namespace SanalProva.Infrastructure.Credits;

/// <summary>Tarifeden çıkan bedel ve uygulanan satırın anahtarı (<c>default</c> = tarifede satır yoktu).</summary>
public readonly record struct CreditQuote(int Cost, string Key);

/// <summary>
/// Kredi tarifesi çözümleyicisi (M5.4). Tarife işin <b>kabulünde</b> bir kez okunur; sonuç işe (<c>generation_jobs.credit_cost</c>) ve defterdeki Reserve girdisine
/// (<c>tariff_key</c>) yazılır: tarife sonradan değişse bile o işin bedeli değişmez. Tarife iş tipi / model / kalite boyutlarındadır (maliyeti belirleyen boyutlar);
/// ürün veya kategori bazlı fiyat yoktur (kombin parça sayısı bile bedeli ayrı belirlemez: ticari karar bekliyor).
/// </summary>
public sealed class CreditTariff(IOptionsMonitor<CreditSettings> settings)
{
    public bool Enforce => settings.CurrentValue.Enforce;

    public CreditQuote Quote(GenerationJobType type, string? modelKey, string? quality)
    {
        var current = settings.CurrentValue;
        var candidates = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(modelKey) && !string.IsNullOrWhiteSpace(quality))
        {
            candidates.Add($"{type}:{modelKey}:{quality}");
        }

        if (!string.IsNullOrWhiteSpace(modelKey))
        {
            candidates.Add($"{type}:{modelKey}");
        }

        candidates.Add(type.ToString());
        foreach (var key in candidates)
        {
            // Yapılandırma bağlayıcısı sözlüğü büyük/küçük harf duyarlı kurabilir: anahtarı elle duyarsız ara
            var hit = current.Tariff.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (hit.Key is not null)
            {
                return new CreditQuote(Math.Clamp(hit.Value, 0, CreditSettings.MaxCostPerJob), hit.Key);
            }
        }

        return new CreditQuote(Math.Clamp(current.DefaultCostPerJob, 0, CreditSettings.MaxCostPerJob), "default");
    }

    /// <summary>Arayüzde göstermek için: varsayılan + açık tarife satırları.</summary>
    public (int DefaultCost, IReadOnlyList<KeyValuePair<string, int>> Lines) Describe()
    {
        var current = settings.CurrentValue;
        return (current.DefaultCostPerJob, current.Tariff.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList());
    }
}
