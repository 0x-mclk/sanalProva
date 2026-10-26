using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SanalProva.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// generation_jobs.attempt EF eşzamanlılık belirteci (çit belirteci) oldu. Şema değişmez; yalnızca model snapshot'ı güncellenir
    /// (EF, snapshot ile model ayrışırsa Migrate sırasında "bekleyen model değişikliği" hatası verir).
    /// </summary>
    public partial class JobAttemptConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
