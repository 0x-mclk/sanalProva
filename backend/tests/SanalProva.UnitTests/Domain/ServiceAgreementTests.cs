using System.Security.Cryptography;
using System.Text;
using SanalProva.Domain.Tenancy;

namespace SanalProva.UnitTests.Domain;

public sealed class ServiceAgreementTests
{
    // Metin değişirse BU TEST KIRILIR: önce ServiceAgreement.Version artırılmalı (mevcut kabuller eski sürüme ait kalır, firmalardan yeniden kabul istenir),
    // sonra buradaki sürüm ve özet bilinçli olarak güncellenmelidir. Metni sürümü artırmadan değiştirmek kabul kayıtlarını yanıltır.
    private const string PinnedVersion = "2026-11-v1";
    private const string PinnedSha256 = "5816914adf826385eed54c0064de502148919943ffe6ee5a1b90e9cb21cb30d7";

    [Fact]
    public void Metin_ozeti_sabitlenmistir_metin_surum_artirilmadan_degistirilemez()
    {
        Assert.Equal(PinnedVersion, ServiceAgreement.Document.Version);
        Assert.Equal(PinnedSha256, ServiceAgreement.Document.TextSha256Hex);
    }

    [Fact]
    public void Ozet_normalize_edilmis_metnin_UTF8_SHA256_sidir_satir_sonu_farki_etkilemez()
    {
        var doc = ServiceAgreement.Document;
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(doc.Text))), doc.TextSha256Hex);
        Assert.Matches("^[0-9a-f]{64}$", doc.TextSha256Hex);
        Assert.DoesNotContain('\r', doc.Text);

        var crlf = new ServiceAgreementDocument("v", "t", "a\r\nb\r\nc");
        var lf = new ServiceAgreementDocument("v", "t", "a\nb\nc");
        Assert.Equal(lf.TextSha256Hex, crlf.TextSha256Hex);
        Assert.NotEqual(lf.TextSha256Hex, new ServiceAgreementDocument("v", "t", "a\nb\nd").TextSha256Hex);
    }

    [Fact]
    public void Taslak_oldugu_metinde_ve_basliktada_acikca_yazar()
    {
        Assert.Contains("TASLAK", ServiceAgreement.Document.Title, StringComparison.Ordinal);
        Assert.Contains("hukuki incelemeden geçmemiştir", ServiceAgreement.Document.Text, StringComparison.Ordinal);
    }
}
