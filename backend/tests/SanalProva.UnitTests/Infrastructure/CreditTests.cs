using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Credits;

namespace SanalProva.UnitTests.Infrastructure;

/// <summary>M5.4: tarife çözümü (en özel eşleşme kazanır, varsayılan güvenli), tarife doğrulaması ve kapanış kararı matrisi.</summary>
public sealed class CreditTests
{
    private static CreditTariff Tariff(Action<CreditSettings>? configure = null)
    {
        var settings = new CreditSettings();
        configure?.Invoke(settings);
        return new CreditTariff(new TestMonitor(settings));
    }

    private sealed class TestMonitor(CreditSettings value) : IOptionsMonitor<CreditSettings>
    {
        public CreditSettings CurrentValue => value;

        public CreditSettings Get(string? name) => value;

        public IDisposable? OnChange(Action<CreditSettings, string?> listener) => null;
    }

    [Fact]
    public void Tarifede_satir_yoksa_guvenli_varsayilan_bir_kredi_uygulanir()
    {
        var quote = Tariff().Quote(GenerationJobType.OutfitSet, "seedream-5-flash", "high");
        Assert.Equal((1, "default"), (quote.Cost, quote.Key));
    }

    [Fact]
    public void En_ozel_tarife_satiri_kazanir_buyuk_kucuk_harf_duyarsizdir()
    {
        var tariff = Tariff(s =>
        {
            s.Tariff["OutfitSet"] = 2;
            s.Tariff["outfitset:gpt-image-2.5"] = 3;
            s.Tariff["OutfitSet:gpt-image-2.5:HIGH"] = 5;
        });

        Assert.Equal((5, "OutfitSet:gpt-image-2.5:HIGH"), (tariff.Quote(GenerationJobType.OutfitSet, "gpt-image-2.5", "high").Cost, tariff.Quote(GenerationJobType.OutfitSet, "gpt-image-2.5", "high").Key));
        Assert.Equal(3, tariff.Quote(GenerationJobType.OutfitSet, "gpt-image-2.5", "medium").Cost);
        Assert.Equal(2, tariff.Quote(GenerationJobType.OutfitSet, "seedream-5-flash", "medium").Cost);
        Assert.Equal(1, tariff.Quote(GenerationJobType.FreeCombination, null, null).Cost); // satır yok → varsayılan
    }

    [Fact]
    public void Varsayilan_deger_degisince_tarifede_olmayan_isler_yeni_degeri_alir_sifir_ucretsizdir()
    {
        var tariff = Tariff(s =>
        {
            s.DefaultCostPerJob = 4;
            s.Tariff["FreeCombination"] = 0;
        });

        Assert.Equal(4, tariff.Quote(GenerationJobType.OutfitSet, null, null).Cost);
        Assert.Equal(0, tariff.Quote(GenerationJobType.FreeCombination, null, null).Cost);
    }

    [Fact]
    public void Tarife_dogrulamasi_sinir_disi_degerleri_reddeder()
    {
        var validator = new CreditSettingsValidator();
        Assert.True(validator.Validate(null, new CreditSettings()).Succeeded);
        Assert.True(validator.Validate(null, new CreditSettings { DefaultCostPerJob = -1 }).Failed);
        Assert.True(validator.Validate(null, new CreditSettings { DefaultCostPerJob = CreditSettings.MaxCostPerJob + 1 }).Failed);
        var bad = new CreditSettings();
        bad.Tariff["OutfitSet"] = -2;
        Assert.True(validator.Validate(null, bad).Failed);
    }

    private static GenerationJob Job(GenerationJobStatus status, GenerationErrorCode? error = null, bool submitted = false) =>
        new() { IdempotencyKey = "k", Status = status, ErrorCode = error, ProviderSubmitStartedAt = submitted ? DateTimeOffset.UtcNow : null };

    [Theory]
    [InlineData(GenerationJobStatus.Succeeded, null, false, null, false, CreditLedgerService.Outcome.Consume)]
    [InlineData(GenerationJobStatus.Queued, null, false, null, false, CreditLedgerService.Outcome.Hold)]
    [InlineData(GenerationJobStatus.Running, null, true, null, false, CreditLedgerService.Outcome.Hold)]
    [InlineData(GenerationJobStatus.Failed, GenerationErrorCode.ProviderUnavailable, true, null, false, CreditLedgerService.Outcome.Release)]
    [InlineData(GenerationJobStatus.Failed, GenerationErrorCode.BudgetExceeded, false, null, false, CreditLedgerService.Outcome.Release)]
    [InlineData(GenerationJobStatus.Failed, GenerationErrorCode.OutcomeUncertain, true, null, false, CreditLedgerService.Outcome.Hold)]
    [InlineData(GenerationJobStatus.Failed, GenerationErrorCode.OutcomeUncertain, true, CostReconciliationState.Open, true, CreditLedgerService.Outcome.Hold)]
    [InlineData(GenerationJobStatus.Failed, GenerationErrorCode.OutcomeUncertain, true, CostReconciliationState.ConfirmedCharged, true, CreditLedgerService.Outcome.Consume)]
    [InlineData(GenerationJobStatus.Failed, GenerationErrorCode.OutcomeUncertain, true, CostReconciliationState.ConfirmedNotCharged, true, CreditLedgerService.Outcome.Release)]
    [InlineData(GenerationJobStatus.Failed, GenerationErrorCode.OutcomeUncertain, true, CostReconciliationState.WrittenOff, true, CreditLedgerService.Outcome.Release)]
    [InlineData(GenerationJobStatus.Cancelled, null, false, null, false, CreditLedgerService.Outcome.Release)] // hiç gönderilmemiş
    [InlineData(GenerationJobStatus.Cancelled, null, true, null, false, CreditLedgerService.Outcome.Hold)] // gönderilmiş, kayıt yok: çağrı sürüyor olabilir
    [InlineData(GenerationJobStatus.Cancelled, null, true, null, true, CreditLedgerService.Outcome.Release)] // worker kendi kaydını yazdı
    [InlineData(GenerationJobStatus.Cancelled, null, true, CostReconciliationState.Open, true, CreditLedgerService.Outcome.Hold)]
    [InlineData(GenerationJobStatus.Cancelled, null, true, CostReconciliationState.ConfirmedCharged, true, CreditLedgerService.Outcome.Consume)]
    public void Kapanis_karari_matrisi(GenerationJobStatus status, GenerationErrorCode? error, bool submitted, CostReconciliationState? state, bool hasCostRecord, CreditLedgerService.Outcome expected) =>
        Assert.Equal(expected, CreditLedgerService.Decide(Job(status, error, submitted), state, hasCostRecord));

    [Fact]
    public void Is_yoksa_oturum_silindi_iade_edilir()
    {
        Assert.Equal(CreditLedgerService.Outcome.Release, CreditLedgerService.Decide(null, null));
    }
}
