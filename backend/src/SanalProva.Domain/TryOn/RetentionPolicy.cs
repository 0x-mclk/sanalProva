namespace SanalProva.Domain.TryOn;

/// <summary>
/// Müşteri görsellerinin saklama süresi (K4). Süreler firma ayarından gelir,
/// ancak kodda sabit üst sınırlar vardır: yapılandırma hatası KVKK ihlaline dönüşemez.
/// </summary>
public static class RetentionPolicy
{
    public static readonly TimeSpan MinRetention = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxDefaultRetention = TimeSpan.FromHours(72);
    public static readonly TimeSpan MaxExtendedRetention = TimeSpan.FromDays(30);

    /// <param name="referenceTime">Oturumun kapanış zamanı, kapanmadıysa son etkinlik zamanı.</param>
    public static DateTimeOffset CalculateRetentionUntil(
        DateTimeOffset referenceTime,
        int defaultRetentionHours,
        int extendedRetentionDays,
        bool extendedRetentionConsent)
    {
        var duration = extendedRetentionConsent
            ? Clamp(TimeSpan.FromDays(extendedRetentionDays), MinRetention, MaxExtendedRetention)
            : Clamp(TimeSpan.FromHours(defaultRetentionHours), MinRetention, MaxDefaultRetention);

        return referenceTime + duration;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}
