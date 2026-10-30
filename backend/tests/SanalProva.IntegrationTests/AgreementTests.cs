using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SanalProva.Api.Features.Agreement;
using SanalProva.Api.Features.Auth;
using SanalProva.Api.Features.TryOn;
using SanalProva.Domain.Tenancy;

namespace SanalProva.IntegrationTests;

/// <summary>
/// M5.1e: işletme hizmet sözleşmesi. Kanıtlananlar: kabul edilene kadar kimliklendirilmiş TÜM uç noktalar (REST + hub) sunucuda kapalıdır ve bunu atlayan uç nokta
/// yoktur (varsayılan zorlama + muaf liste mimari testle sabit); yalnızca Owner kabul eder; yeni sürüm yeniden kabul ister; kabul kaydı eklenir ve veritabanında
/// değiştirilemez; sözleşme ile müşteri KVKK onayı birbirinin yerine geçmez.
/// </summary>
public sealed class AgreementTests(ApiFactory factory) : ApiTestBase(factory)
{
    private const string Accept = "/api/v1/organization/agreement-acceptance";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object CurrentBody() => new { version = ServiceAgreement.Document.Version, textSha256 = ServiceAgreement.Document.TextSha256Hex };

    /// <summary>Sözleşmeyi kabul etmemiş firma: Owner, Manager ve Staff hesapları.</summary>
    private async Task<(TestTenant Tenant, ApiClient Owner, ApiClient Manager, ApiClient Staff)> UnacceptedAsync()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Owner, acceptAgreement: false);
        var managerEmail = await TestData.AddUserAsync(Services, tenant.OrganizationId, [(tenant.StoreId, StoreRole.Manager)]);
        var staffEmail = await TestData.AddUserAsync(Services, tenant.OrganizationId, [(tenant.StoreId, StoreRole.Staff)]);
        return (tenant,
            await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId),
            await ApiClient.LoginAsync(Factory, managerEmail, tenant.StoreId),
            await ApiClient.LoginAsync(Factory, staffEmail, tenant.StoreId));
    }

    private async Task<int> AcceptanceCountAsync(Guid organizationId)
    {
        var (scope, db) = OpenDb(organizationId);
        using (scope)
        {
            return await db.OrganizationAgreementAcceptances.CountAsync(Ct);
        }
    }

    [Fact]
    public async Task Kabul_edilene_kadar_magaza_islemleri_kapali_giris_gorüntuleme_ve_kabul_acik_Owner_kabul_edince_acilir()
    {
        var (tenant, owner, manager, staff) = await UnacceptedAsync();

        // Mağaza işlemleri her rol için kapalı (rol yeterli olsa bile)
        foreach (var client in new[] { owner, manager, staff })
        {
            await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/products"), HttpStatusCode.Forbidden, AgreementEnforcementMiddleware.ErrorCode);
            await ApiClient.AssertProblemAsync(await client.SendAsync(HttpMethod.Post, "/api/v1/sessions", new { customerLabel = "x" }), HttpStatusCode.Forbidden, AgreementEnforcementMiddleware.ErrorCode);
            await ApiClient.AssertProblemAsync(await client.GetAsync("/api/v1/consent-notice/current"), HttpStatusCode.Forbidden, AgreementEnforcementMiddleware.ErrorCode);
        }

        // Giriş, oturum bilgisi ve sözleşme görüntüleme çalışır; yönlendirme bayrakları doğru
        var ownerMe = await owner.GetAsync<MeResponse>("/api/v1/auth/me");
        Assert.True(ownerMe.AgreementAcceptanceRequired);
        Assert.True(ownerMe.CanAcceptAgreement);
        var staffMe = await staff.GetAsync<MeResponse>("/api/v1/auth/me");
        Assert.True(staffMe.AgreementAcceptanceRequired);
        Assert.False(staffMe.CanAcceptAgreement);

        var agreement = await staff.GetAsync<AgreementDto>("/api/v1/agreement/current"); // personel de metni görebilir
        Assert.Equal(ServiceAgreement.Document.Version, agreement.Version);
        Assert.Equal(ServiceAgreement.Document.TextSha256Hex, agreement.TextSha256);
        Assert.False(agreement.Accepted);
        Assert.False(agreement.CanAccept);
        Assert.Contains("TASLAK", agreement.Text, StringComparison.Ordinal);

        // Yalnızca Owner kabul edebilir
        await ApiClient.AssertProblemAsync(await manager.SendAsync(HttpMethod.Post, Accept, CurrentBody()), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        await ApiClient.AssertProblemAsync(await staff.SendAsync(HttpMethod.Post, Accept, CurrentBody()), HttpStatusCode.Forbidden, "INSUFFICIENT_ROLE");
        Assert.Equal(0, await AcceptanceCountAsync(tenant.OrganizationId));

        // Owner kabul eder: 201, kayıt eklenir (sürüm + metin özeti + IP özeti; ham IP yok) ve denetim yazılır
        var accepted = await owner.PostAsync<AgreementDto>(Accept, CurrentBody(), HttpStatusCode.Created);
        Assert.True(accepted.Accepted);
        Assert.NotNull(accepted.AcceptedAt);
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var row = await db.OrganizationAgreementAcceptances.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(ServiceAgreement.Document.Version, row.AgreementVersion);
            Assert.Equal(ServiceAgreement.Document.TextSha256, row.TextSha256);
            Assert.True(row.IpHash is null || System.Text.RegularExpressions.Regex.IsMatch(row.IpHash, "^[0-9a-f]{32}$")); // ham IP asla yazılmaz (test sunucusunda uzak IP olmayabilir)
            Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.OrganizationId == tenant.OrganizationId && a.Action == "Organization.AgreementAccepted" && a.UserId == row.UserId, Ct));
        }

        // Kapı açıldı: tüm roller mağaza işlemlerine döner; ikinci kabul aynı kaydı döner (yineleme yok)
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/v1/products")).StatusCode);
        Assert.False((await staff.GetAsync<MeResponse>("/api/v1/auth/me")).AgreementAcceptanceRequired);
        var again = await owner.PostAsync<AgreementDto>(Accept, CurrentBody());
        Assert.Equal(accepted.AcceptedAt, again.AcceptedAt);
        Assert.Equal(1, await AcceptanceCountAsync(tenant.OrganizationId));

        // Çıkış her zaman çalışır
        Assert.Equal(HttpStatusCode.NoContent, (await (await UnacceptedAsync()).Owner.SendAsync(HttpMethod.Post, "/api/v1/auth/logout")).StatusCode);
    }

    [Fact]
    public async Task Eski_surum_veya_degismis_metin_kabul_edilmez_hicbir_kayit_yazilmaz()
    {
        var (tenant, owner, _, _) = await UnacceptedAsync();
        await ApiClient.AssertProblemAsync(await owner.SendAsync(HttpMethod.Post, Accept, new { version = "2025-01-v0", textSha256 = ServiceAgreement.Document.TextSha256Hex }), HttpStatusCode.Conflict, "AGREEMENT_VERSION_MISMATCH");
        await ApiClient.AssertProblemAsync(await owner.SendAsync(HttpMethod.Post, Accept, new { version = ServiceAgreement.Document.Version, textSha256 = new string('0', 64) }), HttpStatusCode.Conflict, "AGREEMENT_VERSION_MISMATCH");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.SendAsync(HttpMethod.Post, Accept, new { version = ServiceAgreement.Document.Version, textSha256 = "kisa" })).StatusCode);
        Assert.Equal(0, await AcceptanceCountAsync(tenant.OrganizationId));
        await ApiClient.AssertProblemAsync(await owner.GetAsync("/api/v1/products"), HttpStatusCode.Forbidden, AgreementEnforcementMiddleware.ErrorCode);
    }

    [Fact]
    public async Task Yeni_surum_yayimlaninca_firmadan_yeniden_kabul_istenir_eski_kabul_kayitlari_kalir()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Owner); // güncel (v1) kabul edilmiş
        var next = new ServiceAgreementDocument("2027-02-v2", "Sanal Prova Hizmet Sözleşmesi (TASLAK)", ServiceAgreement.Document.Text + "\n11. Yeni madde.");
        using var host = DerivedHost(b => b.ConfigureTestServices(s => s.AddSingleton<IServiceAgreementSource>(new FixedSource(next))));
        var owner = await ApiClient.LoginAsync(host, tenant.Email, tenant.StoreId);

        // Yeni sürümde kapı tekrar kapanır; yeni sürüm bilgisi görünür
        await ApiClient.AssertProblemAsync(await owner.GetAsync("/api/v1/products"), HttpStatusCode.Forbidden, AgreementEnforcementMiddleware.ErrorCode);
        var shown = await owner.GetAsync<AgreementDto>("/api/v1/agreement/current");
        Assert.Equal((next.Version, false), (shown.Version, shown.Accepted));
        Assert.True((await owner.GetAsync<MeResponse>("/api/v1/auth/me")).AgreementAcceptanceRequired);

        // Eski sürümü kabul etmek yetmez; yeni sürüm kabul edilince açılır
        await ApiClient.AssertProblemAsync(await owner.SendAsync(HttpMethod.Post, Accept, CurrentBody()), HttpStatusCode.Conflict, "AGREEMENT_VERSION_MISMATCH");
        await owner.PostAsync<AgreementDto>(Accept, new { version = next.Version, textSha256 = next.TextSha256Hex }, HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/products")).StatusCode);

        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            var versions = await db.OrganizationAgreementAcceptances.AsNoTracking().OrderBy(a => a.AcceptedAt).Select(a => a.AgreementVersion).ToListAsync(Ct);
            Assert.Equal([ServiceAgreement.Document.Version, next.Version], versions); // eski kabul silinmedi/değişmedi
        }
    }

    private sealed class FixedSource(ServiceAgreementDocument document) : IServiceAgreementSource
    {
        public ServiceAgreementDocument Current => document;
    }

    [Fact]
    public async Task Firmalar_birbirinden_bagimsizdir_bir_firmanin_kabulu_digerini_acmaz()
    {
        var (blocked, blockedOwner, _, _) = await UnacceptedAsync();
        var accepted = await TestData.CreateTenantAsync(Services, StoreRole.Owner);
        var acceptedClient = await ApiClient.LoginAsync(Factory, accepted.Email, accepted.StoreId);

        Assert.Equal(HttpStatusCode.OK, (await acceptedClient.GetAsync("/api/v1/products")).StatusCode);
        await ApiClient.AssertProblemAsync(await blockedOwner.GetAsync("/api/v1/products"), HttpStatusCode.Forbidden, AgreementEnforcementMiddleware.ErrorCode);

        // Kabul eden Owner yalnızca KENDİ firması adına yazar (firma kimliği istekten değil kimlikten gelir)
        await acceptedClient.PostAsync<AgreementDto>(Accept, CurrentBody());
        Assert.Equal(0, await AcceptanceCountAsync(blocked.OrganizationId));
        await ApiClient.AssertProblemAsync(await blockedOwner.GetAsync("/api/v1/products"), HttpStatusCode.Forbidden, AgreementEnforcementMiddleware.ErrorCode);
    }

    [Fact]
    public async Task Eszamanli_kabullerde_tek_kayit_olusur_hepsi_basarili_doner()
    {
        var (tenant, _, _, _) = await UnacceptedAsync();
        var secondOwner = await TestData.AddUserAsync(Services, tenant.OrganizationId, [(tenant.StoreId, StoreRole.Owner)]);

        var clients = new List<ApiClient>();
        for (var i = 0; i < 8; i++)
        {
            clients.Add(await ApiClient.LoginAsync(Factory, i % 2 == 0 ? tenant.Email : secondOwner, tenant.StoreId)); // parola özeti CPU yükü sıralı
        }

        var results = await Task.WhenAll(clients.Select(async client => (await client.SendAsync(HttpMethod.Post, Accept, CurrentBody())).StatusCode));

        Assert.Equal(1, results.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(7, results.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(1, await AcceptanceCountAsync(tenant.OrganizationId));
    }

    [Fact]
    public async Task Hizmet_sozlesmesi_ile_musteri_KVKK_onayi_birbirinin_yerine_gecmez()
    {
        var (tenant, owner, _, _) = await UnacceptedAsync();

        // Sözleşme kabulü müşteri onayı yazmaz ve müşteri onayı yerine geçmez
        await owner.PostAsync<AgreementDto>(Accept, CurrentBody(), HttpStatusCode.Created);
        var session = await CreateSessionAsync(owner);
        var (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            Assert.Equal(0, await db.ConsentRecords.CountAsync(Ct));
        }

        var photo = await owner.UploadAsync($"/api/v1/sessions/{session.Id}/photos", TestImages.Portrait());
        await ApiClient.AssertProblemAsync(photo, HttpStatusCode.Conflict, "CONSENT_REQUIRED"); // sözleşme kabul edilmiş olsa da müşteri onayı şart

        // Müşteri onayı sözleşme kaydı yazmaz
        await ConsentAsync(owner, session.Id);
        Assert.Equal(1, await AcceptanceCountAsync(tenant.OrganizationId));
        (scope, db) = OpenDb(tenant.OrganizationId);
        using (scope)
        {
            Assert.Equal(1, await db.ConsentRecords.CountAsync(Ct));
        }

        // Ayrı metin, ayrı sürüm, ayrı özet
        Assert.NotEqual(ConsentNotice.Version, ServiceAgreement.Document.Version);
        Assert.Equal(HttpStatusCode.Created, (await owner.UploadAsync($"/api/v1/sessions/{session.Id}/photos", TestImages.Portrait(2))).StatusCode);

        // Müşteri onayı olan oturum, sözleşmesi kabul edilmemiş firmada kullanılamaz (başka firma: onay yok sayılmaz, kapı kapalı)
        var (_, otherOwner, _, _) = await UnacceptedAsync();
        await ApiClient.AssertProblemAsync(await otherOwner.GetAsync($"/api/v1/sessions/{session.Id}"), HttpStatusCode.Forbidden, AgreementEnforcementMiddleware.ErrorCode);
    }

    [Fact]
    public async Task Kabul_kaydi_veritabaninda_yalnizca_eklenir_uygulama_hesabi_guncelleyip_silemez()
    {
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Owner); // fixture kabul kaydı yazar (INSERT serbest)
        Assert.Equal(1, await AcceptanceCountAsync(tenant.OrganizationId));

        await using var connection = new SqlConnection(Factory.TestConnectionString);
        await connection.OpenAsync(Ct);
        foreach (var sql in new[]
                 {
                     $"UPDATE organization_agreement_acceptances SET agreement_version = N'x' WHERE organization_id = '{tenant.OrganizationId}'",
                     $"DELETE FROM organization_agreement_acceptances WHERE organization_id = '{tenant.OrganizationId}'",
                 })
        {
            await using var command = new SqlCommand(sql, connection);
            var denied = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync(Ct));
            Assert.Equal(229, denied.Number); // DENY: sertleştirme betiği (provision/harden) uygulanmış veritabanı
        }

        Assert.Equal(1, await AcceptanceCountAsync(tenant.OrganizationId));
    }

    // ---------------------------------------------------------------- uç nokta bazında atlanamazlık

    /// <summary>
    /// Kimliklendirilmemiş (anonim) uç noktalar ve gerekçeli muaf uç noktalar. Bu listede olmayan HER uç nokta sözleşme kapısına tabidir (varsayılan zorlama);
    /// yeni anonim/muaf uç nokta bu testi kırar: gerekçe yazılıp listeye eklenmeden birleştirilemez.
    /// </summary>
    private static readonly HashSet<string> NotEnforced = new(StringComparer.Ordinal)
    {
        // --- Anonim (kimlik doğrulaması istemez): sağlık, imzalı dosya, geliştirme/OpenAPI, giriş öncesi akışlar
        "ANY /health/live", "ANY /health/ready", "GET /files/{**key}", "GET /openapi/{documentName}.json",
        "GET /api/v1/auth/csrf", "POST /api/v1/auth/login", "POST /api/v1/account/setup-password",
        "GET /api/v1/dev/status", "POST /api/v1/dev/mock-generation", "ANY /hubs/diagnostics", "ANY /hubs/diagnostics/negotiate", // yalnızca Development'ta kayıtlı
        "POST /api/v1/display/pairing", "GET /api/v1/display/pairing/status", // TV eşleştirme kodu: anonim (cihaz henüz kimliksiz)
        // --- Gerekçeli muaf (AgreementExemptMetadata): giriş/çıkış/oturum bilgisi, sözleşme görüntüleme/kabul, platform yönetimi, TV cihazı
        "POST /api/v1/auth/logout", "GET /api/v1/auth/me", "GET /api/v1/agreement/current", "POST /api/v1/organization/agreement-acceptance",
        "GET /api/v1/platform/cost-cases/", "GET /api/v1/platform/cost-cases/{id:guid}", "POST /api/v1/platform/cost-cases/{id:guid}/decisions",
        "POST /api/v1/platform/organizations/", "GET /api/v1/platform/organizations/", "GET /api/v1/platform/organizations/{id:guid}", "POST /api/v1/platform/organizations/{id:guid}/owner/setup-link",
        "GET /api/v1/display/state", "ANY /hubs/display", "ANY /hubs/display/negotiate",
    };

    private static string KeyOf(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        return $"{(methods is { Count: > 0 } ? string.Join(",", methods.Order(StringComparer.Ordinal)) : "ANY")} {endpoint.RoutePattern.RawText}";
    }

    private IEnumerable<RouteEndpoint> AllEndpoints() =>
        Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Where(e => e.RoutePattern.RawText is not null);

    [Fact]
    public void Her_uc_nokta_ya_zorlamaya_tabidir_ya_da_gerekceli_sabit_listededir()
    {
        var problems = new List<string>();
        var actualNotEnforced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in AllEndpoints())
        {
            var key = KeyOf(endpoint);
            if (AgreementEnforcementMiddleware.Enforced(endpoint))
            {
                continue;
            }

            actualNotEnforced.Add(key);
            var exempt = endpoint.Metadata.GetMetadata<AgreementExemptMetadata>();
            var authorized = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0;
            if (authorized && string.IsNullOrWhiteSpace(exempt?.Reason))
            {
                problems.Add($"{key}: kimlik doğrulaması ister ama muaf gerekçesi yok.");
            }
        }

        problems.AddRange(actualNotEnforced.Except(NotEnforced).Select(k => $"YENİ muaf/anonim uç nokta (listede yok): {k}"));
        problems.AddRange(NotEnforced.Except(actualNotEnforced).Select(k => $"Listede ama artık yok veya zorlanıyor: {k}"));
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task Kabul_etmemis_firma_zorlanan_HER_uc_noktada_403_alir_govde_CSRF_ve_yetkiden_bagimsiz()
    {
        var (tenant, owner, _, _) = await UnacceptedAsync();
        var enforced = AllEndpoints().Where(AgreementEnforcementMiddleware.Enforced).ToList();
        Assert.True(enforced.Count > 40, $"Zorlanan uç nokta sayısı beklenenden az: {enforced.Count}");

        var failures = new List<string>();
        foreach (var endpoint in enforced)
        {
            var path = ConcretePath(endpoint.RoutePattern);
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (var method in methods)
            {
                // Bozuk gövde ve CSRF başlığı olmadan da: kapı bağlamadan/filtrelerden ÖNCE çalışır
                // Form (multipart) uç noktalarında yönlendirme içerik türünü eşleştirir (415); doğru türle gönderilir
                var isForm = endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.ContentTypes.Any(c => c.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)) == true;
                using var request = new HttpRequestMessage(new HttpMethod(method), path)
                {
                    Content = method is "GET" or "HEAD" or "DELETE" ? null
                        : isForm ? new MultipartFormDataContent { { new StringContent("x"), "alan" } }
                        : new StringContent("{bozuk", System.Text.Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-Store-Id", tenant.StoreId.ToString());
                request.Headers.Add("Cookie", owner.AuthCookie);
                using var http = Factory.CreateClient();
                var response = await http.SendAsync(request, Ct);
                var body = await response.Content.ReadAsStringAsync(Ct);
                if (response.StatusCode != HttpStatusCode.Forbidden || !body.Contains(AgreementEnforcementMiddleware.ErrorCode, StringComparison.Ordinal))
                {
                    failures.Add($"{method} {path} → {(int)response.StatusCode}: {body[..Math.Min(120, body.Length)]}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string ConcretePath(RoutePattern pattern)
    {
        var path = new System.Text.StringBuilder();
        foreach (var segment in pattern.PathSegments)
        {
            path.Append('/');
            foreach (var part in segment.Parts)
            {
                path.Append(part switch
                {
                    RoutePatternLiteralPart literal => literal.Content,
                    RoutePatternParameterPart { IsCatchAll: true } => "x/y",
                    RoutePatternParameterPart => Guid.NewGuid().ToString(),
                    _ => string.Empty,
                });
            }
        }

        return path.Length == 0 ? "/" : path.ToString();
    }
}
