using System.Text.Json.Nodes;

namespace SanalProva.IntegrationTests;

/// <summary>
/// Frontend'in tip tanımları (frontend/src/shared/api/schema.d.ts) kayıtlı OpenAPI dokümanından üretilir.
/// Backend sözleşmesi değişip doküman yenilenmediyse bu test kırılır: <c>./scripts/gen-api-client.ps1</c> çalıştırın.
/// </summary>
public sealed class OpenApiContractTests(ApiFactory factory)
{
    private static string? FindCommittedSpec()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "frontend", "openapi", "openapi.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [Fact]
    public async Task Kayitli_openapi_dokumani_canli_sozlesmeyle_ayni()
    {
        var path = FindCommittedSpec();
        Assert.SkipWhen(path is null, "frontend/openapi/openapi.json bulunamadı (depo dışı çalıştırma).");

        var live = JsonNode.Parse(await factory.CreateClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken))!;
        var committed = JsonNode.Parse(await File.ReadAllTextAsync(path!, TestContext.Current.CancellationToken))!;

        // "servers" istek adresine bağlıdır; sözleşme kısmı karşılaştırılır
        Assert.True(JsonNode.DeepEquals(live["paths"], committed["paths"]), "Yollar değişti: ./scripts/gen-api-client.ps1 çalıştırın.");
        Assert.True(JsonNode.DeepEquals(live["components"], committed["components"]), "Şemalar değişti: ./scripts/gen-api-client.ps1 çalıştırın.");
    }

    [Fact]
    public async Task Openapi_dokumani_oturum_katalog_ve_uretim_uc_noktalarini_icerir()
    {
        var doc = JsonNode.Parse(await factory.CreateClient().GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken))!;
        var paths = doc["paths"]!.AsObject().Select(p => p.Key).ToHashSet();

        foreach (var expected in new[]
                 {
                     "/api/v1/sessions", "/api/v1/sessions/{id}/photos", "/api/v1/sessions/{id}/generations", "/api/v1/jobs/{id}",
                     "/api/v1/products", "/api/v1/products/{id}/images", "/api/v1/categories", "/api/v1/auth/login",
                 })
        {
            Assert.Contains(expected, paths);
        }
    }
}
