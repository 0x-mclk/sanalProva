using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SanalProva.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PilotClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "staff_approved_at",
                table: "session_photos",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "staff_approved_by_user_id",
                table: "session_photos",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "admitted_live",
                table: "generation_jobs",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "detail",
                table: "audit_logs",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "request_key_hash",
                table: "account_setup_tokens",
                type: "binary(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "auth_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    last_interaction_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    locked_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_auth_sessions_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_account_setup_tokens_user_request_key",
                table: "account_setup_tokens",
                columns: new[] { "user_id", "request_key_hash" },
                unique: true,
                filter: "[request_key_hash] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_auth_sessions_user_id",
                table: "auth_sessions",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_sessions");

            migrationBuilder.DropIndex(
                name: "ix_account_setup_tokens_user_request_key",
                table: "account_setup_tokens");

            migrationBuilder.DropColumn(
                name: "staff_approved_at",
                table: "session_photos");

            migrationBuilder.DropColumn(
                name: "staff_approved_by_user_id",
                table: "session_photos");

            migrationBuilder.DropColumn(
                name: "admitted_live",
                table: "generation_jobs");

            migrationBuilder.DropColumn(
                name: "detail",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "request_key_hash",
                table: "account_setup_tokens");
        }
    }
}
