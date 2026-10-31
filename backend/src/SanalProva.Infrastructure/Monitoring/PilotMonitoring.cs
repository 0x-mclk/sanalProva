using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SanalProva.Domain.Generation;
using SanalProva.Domain.Tenancy;
using SanalProva.Infrastructure.Ai;
using SanalProva.Infrastructure.Credits;
using SanalProva.Infrastructure.Generation;
using SanalProva.Infrastructure.Persistence;
using SanalProva.Infrastructure.Storage;

namespace SanalProva.Infrastructure.Monitoring;

public enum MonitorLevel
{
    Ok,
    Warning,
    Critical,
}

public sealed record MonitorCheck(string Name, MonitorLevel Level, string Message);

/// <summary>Firma bazlı finans özeti (Faz C): kredi, etkin bütçe sınırları, harcama, açık rezervasyon ve bekleyen belirsiz maliyet. Kişisel veri içermez.</summary>
public sealed record OrganizationFinance(
    Guid OrganizationId, string Name, string Slug, bool IsActive, bool PaidAiEnabled,
    int CreditBalance, int CreditReserved, int CreditAvailable, bool LowCredit,
    decimal DailyLimitUsd, decimal MonthlyLimitUsd, decimal MaxJobCostUsd, decimal SpentTodayUsd, decimal SpentMonthUsd, decimal OpenReservedUsd,
    int OpenUncertainCases, DateTimeOffset? OldestUncertainAt, MonitorLevel BudgetLevel,
    bool SettingsCorrupt = false);

/// <summary>
/// Pilot düzeyinde asgari izleme (Faz C): veritabanı bağlantısı, depo diski, takılı işler, bekleyen belirsiz maliyetler, bütçe ve kredi uyarıları.
/// Yalnızca OKUR (hiçbir kayıt yazmaz); hem platform yöneticisi ucu hem operatör komutu (<c>platform status</c>) kullanır. Dış izleme/uyarı sistemi YOKTUR:
/// çıktıyı cron/izleyici okur (docs/19 §11). Eşikler bilinçli olarak sabittir (pilot); yapılandırma gerektiğinde ayrı karar.
/// </summary>
public sealed class PilotMonitoring(
    IOptionsMonitor<GenerationSettings> generation, IOptionsMonitor<CreditSettings> credits, IOptions<StorageOptions> storage, BudgetGuard budget, TimeProvider clock)
{
    public const double BudgetWarnRatio = 0.8;
    public static readonly TimeSpan StuckQueued = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan StuckRunning = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan UncertainWarnAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan UncertainCriticalAge = TimeSpan.FromHours(72);

    public async Task<IReadOnlyList<OrganizationFinance>> FinanceAsync(AppDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var settings = generation.CurrentValue;
        var threshold = credits.CurrentValue.LowBalanceThreshold;
        // Ayar JSON'u BİLEREK burada okunmaz: tek bozuk satır tüm listeyi düşürmesin (firma başına aşağıda, fail-closed)
        var organizations = await db.Organizations.AsNoTracking().OrderBy(o => o.Name).Select(o => new { o.Id, o.Name, o.Slug, o.IsActive }).ToListAsync(ct);
        var accounts = await db.CreditAccounts.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(a => a.OrganizationId, ct);
        var openCases = await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.ReconciliationState == CostReconciliationState.Open && !l.IsAdjustment)
            .GroupBy(l => l.OrganizationId).Select(g => new { g.Key, Count = g.Count(), Oldest = g.Min(x => x.OccurredAt ?? x.CreatedAt) }).ToListAsync(ct);
        var cases = openCases.ToDictionary(c => c.Key);

        var result = new List<OrganizationFinance>(organizations.Count);
        foreach (var organization in organizations)
        {
            accounts.TryGetValue(organization.Id, out var account);
            var spend = await budget.GetSpendAsync(db, organization.Id, null, ct);
            var (organizationSettings, corrupt) = await OrganizationSettingsReader.ReadAsync(db, organization.Id, ct);
            // Bozuk/okunamayan ayar fail-closed (PaidAiPolicy/BudgetLimits.ForAsync ile aynı kural): ücretli AI kapalı, bütçe sınırları 0
            var limits = corrupt ? new BudgetLimits(0m, 0m, 0m, true, true, true) : BudgetLimits.Resolve(settings.Budget, organizationSettings);
            var open = await BudgetGuard.GetOpenReservedAsync(db, organization.Id, ct);
            var available = account?.AvailableCredits ?? 0;
            cases.TryGetValue(organization.Id, out var uncertain);
            result.Add(new OrganizationFinance(
                organization.Id, organization.Name, organization.Slug, organization.IsActive, !corrupt && organizationSettings!.PaidAiEnabled,
                account?.BalanceCredits ?? 0, account?.ReservedCredits ?? 0, available, threshold > 0 && available <= threshold,
                limits.DailyUsd, limits.MonthlyUsd, limits.MaxJobUsd, spend.Today, spend.Month, open,
                uncertain?.Count ?? 0, uncertain?.Oldest, BudgetLevel(spend.Today, spend.Month, open, limits), corrupt));
        }

        return result;
    }

    internal static MonitorLevel BudgetLevel(decimal today, decimal month, decimal open, BudgetLimits limits)
    {
        static MonitorLevel One(decimal used, decimal limit) =>
            limit <= 0 ? (used > 0 ? MonitorLevel.Critical : MonitorLevel.Ok)
            : used >= limit ? MonitorLevel.Critical
            : used >= limit * (decimal)BudgetWarnRatio ? MonitorLevel.Warning : MonitorLevel.Ok;
        var daily = One(today + open, limits.DailyUsd);
        var monthly = One(month + open, limits.MonthlyUsd);
        return (MonitorLevel)Math.Max((int)daily, (int)monthly);
    }

    public async Task<IReadOnlyList<MonitorCheck>> CheckAsync(AppDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var checks = new List<MonitorCheck>();
        var now = clock.GetUtcNow();

        // Veritabanı (başarısızsa diğer sorgular da anlamsız)
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var reachable = await db.Database.CanConnectAsync(ct);
            checks.Add(reachable
                ? new MonitorCheck("database", started.ElapsedMilliseconds > 2000 ? MonitorLevel.Warning : MonitorLevel.Ok, $"Bağlantı kuruldu ({started.ElapsedMilliseconds} ms).")
                : new MonitorCheck("database", MonitorLevel.Critical, "Veritabanına bağlanılamıyor."));
            if (!reachable)
            {
                return checks;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            checks.Add(new MonitorCheck("database", MonitorLevel.Critical, "Veritabanı denetimi hata verdi."));
            return checks;
        }

        checks.Add(DiskCheck());

        var live = !generation.CurrentValue.UseMockProviders;
        var paidCalls = generation.CurrentValue.Budget.AllowPaidCalls;
        checks.Add(new MonitorCheck("ai-mode", MonitorLevel.Ok, live ? (paidCalls ? "GERÇEK mod: ücretli çağrılar AÇIK." : "Gerçek mod seçili ama ücretli çağrı kapısı kapalı (üretim reddedilir).") : "Mock mod: ücretli çağrı yok."));

        // Takılı işler
        var queuedCut = now - StuckQueued;
        var runningCut = now - StuckRunning;
        var stuck = await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(j => (j.Status == GenerationJobStatus.Queued && j.UpdatedAt < queuedCut) || (j.Status == GenerationJobStatus.Running && j.UpdatedAt < runningCut), ct);
        checks.Add(stuck == 0
            ? new MonitorCheck("jobs", MonitorLevel.Ok, "Takılı iş yok.")
            : new MonitorCheck("jobs", MonitorLevel.Warning, $"{stuck} iş beklenenden uzun süredir kuyrukta/çalışıyor (işçi durmuş olabilir)."));

        var finance = await FinanceAsync(db, ct);

        // Bekleyen belirsiz maliyetler
        var uncertain = finance.Sum(f => f.OpenUncertainCases);
        var oldest = finance.Where(f => f.OldestUncertainAt is not null).Select(f => f.OldestUncertainAt!.Value).DefaultIfEmpty().Min();
        if (uncertain == 0)
        {
            checks.Add(new MonitorCheck("uncertain-cases", MonitorLevel.Ok, "Bekleyen belirsiz maliyet yok."));
        }
        else
        {
            var age = now - oldest;
            var level = age >= UncertainCriticalAge ? MonitorLevel.Critical : age >= UncertainWarnAge ? MonitorLevel.Warning : MonitorLevel.Ok;
            checks.Add(new MonitorCheck("uncertain-cases", level, $"{uncertain} belirsiz maliyet vakası mutabakat bekliyor (en eskisi {(int)age.TotalHours} saat önce). Platform > Mutabakat."));
        }

        // Bütçe ve kredi (yalnızca etkin firmalar)
        foreach (var f in finance.Where(f => f.IsActive))
        {
            if (f.SettingsCorrupt)
            {
                checks.Add(new MonitorCheck($"settings:{f.Slug}", MonitorLevel.Critical, $"{f.Name}: firma ayarları okunamıyor (bozuk JSON). Ücretli AI ve bütçe KAPALI sayılıyor; ayarı onarın."));
            }

            if (f.BudgetLevel != MonitorLevel.Ok)
            {
                checks.Add(new MonitorCheck($"budget:{f.Slug}", f.BudgetLevel,
                    $"{f.Name}: bütçe {(f.BudgetLevel == MonitorLevel.Critical ? "doldu" : "%80'i geçti")} (bugün ${f.SpentTodayUsd:0.00}+${f.OpenReservedUsd:0.00} açık / ${f.DailyLimitUsd:0.00}; ay ${f.SpentMonthUsd:0.00} / ${f.MonthlyLimitUsd:0.00})."));
            }

            if (f.PaidAiEnabled && f.LowCredit)
            {
                checks.Add(new MonitorCheck($"credits:{f.Slug}", f.CreditAvailable <= 0 ? MonitorLevel.Critical : MonitorLevel.Warning, $"{f.Name}: kullanılabilir kredi {f.CreditAvailable}."));
            }
        }

        if (!checks.Any(c => c.Name.StartsWith("budget:", StringComparison.Ordinal)))
        {
            checks.Add(new MonitorCheck("budget", MonitorLevel.Ok, "Hiçbir firmanın bütçesi %80'i geçmedi."));
        }

        if (!checks.Any(c => c.Name.StartsWith("credits:", StringComparison.Ordinal)))
        {
            checks.Add(new MonitorCheck("credits", MonitorLevel.Ok, "Ücretli AI açık firmalarda düşük kredi yok."));
        }

        return checks;
    }

    private MonitorCheck DiskCheck()
    {
        try
        {
            var path = Path.GetFullPath(storage.Value.LocalRoot);
            while (!Directory.Exists(path) && Path.GetDirectoryName(path) is { Length: > 0 } parent)
            {
                path = parent;
            }

            var drive = new DriveInfo(path);
            var freeRatio = drive.TotalSize <= 0 ? 0 : (double)drive.AvailableFreeSpace / drive.TotalSize;
            var message = $"Depo diskinde %{freeRatio * 100:0} boş ({drive.AvailableFreeSpace / (1024 * 1024 * 1024)} GB).";
            return new MonitorCheck("disk", freeRatio < 0.10 ? MonitorLevel.Critical : freeRatio < 0.20 ? MonitorLevel.Warning : MonitorLevel.Ok, message);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return new MonitorCheck("disk", MonitorLevel.Warning, "Disk doluluğu okunamadı.");
        }
    }
}

/// <summary>
/// Tek firmanın ayarını DAYANIKLI okur (platform listesi/ayrıntısı, finans ve izleme ortak kullanır). JSON bozuk/tür uyumsuzsa EF okuma istisnası fırlatır:
/// <c>Corrupt = true</c> döner ve çağıran fail-closed davranır (ücretli AI kapalı, sınırlar 0). Veritabanı/bağlantı hataları ve iptal çağırana yayılır (altyapı hatası
/// "ayar bozuk" demek değildir). Kaynak: <c>PaidAiPolicy.IsEnabledForAsync</c> ile aynı ayrım. Firma filtresi uygulanmaz: platform yöneticisi uçlarından çağrılır.
/// </summary>
public static class OrganizationSettingsReader
{
    public static async Task<(OrganizationSettings? Settings, bool Corrupt)> ReadAsync(AppDbContext db, Guid organizationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            return (await db.Organizations.AsNoTracking().Where(o => o.Id == organizationId).Select(o => o.Settings).SingleAsync(ct), false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or System.Data.Common.DbException or DbUpdateException))
        {
            return (null, true);
        }
    }
}
