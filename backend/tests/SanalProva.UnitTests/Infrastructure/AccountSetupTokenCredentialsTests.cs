using System.ComponentModel.DataAnnotations;
using SanalProva.Infrastructure.Identity;

namespace SanalProva.UnitTests.Infrastructure;

public sealed class AccountSetupTokenCredentialsTests
{
    [Fact]
    public void Uretilen_tokenlar_benzersiz_ve_256_bit_url_guvenlidir()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => AccountSetupTokenCredentials.NewToken()).ToList();
        Assert.Equal(200, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tokens, t => Assert.True(t.Length >= 43)); // 256 bit, base64url, dolgu yok
        Assert.All(tokens, t => Assert.Matches("^[A-Za-z0-9_-]+$", t));
    }

    [Fact]
    public void Ozet_anahtara_baglidir_tokeni_aciga_cikarmaz_sabit_32_bayttir()
    {
        var token = AccountSetupTokenCredentials.NewToken();
        var a = AccountSetupTokenCredentials.HashToken(token, "anahtar-bir-en-az-32-karakter-uzunlugunda");
        var b = AccountSetupTokenCredentials.HashToken(token, "anahtar-bir-en-az-32-karakter-uzunlugunda");
        var c = AccountSetupTokenCredentials.HashToken(token, "farkli-anahtar-en-az-32-karakter-uzunlugunda");

        Assert.Equal(a, b); // aynı girdi + aynı anahtar → aynı özet (determinizm)
        Assert.NotEqual(a, c); // farklı anahtar → farklı özet (HMAC, salt SHA-256 değil)
        Assert.Equal(32, a.Length); // HMAC-SHA256 çıktısı
        Assert.DoesNotContain(token, Convert.ToHexStringLower(a), StringComparison.Ordinal);
    }

    [Fact]
    public void Farkli_tokenlar_farkli_ozet_uretir()
    {
        var key = "sabit-anahtar-en-az-32-karakter-uzunlugunda";
        var a = AccountSetupTokenCredentials.HashToken(AccountSetupTokenCredentials.NewToken(), key);
        var b = AccountSetupTokenCredentials.HashToken(AccountSetupTokenCredentials.NewToken(), key);
        Assert.NotEqual(a, b);
    }
}

public sealed class SetupTokenOptionsTests
{
    private static List<ValidationResult> Validate(SetupTokenOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Bos_anahtar_gecersizdir()
    {
        var results = Validate(new SetupTokenOptions { SetupTokenKey = "" });
        Assert.NotEmpty(results);
    }

    [Fact]
    public void Kisa_anahtar_gecersizdir()
    {
        var results = Validate(new SetupTokenOptions { SetupTokenKey = new string('a', 31) });
        Assert.NotEmpty(results);
    }

    [Fact]
    public void En_az_32_karakter_anahtar_gecerlidir()
    {
        var results = Validate(new SetupTokenOptions { SetupTokenKey = new string('a', 32) });
        Assert.Empty(results);
    }
}
