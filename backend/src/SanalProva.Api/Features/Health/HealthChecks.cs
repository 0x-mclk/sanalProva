using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SanalProva.Infrastructure.ImageWorker;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Storage;

namespace SanalProva.Api.Features.Health;

internal sealed class StorageHealthCheck(IImageStorageService storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await storage.IsHealthyAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Depolama klasörüne yazılamıyor.");
}

/// <summary>Görsel servis yalnızca revizyon motoru için gerekli; erişilemezse uygulama "Degraded" olur, durmaz.</summary>
internal sealed class ImageWorkerHealthCheck(ImageWorkerClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var health = await client.GetHealthAsync(timeout.Token);
            return HealthCheckResult.Healthy(
                $"segmentation={health.Segmentation}",
                new Dictionary<string, object> { ["segmentation"] = health.Segmentation, ["opencv"] = health.OpenCvVersion });
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Görsel işleme servisine ulaşılamıyor.");
        }
    }
}

public static class HealthEndpoints
{
    private const string ReadyTag = "ready";

    public static IServiceCollection AddAppHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("database", HealthStatus.Unhealthy, [ReadyTag])
            .AddCheck<StorageHealthCheck>("storage", HealthStatus.Unhealthy, [ReadyTag])
            .AddCheck<ImageWorkerHealthCheck>("image-worker", HealthStatus.Degraded, [ReadyTag]);
        return services;
    }

    public static IEndpointRouteBuilder MapAppHealthChecks(this IEndpointRouteBuilder app)
    {
        // live: süreç ayakta mı (bağımlılık kontrol edilmez)
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync });

        // ready: veritabanı, depolama ve görsel servis
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains(ReadyTag),
            ResponseWriter = WriteAsync,
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        });
        return app;
    }

    /// <summary>Hata ayrıntısı (exception, bağlantı dizesi) dışarı verilmez; yalnızca durum ve kısa açıklama.</summary>
    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = new
        {
            status = report.Status.ToString(),
            durationMs = (int)report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = (int)e.Value.Duration.TotalMilliseconds,
            }),
        };
        return context.Response.WriteAsJsonAsync(payload);
    }
}
