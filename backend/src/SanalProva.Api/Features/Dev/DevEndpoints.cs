using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Ai.Abstractions;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Api.Features.Dev;

public sealed record StrategyInfo(GenerationJobType JobType, string Provider, string ConfiguredProvider, string Model, GenerationJobType? Fallback);

public sealed record DatabaseInfo(bool CanConnect, string? Server, string? Database, IReadOnlyList<string> AppliedMigrations, IReadOnlyList<string> PendingMigrations);

public sealed record DevStatusResponse(
    string Environment, string Version, string Runtime, DateTimeOffset ServerTime,
    DatabaseInfo Database, bool UseMockProviders, IReadOnlyList<StrategyInfo> Strategies);

public sealed record MockGenerationRequest(GenerationJobType JobType);

public sealed record MockGenerationResponse(GenerationJobType JobType, string Provider, string Model, string? ProviderRequestId, int DurationMs, int OutputBytes);

/// <summary>
/// Yalnızca Development ortamında eşlenir. Frontend geliştirme ekranının backend, veritabanı,
/// sağlayıcı stratejisi ve mock adapter zincirini doğrulaması içindir. Kişisel veri döndürmez.
/// </summary>
public static class DevEndpoints
{
    // 1×1 piksel şeffaf PNG: mock zinciri gerçek görsel olmadan denenir
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    public static RouteGroupBuilder MapDevEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/dev").WithTags("Dev");
        group.MapGet("/status", GetStatusAsync);
        group.MapPost("/mock-generation", RunMockGenerationAsync);
        return api;
    }

    private static async Task<DevStatusResponse> GetStatusAsync(
        AppDbContext db, GenerationStrategy strategy, IHostEnvironment env, TimeProvider clock,
        Microsoft.Extensions.Options.IOptionsMonitor<GenerationSettings> settings, CancellationToken ct)
    {
        DatabaseInfo database;
        try
        {
            var canConnect = await db.Database.CanConnectAsync(ct);
            var connection = db.Database.GetDbConnection();
            database = new DatabaseInfo(
                canConnect,
                $"{connection.DataSource}",
                connection.Database,
                canConnect ? [.. await db.Database.GetAppliedMigrationsAsync(ct)] : [],
                canConnect ? [.. await db.Database.GetPendingMigrationsAsync(ct)] : [.. db.Database.GetMigrations()]);
        }
        catch (Npgsql.NpgsqlException)
        {
            database = new DatabaseInfo(false, null, null, [], [.. db.Database.GetMigrations()]);
        }

        var strategies = Enum.GetValues<GenerationJobType>()
            .Where(t => settings.CurrentValue.Strategies.ContainsKey(t))
            .Select(t => strategy.Resolve(t))
            .Select(s => new StrategyInfo(s.JobType, s.Provider, s.ConfiguredProvider, s.Options.Model, s.Fallback))
            .ToList();

        var version = typeof(DevEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return new DevStatusResponse(
            env.EnvironmentName, version, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            clock.GetUtcNow(), database, settings.CurrentValue.UseMockProviders, strategies);
    }

    private static async Task<IResult> RunMockGenerationAsync(MockGenerationRequest request, GenerationStrategy strategy, CancellationToken ct)
    {
        var resolved = strategy.Resolve(request.JobType);
        if (resolved.Provider != GenerationStrategy.MockProvider)
        {
            // Güvenlik ağı: geliştirme ekranı asla ücretli sağlayıcıyı tetiklememeli
            return Results.Problem(
                title: "Geliştirme denemesi yalnızca mock sağlayıcıyla çalışır.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = "MOCK_ONLY" });
        }

        var image = new ImageInput(TinyPng, "image/png");
        var result = request.JobType switch
        {
            GenerationJobType.OutfitSet => await strategy.GetTryOnProvider(resolved).TryOnAsync(image, image, resolved.Options, ct),
            GenerationJobType.FreeCombination => await strategy.GetGenerationProvider(resolved).ComposeOutfitAsync(
                image, [new GarmentReference(image, "Şalvar", "Demo Şalvar", "Bordo", 20, null)], "dev", resolved.Options, ct),
            _ => await strategy.GetEditProvider(resolved).EditAsync(image, "dev", [], resolved.Options, ct),
        };

        return TypedResults.Ok(new MockGenerationResponse(
            request.JobType, result.Provider, result.Model, result.ProviderRequestId, result.Usage.DurationMs, result.Images.Sum(i => i.Content.Length)));
    }
}
