using Microsoft.EntityFrameworkCore;
using SanalProva.Domain.Audit;
using SanalProva.Domain.Credits;
using SanalProva.Domain.Generation;
using SanalProva.Infrastructure.Persistence;

namespace SanalProva.Infrastructure.Credits;

public enum CreditReserveStatus
{
    Reserved,

    /// <summary>Kullanılabilir kredi yetmiyor (hesap yoksa kullanılabilir 0).</summary>
    Insufficient,
}

public sealed record CreditReserveResult(CreditReserveStatus Status, int Available);

public enum CreditOperatorStatus
{
    Applied,

    /// <summary>Aynı referans ve aynı değerlerle daha önce uygulanmıştı: ikinci kez uygulanmadı.</summary>
    Replayed,

    /// <summary>Aynı referans farklı tür/tutarla kullanılmış.</summary>
    ReferenceConflict,

    /// <summary>Düzeltme bakiyeyi rezerve tutarın (veya sıfırın) altına indirirdi.</summary>
    WouldGoBelowReserved,

    Invalid,
}

public sealed record CreditOperatorResult(CreditOperatorStatus Status, int BalanceAfter, int ReservedAfter);

public enum CreditSettleStatus
{
    /// <summary>İş için açık rezervasyon yok (kredi uygulanmamış veya zaten kapanmış).</summary>
    NothingToSettle,

    /// <summary>Sonuç henüz belli değil (çalışıyor / sonucu belirsiz, mutabakat bekliyor): kredi bloke kalır.</summary>
    Held,

    Consumed,
    Released,
}

/// <summary>
/// Firma kredi defteri servisi (M5.4). <b>Tek yazıcı</b>: hesap (<c>credit_accounts</c>) ve defter (<c>credit_ledger</c>) yalnızca burada değişir (kaynak taraması testi).
/// Her değişiklik firma başına uygulama kilidi (<see cref="AppLocks.OrganizationCredits"/>) altında, tek transaction'da hesap güncellemesi + değiştirilemez defter girdisi olarak yapılır;
/// bakiye hiçbir zaman negatif olmaz (hem kod hem veritabanı CHECK). İş başına en fazla bir Reserve ve bir kapanış (Release|Consume) vardır (filtreli benzersiz indeks):
/// aynı işi iki kez kapatmak, çift düşüm veya çift iade mümkün değildir; <see cref="SettleJobAsync"/> bu yüzden her yerden güvenle tekrar çağrılabilir (idempotent).
/// <para>
/// <b>Kapanış kuralı</b> (<see cref="Decide"/>): Başarılı → Consume. Kesin başarısızlık, hiç gönderilmemiş/ücretlenmemiş iptal, iş yok (oturum silindi) → Release.
/// Sonucu belirsiz (<c>OutcomeUncertain</c>) iş ve gönderilmiş + belirsiz maliyet kaydı açık iptal → mutabakata kadar <b>bloke</b> (Hold); karar "ücretlendi" → Consume,
/// "ücretlenmedi" / "yazıldı-silindi" → Release (kanıtlanamayan ücret müşteriye yansıtılmaz; platform kaybı USD tarafında kalır).
/// </para>
/// </summary>
public sealed class CreditLedgerService(TimeProvider clock)
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    // ------------------------------------------------------------------ rezervasyon (iş kabulü)

    /// <summary>
    /// İş kabulünde krediyi ayırır. <b>Açık bir transaction gerektirir</b> (kabul transaction'ı): kilit ve yazımlar o transaction'ın parçasıdır ve çağıranın
    /// <c>SaveChanges</c> çağrısıyla işle birlikte kesinleşir; transaction geri alınırsa rezervasyon da geri alınır (yetim rezervasyon oluşmaz).
    /// </summary>
    public async Task<CreditReserveResult> ReserveAsync(AppDbContext db, Guid organizationId, Guid jobId, int cost, string tariffKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (cost <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cost), cost, "Rezervasyon tutarı pozitif olmalı (ücretsiz işte rezervasyon yapılmaz).");
        }

        await AppLocks.AcquireExclusiveAsync(db, AppLocks.OrganizationCredits(organizationId), LockTimeout, ct);
        var account = await db.CreditAccounts.IgnoreQueryFilters().SingleOrDefaultAsync(a => a.OrganizationId == organizationId, ct);
        var available = account?.AvailableCredits ?? 0;
        if (account is null || available < cost)
        {
            return new CreditReserveResult(CreditReserveStatus.Insufficient, Math.Max(0, available));
        }

        account.ReservedCredits += cost;
        account.UpdatedAt = clock.GetUtcNow();
        db.CreditLedger.Add(Entry(organizationId, CreditEntryType.Reserve, cost, account, jobId: jobId, source: CreditSource.System, tariffKey: tariffKey));
        return new CreditReserveResult(CreditReserveStatus.Reserved, available - cost);
    }

    // ------------------------------------------------------------------ operatör: yükleme / düzeltme

    /// <summary>
    /// Kredi yükleme (<paramref name="type"/> = Grant, pozitif) veya düzeltme (Adjustment, işaretli). <paramref name="reference"/> firma içinde benzersizdir (idempotency):
    /// aynı referans + aynı tür/tutar ikinci kez UYGULANMAZ (<see cref="CreditOperatorStatus.Replayed"/>); farklı değerlerle kullanılırsa reddedilir.
    /// Hesap yoksa ilk yüklemede oluşturulur. Düzeltme bakiyeyi rezerve tutarın altına indiremez.
    /// </summary>
    public async Task<CreditOperatorResult> ApplyOperatorEntryAsync(
        AppDbContext db, Guid organizationId, CreditEntryType type, int amount, string reference, CreditSource source, string? note, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var valid = type switch
        {
            CreditEntryType.Grant => amount > 0,
            CreditEntryType.Adjustment => amount != 0,
            _ => false,
        };
        if (!valid)
        {
            return new CreditOperatorResult(CreditOperatorStatus.Invalid, 0, 0);
        }

        // Çağıranın açık transaction'ı varsa ona katılır (kilit ve yazımlar onun parçası); yoksa kendi transaction'ını açar
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await AppLocks.AcquireExclusiveAsync(db, AppLocks.OrganizationCredits(organizationId), LockTimeout, ct);

        var existing = await db.CreditLedger.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(e => e.OrganizationId == organizationId && e.Reference == reference, ct);
        if (existing is not null)
        {
            return existing.EntryType == type && existing.Amount == amount
                ? new CreditOperatorResult(CreditOperatorStatus.Replayed, existing.BalanceAfter, existing.ReservedAfter)
                : new CreditOperatorResult(CreditOperatorStatus.ReferenceConflict, 0, 0);
        }

        var account = await db.CreditAccounts.IgnoreQueryFilters().SingleOrDefaultAsync(a => a.OrganizationId == organizationId, ct);
        if (account is null)
        {
            account = new CreditAccount { OrganizationId = organizationId, UpdatedAt = clock.GetUtcNow() };
            db.CreditAccounts.Add(account); // gezinti özelliği olmayan yeni varlık: açık Add (EF anahtar tuzağı)
        }

        if (account.BalanceCredits + amount < account.ReservedCredits || account.BalanceCredits + amount < 0)
        {
            return new CreditOperatorResult(CreditOperatorStatus.WouldGoBelowReserved, account.BalanceCredits, account.ReservedCredits);
        }

        account.BalanceCredits += amount;
        account.UpdatedAt = clock.GetUtcNow();
        db.CreditLedger.Add(Entry(organizationId, type, amount, account, reference: reference, source: source, note: note));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // Kilit dışı bir yazma yarışı aynı referansı yazdı (benzersiz indeks): yeniden okuyup aynı sonuca bağla
            db.ChangeTracker.Clear();
            var raced = await db.CreditLedger.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.OrganizationId == organizationId && e.Reference == reference, ct);
            return raced.EntryType == type && raced.Amount == amount
                ? new CreditOperatorResult(CreditOperatorStatus.Replayed, raced.BalanceAfter, raced.ReservedAfter)
                : new CreditOperatorResult(CreditOperatorStatus.ReferenceConflict, 0, 0);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return new CreditOperatorResult(CreditOperatorStatus.Applied, account.BalanceCredits, account.ReservedCredits);
    }

    // ------------------------------------------------------------------ kapanış

    public enum Outcome
    {
        Hold,
        Consume,
        Release,
    }

    /// <summary>
    /// Kapanış kararı (saf fonksiyon, birim testli). <paramref name="uncertainState"/>: işin belirsiz maliyet kaydının mutabakat durumu (yoksa null);
    /// <paramref name="hasCostRecord"/>: işin herhangi bir (düzeltme olmayan) maliyet kaydı var mı (iptalde worker kendi en kötü durum kaydını yazmış olabilir).
    /// </summary>
    internal static Outcome Decide(GenerationJob? job, CostReconciliationState? uncertainState, bool hasCostRecord = false)
    {
        if (job is null)
        {
            return Outcome.Release; // iş yok (oturum silindi): sonuç atılır, müşteriye yansıtılmaz
        }

        switch (job.Status)
        {
            case GenerationJobStatus.Succeeded:
                return Outcome.Consume;
            case GenerationJobStatus.Queued:
            case GenerationJobStatus.Running:
                return Outcome.Hold;
            case GenerationJobStatus.Failed when job.ErrorCode != GenerationErrorCode.OutcomeUncertain:
                return Outcome.Release; // kesin başarısızlık: sonuç yok, müşteri ödemez
            case GenerationJobStatus.Failed:
                return ByDecision(uncertainState);
            case GenerationJobStatus.Cancelled:
            {
                // Hiç gönderilmemişse kesin serbest. Gönderilmişse: açık belirsiz vaka → mutabakata kadar bloke; karar verildiyse karara göre; worker kendi (vakasız) en kötü durum
                // kaydını yazdıysa iptal eden müşteriye yansıtılmaz (serbest); henüz hiçbir kayıt yoksa (çağrı sürüyor olabilir) bloke — işçi veya süpürücü sonunda kapatır
                var submitted = job.ProviderSubmitStartedAt is not null || job.ProviderRequestId is not null;
                if (!submitted)
                {
                    return Outcome.Release;
                }

                return uncertainState is not null ? ByDecision(uncertainState) : hasCostRecord ? Outcome.Release : Outcome.Hold;
            }
            default:
                return Outcome.Hold;
        }

        static Outcome ByDecision(CostReconciliationState? state, Outcome defaultOutcome = Outcome.Hold) => state switch
        {
            CostReconciliationState.ConfirmedCharged => Outcome.Consume,
            CostReconciliationState.ConfirmedNotCharged or CostReconciliationState.WrittenOff => Outcome.Release,
            _ => defaultOutcome,
        };
    }

    /// <summary>
    /// İşin açık kredi rezervasyonunu, işin ŞU ANKİ durumuna göre kapatır (idempotent; çağrı sırası önemsizdir). Kapanış ve hesap güncellemesi tek transaction'dadır.
    /// Çağıranlar: işçi (iş bitince), iptal ucu, maliyet kararı, süpürücü (kendini onarma).
    /// </summary>
    public async Task<CreditSettleStatus> SettleJobAsync(AppDbContext db, Guid jobId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var reserve = await db.CreditLedger.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(e => e.JobId == jobId && e.EntryType == CreditEntryType.Reserve, ct);
        if (reserve is null)
        {
            return CreditSettleStatus.NothingToSettle;
        }

        var job = await db.GenerationJobs.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, ct);
        CostReconciliationState? state = null;
        var hasCostRecord = false;
        if (job is not null && job.Status is GenerationJobStatus.Failed or GenerationJobStatus.Cancelled)
        {
            hasCostRecord = await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking().AnyAsync(l => l.JobId == jobId && !l.IsAdjustment, ct);
            // En son belirsiz maliyet vakası (düzeltme satırları değil): karar durumu kredinin kaderini belirler
            state = await db.AiUsageLogs.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.JobId == jobId && !l.IsAdjustment && l.ReconciliationState != null)
                .OrderByDescending(l => l.CreatedAt).Select(l => l.ReconciliationState).FirstOrDefaultAsync(ct);
        }

        var outcome = Decide(job, state, hasCostRecord);
        if (outcome == Outcome.Hold)
        {
            return CreditSettleStatus.Held;
        }

        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await AppLocks.AcquireExclusiveAsync(db, AppLocks.OrganizationCredits(reserve.OrganizationId), LockTimeout, ct);
        if (await db.CreditLedger.IgnoreQueryFilters().AnyAsync(e => e.JobId == jobId && (e.EntryType == CreditEntryType.Release || e.EntryType == CreditEntryType.Consume), ct))
        {
            return CreditSettleStatus.NothingToSettle; // başka bir çağıran az önce kapattı
        }

        var account = await db.CreditAccounts.IgnoreQueryFilters().SingleAsync(a => a.OrganizationId == reserve.OrganizationId, ct);
        account.ReservedCredits -= reserve.Amount;
        if (outcome == Outcome.Consume)
        {
            account.BalanceCredits -= reserve.Amount;
        }

        account.UpdatedAt = clock.GetUtcNow();
        db.CreditLedger.Add(Entry(reserve.OrganizationId, outcome == Outcome.Consume ? CreditEntryType.Consume : CreditEntryType.Release, reserve.Amount, account, jobId: jobId, source: CreditSource.System));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return CreditSettleStatus.NothingToSettle; // benzersiz indeks: ikinci kapanış reddedildi
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return outcome == Outcome.Consume ? CreditSettleStatus.Consumed : CreditSettleStatus.Released;
    }

    /// <summary>Açık (kapanışı olmayan) tüm rezervasyonları dener (süpürücü). Kapatılanların sayısını döner.</summary>
    public async Task<int> SettleOpenAsync(AppDbContext db, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var open = await db.CreditLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.EntryType == CreditEntryType.Reserve && e.JobId != null
                && !db.CreditLedger.IgnoreQueryFilters().Any(t => t.JobId == e.JobId && (t.EntryType == CreditEntryType.Release || t.EntryType == CreditEntryType.Consume)))
            .OrderBy(e => e.CreatedAt).Select(e => e.JobId!.Value).Take(take).ToListAsync(ct);
        var closed = 0;
        foreach (var jobId in open)
        {
            if (await SettleJobAsync(db, jobId, ct) is CreditSettleStatus.Consumed or CreditSettleStatus.Released)
            {
                closed++;
            }
        }

        return closed;
    }

    private CreditLedgerEntry Entry(
        Guid organizationId, CreditEntryType type, int amount, CreditAccount account, Guid? jobId = null, string? reference = null, CreditSource source = CreditSource.System,
        string? note = null, string? tariffKey = null) => new()
    {
        OrganizationId = organizationId, EntryType = type, Amount = amount, JobId = jobId, Reference = reference, Source = source, Note = note, TariffKey = tariffKey,
        BalanceAfter = account.BalanceCredits, ReservedAfter = account.ReservedCredits, CreatedAt = clock.GetUtcNow(),
    };
}
