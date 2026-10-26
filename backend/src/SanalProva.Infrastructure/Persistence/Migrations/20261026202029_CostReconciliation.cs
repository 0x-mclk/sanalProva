using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SanalProva.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CostReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_platform_admin",
                table: "users",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "reserved_cost_usd",
                table: "generation_jobs",
                type: "decimal(10,4)",
                precision: 10,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "adjusts_log_id",
                table: "ai_usage_logs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "event_key",
                table: "ai_usage_logs",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_adjustment",
                table: "ai_usage_logs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "occurred_at",
                table: "ai_usage_logs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reconciliation_state",
                table: "ai_usage_logs",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_cost_reconciliations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    usage_log_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    job_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    decision = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    effective_cost_usd = table.Column<decimal>(type: "decimal(10,4)", precision: 10, scale: 4, nullable: false),
                    confirmed_amount_usd = table.Column<decimal>(type: "decimal(10,4)", precision: 10, scale: 4, nullable: true),
                    note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    decided_by_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    idempotency_key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    request_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_cost_reconciliations", x => x.id);
                    table.CheckConstraint("ck_ai_cost_reconciliations_amount", "[effective_cost_usd] >= 0 AND ([confirmed_amount_usd] IS NULL OR [confirmed_amount_usd] > 0)");
                    table.CheckConstraint("ck_ai_cost_reconciliations_decision", "[decision] IN (N'ConfirmedCharged', N'ConfirmedNotCharged', N'WrittenOff')");
                    table.CheckConstraint("ck_ai_cost_reconciliations_sequence", "[sequence] >= 1");
                    table.ForeignKey(
                        name: "fk_ai_cost_reconciliations_ai_usage_logs_usage_log_id",
                        column: x => x.usage_log_id,
                        principalTable: "ai_usage_logs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_logs_adjusts_log_id",
                table: "ai_usage_logs",
                column: "adjusts_log_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_logs_reconciliation",
                table: "ai_usage_logs",
                column: "reconciliation_state",
                filter: "[reconciliation_state] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_ai_usage_logs_event_key",
                table: "ai_usage_logs",
                column: "event_key",
                unique: true,
                filter: "[event_key] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_usage_logs_adjustment",
                table: "ai_usage_logs",
                sql: "[is_adjustment] = 0 OR ([adjusts_log_id] IS NOT NULL AND [event_key] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_usage_logs_reconciliation_state",
                table: "ai_usage_logs",
                sql: "[reconciliation_state] IN (N'Open', N'ConfirmedCharged', N'ConfirmedNotCharged', N'WrittenOff')");

            migrationBuilder.CreateIndex(
                name: "ix_ai_cost_reconciliations_organization_id",
                table: "ai_cost_reconciliations",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ux_ai_cost_reconciliations_case_key",
                table: "ai_cost_reconciliations",
                columns: new[] { "usage_log_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_ai_cost_reconciliations_case_sequence",
                table: "ai_cost_reconciliations",
                columns: new[] { "usage_log_id", "sequence" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_ai_usage_logs_ai_usage_logs_adjusts_log_id",
                table: "ai_usage_logs",
                column: "adjusts_log_id",
                principalTable: "ai_usage_logs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_ai_usage_logs_ai_usage_logs_adjusts_log_id",
                table: "ai_usage_logs");

            migrationBuilder.DropTable(
                name: "ai_cost_reconciliations");

            migrationBuilder.DropIndex(
                name: "ix_ai_usage_logs_adjusts_log_id",
                table: "ai_usage_logs");

            migrationBuilder.DropIndex(
                name: "ix_ai_usage_logs_reconciliation",
                table: "ai_usage_logs");

            migrationBuilder.DropIndex(
                name: "ux_ai_usage_logs_event_key",
                table: "ai_usage_logs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_usage_logs_adjustment",
                table: "ai_usage_logs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_usage_logs_reconciliation_state",
                table: "ai_usage_logs");

            migrationBuilder.DropColumn(
                name: "is_platform_admin",
                table: "users");

            migrationBuilder.DropColumn(
                name: "reserved_cost_usd",
                table: "generation_jobs");

            migrationBuilder.DropColumn(
                name: "adjusts_log_id",
                table: "ai_usage_logs");

            migrationBuilder.DropColumn(
                name: "event_key",
                table: "ai_usage_logs");

            migrationBuilder.DropColumn(
                name: "is_adjustment",
                table: "ai_usage_logs");

            migrationBuilder.DropColumn(
                name: "occurred_at",
                table: "ai_usage_logs");

            migrationBuilder.DropColumn(
                name: "reconciliation_state",
                table: "ai_usage_logs");
        }
    }
}
