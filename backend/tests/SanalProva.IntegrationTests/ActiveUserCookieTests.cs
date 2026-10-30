using System.Net;
using Microsoft.EntityFrameworkCore;
using SanalProva.Domain.Tenancy;

namespace SanalProva.IntegrationTests;

/// <summary>M5.2a: açık çerez, hesap pasife alınınca veya güvenlik damgası yenilenince bir sonraki istekte düşer (Identity varsayılanı bunu 30 dk'da bir yapar ve pasifliği hiç denetlemez).</summary>
public sealed class ActiveUserCookieTests(ApiFactory factory) : ApiTestBase(factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task UpdateUserAsync(string email, Action<SanalProva.Infrastructure.Identity.AppUser> change)
    {
        var (scope, db) = OpenDb();
        using (scope)
        {
            var user = await db.Users.SingleAsync(u => u.Email == email, Ct);
            change(user);
            await db.SaveChangesAsync(Ct);
        }
    }

    [Fact]
    public async Task Pasife_alinan_kullanicinin_acik_cerezi_hemen_reddedilir()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Factory.Services, StoreRole.Staff);
        var client = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/displays")).StatusCode);

        await UpdateUserAsync(tenant.Email, u => u.IsActive = false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/displays")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Guvenlik_damgasi_yenilenince_eski_cerez_duser_yeniden_giris_calisir()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Factory.Services, StoreRole.Staff);
        var client = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);

        await UpdateUserAsync(tenant.Email, u => u.SecurityStamp = Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/displays")).StatusCode);
        var again = await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/api/v1/displays")).StatusCode);
    }
}
