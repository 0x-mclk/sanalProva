using SanalProva.Domain.TryOn;

namespace SanalProva.UnitTests.Domain;

public sealed class RetentionPolicyTests
{
    private static readonly DateTimeOffset Reference = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Varsayilan_saklama_24_saattir() =>
        Assert.Equal(Reference.AddHours(24), RetentionPolicy.CalculateRetentionUntil(Reference, 24, 30, extendedRetentionConsent: false));

    [Fact]
    public void Ek_onayla_uzatilmis_saklama_uygulanir() =>
        Assert.Equal(Reference.AddDays(30), RetentionPolicy.CalculateRetentionUntil(Reference, 24, 30, extendedRetentionConsent: true));

    [Theory]
    [InlineData(1000, false, 72)]   // varsayılan süre 72 saati aşamaz
    [InlineData(0, false, 1)]       // en az 1 saat
    public void Varsayilan_sure_kod_sinirlariyla_kirpilir(int configuredHours, bool extended, int expectedHours) =>
        Assert.Equal(Reference.AddHours(expectedHours), RetentionPolicy.CalculateRetentionUntil(Reference, configuredHours, 30, extended));

    [Fact]
    public void Uzatilmis_sure_30_gunu_asamaz() =>
        Assert.Equal(Reference.AddDays(30), RetentionPolicy.CalculateRetentionUntil(Reference, 24, 365, extendedRetentionConsent: true));
}
