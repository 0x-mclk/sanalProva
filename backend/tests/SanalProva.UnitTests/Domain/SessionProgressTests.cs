using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Ai.Abstractions;

namespace SanalProva.UnitTests.Domain;

public sealed class SessionProgressTests
{
    [Theory]
    [InlineData(SessionStatus.Open, false, false, false, false, false, SessionStep.Consent)]
    [InlineData(SessionStatus.Open, true, false, false, false, false, SessionStep.Photo)]
    [InlineData(SessionStatus.Open, true, true, false, false, false, SessionStep.Mode)]
    [InlineData(SessionStatus.Open, true, true, true, false, false, SessionStep.Outfit)]
    [InlineData(SessionStatus.Open, true, true, true, true, false, SessionStep.Generating)]
    [InlineData(SessionStatus.Open, true, true, true, false, true, SessionStep.Result)]
    [InlineData(SessionStatus.Open, true, true, false, true, true, SessionStep.Generating)] // aktif iş sonuçtan önce gelir
    [InlineData(SessionStatus.Closed, true, true, true, false, true, SessionStep.Closed)]
    public void Bir_sonraki_adim_oturum_durumundan_cikarilir(SessionStatus status, bool consent, bool photo, bool draft, bool active, bool result, SessionStep expected) =>
        Assert.Equal(expected, SessionProgress.NextStep(status, consent, photo, draft, active, result));

    [Fact]
    public void Oturum_kodu_PR_ve_uc_hane_olur_ve_ayni_kimlik_icin_sabittir()
    {
        var id = Guid.CreateVersion7();

        Assert.Matches(@"^PR-\d{3}$", SessionProgress.DisplayCode(id));
        Assert.Equal(SessionProgress.DisplayCode(id), SessionProgress.DisplayCode(id));
    }
}

public sealed class PromptBuilderTests
{
    private static GarmentReference Garment(string category, string name, int layer, string? color = null, string? hint = null) =>
        new(new ImageInput(new byte[] { 1 }, "image/jpeg"), category, name, color, layer, hint);

    [Fact]
    public void Serbest_kombin_prompt_u_katman_sirasina_gore_urunleri_ve_ipuclarini_icerir()
    {
        var prompt = PromptBuilder.BuildFreeCombination([
            Garment("Kemer", "Altın Kemer", 40, "Altın", "Kemer belde durur."),
            Garment("Şalvar", "Lale Şalvar", 10, "Bordo"),
            Garment("Yelek", "Kadife Yelek", 20),
        ]);

        // Görsel numaraları içten dışa: şalvar → yelek → kemer
        Assert.True(prompt.IndexOf("Image 2: Şalvar", StringComparison.Ordinal) < prompt.IndexOf("Image 3: Yelek", StringComparison.Ordinal));
        Assert.True(prompt.IndexOf("Image 3: Yelek", StringComparison.Ordinal) < prompt.IndexOf("Image 4: Kemer", StringComparison.Ordinal));
        Assert.Contains("color: Bordo", prompt, StringComparison.Ordinal);
        Assert.Contains("Kemer belde durur.", prompt, StringComparison.Ordinal);
        Assert.Contains("Keep the person's face", prompt, StringComparison.Ordinal);
        Assert.Contains("Images 2 to 4", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_kullanici_metni_veya_musteri_bilgisi_almaz_yalnizca_katalog_alanlari_girer()
    {
        var prompt = PromptBuilder.BuildFreeCombination([Garment("Şalvar", "Lale Şalvar", 10)]);

        Assert.DoesNotContain("customer", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{", prompt, StringComparison.Ordinal); // doldurulmamış yer tutucu kalmaz
    }
}
