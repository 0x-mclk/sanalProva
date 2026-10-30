using System.Text.Json;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Generation;

namespace SanalProva.UnitTests.Domain;

public sealed class PaidAiPolicyTests
{
    [Fact]
    public void Yeni_firma_ayari_ucretli_AI_kapali_baslar()
    {
        Assert.False(new OrganizationSettings().PaidAiEnabled);
        Assert.False(PaidAiPolicy.IsEnabled(new OrganizationSettings()));
    }

    [Fact]
    public void Ayar_yoksa_veya_eksikse_kapali_sayilir()
    {
        Assert.False(PaidAiPolicy.IsEnabled(null)); // fail-closed
        Assert.False(JsonSerializer.Deserialize<OrganizationSettings>("{}")!.PaidAiEnabled); // eski kayıtlarda alan yok
        Assert.True(JsonSerializer.Deserialize<OrganizationSettings>("{\"PaidAiEnabled\":true}")!.PaidAiEnabled);
    }

    [Fact]
    public void Yalnizca_acikca_true_olan_ayar_acik_sayilir()
    {
        Assert.True(PaidAiPolicy.IsEnabled(new OrganizationSettings { PaidAiEnabled = true }));
        Assert.False(PaidAiPolicy.IsEnabled(new OrganizationSettings { PaidAiEnabled = false }));
    }
}
