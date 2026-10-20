using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SanalProva.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase2Sessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_generation_jobs_error_code",
                table: "generation_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_usage_logs_error_code",
                table: "ai_usage_logs");

            migrationBuilder.AddColumn<string>(
                name: "draft",
                table: "tryon_sessions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tagline",
                table: "products",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stage",
                table: "generation_jobs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_demo",
                table: "generated_images",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_generation_jobs_error_code",
                table: "generation_jobs",
                sql: "error_code IN ('InvalidInput', 'ContentRejected', 'RateLimited', 'Timeout', 'ProviderUnavailable', 'InvalidOutput', 'MaskQualityLow', 'QuotaExceeded', 'NotImplemented', 'Unknown')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_generation_jobs_stage",
                table: "generation_jobs",
                sql: "stage IN ('Preparing', 'Generating', 'Validating')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_usage_logs_error_code",
                table: "ai_usage_logs",
                sql: "error_code IN ('InvalidInput', 'ContentRejected', 'RateLimited', 'Timeout', 'ProviderUnavailable', 'InvalidOutput', 'MaskQualityLow', 'QuotaExceeded', 'NotImplemented', 'Unknown')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_generation_jobs_error_code",
                table: "generation_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_generation_jobs_stage",
                table: "generation_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_usage_logs_error_code",
                table: "ai_usage_logs");

            migrationBuilder.DropColumn(
                name: "draft",
                table: "tryon_sessions");

            migrationBuilder.DropColumn(
                name: "tagline",
                table: "products");

            migrationBuilder.DropColumn(
                name: "stage",
                table: "generation_jobs");

            migrationBuilder.DropColumn(
                name: "is_demo",
                table: "generated_images");

            migrationBuilder.AddCheckConstraint(
                name: "ck_generation_jobs_error_code",
                table: "generation_jobs",
                sql: "error_code IN ('InvalidInput', 'ContentRejected', 'RateLimited', 'Timeout', 'ProviderUnavailable', 'InvalidOutput', 'MaskQualityLow', 'QuotaExceeded', 'Unknown')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_usage_logs_error_code",
                table: "ai_usage_logs",
                sql: "error_code IN ('InvalidInput', 'ContentRejected', 'RateLimited', 'Timeout', 'ProviderUnavailable', 'InvalidOutput', 'MaskQualityLow', 'QuotaExceeded', 'Unknown')");
        }
    }
}
