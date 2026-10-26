using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SanalProva.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProviderOutcomeUncertain : Migration
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

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "provider_submit_started_at",
                table: "generation_jobs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_generation_jobs_error_code",
                table: "generation_jobs",
                sql: "[error_code] IN (N'InvalidInput', N'ContentRejected', N'RateLimited', N'Timeout', N'ProviderUnavailable', N'InvalidOutput', N'MaskQualityLow', N'QuotaExceeded', N'NotImplemented', N'OutcomeUncertain', N'BudgetExceeded', N'ProviderNotConfigured', N'Unknown')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_usage_logs_error_code",
                table: "ai_usage_logs",
                sql: "[error_code] IN (N'InvalidInput', N'ContentRejected', N'RateLimited', N'Timeout', N'ProviderUnavailable', N'InvalidOutput', N'MaskQualityLow', N'QuotaExceeded', N'NotImplemented', N'OutcomeUncertain', N'BudgetExceeded', N'ProviderNotConfigured', N'Unknown')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_generation_jobs_error_code",
                table: "generation_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_usage_logs_error_code",
                table: "ai_usage_logs");

            migrationBuilder.DropColumn(
                name: "provider_submit_started_at",
                table: "generation_jobs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_generation_jobs_error_code",
                table: "generation_jobs",
                sql: "[error_code] IN (N'InvalidInput', N'ContentRejected', N'RateLimited', N'Timeout', N'ProviderUnavailable', N'InvalidOutput', N'MaskQualityLow', N'QuotaExceeded', N'NotImplemented', N'BudgetExceeded', N'ProviderNotConfigured', N'Unknown')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_usage_logs_error_code",
                table: "ai_usage_logs",
                sql: "[error_code] IN (N'InvalidInput', N'ContentRejected', N'RateLimited', N'Timeout', N'ProviderUnavailable', N'InvalidOutput', N'MaskQualityLow', N'QuotaExceeded', N'NotImplemented', N'BudgetExceeded', N'ProviderNotConfigured', N'Unknown')");
        }
    }
}
