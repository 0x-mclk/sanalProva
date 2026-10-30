using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SanalProva.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlatformOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ÖN KONTROL (M5.1 D4): e-posta tüm sistemde benzersiz olacak (CREATE UNIQUE INDEX aşağıda). Mevcut veride aynı normalized_email'e
            // sahip birden fazla kullanıcı varsa migration (tek transaction) açık bir hatayla DURUR ve hiçbir şey değişmez; genel SQL Server
            // 1505 hatası yerine anlaşılır bir mesaj ve veriyi inceleme çağrısı verir (TenantConsistency'deki 51200 ön kontrol deseniyle aynı).
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM users WHERE normalized_email IS NOT NULL GROUP BY normalized_email HAVING COUNT(*) > 1) " +
                "THROW 51201, N'E-posta benzersizlik ihlali: aynı normalized_email birden fazla kullanıcıda var. Migration durduruldu; veriyi inceleyin.', 1;");

            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_generation_jobs_error_code",
                table: "generation_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_usage_logs_error_code",
                table: "ai_usage_logs");

            migrationBuilder.AddColumn<Guid>(
                name: "created_by_user_id",
                table: "organizations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "creation_request_hash",
                table: "organizations",
                type: "binary(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "creation_request_key",
                table: "organizations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "account_setup_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    purpose = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    token_hash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_setup_tokens", x => x.id);
                    table.CheckConstraint("ck_account_setup_tokens_expires_after_created", "[expires_at] > [created_at]");
                    table.CheckConstraint("ck_account_setup_tokens_purpose", "[purpose] IN (N'InitialPassword', N'PasswordReset')");
                    table.ForeignKey(
                        name: "fk_account_setup_tokens_asp_net_users_user_id_organization_id",
                        columns: x => new { x.user_id, x.organization_id },
                        principalTable: "users",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_account_setup_tokens_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "organization_agreement_acceptances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    agreement_version = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    text_sha256 = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ip_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization_agreement_acceptances", x => x.id);
                    table.ForeignKey(
                        name: "fk_organization_agreement_acceptances_asp_net_users_user_id_organization_id",
                        columns: x => new { x.user_id, x.organization_id },
                        principalTable: "users",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_organization_agreement_acceptances_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "users",
                column: "normalized_email",
                unique: true,
                filter: "[normalized_email] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_organizations_creation_request",
                table: "organizations",
                columns: new[] { "created_by_user_id", "creation_request_key" },
                unique: true,
                filter: "[creation_request_key] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_generation_jobs_error_code",
                table: "generation_jobs",
                sql: "[error_code] IN (N'InvalidInput', N'ContentRejected', N'RateLimited', N'Timeout', N'ProviderUnavailable', N'InvalidOutput', N'MaskQualityLow', N'QuotaExceeded', N'NotImplemented', N'OutcomeUncertain', N'BudgetExceeded', N'ProviderNotConfigured', N'PaidAiNotEnabled', N'Unknown')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_usage_logs_error_code",
                table: "ai_usage_logs",
                sql: "[error_code] IN (N'InvalidInput', N'ContentRejected', N'RateLimited', N'Timeout', N'ProviderUnavailable', N'InvalidOutput', N'MaskQualityLow', N'QuotaExceeded', N'NotImplemented', N'OutcomeUncertain', N'BudgetExceeded', N'ProviderNotConfigured', N'PaidAiNotEnabled', N'Unknown')");

            migrationBuilder.CreateIndex(
                name: "ix_account_setup_tokens_organization_id",
                table: "account_setup_tokens",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_account_setup_tokens_user_id_organization_id",
                table: "account_setup_tokens",
                columns: new[] { "user_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_account_setup_tokens_user_purpose_open",
                table: "account_setup_tokens",
                columns: new[] { "user_id", "purpose" },
                filter: "[used_at] IS NULL AND [revoked_at] IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_account_setup_tokens_token_hash",
                table: "account_setup_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_organization_agreement_acceptances_user_id_organization_id",
                table: "organization_agreement_acceptances",
                columns: new[] { "user_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ux_org_agreement_acceptances_org_version",
                table: "organization_agreement_acceptances",
                columns: new[] { "organization_id", "agreement_version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_setup_tokens");

            migrationBuilder.DropTable(
                name: "organization_agreement_acceptances");

            migrationBuilder.DropIndex(
                name: "EmailIndex",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ux_organizations_creation_request",
                table: "organizations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_generation_jobs_error_code",
                table: "generation_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_usage_logs_error_code",
                table: "ai_usage_logs");

            migrationBuilder.DropColumn(
                name: "created_by_user_id",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "creation_request_hash",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "creation_request_key",
                table: "organizations");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "users",
                column: "normalized_email");

            migrationBuilder.AddCheckConstraint(
                name: "ck_generation_jobs_error_code",
                table: "generation_jobs",
                sql: "[error_code] IN (N'InvalidInput', N'ContentRejected', N'RateLimited', N'Timeout', N'ProviderUnavailable', N'InvalidOutput', N'MaskQualityLow', N'QuotaExceeded', N'NotImplemented', N'OutcomeUncertain', N'BudgetExceeded', N'ProviderNotConfigured', N'Unknown')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_usage_logs_error_code",
                table: "ai_usage_logs",
                sql: "[error_code] IN (N'InvalidInput', N'ContentRejected', N'RateLimited', N'Timeout', N'ProviderUnavailable', N'InvalidOutput', N'MaskQualityLow', N'QuotaExceeded', N'NotImplemented', N'OutcomeUncertain', N'BudgetExceeded', N'ProviderNotConfigured', N'Unknown')");
        }
    }
}
