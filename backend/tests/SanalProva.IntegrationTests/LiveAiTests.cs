using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Ai.Abstractions;
using SanalProva.Infrastructure.Ai.Fal;
using SanalProva.Infrastructure.Ai.Fashn;
using SanalProva.Infrastructure.Ai.Gemini;
using SanalProva.Infrastructure.Ai.Pruna;
using SanalProva.Infrastructure.Ai.TryOn;

namespace SanalProva.IntegrationTests;

/// <summary>
/// KONTROLLÜ GERÇEK API TESTİ (ÜCRETLİ). Varsayılan olarak <b>atlanır</b>; hiçbir koşulda kendiliğinden çalışmaz.
/// Çalışması için üç şey birlikte gerekir:
/// <list type="number">
/// <item><c>SANALPROVA_LIVE_AI=ODEME-ONAYLANDI</c> ortam değişkeni (yalnızca proje sahibinin açık onayından sonra, docs/11 §5);</item>
/// <item>API anahtarları: <c>Ai__Fal__ApiKey</c> ve/veya <c>Ai__Pruna__ApiKey</c> ortam değişkeni (kaynağa/depoya yazılmaz; eski adımlar için <c>Ai__Fashn__ApiKey</c>, <c>Ai__Gemini__ApiKey</c>);</item>
/// <item><c>testdata/ai-live/</c> klasöründe <b>izinli veya sentetik</b> test görselleri (gerçek müşteri fotoğrafı DEĞİL): <c>person.jpg</c> ve <c>kaftan.jpg</c> (eski adımlar için ayrıca <c>set.jpg</c>, <c>salvar.jpg</c>, <c>gomlek.jpg</c>, <c>yelek.jpg</c>, <c>kemer.jpg</c>, <c>kemer-yeni.jpg</c>).</item>
/// </list>
/// Harcama sınırı: <c>SANALPROVA_LIVE_AI_BUDGET_USD</c> (varsayılan 0.50). Her çağrıdan önce en kötü durum tahmini kalan bütçeyle karşılaştırılır; aşılacaksa çağrı yapılmaz.
/// Adımlar: <c>SANALPROVA_LIVE_STEPS</c> (virgüllü; varsayılan <c>seedream,gpt-medium,pruna</c>; ayrıca <c>gpt-low</c>, <c>gpt-high</c>, <c>pruna-turbo</c>; pahalı: <c>gpt-auto</c>, <c>gpt-xhigh</c>, <c>gpt-max</c>; eski: <c>fashn</c>, <c>gemini-combo</c>…). Çıktılar ve maliyet kaydı <c>docs/ai-benchmark/runs/</c> altına yazılır (git'e girmez).
/// </summary>
public sealed class LiveAiTests
{
    private const string Gate = "SANALPROVA_LIVE_AI";
    private const string Approval = "ODEME-ONAYLANDI";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private sealed record Entry(string Step, string Provider, string Model, string Parameters, int DurationMs, decimal? Credits, int? InputTokens, int? OutputTokens, decimal EstimatedUsd, decimal? ActualUsd, string? RequestId, string OutputFile);

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

        var budget = decimal.Parse(Environment.GetEnvironmentVariable("SANALPROVA_LIVE_AI_BUDGET_USD") ?? "0.50", CultureInfo.InvariantCulture);
        // Varsayılan adımlar yalnızca tek kaftan giydirme modelleridir. Eski FASHN/Gemini adımları (fashn, fashn-2k, gemini-combo, gemini-edit)
        // yalnızca açıkça listelenirse çalışır; beş parçalı kombin bu aşamanın kapsamı dışındadır.
        var steps = (Environment.GetEnvironmentVariable("SANALPROVA_LIVE_STEPS") ?? "seedream,gpt-medium,pruna").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var settings = new GenerationSettings();
        var monitor = new StaticMonitor(settings);
        var costs = new CostEstimator(monitor);
        var fal = new FalQueueClient(new HttpClient { BaseAddress = new Uri("https://queue.fal.run/"), Timeout = Timeout.InfiniteTimeSpan },
            Options.Create(new FalOptions { ApiKey = Environment.GetEnvironmentVariable("Ai__Fal__ApiKey") ?? string.Empty }));
        var pruna = new PrunaTryOnProvider(new HttpClient { BaseAddress = new Uri("https://api.pruna.ai/"), Timeout = Timeout.InfiniteTimeSpan },
            Options.Create(new PrunaOptions { ApiKey = Environment.GetEnvironmentVariable("Ai__Pruna__ApiKey") ?? string.Empty }), monitor);
        var fashn = new FashnTryOnProvider(new HttpClient { BaseAddress = new Uri("https://api.fashn.ai/"), Timeout = Timeout.InfiniteTimeSpan },
            Options.Create(new FashnOptions { ApiKey = Environment.GetEnvironmentVariable("Ai__Fashn__ApiKey") ?? string.Empty }), costs);
        var gemini = new GeminiProvider(new HttpClient { BaseAddress = new Uri("https://generativelanguage.googleapis.com/"), Timeout = Timeout.InfiniteTimeSpan },
            Options.Create(new GeminiOptions { ApiKey = Environment.GetEnvironmentVariable("Ai__Gemini__ApiKey") ?? string.Empty }), costs);

        var runDir = Path.Combine(root, "docs", "ai-benchmark", "runs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(runDir);
        var entries = new List<Entry>();
        var spent = 0m;
        var ct = TestContext.Current.CancellationToken;

        ImageInput Load(string name) => new(File.ReadAllBytes(Path.Combine(inputs, name)), "image/jpeg");
        var person = Load("person.jpg");

        void Guard(decimal worstCase)
        {
            Assert.True(spent + worstCase <= budget, $"Bütçe aşılacaktı ({spent + worstCase:0.0000} > {budget:0.0000} USD): çağrı yapılmadı.");
        }

        void Record(string step, ProviderResult result, string parameters)
        {
            var file = $"{step}.jpg";
            File.WriteAllBytes(Path.Combine(runDir, file), result.Images[0].Content);
            OutputValidator.Validate(result.Images[0], person);
            spent += result.Usage.EstimatedCostUsd;
            entries.Add(new Entry(step, result.Provider, result.Model, parameters, result.Usage.DurationMs, result.Usage.Credits, result.Usage.InputTokens, result.Usage.OutputTokens,
                result.Usage.EstimatedCostUsd, result.Usage.ActualCostUsd, result.ProviderRequestId, file));
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

            var options = new ProviderRequestOptions(model.ModelId, parameters, Seed: 42);
            var garmentImage = Load("kaftan.jpg");
            Guard(TryOnModels.Price(model, tier, settings.Pricing, ProviderHttp.CostInput(options, person, garmentImage)).WorstCase); // gerçek görsel boyutlarıyla en kötü durum
            ITryOnProvider provider = model.Provider switch
            {
                "FalSeedream" => new FalSeedreamTryOnProvider(fal, monitor),
                "FalGptImage" => new FalGptImageTryOnProvider(fal, monitor),
                _ => pruna,
            };
            Record($"kaftan-{step}", await provider.TryOnAsync(person, garmentImage, options, ct), $"{model.DisplayName} · {tier.Label}");
        }

        foreach (var (step, resolution) in new[] { ("fashn", "1k"), ("fashn-2k", "2k") })
        {
            if (!steps.Contains(step))
            {
                continue;
            }

            var options = new ProviderRequestOptions("tryon-max", new Dictionary<string, string> { ["resolution"] = resolution, ["generation_mode"] = "balanced" }, Seed: 42);
            Guard(costs.WorstCasePerCallUsd("Fashn", options.Model, options.Parameters, 2));
            Record($"eski-fashn-{resolution}", await fashn.TryOnAsync(person, Load("set.jpg"), options, ct), $"resolution={resolution}, generation_mode=balanced");
        }

        if (steps.Contains("gemini-combo"))
        {
            var garments = new[] { ("Şalvar", "salvar.jpg", 10), ("Gömlek", "gomlek.jpg", 20), ("Yelek", "yelek.jpg", 30), ("Kaftan", "kaftan.jpg", 40), ("Kemer", "kemer.jpg", 50) }
                .Select(g => new GarmentReference(Load(g.Item2), g.Item1, g.Item1, null, g.Item3, null)).ToList();
            var options = new ProviderRequestOptions("gemini-3-pro-image", new Dictionary<string, string> { ["image_size"] = "2K" }, Seed: 42);
            Guard(costs.WorstCasePerCallUsd("Gemini", options.Model, options.Parameters, 6));
            Record("eski-gemini-pro-5-parca", await gemini.ComposeOutfitAsync(person, garments, PromptBuilder.BuildFreeCombination(garments), options, ct), "image_size=2K");
        }

        if (steps.Contains("gemini-edit") && entries.Count > 0)
        {
            var baseImage = new ImageInput(File.ReadAllBytes(Path.Combine(runDir, entries[^1].OutputFile)), "image/jpeg");
            var options = new ProviderRequestOptions("gemini-nano-banana-2.1", new Dictionary<string, string> { ["image_size"] = "2K" }, Seed: 42);
            Guard(costs.WorstCasePerCallUsd("Gemini", options.Model, options.Parameters, 2));
            var instruction = PromptBuilder.BuildProductSwap("Kemer", "Yeni Kemer", null);
            Record("eski-gemini-2.1-revizyon", await gemini.EditAsync(baseImage, instruction, [Load("kemer-yeni.jpg")], options, ct), "image_size=2K");
        }

        WriteRecord(runDir, entries, spent, budget);
        Assert.NotEmpty(entries);
    }

    private static void WriteRecord(string runDir, List<Entry> entries, decimal spent, decimal budget)
    {
        File.WriteAllText(Path.Combine(runDir, "kayit.json"), JsonSerializer.Serialize(new { budgetUsd = budget, estimatedSpentUsd = spent, entries }, Indented));
        var md = new StringBuilder();
        md.AppendLine("# Gerçek API test kaydı");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"Tahmini toplam harcama: **{spent:0.0000} USD** (bütçe {budget:0.00} USD). Tutarlar tahmindir; fatura sağlayıcıdadır.");
        md.AppendLine();
        md.AppendLine("| Adım | Sağlayıcı / model | Parametre | Süre | Kredi | Jeton (gir/çık) | Tahmini $ | Bildirilen $ | İstek kimliği | Kimlik (1-5) | Poz (1-5) | Nakış/desen (1-5) | Renk (1-5) | Artefakt | Not |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var e in entries)
        {
            md.AppendLine(CultureInfo.InvariantCulture, $"| {e.Step} | {e.Provider} {e.Model} | {e.Parameters} | {e.DurationMs / 1000.0:0.0} sn | {e.Credits?.ToString(CultureInfo.InvariantCulture) ?? "—"} | {e.InputTokens?.ToString(CultureInfo.InvariantCulture) ?? "—"} / {e.OutputTokens?.ToString(CultureInfo.InvariantCulture) ?? "—"} | {e.EstimatedUsd:0.0000} | {e.ActualUsd?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "—"} | {e.RequestId} |  |  |  |  |  |  |");
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
