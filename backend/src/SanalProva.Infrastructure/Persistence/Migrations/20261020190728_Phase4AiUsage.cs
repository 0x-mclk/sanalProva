using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SanalProva.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase4AiUsage : Migration
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
                name: "request_fingerprint",
                table: "generation_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "actual_cost_usd",
                table: "ai_usage_logs",
                type: "numeric(10,4)",
                precision: 10,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_request_id",
                table: "ai_usage_logs",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_generation_jobs_session_id_request_fingerprint",
                table: "generation_jobs",
                columns: new[] { "session_id", "request_fingerprint" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_generation_jobs_error_code",
                table: "generation_jobs",
                sql: "error_code IN ('InvalidInput', 'ContentRejected', 'RateLimited', 'Timeout', 'ProviderUnavailable', 'InvalidOutput', 'MaskQualityLow', 'QuotaExceeded', 'NotImplemented', 'BudgetExceeded', 'ProviderNotConfigured', 'Unknown')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_usage_logs_error_code",
                table: "ai_usage_logs",
                sql: "error_code IN ('InvalidInput', 'ContentRejected', 'RateLimited', 'Timeout', 'ProviderUnavailable', 'InvalidOutput', 'MaskQualityLow', 'QuotaExceeded', 'NotImplemented', 'BudgetExceeded', 'ProviderNotConfigured', 'Unknown')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_generation_jobs_session_id_request_fingerprint",
                table: "generation_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_generation_jobs_error_code",
                table: "generation_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_usage_logs_error_code",
                table: "ai_usage_logs");

            migrationBuilder.DropColumn(
                name: "request_fingerprint",
                table: "generation_jobs");

            migrationBuilder.DropColumn(
                name: "actual_cost_usd",
                table: "ai_usage_logs");

            migrationBuilder.DropColumn(
                name: "provider_request_id",
                table: "ai_usage_logs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_generation_jobs_error_code",
                table: "generation_jobs",
                sql: "error_code IN ('InvalidInput', 'ContentRejected', 'RateLimited', 'Timeout', 'ProviderUnavailable', 'InvalidOutput', 'MaskQualityLow', 'QuotaExceeded', 'NotImplemented', 'Unknown')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_usage_logs_error_code",
                table: "ai_usage_logs",
                sql: "error_code IN ('InvalidInput', 'ContentRejected', 'RateLimited', 'Timeout', 'ProviderUnavailable', 'InvalidOutput', 'MaskQualityLow', 'QuotaExceeded', 'NotImplemented', 'Unknown')");
        }
    }
}
