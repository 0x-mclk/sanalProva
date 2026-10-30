using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SanalProva.Domain.Tenancy;

namespace SanalProva.IntegrationTests;

/// <summary>M5.2b/c: personel daveti, şube/rol ataması, Manager sınırları, pasife alma, parola sıfırlama bağlantısı, firma izolasyonu.</summary>
public sealed class StaffManagementTests(ApiFactory factory) : ApiTestBase(factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string NewPassword = "Yeni-Parola-98765";

    private sealed record AssignmentDto(Guid StoreId, string StoreName, string StoreCode, bool StoreActive, StoreRole Role);
    private sealed record MemberDto(Guid Id, string DisplayName, string Email, bool IsActive, bool SetupPending, bool IsSelf, bool CanManage, DateTimeOffset? OpenLinkExpiresAt, List<AssignmentDto> Assignments);
    private sealed record InviteDto(MemberDto Member, string SetupLink, DateTimeOffset SetupLinkExpiresAt);
    private sealed record LinkDto(Guid UserId, string SetupLink, DateTimeOffset ExpiresAt, string Purpose);

    private sealed record Org(TestTenant Tenant, ApiClient Owner, Guid SecondStore);

    /// <summary>İki şubeli firma + giriş yapmış Owner.</summary>
    private async Task<Org> OrgAsync()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Factory.Services, StoreRole.Owner);
        var second = await TestData.AddStoreAsync(Factory.Services, tenant.OrganizationId, "IKI");
        await AddOwnerRowAsync(tenant, second);
        return new Org(tenant, await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId), second);
    }

    private async Task AddOwnerRowAsync(TestTenant tenant, Guid storeId)
    {
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var ownerId = await db.Users.Where(u => u.Email == tenant.Email).Select(u => u.Id).SingleAsync(Ct);
            db.UserStoreRoles.Add(new UserStoreRole { UserId = ownerId, StoreId = storeId, OrganizationId = tenant.OrganizationId, Role = StoreRole.Owner });
            await db.SaveChangesAsync(Ct);
        }
    }

    private static object Invite(string email, params (Guid Store, StoreRole Role)[] assignments) =>
        new { email, displayName = "Yeni Personel", assignments = assignments.Select(a => new { storeId = a.Store, role = a.Role }).ToArray() };

    private static object Assign(params (Guid Store, StoreRole Role)[] assignments) => new { assignments = assignments.Select(a => new { storeId = a.Store, role = a.Role }).ToArray() };

    private static string Email() => $"yeni-{Guid.NewGuid().ToString("N")[..10]}@test.local";

    private static string TokenOf(string link) => link[(link.IndexOf("#t=", StringComparison.Ordinal) + 3)..];

    private async Task SetPasswordAsync(string link, string password = NewPassword)
    {
        var anonymous = new ApiClient(Factory.CreateClient());
        var response = await anonymous.SendAsync(HttpMethod.Post, "/api/v1/account/setup-password", new { token = TokenOf(link), password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>Davet eder, şifreyi belirler, kişi olarak giriş yapar.</summary>
    private async Task<(MemberDto Member, string Email, ApiClient Client)> InviteAndActivateAsync(ApiClient inviter, Guid loginStore, params (Guid Store, StoreRole Role)[] assignments)
    {
        var email = Email();
        var invited = await inviter.PostAsync<InviteDto>("/api/v1/management/users", Invite(email, assignments), HttpStatusCode.Created);
        await SetPasswordAsync(invited.SetupLink);
        return (invited.Member, email, await ApiClient.LoginAsync(Factory, email, loginStore, NewPassword));
    }

    private async Task<ApiClient> ManagerAsync(Org org, params Guid[] stores)
    {
        var (_, _, client) = await InviteAndActivateAsync(org.Owner, stores[0], stores.Select(s => (s, StoreRole.Manager)).ToArray());
        return client;
    }

    // ---- Davet ----

    [Fact]
    public async Task Owner_davet_eder_baglanti_ilk_yanitta_gelir_kisi_sifre_belirleyip_giris_yapar()
    {
        var org = await OrgAsync();
        var email = Email();

        var invited = await org.Owner.PostAsync<InviteDto>("/api/v1/management/users", Invite(email, (org.Tenant.StoreId, StoreRole.Staff)), HttpStatusCode.Created);

        Assert.Contains("/hesap/sifre-belirle#t=", invited.SetupLink, StringComparison.Ordinal);
        Assert.True(invited.Member.SetupPending);
        Assert.Single(invited.Member.Assignments);
        // Şifre belirlenmeden giriş yok
        var anonymous = new ApiClient(Factory.CreateClient());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = NewPassword, rememberMe = false })).StatusCode);

        await SetPasswordAsync(invited.SetupLink);
        var staff = await ApiClient.LoginAsync(Factory, email, org.Tenant.StoreId, NewPassword);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/v1/displays")).StatusCode);

        // Liste bağlantıyı göstermez; kullanıldığı için açık bağlantı da kalmadı
        var list = await org.Owner.GetAsync<List<MemberDto>>("/api/v1/management/users");
        var listed = Assert.Single(list, m => m.Id == invited.Member.Id);
        Assert.False(listed.SetupPending);
        Assert.Null(listed.OpenLinkExpiresAt);
        Assert.DoesNotContain(TokenOf(invited.SetupLink), JsonSerializer.Serialize(list), StringComparison.Ordinal);
        // Bağlantı ikinci kez kullanılamaz
        var again = await new ApiClient(Factory.CreateClient()).SendAsync(HttpMethod.Post, "/api/v1/account/setup-password", new { token = TokenOf(invited.SetupLink), password = NewPassword });
        await ApiClient.AssertProblemAsync(again, HttpStatusCode.BadRequest, "SETUP_LINK_INVALID");
    }

    [Fact]
    public async Task Davet_dogrulamalari_tekrar_eposta_yabanci_sube_bos_atama()
    {
        var org = await OrgAsync();
        var foreign = await TestData.CreateTenantAsync(Factory.Services, StoreRole.Owner);
        var email = Email();
        await org.Owner.PostAsync<InviteDto>("/api/v1/management/users", Invite(email, (org.Tenant.StoreId, StoreRole.Staff)), HttpStatusCode.Created);

        await ApiClient.AssertProblemAsync(await org.Owner.SendAsync(HttpMethod.Post, "/api/v1/management/users", Invite(email.ToUpperInvariant(), (org.Tenant.StoreId, StoreRole.Staff))), HttpStatusCode.Conflict, "EMAIL_TAKEN");
        await ApiClient.AssertProblemAsync(await org.Owner.SendAsync(HttpMethod.Post, "/api/v1/management/users", Invite(Email(), (foreign.StoreId, StoreRole.Staff))), HttpStatusCode.BadRequest, "STORE_NOT_FOUND");
        Assert.Equal(HttpStatusCode.BadRequest, (await org.Owner.SendAsync(HttpMethod.Post, "/api/v1/management/users", Invite(Email()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await org.Owner.SendAsync(HttpMethod.Post, "/api/v1/management/users", Invite("gecersiz", (org.Tenant.StoreId, StoreRole.Staff)))).StatusCode);
    }

    [Fact]
    public async Task Owner_daveti_tum_subelerde_Owner_olur_pasif_subeye_yeni_atama_yapilamaz()
    {
        var org = await OrgAsync();
        var invited = await org.Owner.PostAsync<InviteDto>("/api/v1/management/users", Invite(Email(), (org.Tenant.StoreId, StoreRole.Owner)), HttpStatusCode.Created);
        Assert.Equal(2, invited.Member.Assignments.Count);
        Assert.All(invited.Member.Assignments, a => Assert.Equal(StoreRole.Owner, a.Role));

        await org.Owner.PatchAsync<JsonElement>($"/api/v1/management/stores/{org.SecondStore}/status", new { isActive = false });
        await ApiClient.AssertProblemAsync(await org.Owner.SendAsync(HttpMethod.Post, "/api/v1/management/users", Invite(Email(), (org.SecondStore, StoreRole.Staff))), HttpStatusCode.Conflict, "STORE_INACTIVE");
    }

    // ---- Yetki sınırları ----

    [Fact]
    public async Task Staff_personel_yonetemez_anonim_401()
    {
        var org = await OrgAsync();
        var (_, _, staff) = await InviteAndActivateAsync(org.Owner, org.Tenant.StoreId, (org.Tenant.StoreId, StoreRole.Staff));

        await ApiClient.AssertProblemAsync(await staff.GetAsync("/api/v1/management/users"), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        await ApiClient.AssertProblemAsync(await staff.SendAsync(HttpMethod.Post, "/api/v1/management/users", Invite(Email(), (org.Tenant.StoreId, StoreRole.Staff))), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        Assert.Equal(HttpStatusCode.Unauthorized, (await new ApiClient(Factory.CreateClient()).GetAsync("/api/v1/management/users")).StatusCode);
    }

    [Fact]
    public async Task Manager_yalnizca_kendi_subesine_Staff_davet_eder()
    {
        var org = await OrgAsync();
        var manager = await ManagerAsync(org, org.Tenant.StoreId);

        var ok = await manager.PostAsync<InviteDto>("/api/v1/management/users", Invite(Email(), (org.Tenant.StoreId, StoreRole.Staff)), HttpStatusCode.Created);
        Assert.Equal(StoreRole.Staff, Assert.Single(ok.Member.Assignments).Role);

        await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Post, "/api/v1/management/users", Invite(Email(), (org.Tenant.StoreId, StoreRole.Manager))), HttpStatusCode.Forbidden, "MANAGER_LIMIT");
        await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Post, "/api/v1/management/users", Invite(Email(), (org.Tenant.StoreId, StoreRole.Owner))), HttpStatusCode.Forbidden, "MANAGER_LIMIT");
        await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Post, "/api/v1/management/users", Invite(Email(), (org.SecondStore, StoreRole.Staff))), HttpStatusCode.Forbidden, "MANAGER_LIMIT");
        // Şube yönetimi yalnızca Owner
        await ApiClient.AssertProblemAsync(await manager.GetAsync("/api/v1/management/stores"), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
    }

    [Fact]
    public async Task Manager_Owner_ve_Manager_uzerinde_islem_yapamaz_yalnizca_kendi_Staff_listesini_gorur()
    {
        var org = await OrgAsync();
        var manager = await ManagerAsync(org, org.Tenant.StoreId);
        var otherManager = await org.Owner.PostAsync<InviteDto>("/api/v1/management/users", Invite(Email(), (org.Tenant.StoreId, StoreRole.Manager)), HttpStatusCode.Created);
        var ownerId = (await org.Owner.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetGuid();
        var staffHere = await org.Owner.PostAsync<InviteDto>("/api/v1/management/users", Invite(Email(), (org.Tenant.StoreId, StoreRole.Staff)), HttpStatusCode.Created);
        var staffElsewhere = await org.Owner.PostAsync<InviteDto>("/api/v1/management/users", Invite(Email(), (org.SecondStore, StoreRole.Staff)), HttpStatusCode.Created);

        foreach (var targetId in new[] { ownerId, otherManager.Member.Id })
        {
            await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Patch, $"/api/v1/management/users/{targetId}/status", new { isActive = false }), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
            await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Put, $"/api/v1/management/users/{targetId}/assignments", Assign((org.Tenant.StoreId, StoreRole.Staff))), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
            await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Post, $"/api/v1/management/users/{targetId}/setup-link"), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        }

        // Başka şubenin Staff'ı Manager'a "yönetilemez" görünür
        await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Patch, $"/api/v1/management/users/{staffElsewhere.Member.Id}/status", new { isActive = false }), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");

        var list = await manager.GetAsync<List<MemberDto>>("/api/v1/management/users");
        Assert.DoesNotContain(list, m => m.Id == staffElsewhere.Member.Id);
        Assert.Contains(list, m => m.Id == staffHere.Member.Id && m.CanManage);
        Assert.Contains(list, m => m.Id == otherManager.Member.Id && !m.CanManage);
    }

    [Fact]
    public async Task Manager_kendi_subesindeki_Staff_atamasini_degistirir_diger_subelerdeki_rol_korunur()
    {
        var org = await OrgAsync();
        var manager = await ManagerAsync(org, org.Tenant.StoreId);
        // Staff hem Manager'ın şubesinde hem başka şubede
        var staff = await org.Owner.PostAsync<InviteDto>("/api/v1/management/users", Invite(Email(), (org.Tenant.StoreId, StoreRole.Staff), (org.SecondStore, StoreRole.Staff)), HttpStatusCode.Created);

        // Kendi şubesinden çıkarma: diğer şubedeki atama kalır
        var removed = await manager.PutAsync<MemberDto>($"/api/v1/management/users/{staff.Member.Id}/assignments", Assign());
        Assert.Equal(org.SecondStore, Assert.Single(removed.Assignments).StoreId);
        // Başka şubeye atayamaz, rol yükseltemez
        await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Put, $"/api/v1/management/users/{staff.Member.Id}/assignments", Assign((org.SecondStore, StoreRole.Staff))), HttpStatusCode.Forbidden, "MANAGER_LIMIT");
        await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Put, $"/api/v1/management/users/{staff.Member.Id}/assignments", Assign((org.Tenant.StoreId, StoreRole.Manager))), HttpStatusCode.Forbidden, "MANAGER_LIMIT");
        // Geri ekleyebilir
        var back = await manager.PutAsync<MemberDto>($"/api/v1/management/users/{staff.Member.Id}/assignments", Assign((org.Tenant.StoreId, StoreRole.Staff)));
        Assert.Equal(2, back.Assignments.Count);
    }

    // ---- Rol değişikliği ----

    [Fact]
    public async Task Owner_rol_degistirir_yetki_anında_gecerli_olur_ve_Owner_yapma_tum_subelere_yayilir()
    {
        var org = await OrgAsync();
        var (member, _, client) = await InviteAndActivateAsync(org.Owner, org.Tenant.StoreId, (org.Tenant.StoreId, StoreRole.Staff));
        await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/management/users"), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");

        var promoted = await org.Owner.PutAsync<MemberDto>($"/api/v1/management/users/{member.Id}/assignments", Assign((org.Tenant.StoreId, StoreRole.Manager)));
        Assert.Equal(StoreRole.Manager, Assert.Single(promoted.Assignments).Role);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/management/users")).StatusCode); // aynı oturum, yeni yetki

        var owner = await org.Owner.PutAsync<MemberDto>($"/api/v1/management/users/{member.Id}/assignments", Assign((org.Tenant.StoreId, StoreRole.Owner)));
        Assert.Equal(2, owner.Assignments.Count);
        Assert.All(owner.Assignments, a => Assert.Equal(StoreRole.Owner, a.Role));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/management/stores")).StatusCode);

        // İki Owner varken biri Staff'a düşürülebilir; yetki hemen kalkar
        var demoted = await org.Owner.PutAsync<MemberDto>($"/api/v1/management/users/{member.Id}/assignments", Assign((org.Tenant.StoreId, StoreRole.Staff)));
        Assert.Equal(StoreRole.Staff, Assert.Single(demoted.Assignments).Role);
        await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/management/stores"), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
    }

    [Fact]
    public async Task Kendi_rolunu_ve_durumunu_degistiremez_bos_atama_reddedilir_yabanci_sube_reddedilir()
    {
        var org = await OrgAsync();
        var ownerId = (await org.Owner.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetGuid();
        var staff = await org.Owner.PostAsync<InviteDto>("/api/v1/management/users", Invite(Email(), (org.Tenant.StoreId, StoreRole.Staff)), HttpStatusCode.Created);
        var foreign = await TestData.CreateTenantAsync(Factory.Services, StoreRole.Owner);

        await ApiClient.AssertProblemAsync(await org.Owner.SendAsync(HttpMethod.Put, $"/api/v1/management/users/{ownerId}/assignments", Assign((org.Tenant.StoreId, StoreRole.Staff))), HttpStatusCode.Forbidden, "SELF_CHANGE_FORBIDDEN");
        await ApiClient.AssertProblemAsync(await org.Owner.SendAsync(HttpMethod.Patch, $"/api/v1/management/users/{ownerId}/status", new { isActive = false }), HttpStatusCode.Forbidden, "SELF_CHANGE_FORBIDDEN");
        await ApiClient.AssertProblemAsync(await org.Owner.SendAsync(HttpMethod.Put, $"/api/v1/management/users/{staff.Member.Id}/assignments", Assign()), HttpStatusCode.BadRequest, "ASSIGNMENT_REQUIRED");
        await ApiClient.AssertProblemAsync(await org.Owner.SendAsync(HttpMethod.Put, $"/api/v1/management/users/{staff.Member.Id}/assignments", Assign((foreign.StoreId, StoreRole.Staff))), HttpStatusCode.BadRequest, "STORE_NOT_FOUND");
    }

    // ---- Firma izolasyonu ----

    [Fact]
    public async Task Baska_firmanin_kullanicisi_listelenmez_ve_hicbir_islem_yapilamaz()
    {
        var org = await OrgAsync();
        var other = await OrgAsync();
        var victim = await other.Owner.PostAsync<InviteDto>("/api/v1/management/users", Invite(Email(), (other.Tenant.StoreId, StoreRole.Staff)), HttpStatusCode.Created);
        var id = victim.Member.Id;

        Assert.DoesNotContain(await org.Owner.GetAsync<List<MemberDto>>("/api/v1/management/users"), m => m.Id == id);
        await ApiClient.AssertProblemAsync(await org.Owner.SendAsync(HttpMethod.Patch, $"/api/v1/management/users/{id}/status", new { isActive = false }), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await org.Owner.SendAsync(HttpMethod.Put, $"/api/v1/management/users/{id}/assignments", Assign((org.Tenant.StoreId, StoreRole.Owner))), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await org.Owner.SendAsync(HttpMethod.Post, $"/api/v1/management/users/{id}/setup-link"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ApiClient.AssertProblemAsync(await org.Owner.SendAsync(HttpMethod.Delete, $"/api/v1/management/users/{id}/setup-link"), HttpStatusCode.NotFound, "NOT_FOUND");

        // Hedef bozulmadı: hâlâ etkin, bağlantısı hâlâ geçerli
        var (scope, db) = OpenDb();
        using (scope)
        {
            Assert.True((await db.Users.SingleAsync(u => u.Id == id, Ct)).IsActive);
        }

        await SetPasswordAsync(victim.SetupLink);
    }

    // ---- Pasife alma ----

    [Fact]
    public async Task Pasife_alinan_personelin_acik_oturumu_dusar_giris_yapamaz_yeniden_etkinlesir()
    {
        var org = await OrgAsync();
        var (member, email, client) = await InviteAndActivateAsync(org.Owner, org.Tenant.StoreId, (org.Tenant.StoreId, StoreRole.Staff));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/displays")).StatusCode);

        var off = await org.Owner.PatchAsync<MemberDto>($"/api/v1/management/users/{member.Id}/status", new { isActive = false });
        Assert.False(off.IsActive);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/displays")).StatusCode);
        var anonymous = new ApiClient(Factory.CreateClient());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = NewPassword, rememberMe = false })).StatusCode);
        await ApiClient.AssertProblemAsync(await org.Owner.SendAsync(HttpMethod.Post, $"/api/v1/management/users/{member.Id}/setup-link"), HttpStatusCode.Conflict, "USER_INACTIVE");

        var on = await org.Owner.PatchAsync<MemberDto>($"/api/v1/management/users/{member.Id}/status", new { isActive = true });
        Assert.True(on.IsActive);
        var again = await ApiClient.LoginAsync(Factory, email, org.Tenant.StoreId, NewPassword);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/api/v1/displays")).StatusCode);
    }

    [Fact]
    public async Task Pasife_alinan_bekleyen_davetin_baglantisi_kullanilamaz()
    {
        var org = await OrgAsync();
        var invited = await org.Owner.PostAsync<InviteDto>("/api/v1/management/users", Invite(Email(), (org.Tenant.StoreId, StoreRole.Staff)), HttpStatusCode.Created);

        await org.Owner.PatchAsync<MemberDto>($"/api/v1/management/users/{invited.Member.Id}/status", new { isActive = false });

        var response = await new ApiClient(Factory.CreateClient()).SendAsync(HttpMethod.Post, "/api/v1/account/setup-password", new { token = TokenOf(invited.SetupLink), password = NewPassword });
        await ApiClient.AssertProblemAsync(response, HttpStatusCode.BadRequest, "SETUP_LINK_INVALID");
    }

    [Fact]
    public async Task Manager_kendi_Staff_unu_pasife_alir_baska_subeye_de_atanmissa_alamaz()
    {
        var org = await OrgAsync();
        var manager = await ManagerAsync(org, org.Tenant.StoreId);
        var only = await org.Owner.PostAsync<InviteDto>("/api/v1/management/users", Invite(Email(), (org.Tenant.StoreId, StoreRole.Staff)), HttpStatusCode.Created);
        var both = await org.Owner.PostAsync<InviteDto>("/api/v1/management/users", Invite(Email(), (org.Tenant.StoreId, StoreRole.Staff), (org.SecondStore, StoreRole.Staff)), HttpStatusCode.Created);

        Assert.False((await manager.PatchAsync<MemberDto>($"/api/v1/management/users/{only.Member.Id}/status", new { isActive = false })).IsActive);
        await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Patch, $"/api/v1/management/users/{both.Member.Id}/status", new { isActive = false }), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
    }

    [Fact]
    public async Task Iki_Owner_birbirini_ayni_anda_pasife_alirsa_firmada_en_az_bir_etkin_Owner_kalir()
    {
        var org = await OrgAsync();
        var (second, _, secondClient) = await InviteAndActivateAsync(org.Owner, org.Tenant.StoreId, (org.Tenant.StoreId, StoreRole.Owner));
        var firstId = (await org.Owner.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetGuid();

        var results = await Task.WhenAll(
            org.Owner.SendAsync(HttpMethod.Patch, $"/api/v1/management/users/{second.Id}/status", new { isActive = false }),
            secondClient.SendAsync(HttpMethod.Patch, $"/api/v1/management/users/{firstId}/status", new { isActive = false }));

        Assert.Contains(results, r => r.StatusCode == HttpStatusCode.OK);
        var (scope, db) = OpenDb();
        using (scope)
        {
            Assert.True(await db.Users.CountAsync(u => (u.Id == firstId || u.Id == second.Id) && u.IsActive, Ct) >= 1);
        }
    }

    // ---- Bağlantı yenileme / sıfırlama ----

    [Fact]
    public async Task Bekleyen_davet_yeniden_uretilir_eski_baglanti_gecersiz_olur_iptal_edilebilir()
    {
        var org = await OrgAsync();
        var invited = await org.Owner.PostAsync<InviteDto>("/api/v1/management/users", Invite(Email(), (org.Tenant.StoreId, StoreRole.Staff)), HttpStatusCode.Created);

        var fresh = await org.Owner.PostAsync<LinkDto>($"/api/v1/management/users/{invited.Member.Id}/setup-link");
        Assert.Equal("InitialPassword", fresh.Purpose);
        var oldTry = await new ApiClient(Factory.CreateClient()).SendAsync(HttpMethod.Post, "/api/v1/account/setup-password", new { token = TokenOf(invited.SetupLink), password = NewPassword });
        await ApiClient.AssertProblemAsync(oldTry, HttpStatusCode.BadRequest, "SETUP_LINK_INVALID");

        var listed = Assert.Single(await org.Owner.GetAsync<List<MemberDto>>("/api/v1/management/users"), m => m.Id == invited.Member.Id);
        Assert.NotNull(listed.OpenLinkExpiresAt);

        Assert.Equal(HttpStatusCode.NoContent, (await org.Owner.SendAsync(HttpMethod.Delete, $"/api/v1/management/users/{invited.Member.Id}/setup-link")).StatusCode);
        var revokedTry = await new ApiClient(Factory.CreateClient()).SendAsync(HttpMethod.Post, "/api/v1/account/setup-password", new { token = TokenOf(fresh.SetupLink), password = NewPassword });
        await ApiClient.AssertProblemAsync(revokedTry, HttpStatusCode.BadRequest, "SETUP_LINK_INVALID");
    }

    [Fact]
    public async Task Parola_sifirlama_baglantisi_parolayi_degistirir_eski_oturumu_dusurur_kilidi_acar_tek_kullanimlidir()
    {
        var org = await OrgAsync();
        var (member, email, client) = await InviteAndActivateAsync(org.Owner, org.Tenant.StoreId, (org.Tenant.StoreId, StoreRole.Staff));

        var link = await org.Owner.PostAsync<LinkDto>($"/api/v1/management/users/{member.Id}/setup-link");
        Assert.Equal("PasswordReset", link.Purpose);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/displays")).StatusCode); // bağlantı üretmek oturumu düşürmez

        const string Reset = "Sifirlanmis-Parola-55555";
        await SetPasswordAsync(link.SetupLink, Reset);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/displays")).StatusCode); // eski oturum düştü
        var anonymous = new ApiClient(Factory.CreateClient());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password = NewPassword, rememberMe = false })).StatusCode);
        var fresh = await ApiClient.LoginAsync(Factory, email, org.Tenant.StoreId, Reset);
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/v1/displays")).StatusCode);

        var reuse = await anonymous.SendAsync(HttpMethod.Post, "/api/v1/account/setup-password", new { token = TokenOf(link.SetupLink), password = "Baska-Parola-11111" });
        await ApiClient.AssertProblemAsync(reuse, HttpStatusCode.BadRequest, "SETUP_LINK_INVALID");
    }

    [Fact]
    public async Task Sifirlama_politika_ihlalinde_baglanti_yanmaz_ve_parola_degismez()
    {
        var org = await OrgAsync();
        var (member, email, _) = await InviteAndActivateAsync(org.Owner, org.Tenant.StoreId, (org.Tenant.StoreId, StoreRole.Staff));
        var link = await org.Owner.PostAsync<LinkDto>($"/api/v1/management/users/{member.Id}/setup-link");
        var anonymous = new ApiClient(Factory.CreateClient());

        await ApiClient.AssertProblemAsync(await anonymous.SendAsync(HttpMethod.Post, "/api/v1/account/setup-password", new { token = TokenOf(link.SetupLink), password = "kisa" }), HttpStatusCode.BadRequest, "PASSWORD_POLICY");

        // Eski parola hâlâ geçerli, bağlantı hâlâ kullanılabilir
        var stillWorks = await ApiClient.LoginAsync(Factory, email, org.Tenant.StoreId, NewPassword);
        Assert.Equal(HttpStatusCode.OK, (await stillWorks.GetAsync("/api/v1/displays")).StatusCode);
        await SetPasswordAsync(link.SetupLink, "Gecerli-Parola-77777");
    }

    // ---- Denetim ----

    [Fact]
    public async Task Islemler_denetim_kaydi_birakir_kisisel_veri_ve_token_icermez()
    {
        var org = await OrgAsync();
        var email = Email();
        var invited = await org.Owner.PostAsync<InviteDto>("/api/v1/management/users", Invite(email, (org.Tenant.StoreId, StoreRole.Staff)), HttpStatusCode.Created);
        await org.Owner.PutAsync<MemberDto>($"/api/v1/management/users/{invited.Member.Id}/assignments", Assign((org.Tenant.StoreId, StoreRole.Manager)));
        await org.Owner.PostAsync<LinkDto>($"/api/v1/management/users/{invited.Member.Id}/setup-link");
        await org.Owner.PatchAsync<MemberDto>($"/api/v1/management/users/{invited.Member.Id}/status", new { isActive = false });

        var (scope, db) = OpenDb(org.Tenant.OrganizationId);
        using (scope)
        {
            var logs = await db.AuditLogs.AsNoTracking().Where(a => a.OrganizationId == org.Tenant.OrganizationId).ToListAsync(Ct);
            var actions = logs.Select(a => a.Action).ToList();
            Assert.Contains("User.Invited", actions);
            Assert.Contains("User.RolesChanged", actions);
            Assert.Contains("User.SetupLinkIssued", actions);
            Assert.Contains("User.Deactivated", actions);
            var dump = JsonSerializer.Serialize(logs);
            Assert.DoesNotContain(email, dump, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(TokenOf(invited.SetupLink), dump, StringComparison.Ordinal);
        }
    }
}
