using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Ai.Abstractions;
using SanalProva.Infrastructure.Ai.Fal;
using SanalProva.Infrastructure.Ai.Fashn;
using SanalProva.Infrastructure.Ai.Gemini;
using SanalProva.Infrastructure.Ai.Pruna;
using SanalProva.Infrastructure.Ai.TryOn;
using SanalProva.Infrastructure.Images;

namespace SanalProva.IntegrationTests;

/// <summary>
/// KONTROLLÜ GERÇEK API TESTİ (ÜCRETLİ). Varsayılan olarak <b>atlanır</b>; hiçbir koşulda kendiliğinden çalışmaz.
/// Çalışması için üç şey birlikte gerekir:
/// <list type="number">
/// <item><c>SANALPROVA_LIVE_AI=ODEME-ONAYLANDI</c> ortam değişkeni (yalnızca proje sahibinin açık onayından sonra, docs/11 §5);</item>
/// <item>API anahtarları: API projesinin .NET User Secrets kaydı (<c>Ai:Fal:ApiKey</c>, <c>Ai:Pruna:ApiKey</c>) veya aynı adlı ortam değişkenleri
/// (<c>Ai__Fal__ApiKey</c>…; ortam değişkeni önceliklidir). Kaynağa/depoya yazılmaz, günlüğe ve kayda yazılmaz;</item>
/// <item><c>testdata/ai-live/</c> klasöründe <b>izinli veya sentetik</b> test görselleri (gerçek müşteri fotoğrafı DEĞİL): <c>person.jpg</c> ve <c>kaftan.jpg</c> (eski adımlar için ayrıca <c>set.jpg</c>, <c>salvar.jpg</c>, <c>gomlek.jpg</c>, <c>yelek.jpg</c>, <c>kemer.jpg</c>, <c>kemer-yeni.jpg</c>).</item>
/// </list>
/// Harcama sınırları: toplam <c>SANALPROVA_LIVE_AI_BUDGET_USD</c> (varsayılan 0.50) ve çağrı başı <c>SANALPROVA_LIVE_AI_MAX_CALL_USD</c> (varsayılan 0.10).
/// Her çağrıdan önce gerçek görsel boyutlarıyla hesaplanan en kötü durum ikisiyle de karşılaştırılır; aşılacaksa çağrı yapılmaz.
/// Adımlar: <c>SANALPROVA_LIVE_STEPS</c> (virgüllü; varsayılan <c>seedream,gpt-medium</c>; ayrıca <c>gpt-low</c>, <c>gpt-high</c>; pahalı: <c>gpt-auto</c>, <c>gpt-xhigh</c>, <c>gpt-max</c>;
/// eski: <c>fashn</c>, <c>gemini-combo</c>…). Pruna adımları (<c>pruna</c>, <c>pruna-turbo</c>) görselleri Pruna'ya dosya olarak yüklediği için (docs/12 §4)
/// ayrıca <c>SANALPROVA_LIVE_PRUNA=SENTETIK-GORSEL</c> ister: yalnızca sentetik/izinli görsellerle çalıştırılır.
/// Görseller gönderilmeden önce uygulamadaki gibi doğrulanıp yeniden kodlanır (EXIF/konum silinir, boyut sınırlanır).
/// Çıktılar ve maliyet kaydı (başarısız çağrılar en kötü durum tutarıyla dahil) <c>docs/ai-benchmark/runs/</c> altına yazılır (git'e girmez).
/// </summary>
public sealed class LiveAiTests
{
    private const string Gate = "SANALPROVA_LIVE_AI";
    private const string Approval = "ODEME-ONAYLANDI";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private const string PrunaGate = "SANALPROVA_LIVE_PRUNA";
    private const string PrunaApproval = "SENTETIK-GORSEL";

    /// <param name="WorstCaseUsd">Çağrı öncesi bütçe kapısında kullanılan en kötü durum.</param>
    /// <param name="Status">"ok" veya hata özeti (gövde/anahtar içermez). Başarısız çağrıda tahmini tutar en kötü durumdur (ücret doğmuş olabilir).</param>
    private sealed record Entry(
        string Step, string Provider, string Model, string Parameters, int DurationMs, decimal? Credits, int? InputTokens, int? OutputTokens,
        decimal EstimatedUsd, decimal WorstCaseUsd, decimal? ActualUsd, string? RequestId, string? OutputFile, string Status);

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    [Fact]
    public async Task Kontrollu_gercek_api_testi_onay_olmadan_calismaz()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(Gate) == Approval, $"Ücretli gerçek API testi kapalı. Proje sahibinin onayından sonra {Gate}={Approval} ile açılır (docs/11).");
        var root = FindRepoRoot() ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
        var inputs = Path.Combine(root, "testdata", "ai-live");
        Assert.SkipUnless(File.Exists(Path.Combine(inputs, "person.jpg")), "testdata/ai-live/person.jpg (izinli/sentetik test fotoğrafı) yok.");

        Assert.SkipUnless(File.Exists(Path.Combine(inputs, "kaftan.jpg")), "testdata/ai-live/kaftan.jpg (test ürün görseli) yok.");

        var budget = decimal.Parse(Environment.GetEnvironmentVariable("SANALPROVA_LIVE_AI_BUDGET_USD") ?? "0.50", CultureInfo.InvariantCulture);
        var maxCall = decimal.Parse(Environment.GetEnvironmentVariable("SANALPROVA_LIVE_AI_MAX_CALL_USD") ?? "0.10", CultureInfo.InvariantCulture);
        // Varsayılan adımlar yalnızca Seedream ve GPT Image medium'dur. Pruna ayrı kapı ister; eski FASHN/Gemini adımları (fashn, fashn-2k, gemini-combo,
        // gemini-edit) yalnızca açıkça listelenirse çalışır; beş parçalı kombin bu aşamanın kapsamı dışındadır.
        var steps = (Environment.GetEnvironmentVariable("SANALPROVA_LIVE_STEPS") ?? "seedream,gpt-medium").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (steps.Any(s => s.StartsWith("pruna", StringComparison.Ordinal)))
        {
            Assert.True(Environment.GetEnvironmentVariable(PrunaGate) == PrunaApproval,
                $"Pruna adımı görselleri Pruna'ya yükler (docs/12 §4): yalnızca sentetik/izinli görsellerle, {PrunaGate}={PrunaApproval} ile çalışır. Hiçbir çağrı yapılmadı.");
        }

        // Anahtarlar: ortam değişkeni (öncelikli) veya API projesinin User Secrets kaydı. Değerler hiçbir yere yazdırılmaz.
        var config = new ConfigurationBuilder().AddUserSecrets(typeof(Program).Assembly, optional: true).AddEnvironmentVariables().Build();
        string Key(string provider) => config[$"Ai:{provider}:ApiKey"] ?? string.Empty;
        var needsFal = steps.Any(s => s is "seedream" || s.StartsWith("gpt-", StringComparison.Ordinal));
        Assert.True(!needsFal || Key("Fal").Length > 0, "fal anahtarı tanımlı değil (Ai:Fal:ApiKey). Hiçbir çağrı yapılmadı.");
        Assert.True(!steps.Any(s => s.StartsWith("pruna", StringComparison.Ordinal)) || Key("Pruna").Length > 0, "Pruna anahtarı tanımlı değil (Ai:Pruna:ApiKey). Hiçbir çağrı yapılmadı.");
        var settings = new GenerationSettings();
        var monitor = new StaticMonitor(settings);
        var costs = new CostEstimator(monitor);
        var fal = new FalQueueClient(new HttpClient { BaseAddress = new Uri("https://queue.fal.run/"), Timeout = Timeout.InfiniteTimeSpan },
            Options.Create(new FalOptions { ApiKey = Key("Fal") }));
        var pruna = new PrunaTryOnProvider(new HttpClient { BaseAddress = new Uri("https://api.pruna.ai/"), Timeout = Timeout.InfiniteTimeSpan },
            Options.Create(new PrunaOptions { ApiKey = Key("Pruna") }), monitor);
        var fashn = new FashnTryOnProvider(new HttpClient { BaseAddress = new Uri("https://api.fashn.ai/"), Timeout = Timeout.InfiniteTimeSpan },
            Options.Create(new FashnOptions { ApiKey = Key("Fashn") }), costs);
        var gemini = new GeminiProvider(new HttpClient { BaseAddress = new Uri("https://generativelanguage.googleapis.com/"), Timeout = Timeout.InfiniteTimeSpan },
            Options.Create(new GeminiOptions { ApiKey = Key("Gemini") }), costs);

        var runDir = Path.Combine(root, "docs", "ai-benchmark", "runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(runDir);
        var entries = new List<Entry>();
        var spent = 0m;
        var ct = TestContext.Current.CancellationToken;

        // Uygulamadaki yükleme hattıyla aynı: biçim/boyut doğrulanır, yeniden kodlanır (EXIF ve konum bilgisi silinir); geçersizse hiçbir çağrı yapılmaz
        ImageInput Load(string name, ImageProcessingOptions options) =>
            new(ImageProcessor.Process(File.ReadAllBytes(Path.Combine(inputs, name)), options).Main, "image/jpeg");
        ImageInput Garment(string name) => Load(name, ImageProcessingOptions.CatalogImage);
        var person = Load("person.jpg", ImageProcessingOptions.CustomerPhoto);
        var kaftan = Garment("kaftan.jpg");
        decimal lastWorst = 0m;
        string? lastRequestId = null;

        void Guard(decimal worstCase)
        {
            Assert.True(worstCase <= maxCall, $"Çağrı başı sınır aşılacaktı ({worstCase:0.0000} > {maxCall:0.0000} USD): çağrı yapılmadı.");
            Assert.True(spent + worstCase <= budget, $"Bütçe aşılacaktı ({spent + worstCase:0.0000} > {budget:0.0000} USD): çağrı yapılmadı.");
            lastWorst = worstCase;
            lastRequestId = null;
        }

        // İstek kimliği gönderim anında yakalanır: hata/zaman aşımında sağlayıcı panelinde aynı istek bulunabilir
        ProviderRequestOptions Tracked(ProviderRequestOptions options) => options with { OnSubmitted = id => { lastRequestId = id; return Task.CompletedTask; } };

        async Task Run(string step, string parameters, string provider, string model, Func<Task<ProviderResult>> call)
        {
            ProviderResult result;
            try
            {
                result = await call();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Ücret doğmuş olabilir: en kötü durum tutarı kayda ve harcamaya yazılır, kayıt kapatılır, test durur
                spent += lastWorst;
                var status = ex is ProviderException p ? $"hata: {p.Code}" : $"hata: {ex.GetType().Name}";
                entries.Add(new Entry(step, provider, model, parameters, 0, null, null, null, lastWorst, lastWorst, null, lastRequestId, null, status));
                WriteRecord(runDir, entries, spent, budget, maxCall);
                throw;
            }

            var file = $"{step}.jpg";
            File.WriteAllBytes(Path.Combine(runDir, file), result.Images[0].Content);
            spent += result.Usage.EstimatedCostUsd;
            var valid = "ok";
            try
            {
                OutputValidator.Validate(result.Images[0], person);
            }
            catch (ProviderException invalid)
            {
                valid = $"geçersiz çıktı: {invalid.Code}";
            }

            entries.Add(new Entry(step, result.Provider, result.Model, parameters, result.Usage.DurationMs, result.Usage.Credits, result.Usage.InputTokens, result.Usage.OutputTokens,
                result.Usage.EstimatedCostUsd, lastWorst, result.Usage.ActualCostUsd, result.ProviderRequestId, file, valid));
            WriteRecord(runDir, entries, spent, budget, maxCall); // her adımdan sonra: sonraki adım çökse bile kayıt kalır
        }

        // Tek kaftan: aynı kişi ve aynı kaftan görseliyle her model/kalite; yüz, kumaş, nakış ve ürün sadakati kayıtta puanlanır
        var tryOnSteps = new (string Step, string ModelKey, string Quality)[]
        {
            ("seedream", TryOnModels.Seedream, "standard"),
            ("gpt-low", TryOnModels.GptImage, "low"), ("gpt-medium", TryOnModels.GptImage, "medium"), ("gpt-high", TryOnModels.GptImage, "high"),
            ("gpt-auto", TryOnModels.GptImage, "auto"), ("gpt-xhigh", TryOnModels.GptImage, "xhigh"), ("gpt-max", TryOnModels.GptImage, "max"), // yalnızca açıkça listelenirse
            ("pruna", TryOnModels.Pruna, "standard"), ("pruna-turbo", TryOnModels.Pruna, "turbo"),
        };
        foreach (var (step, modelKey, quality) in tryOnSteps)
        {
            if (!steps.Contains(step))
            {
                continue;
            }

            var model = TryOnModels.Find(modelKey)!;
            var tier = model.Quality(quality)!;
            var parameters = new Dictionary<string, string>(tier.Parameters) { ["model_key"] = model.Key, ["tier"] = tier.Key };
            if (model.Provider != "Pruna")
            {
                parameters["prompt"] = PromptBuilder.BuildSingleGarment("Kaftan", "Test kaftanı", null, isCompleteSet: false);
            }

            var options = Tracked(new ProviderRequestOptions(model.ModelId, parameters, Seed: 42));
            Guard(TryOnModels.Price(model, tier, settings.Pricing, ProviderHttp.CostInput(options, person, kaftan)).WorstCase); // gerçek görsel boyutlarıyla en kötü durum
            ITryOnProvider provider = model.Provider switch
            {
                "FalSeedream" => new FalSeedreamTryOnProvider(fal, monitor),
                "FalGptImage" => new FalGptImageTryOnProvider(fal, monitor),
                _ => pruna,
            };
            await Run($"kaftan-{step}", $"{model.DisplayName} · {tier.Label}", model.Provider, model.ModelId, () => provider.TryOnAsync(person, kaftan, options, ct));
        }

        foreach (var (step, resolution) in new[] { ("fashn", "1k"), ("fashn-2k", "2k") })
        {
            if (!steps.Contains(step))
            {
                continue;
            }

            var options = Tracked(new ProviderRequestOptions("tryon-max", new Dictionary<string, string> { ["resolution"] = resolution, ["generation_mode"] = "balanced" }, Seed: 42));
            Guard(costs.WorstCasePerCallUsd("Fashn", options.Model, options.Parameters, 2));
            var set = Garment("set.jpg");
            await Run($"eski-fashn-{resolution}", $"resolution={resolution}, generation_mode=balanced", "Fashn", options.Model, () => fashn.TryOnAsync(person, set, options, ct));
        }

        if (steps.Contains("gemini-combo"))
        {
            var garments = new[] { ("Şalvar", "salvar.jpg", 10), ("Gömlek", "gomlek.jpg", 20), ("Yelek", "yelek.jpg", 30), ("Kaftan", "kaftan.jpg", 40), ("Kemer", "kemer.jpg", 50) }
                .Select(g => new GarmentReference(Garment(g.Item2), g.Item1, g.Item1, null, g.Item3, null)).ToList();
            var options = new ProviderRequestOptions("gemini-3-pro-image", new Dictionary<string, string> { ["image_size"] = "2K" }, Seed: 42);
            Guard(costs.WorstCasePerCallUsd("Gemini", options.Model, options.Parameters, 6));
            await Run("eski-gemini-pro-5-parca", "image_size=2K", "Gemini", options.Model, () => gemini.ComposeOutfitAsync(person, garments, PromptBuilder.BuildFreeCombination(garments), options, ct));
        }

        if (steps.Contains("gemini-edit") && entries.Count > 0)
        {
            var baseImage = new ImageInput(File.ReadAllBytes(Path.Combine(runDir, entries[^1].OutputFile!)), "image/jpeg");
            var options = new ProviderRequestOptions("gemini-nano-banana-2.1", new Dictionary<string, string> { ["image_size"] = "2K" }, Seed: 42);
            Guard(costs.WorstCasePerCallUsd("Gemini", options.Model, options.Parameters, 2));
            var instruction = PromptBuilder.BuildProductSwap("Kemer", "Yeni Kemer", null);
            var newBelt = Garment("kemer-yeni.jpg");
            await Run("eski-gemini-2.1-revizyon", "image_size=2K", "Gemini", options.Model, () => gemini.EditAsync(baseImage, instruction, [newBelt], options, ct));
        }

        WriteRecord(runDir, entries, spent, budget, maxCall);
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Equal("ok", e.Status));
    }

    private static void WriteRecord(string runDir, List<Entry> entries, decimal spent, decimal budget, decimal maxCall)
    {
        File.WriteAllText(Path.Combine(runDir, "kayit.json"), JsonSerializer.Serialize(new { budgetUsd = budget, maxCallUsd = maxCall, estimatedSpentUsd = spent, entries }, Indented));
        var md = new StringBuilder();
        md.AppendLine("# Gerçek API test kaydı");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"Tahmini toplam harcama: **{spent:0.0000} USD** (toplam bütçe {budget:0.00} USD, çağrı başı sınır {maxCall:0.00} USD). Tutarlar tahmindir; fatura sağlayıcıdadır. Başarısız çağrılar en kötü durum tutarıyla sayılır.");
        md.AppendLine();
        md.AppendLine("| Adım | Durum | Sağlayıcı / model | Parametre | Süre | Kredi | Jeton (gir/çık) | Tahmini $ | En kötü $ | Bildirilen $ | İstek kimliği | Kimlik (1-5) | Poz (1-5) | Nakış/desen (1-5) | Renk (1-5) | Artefakt | Not |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var e in entries)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {e.Step} | {e.Status} | {e.Provider} {e.Model} | {e.Parameters} | {e.DurationMs / 1000.0:0.0} sn | {e.Credits?.ToString(CultureInfo.InvariantCulture) ?? "—"} | {e.InputTokens?.ToString(CultureInfo.InvariantCulture) ?? "—"} / {e.OutputTokens?.ToString(CultureInfo.InvariantCulture) ?? "—"} | {e.EstimatedUsd:0.0000} | {e.WorstCaseUsd:0.0000} | {e.ActualUsd?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "—"} | {e.RequestId} |  |  |  |  |  |  |");
        }

        md.AppendLine();
        md.AppendLine("Puanlama: görsel çıktılar (`*.jpg`) girdi fotoğrafı ve ürün görselleriyle yan yana açılıp iki değerlendirici tarafından doldurulur (docs/11 §6).");
        File.WriteAllText(Path.Combine(runDir, "kayit.md"), md.ToString());
    }

    private sealed class StaticMonitor(GenerationSettings value) : IOptionsMonitor<GenerationSettings>
    {
        public GenerationSettings CurrentValue { get; } = value;

        public GenerationSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<GenerationSettings, string?> listener) => null;
    }
}
