using System.Net;
using Microsoft.EntityFrameworkCore;
using SanalProva.Domain.TryOn;

namespace SanalProva.IntegrationTests;

/// <summary>
/// ÖLÇÜM, DÜZELTME DEĞİL (R3 öncesi): günlük şube kotası ve gerçek moddaki "aynı fotoğraf + seçim" parmak izi kontrolü
/// "say/sorgula, sonra ekle" biçiminde ve kilitsizdir; eşzamanlı isteklerde sınır aşılabilir.
/// Varsayılan olarak atlanır (kırmızı/yeşil anlamı yoktur); <c>SANALPROVA_MEASURE_RACES=1</c> ile çalışır ve sonuçları test çıktısına yazar.
/// R3'te bu ölçümler ideal değeri doğrulayan gerçek testlere dönüşecek.
/// </summary>
public sealed class RaceMeasurementTests(ApiFactory factory) : ApiTestBase(factory), IDisposable
{
    private const int Rounds = 3;
    private const int ConcurrentRequests = 12;
    private readonly RealModeApiFactory _real = new(factory.StorageRoot);

    public void Dispose() => _real.Dispose();

    private static void RequireMeasurement() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SANALPROVA_MEASURE_RACES") == "1", "Yarış ölçümü yalnızca SANALPROVA_MEASURE_RACES=1 ile çalışır.");

    [Fact]
    public async Task Olcum_gunluk_sube_kotasi_eszamanli_isteklerde()
    {
        RequireMeasurement();
        ApiFactory.RequireDatabase(Factory);
        var ct = TestContext.Current.CancellationToken;
        const int quota = 3;
        var accepted = new List<int>();
        for (var round = 0; round < Rounds; round++)
        {
            var (tenant, client) = await StaffAsync();
            var catalog = await TestData.CreateCatalogAsync(Services, tenant.OrganizationId);
            var (session, _) = await PreparedSessionAsync(client);
            await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);
            var (scope, db) = OpenDb(tenant.OrganizationId);
            using (scope)
            {
                await db.Stores.Where(s => s.Id == tenant.StoreId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DailyGenerationQuota, quota), ct);
            }

            var responses = await Task.WhenAll(Enumerable.Range(0, ConcurrentRequests).Select(_ =>
                client.SendAsync(HttpMethod.Post, $"/api/v1/sessions/{session.Id}/generations", new { },
                    configure: r => r.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N")))));
            Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests }));
            accepted.Add(responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"ÖLÇÜM günlük şube kotası = {quota}, eşzamanlı istek = {ConcurrentRequests}: kabul edilen (tur başına) = {string.Join(", ", accepted)}; ideal ≤ {quota}");
        Assert.All(accepted, n => Assert.InRange(n, 1, ConcurrentRequests));
    }

    [Fact]
    public async Task Olcum_gercek_modda_ayni_secimle_eszamanli_istekler_parmak_izi()
    {
        RequireMeasurement();
        ApiFactory.RequireDatabase(Factory);
        var created = new List<int>();
        for (var round = 0; round < Rounds; round++)
        {
            var tenant = await TestData.CreateTenantAsync(_real.Services);
            var catalog = await TestData.CreateCatalogAsync(_real.Services, tenant.OrganizationId);
            var client = await ApiClient.LoginAsync(_real, tenant.Email, tenant.StoreId);
            var (session, _) = await PreparedSessionAsync(client);
            await SaveDraftAsync(client, session.Id, OutfitMode.OutfitSet, catalog.SetProduct);

            // Farklı Idempotency-Key, aynı fotoğraf + seçim: gerçek modda ideal olan tek ücretli iş
            var started = await Task.WhenAll(Enumerable.Range(0, ConcurrentRequests).Select(_ => StartGenerationAsync(client, session.Id)));
            created.Add(started.Select(j => j.Id).Distinct().Count());
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"ÖLÇÜM gerçek mod parmak izi, eşzamanlı istek = {ConcurrentRequests}: açılan ücretli iş (tur başına) = {string.Join(", ", created)}; ideal = 1");
        Assert.All(created, n => Assert.InRange(n, 1, ConcurrentRequests));
    }
}
