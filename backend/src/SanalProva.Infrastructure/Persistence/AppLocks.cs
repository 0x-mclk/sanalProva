using Microsoft.EntityFrameworkCore;

namespace SanalProva.Infrastructure.Persistence;

/// <summary>
/// SQL Server uygulama kilitleri (<c>sp_getapplock</c>): satır değil, adı verilen bir kaynak kilitlenir (ör. "bu oturumun sürüm numarası").
/// Kilit transaction'a aittir (<c>@LockOwner = 'Transaction'</c>): COMMIT veya ROLLBACK ile kendiliğinden bırakılır, unutulup asılı kalmaz.
/// </summary>
public static class AppLocks
{
    /// <summary>
    /// Kilit verilmediğinde T-SQL'in fırlattığı hata numarası. <c>sp_getapplock</c> dönüş kodları: 0 = hemen verildi, 1 = bekleyip verildi,
    /// −1 = zaman aşımı, −2 = iptal, −3 = kilitlenme kurbanı, −999 = parametre/çağrı hatası. Negatif her sonuç bu hataya çevrilir.
    /// </summary>
    public const int NotGrantedError = 51001;

    /// <summary>Oturum içindeki sonuç sürüm numarasının (1, 2, 3…) kilidi.</summary>
    public static string SessionVersion(Guid sessionId) => $"sanalprova:session-version:{sessionId:N}";

    /// <summary>Şubenin yeni iş kabul kilidi: günlük kota, oturum sınırı ve tekil ücretli iş kontrolü + ekleme bu kilit altında yapılır.</summary>
    public static string StoreAdmission(Guid storeId) => $"sanalprova:store-admission:{storeId:N}";

    /// <summary>Firmanın AI bütçe kapısı: harcama + açık rezervasyon hesabı ve yeni rezervasyonun yazılması bu kilit altında, tek transaction'da yapılır.</summary>
    public static string OrganizationBudget(Guid organizationId) => $"sanalprova:org-budget:{organizationId:N}";

    /// <summary>Açık transaction içinde özel (exclusive) kilit alır; alınamazsa <see cref="NotGrantedError"/> numaralı SqlException fırlatır.</summary>
    public static async Task AcquireExclusiveAsync(AppDbContext db, string resource, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Uygulama kilidi açık bir transaction gerektirir (kilit transaction'a aittir).");
        }

        var timeoutMs = (int)timeout.TotalMilliseconds;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = {resource}, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = {timeoutMs};
            IF @result < 0
            BEGIN
                DECLARE @message nvarchar(200) = CONCAT(N'Uygulama kilidi alınamadı (sp_getapplock sonucu ', @result, N').');
                THROW 51001, @message, 1;
            END;
            """, ct);
    }
}
