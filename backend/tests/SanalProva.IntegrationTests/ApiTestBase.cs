using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SanalProva.Api.Features.Catalog;
using SanalProva.Api.Features.TryOn;
using SanalProva.Domain.Generation;
using SanalProva.Domain.Tenancy;
using SanalProva.Domain.TryOn;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Tenancy;

namespace SanalProva.IntegrationTests;

/// <summary>Ortak test yardımcıları: veritabanı erişimi, oturum hazırlama, iş bekleme.</summary>
public abstract class ApiTestBase(ApiFactory factory)
{
    protected ApiFactory Factory { get; } = factory;

    protected IServiceProvider Services => Factory.Services;

    /// <summary>
    /// İkinci bir test sunucusu (aynı veritabanı). Üretim işçisi bu sunucuda <b>hiç çalışmaz</b>: işçi döngüleri açılışta hemen iş kapar; paylaşılan veritabanında başka testlerin
    /// işlerini kapıp sunucu kapanınca yarıda bırakırdı (kilit süresi dolana kadar o testler zaman aşımına uğrar).
    /// </summary>
    protected WebApplicationFactory<Program> DerivedHost(Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? configure = null) =>
        Factory.WithWebHostBuilder(b =>
        {
            b.ConfigureTestServices(services =>
            {
                foreach (var worker in services.Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType == typeof(SanalProva.Infrastructure.Generation.GenerationWorker)).ToList())
                {
                    services.Remove(worker);
                }
            });
            configure?.Invoke(b);
        });

    /// <summary>Kiracı bağlamı atanmış kapsamlı DbContext. <c>using</c> ile kullanılmalı.</summary>
    protected (IServiceScope Scope, AppDbContext Db) OpenDb(Guid? organizationId = null)
    {
        var scope = Services.CreateScope();
        if (organizationId is { } org)
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().OrganizationId = org;
        }

        return (scope, scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    internal async Task<(TestTenant Tenant, ApiClient Client)> StaffAsync()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services);
        return (tenant, await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId));
    }

    internal async Task<(TestTenant Tenant, ApiClient Manager, ApiClient Staff)> ManagerAndStaffAsync()
    {
        ApiFactory.RequireDatabase(Factory);
        var tenant = await TestData.CreateTenantAsync(Services, StoreRole.Manager);
        var staffEmail = await TestData.AddUserAsync(Services, tenant.OrganizationId, [(tenant.StoreId, StoreRole.Staff)]);
        return (tenant,
            await ApiClient.LoginAsync(Factory, tenant.Email, tenant.StoreId),
            await ApiClient.LoginAsync(Factory, staffEmail, tenant.StoreId));
    }

    internal static Task<SessionDetail> CreateSessionAsync(ApiClient client, string? label = "Test Müşteri") =>
        client.PostAsync<SessionDetail>("/api/v1/sessions", new { customerLabel = label }, HttpStatusCode.Created);

    internal static object FullConsent(bool extended = false) => new
    {
        noticeVersion = ConsentNotice.Version, processingConsent = true, foreignTransferConsent = true,
        extendedRetentionConsent = extended, isMinor = false, method = "CustomerTapped",
    };

    internal static Task<SessionDetail> ConsentAsync(ApiClient client, Guid sessionId, bool extended = false) =>
        client.PostAsync<SessionDetail>($"/api/v1/sessions/{sessionId}/consent", FullConsent(extended));

    internal static async Task<PhotoDto> UploadPhotoAsync(ApiClient client, Guid sessionId, byte[]? bytes = null)
    {
        var response = await client.UploadAsync($"/api/v1/sessions/{sessionId}/photos", bytes ?? TestImages.Portrait());
        return await ApiClient.ReadAsync<PhotoDto>(response, HttpStatusCode.Created);
    }

    /// <summary>Onaylı, fotoğraflı oturum.</summary>
    internal static async Task<(SessionDetail Session, PhotoDto Photo)> PreparedSessionAsync(ApiClient client)
    {
        var session = await CreateSessionAsync(client);
        await ConsentAsync(client, session.Id);
        var photo = await UploadPhotoAsync(client, session.Id);
        return (session, photo);
    }

    internal static Task<DraftDto> SaveDraftAsync(ApiClient client, Guid sessionId, OutfitMode mode, params Guid[] productIds) =>
        client.PutAsync<DraftDto>($"/api/v1/sessions/{sessionId}/draft", new { mode, items = productIds.Select(p => new { productId = p }).ToArray() });

    internal static Task<JobDto> StartGenerationAsync(ApiClient client, Guid sessionId, string? key = null) =>
        client.PostAsync<JobDto>($"/api/v1/sessions/{sessionId}/generations", new { }, HttpStatusCode.Accepted,
            r => r.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N")));

    internal static async Task<JobDto> WaitForJobAsync(ApiClient client, Guid jobId, Func<JobDto, bool>? done = null, int timeoutSeconds = 20)
    {
        done ??= j => j.Status is GenerationJobStatus.Succeeded or GenerationJobStatus.Failed or GenerationJobStatus.Cancelled;
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        JobDto job;
        do
        {
            job = await client.GetAsync<JobDto>($"/api/v1/jobs/{jobId}");
            if (done(job))
            {
                return job;
            }

            await Task.Delay(40, TestContext.Current.CancellationToken);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"İş {timeoutSeconds} sn içinde tamamlanmadı: {job.Status} ({job.Stage}), deneme {job.Attempt}");
    }
}
