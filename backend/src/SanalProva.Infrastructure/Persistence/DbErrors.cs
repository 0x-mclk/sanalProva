using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace SanalProva.Infrastructure.Persistence;

/// <summary>SQL Server hata numaralarını uygulama anlamına çevirir. Sağlayıcıya özgü bilgi tek yerde kalır.</summary>
public static class DbErrors
{
    /// <summary>Benzersizlik ihlali: 2601 = benzersiz indeks, 2627 = PRIMARY KEY / UNIQUE constraint.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception?.InnerException is SqlException { Number: 2601 or 2627 };
}
