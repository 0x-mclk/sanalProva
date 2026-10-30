using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SanalProva.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CreditSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "credit_cost",
                table: "generation_jobs",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "credit_accounts",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    balance_credits = table.Column<int>(type: "int", nullable: false),
                    reserved_credits = table.Column<int>(type: "int", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credit_accounts", x => x.organization_id);
                    table.CheckConstraint("ck_credit_accounts_balance_non_negative", "[balance_credits] >= 0");
                    table.CheckConstraint("ck_credit_accounts_reserved_valid", "[reserved_credits] >= 0 AND [reserved_credits] <= [balance_credits]");
                    table.ForeignKey(
                        name: "fk_credit_accounts_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "credit_ledger",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    entry_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    amount = table.Column<int>(type: "int", nullable: false),
                    job_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    reference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    tariff_key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    balance_after = table.Column<int>(type: "int", nullable: false),
                    reserved_after = table.Column<int>(type: "int", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credit_ledger", x => x.id);
                    table.CheckConstraint("ck_credit_ledger_after_valid", "[balance_after] >= 0 AND [reserved_after] >= 0 AND [reserved_after] <= [balance_after]");
                    table.CheckConstraint("ck_credit_ledger_amount", "([entry_type] = N'Adjustment' AND [amount] <> 0) OR ([entry_type] <> N'Adjustment' AND [amount] > 0)");
                    table.CheckConstraint("ck_credit_ledger_entry_type", "[entry_type] IN (N'Grant', N'Adjustment', N'Reserve', N'Release', N'Consume')");
                    table.CheckConstraint("ck_credit_ledger_shape", "([entry_type] IN (N'Grant', N'Adjustment') AND [reference] IS NOT NULL AND [job_id] IS NULL) OR ([entry_type] IN (N'Reserve', N'Release', N'Consume') AND [job_id] IS NOT NULL)");
                    table.CheckConstraint("ck_credit_ledger_source", "[source] IN (N'Operator', N'System', N'Payment')");
                    table.ForeignKey(
                        name: "fk_credit_ledger_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_credit_ledger_org_created",
                table: "credit_ledger",
                columns: new[] { "organization_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_credit_ledger_job_close",
                table: "credit_ledger",
                column: "job_id",
                unique: true,
                filter: "[entry_type] IN (N'Release', N'Consume') AND [job_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_credit_ledger_job_reserve",
                table: "credit_ledger",
                columns: new[] { "job_id", "entry_type" },
                unique: true,
                filter: "[entry_type] = N'Reserve' AND [job_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_credit_ledger_org_reference",
                table: "credit_ledger",
                columns: new[] { "organization_id", "reference" },
                unique: true,
                filter: "[reference] IS NOT NULL");

            // Değiştirilemez defter: UPDATE ve DELETE veritabanı düzeyinde reddedilir (yetkili hesap dahil). INSTEAD OF tetikleyici INSERT'i etkilemez.
            // Mali kayıt düzeltmesi yeni bir Adjustment girdisidir; satır değiştirilmez/silinmez.
            // EXEC(N'...'): idempotent betikte (IF NOT EXISTS ... BEGIN ... END) CREATE TRIGGER toplu işin ilk deyimi olamaz; dinamik SQL bunu çözer
            migrationBuilder.Sql("EXEC(N'CREATE TRIGGER dbo.tr_credit_ledger_append_only ON dbo.credit_ledger INSTEAD OF UPDATE, DELETE AS BEGIN THROW 51200, N''credit_ledger yalnizca eklemeye aciktir (append-only): satir degistirilemez veya silinemez.'', 1; END')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Politika: migration yalnızca ileri yönlüdür; Down mali kayıt silerdi (kredi defteri). Yalnızca geliştirme/test için tablolar tetikleyiciyle birlikte kaldırılır.
            migrationBuilder.DropTable(
                name: "credit_accounts");

            migrationBuilder.DropTable(
                name: "credit_ledger");

            migrationBuilder.DropColumn(
                name: "credit_cost",
                table: "generation_jobs");
        }
    }
}
