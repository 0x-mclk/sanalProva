using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using SanalProva.Api.Features.Account;
using SanalProva.Api.Features.Admin;
using SanalProva.Api.Features.Agreement;
using SanalProva.Api.Features.Auth;
using SanalProva.Api.Features.Catalog;
using SanalProva.Api.Features.Dev;
using SanalProva.Api.Features.Displays;
using SanalProva.Api.Features.Files;
using SanalProva.Api.Features.Generation;
using SanalProva.Api.Features.Health;
using SanalProva.Api.Features.Platform;
using SanalProva.Api.Features.TryOn;
using SanalProva.Api.Hubs;
using SanalProva.Api.Infrastructure;
using SanalProva.Infrastructure;
using SanalProva.Infrastructure.Identity;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Tenancy;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, logger) => logger.ReadFrom.Configuration(context.Configuration));

// --- Altyapı (veritabanı, depolama, AI sağlayıcıları, görsel servis) ---
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddInfrastructure(builder.Configuration);

// --- Kimlik: HttpOnly çerez, API için yönlendirme yerine 401/403 ---
builder.Services
    .AddIdentityCore<AppUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 10;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddSignInManager()
    .AddClaimsPrincipalFactory<AppUserClaimsPrincipalFactory>()
    .AddEntityFrameworkStores<AppDbContext>();

var authentication = builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme);
authentication.AddIdentityCookies();
// TV cihaz belirteci: personel çerezinden ayrı şema; yalnızca DisplayDevice politikalı uç noktalar/hub bu şemayla kimlik doğrular
authentication.AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>(DeviceTokenDefaults.Scheme, null);
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "sp_auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(DisplayHub.DevicePolicy, policy => policy
        .AddAuthenticationSchemes(DeviceTokenDefaults.Scheme)
        .RequireAuthenticatedUser()
        .RequireClaim(DeviceTokenDefaults.DeviceIdClaim));

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = AntiforgeryFilter.HeaderName;
    options.Cookie.Name = "sp_csrf";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("Security:LoginRateLimitPerMinute", 5), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    // TV eşleştirme: kod üretimi, durum yoklaması, kod girme ve durum çekme için IP başına sınırlar (kod tahminini ve kod uzayını doldurmayı zorlaştırır)
    void AddIpLimit(string policy, string key, int defaultLimit) =>
        options.AddPolicy(policy, http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue(key, defaultLimit), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    AddIpLimit(DisplayEndpoints.PairingRateLimit, "Security:DisplayPairingRateLimitPerMinute", 10);
    AddIpLimit(DisplayEndpoints.PairingStatusRateLimit, "Security:DisplayPairingStatusRateLimitPerMinute", 60);
    AddIpLimit(DisplayEndpoints.ClaimRateLimit, "Security:DisplayClaimRateLimitPerMinute", 20);
    AddIpLimit(DisplayEndpoints.DeviceStateRateLimit, "Security:DisplayStateRateLimitPerMinute", 300);
    AddIpLimit(AccountEndpoints.SetupPasswordRateLimit, "Security:SetupPasswordRateLimitPerMinute", 10);
});

// --- API ---
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddValidation();
builder.Services.AddScoped<ImageLinks>();
builder.Services.AddScoped<SessionReader>();
builder.Services.AddScoped<AuditTrail>();
builder.Services.AddScoped<PlatformOnboardingService>();
builder.Services.AddSingleton<IServiceAgreementSource, StaticServiceAgreementSource>();
builder.Services.AddSingleton<AgreementGate>();
builder.Services.AddSingleton<SanalProva.Infrastructure.Generation.IJobNotifier, SignalRJobNotifier>();
builder.Services.AddSingleton<SanalProva.Infrastructure.Displays.IDisplayNotifier, SignalRDisplayNotifier>();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 20 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 20 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    // Sayılar yalnızca JSON sayısı olarak okunur: OpenAPI'de "integer | string" yerine saf "integer" üretilir (frontend tipleri doğru çıkar)
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.Services.AddOpenApi();
builder.Services.AddSignalR().AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddAppHealthChecks();

var app = builder.Build();

// Operatör komutları (platform bootstrap/paid-ai/...): web sunucusu başlatılmadan çalışır ve süreç biter; HTTP ile tetiklenemez
if (PlatformCommands.IsCommand(args))
{
    return await PlatformCommands.RunAsync(app.Services, args, Console.Out, CancellationToken.None);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseApiSecurityHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseSerilogRequestLogging(); // Yalnızca yol loglanır; sorgu dizesi (imzalı URL) loglanmaz
app.UseRateLimiter();
app.UseAuthentication();
app.UseTenantContext();
app.UseAuthorization();
app.UseAgreementEnforcement();

app.MapAppHealthChecks();
app.MapFileEndpoints();

var api = app.MapGroup("/api/v1").AddEndpointFilter<AntiforgeryFilter>();
api.MapAuthEndpoints();
api.MapAccountEndpoints();
api.MapAgreementEndpoints();
api.MapCatalogEndpoints();
api.MapSessionEndpoints();
api.MapGenerationEndpoints();
api.MapDisplayManagementEndpoints();
api.MapUsageEndpoints();
api.MapPlatformCostEndpoints();
api.MapPlatformOrganizationEndpoints();
api.MapAiQualityEndpoints();
api.MapAiModelEndpoints();
app.MapDisplayDeviceEndpoints();

app.MapHub<StaffHub>(StaffHub.Path);
app.MapHub<DisplayHub>(DisplayHub.Path).AllowWithoutAgreement("TV cihazı: ayrı cihaz belirteci kimliği; sözleşmesi kabul edilmemiş firmada personel oturumu zaten açılamaz, TV durumu yalnızca bağlı oturumdan gelir");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    api.MapDevEndpoints();
    app.MapHub<DiagnosticsHub>(DiagnosticsHub.Path);

    if (app.Configuration.GetValue("Seed:Enabled", false))
    {
        await DevDataSeeder.SeedAsync(app.Services);
    }
}

await app.RunAsync();
return 0;

/// <summary>Entegrasyon testleri (WebApplicationFactory) için.</summary>
public partial class Program;
