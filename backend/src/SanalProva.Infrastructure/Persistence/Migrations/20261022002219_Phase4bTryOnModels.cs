using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SanalProva.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase4bTryOnModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "model_key",
                table: "generation_jobs",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "quality",
                table: "generation_jobs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "quality",
                table: "ai_usage_logs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "model_key",
                table: "generation_jobs");

            migrationBuilder.DropColumn(
                name: "quality",
                table: "generation_jobs");

            migrationBuilder.DropColumn(
                name: "quality",
                table: "ai_usage_logs");
        }
    }
}
