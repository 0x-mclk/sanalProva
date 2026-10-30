using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Generation;

namespace SanalProva.UnitTests.Infrastructure;

/// <summary>M5.3: etkin firma sınırı = min(platform tavanı, firma sınırı); firma tavanı yükseltemez; boş firma değeri tavanı bırakır; bozuk (negatif) değer fail-closed.</summary>
public sealed class BudgetLimitsTests
{
    private static BudgetSettings Platform() => new() { DailyLimitUsd = 10m, MonthlyLimitUsd = 150m, MaxJobCostUsd = 0.5m };

    [Fact]
    public void Firma_degeri_yoksa_platform_tavani_gecerlidir()
    {
        var limits = BudgetLimits.Resolve(Platform(), new OrganizationSettings());
        Assert.Equal((10m, 150m, 0.5m), (limits.DailyUsd, limits.MonthlyUsd, limits.MaxJobUsd));
        Assert.False(limits.DailyFromOrganization || limits.MonthlyFromOrganization || limits.MaxJobFromOrganization);
        Assert.Equal(limits, BudgetLimits.Resolve(Platform(), null));
    }

    [Fact]
    public void Dusuk_firma_degeri_gecerlidir_yuksek_firma_degeri_tavani_asamaz()
    {
        var limits = BudgetLimits.Resolve(Platform(), new OrganizationSettings { DailyBudgetUsd = 2m, MonthlyBudgetUsd = 1000m, MaxJobCostUsd = 0.5m });
        Assert.Equal((2m, 150m, 0.5m), (limits.DailyUsd, limits.MonthlyUsd, limits.MaxJobUsd));
        Assert.True(limits.DailyFromOrganization);
        Assert.False(limits.MonthlyFromOrganization); // tavan geçerli: firma değeri etkin değil
        Assert.False(limits.MaxJobFromOrganization); // eşit: tavandan düşük değil
    }

    [Fact]
    public void Sifir_ve_negatif_firma_degeri_hic_harcamaya_izin_vermez()
    {
        var zero = BudgetLimits.Resolve(Platform(), new OrganizationSettings { DailyBudgetUsd = 0m });
        Assert.Equal(0m, zero.DailyUsd);
        var negative = BudgetLimits.Resolve(Platform(), new OrganizationSettings { MonthlyBudgetUsd = -5m });
        Assert.Equal(0m, negative.MonthlyUsd);
    }
}
