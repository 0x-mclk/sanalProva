using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SanalProva.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdmissionDedupeKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "dedupe_key",
                table: "generation_jobs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_generation_jobs_active_dedupe",
                table: "generation_jobs",
                columns: new[] { "session_id", "dedupe_key" },
                unique: true,
                filter: "[dedupe_key] IS NOT NULL AND [status] IN (N'Queued', N'Running', N'Succeeded')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_generation_jobs_active_dedupe",
                table: "generation_jobs");

            migrationBuilder.DropColumn(
                name: "dedupe_key",
                table: "generation_jobs");
        }
    }
}
