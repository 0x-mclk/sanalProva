using System.Net;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using SanalProva.Domain.Tenancy;

namespace SanalProva.IntegrationTests;

/// <summary>M5.2: pasife alınan/parolası sıfırlanan personelin AÇIK SignalR bağlantısı hemen kesilir; pasif hesap/şube yeni abonelik alamaz.</summary>
public sealed class StaffHubRevocationTests(ApiFactory factory) : ApiTestBase(factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record MemberDto(Guid Id);
    private sealed record InviteDto(MemberDto Member, string SetupLink);
    private sealed record LinkDto(string SetupLink);

    internal static HubConnection ConnectHub(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, string authCookie) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(host.Server.BaseAddress, SanalProva.Api.Hubs.StaffHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
                options.Transports = HttpTransportType.WebSockets;
                options.Headers["Cookie"] = authCookie;
                options.WebSocketFactory = async (context, ct) =>
                {
                    var socketClient = host.Server.CreateWebSocketClient();
                    socketClient.ConfigureRequest = request => request.Headers["Cookie"] = authCookie;
                    return await socketClient.ConnectAsync(context.Uri, ct);
                };
            })
            .Build();

    internal static async Task WaitDisconnectedAsync(HubConnection connection)
    {
        for (var i = 0; i < 100 && connection.State == HubConnectionState.Connected; i++)
        {
            await Task.Delay(50, Ct);
        }
    }

    private static async Task<(string Email, MemberDto Member, ApiClient Client)> StaffAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, ApiClient owner, Guid storeId)
    {
        var email = $"hub-{Guid.NewGuid().ToString("N")[..10]}@test.local";
        var invited = await owner.PostAsync<InviteDto>("/api/v1/management/users", new { email, displayName = "Hub Personel", assignments = new[] { new { storeId, role = StoreRole.Staff } } }, HttpStatusCode.Created);
        var token = invited.SetupLink[(invited.SetupLink.IndexOf("#t=", StringComparison.Ordinal) + 3)..];
        var anonymous = new ApiClient(host.CreateClient());
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.SendAsync(HttpMethod.Post, "/api/v1/account/setup-password", new { token, password = "Yeni-Parola-98765" })).StatusCode);
        return (email, invited.Member, await ApiClient.LoginAsync(host, email, storeId, "Yeni-Parola-98765"));
    }

    [Fact]
    public async Task Pasife_alinan_personelin_acik_hub_baglantisi_kesilir()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        using var host = DerivedHost();
        var owner = await ApiClient.LoginAsync(host, tenant.Email, tenant.StoreId);
        var (_, member, staff) = await StaffAsync(host, owner, tenant.StoreId);

        await using var connection = ConnectHub(host, staff.AuthCookie);
        await connection.StartAsync(Ct);
        Assert.True(await connection.InvokeAsync<bool>("JoinStore", tenant.StoreId, Ct));

        await owner.PatchAsync<MemberDto>($"/api/v1/management/users/{member.Id}/status", new { isActive = false });

        await WaitDisconnectedAsync(connection);
        Assert.NotEqual(HubConnectionState.Connected, connection.State);
        await using var fresh = ConnectHub(host, staff.AuthCookie);
        await Assert.ThrowsAnyAsync<Exception>(() => fresh.StartAsync(Ct));
    }

    [Fact]
    public async Task Parolasi_sifirlanan_personelin_acik_hub_baglantisi_kesilir()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        using var host = DerivedHost();
        var owner = await ApiClient.LoginAsync(host, tenant.Email, tenant.StoreId);
        var (_, member, staff) = await StaffAsync(host, owner, tenant.StoreId);

        await using var connection = ConnectHub(host, staff.AuthCookie);
        await connection.StartAsync(Ct);
        Assert.True(await connection.InvokeAsync<bool>("JoinStore", tenant.StoreId, Ct));

        var link = await owner.PostAsync<LinkDto>($"/api/v1/management/users/{member.Id}/setup-link");
        var token = link.SetupLink[(link.SetupLink.IndexOf("#t=", StringComparison.Ordinal) + 3)..];
        var anonymous = new ApiClient(host.CreateClient());
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.SendAsync(HttpMethod.Post, "/api/v1/account/setup-password", new { token, password = "Sifirlanmis-Parola-55555" })).StatusCode);

        await WaitDisconnectedAsync(connection);
        Assert.NotEqual(HubConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task Pasif_subeye_hub_aboneligi_verilmez()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var second = await TestData.AddStoreAsync(Services, tenant.OrganizationId, "HUB2");
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var ownerId = db.Users.Where(u => u.Email == tenant.Email).Select(u => u.Id).Single();
            db.UserStoreRoles.Add(new UserStoreRole { UserId = ownerId, StoreId = second, OrganizationId = tenant.OrganizationId, Role = StoreRole.Owner });
            await db.SaveChangesAsync(Ct);
        }

        using var host = DerivedHost();
        var owner = await ApiClient.LoginAsync(host, tenant.Email, tenant.StoreId);
        await using var connection = ConnectHub(host, owner.AuthCookie);
        await connection.StartAsync(Ct);
        Assert.True(await connection.InvokeAsync<bool>("JoinStore", second, Ct));

        await owner.PatchAsync<System.Text.Json.JsonElement>($"/api/v1/management/stores/{second}/status", new { isActive = false });

        // Pasif şubeye abone açık bağlantı SUNUCUDA kesilir (eski grup üyeliği üzerinden bildirim sürmez); yeniden bağlanan istemci pasif şubeye abone olamaz
        await WaitDisconnectedAsync(connection);
        Assert.NotEqual(HubConnectionState.Connected, connection.State);
        await using var again = ConnectHub(host, owner.AuthCookie);
        await again.StartAsync(Ct);
        Assert.False(await again.InvokeAsync<bool>("JoinStore", second, Ct));
        Assert.True(await again.InvokeAsync<bool>("JoinStore", tenant.StoreId, Ct));
    }
}
