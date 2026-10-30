using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SanalProva.Domain.Tenancy;

namespace SanalProva.IntegrationTests;

/// <summary>M5.3: firma bütçe sınırlarının raporlanması (etkin değer + platform tavanı + açık rezervasyon) ve yazma yetkisinin operatör komutuyla sınırlı olması.</summary>
public sealed class BudgetLimitsApiTests(ApiFactory factory) : ApiTestBase(factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record BudgetDto(
        decimal DailyLimitUsd, decimal MonthlyLimitUsd, decimal MaxJobCostUsd, decimal PlatformDailyLimitUsd, decimal PlatformMonthlyLimitUsd, decimal PlatformMaxJobCostUsd,
        bool DailyLimitSetByOrganization, bool MonthlyLimitSetByOrganization, bool MaxJobSetByOrganization, decimal OpenReservedUsd, decimal SpentTodayUsd);

    private sealed record UsageDto(BudgetDto Budget);

    private async Task SetBudgetAsync(Guid organizationId, decimal? daily, decimal? monthly, decimal? maxJob)
    {
        var (scope, db) = OpenDb();
        using (scope)
        {
            var organization = await db.Organizations.SingleAsync(o => o.Id == organizationId, Ct);
            organization.Settings.DailyBudgetUsd = daily;
            organization.Settings.MonthlyBudgetUsd = monthly;
            organization.Settings.MaxJobCostUsd = maxJob;
            await db.SaveChangesAsync(Ct);
        }
    }

    [Fact]
    public async Task Kullanim_raporu_etkin_siniri_ve_platform_tavanini_ayri_gosterir_firmalar_birbirinin_degerini_gormez()
    {
        ApiFactory.RequireDatabase(Factory);
        var a = await TestData.CreateTenantAsync(Factory.Services, StoreRole.Manager);
        var b = await TestData.CreateTenantAsync(Factory.Services, StoreRole.Manager);
        await SetBudgetAsync(a.OrganizationId, 1.25m, null, 0.20m);
        var clientA = await ApiClient.LoginAsync(Factory, a.Email, a.StoreId);
        var clientB = await ApiClient.LoginAsync(Factory, b.Email, b.StoreId);

        var budgetA = (await clientA.GetAsync<UsageDto>("/api/v1/admin/usage")).Budget;
        var budgetB = (await clientB.GetAsync<UsageDto>("/api/v1/admin/usage")).Budget;

        Assert.Equal(1.25m, budgetA.DailyLimitUsd);
        Assert.True(budgetA.DailyLimitSetByOrganization);
        Assert.Equal(0.20m, budgetA.MaxJobCostUsd);
        Assert.True(budgetA.MaxJobSetByOrganization);
        Assert.False(budgetA.MonthlyLimitSetByOrganization);
        Assert.Equal(budgetA.PlatformMonthlyLimitUsd, budgetA.MonthlyLimitUsd); // boş firma değeri: tavan
        Assert.True(budgetA.PlatformDailyLimitUsd >= budgetA.DailyLimitUsd);
        // B hiçbir firma değeri taşımıyor: tavanı görür, A'nın sınırını görmez
        Assert.Equal(budgetB.PlatformDailyLimitUsd, budgetB.DailyLimitUsd);
        Assert.False(budgetB.DailyLimitSetByOrganization || budgetB.MaxJobSetByOrganization);
        Assert.Equal(0m, budgetB.OpenReservedUsd);
    }

    [Fact]
    public void Kaynak_kodda_firma_butce_sinirlarini_yazan_hicbir_yer_yok_yalnizca_operator_komutu()
    {
        var src = Directory.GetParent(Path.GetDirectoryName(MigrationLab.RepoFile("backend/src/SanalProva.Api/SanalProva.Api.csproj"))!)!.FullName;
        var allowedWriters = new HashSet<string>(StringComparer.Ordinal) { "SanalProva.Api/Features/Platform/PlatformCommands.cs" }; // operatör komutu (budget): denetimli tek yazıcı
        var writes = new Regex(@"\b(DailyBudgetUsd|MonthlyBudgetUsd|MaxJobCostUsd)\s*(=(?![=>])|\+=)|SetProperty\([^;]*(DailyBudgetUsd|MonthlyBudgetUsd)", RegexOptions.CultureInvariant);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/Migrations/", StringComparison.Ordinal))
            {
                continue;
            }

            // BudgetSettings.MaxJobCostUsd (platform yapılandırması) varsayılan değer atamasıdır; firma alanı OrganizationSettings'tedir
            if (relative.EndsWith("Ai/AiSettings.cs", StringComparison.Ordinal) || relative.EndsWith("Domain/Tenancy/Tenancy.cs", StringComparison.Ordinal))
            {
                continue;
            }

            if (writes.IsMatch(File.ReadAllText(file)) && !allowedWriters.Contains(relative))
            {
                offenders.Add(relative);
            }
        }

        Assert.True(offenders.Count == 0, "Firma bütçe sınırını yazan izinsiz dosya(lar): " + string.Join(", ", offenders));
    }
}
