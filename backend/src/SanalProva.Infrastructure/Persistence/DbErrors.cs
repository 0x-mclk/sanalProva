using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace SanalProva.Infrastructure.Persistence;

/// <summary>SQL Server hata numaralarını uygulama anlamına çevirir. Sağlayıcıya özgü bilgi tek yerde kalır.</summary>
public static class DbErrors
{
    /// <summary>Kilitlenme kurbanı: SQL Server transaction'ı geri aldı; deyim güvenle yeniden denenebilir.</summary>
    public const int DeadlockVictim = 1205;

    /// <summary>Kilit isteği zaman aşımı (SET LOCK_TIMEOUT).</summary>
    public const int LockRequestTimeout = 1222;

    /// <summary>Benzersizlik ihlali: 2601 = benzersiz indeks, 2627 = PRIMARY KEY / UNIQUE constraint.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception?.InnerException is SqlException { Number: 2601 or 2627 };

    /// <summary>Kilitlenme kurbanı mı (doğrudan veya EF'in DbUpdateException'ı içinde).</summary>
    public static bool IsDeadlock(Exception exception) => Find(exception) is { Number: DeadlockVictim };

    /// <summary>
    /// Geçici kilit hatası: kilitlenme kurbanı, kilit zaman aşımı veya uygulama kilidinin verilmemesi (<see cref="AppLocks.NotGrantedError"/>).
    /// Hepsinde transaction geri alınmıştır; aynı iş birimi baştan yeniden denenebilir.
    /// </summary>
    public static bool IsTransientLockFailure(Exception exception) =>
        Find(exception) is { Number: DeadlockVictim or LockRequestTimeout or AppLocks.NotGrantedError };

    private static SqlException? Find(Exception? exception) => exception switch
    {
        SqlException sql => sql,
        DbUpdateException { InnerException: SqlException inner } => inner,
        _ => null,
    };
}
