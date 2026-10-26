using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SanalProva.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSqlServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    device_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    action = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    entity_type = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ip_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    slug = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    is_active = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    settings = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    slug = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    sort_order = table.Column<int>(type: "int", nullable: false),
                    body_zone = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    layer_order = table.Column<int>(type: "int", nullable: false),
                    max_selection = table.Column<int>(type: "int", nullable: false),
                    conflicts_with = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ai_hint = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    is_active = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                    table.CheckConstraint("ck_categories_body_zone", "[body_zone] IN (N'Upper', N'Lower', N'FullBody', N'Waist', N'Accessory')");
                    table.CheckConstraint("ck_categories_max_selection", "max_selection >= 1");
                    table.ForeignKey(
                        name: "fk_categories_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stored_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    storage_key = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    content_type = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    width = table.Column<int>(type: "int", nullable: true),
                    height = table.Column<int>(type: "int", nullable: true),
                    purpose = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    retention_until = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stored_files", x => x.id);
                    table.CheckConstraint("ck_stored_files_purpose", "[purpose] IN (N'CustomerPhoto', N'GeneratedImage', N'ProductImage', N'Mask')");
                    table.ForeignKey(
                        name: "fk_stored_files_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stores",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    timezone = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    daily_generation_quota = table.Column<int>(type: "int", nullable: true),
                    is_active = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stores", x => x.id);
                    table.ForeignKey(
                        name: "fk_stores_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    display_name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    pin_hash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    is_active = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    user_name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    normalized_user_name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    normalized_email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    email_confirmed = table.Column<bool>(type: "bit", nullable: false),
                    password_hash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    security_stamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    concurrency_stamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    phone_number = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    phone_number_confirmed = table.Column<bool>(type: "bit", nullable: false),
                    two_factor_enabled = table.Column<bool>(type: "bit", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    lockout_enabled = table.Column<bool>(type: "bit", nullable: false),
                    access_failed_count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.ForeignKey(
                        name: "fk_users_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    category_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false, collation: "Turkish_100_CI_AS"),
                    description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    tagline = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    product_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    gender_target = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    layer_order_override = table.Column<int>(type: "int", nullable: true),
                    ai_notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    is_active = table.Column<bool>(type: "bit", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.CheckConstraint("ck_products_gender_target", "[gender_target] IN (N'Women', N'Men', N'Unisex', N'Kids')");
                    table.CheckConstraint("ck_products_product_type", "[product_type] IN (N'Single', N'OutfitSet')");
                    table.ForeignKey(
                        name: "fk_products_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_products_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tryon_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    customer_label = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    retention_until = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    last_activity_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    draft = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tryon_sessions", x => x.id);
                    table.CheckConstraint("ck_tryon_sessions_status", "[status] IN (N'Open', N'Closed', N'Purged')");
                    table.ForeignKey(
                        name: "fk_tryon_sessions_asp_net_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tryon_sessions_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_claims",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    claim_type = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    claim_value = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_claims", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_claims_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_logins",
                columns: table => new
                {
                    login_provider = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    provider_key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    provider_display_name = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_logins", x => new { x.login_provider, x.provider_key });
                    table.ForeignKey(
                        name: "fk_user_logins_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_store_roles",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    role = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_store_roles", x => new { x.user_id, x.store_id });
                    table.CheckConstraint("ck_user_store_roles_role", "[role] IN (N'Staff', N'Manager', N'Owner')");
                    table.ForeignKey(
                        name: "fk_user_store_roles_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_store_roles_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_store_roles_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_tokens",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    login_provider = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    value = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_tokens", x => new { x.user_id, x.login_provider, x.name });
                    table.ForeignKey(
                        name: "fk_user_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_variants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    product_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    color_name = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    color_hex = table.Column<string>(type: "nchar(7)", fixedLength: true, maxLength: 7, nullable: true),
                    is_default = table.Column<bool>(type: "bit", nullable: false),
                    is_active = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_variants", x => x.id);
                    table.CheckConstraint("ck_product_variants_color_hex", "[color_hex] IS NULL OR [color_hex] LIKE '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'");
                    table.ForeignKey(
                        name: "fk_product_variants_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "consent_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    session_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    notice_version = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    processing_consent = table.Column<bool>(type: "bit", nullable: false),
                    foreign_transfer_consent = table.Column<bool>(type: "bit", nullable: false),
                    extended_retention_consent = table.Column<bool>(type: "bit", nullable: false),
                    guardian_consent = table.Column<bool>(type: "bit", nullable: true),
                    method = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    recorded_by_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    withdrawn_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consent_records", x => x.id);
                    table.CheckConstraint("ck_consent_records_method", "[method] IN (N'StaffConfirmedVerbal', N'CustomerTapped', N'SignedOnTablet')");
                    table.ForeignKey(
                        name: "fk_consent_records_asp_net_users_recorded_by_user_id",
                        column: x => x.recorded_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_consent_records_tryon_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "tryon_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "display_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    token_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    current_session_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    state_version = table.Column<long>(type: "bigint", nullable: false),
                    state_changed_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    display_state = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_display_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_display_devices_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_display_devices_try_on_sessions_current_session_id",
                        column: x => x.current_session_id,
                        principalTable: "tryon_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "outfit_selections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    session_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    mode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outfit_selections", x => x.id);
                    table.CheckConstraint("ck_outfit_selections_mode", "[mode] IN (N'OutfitSet', N'FreeCombination')");
                    table.ForeignKey(
                        name: "fk_outfit_selections_try_on_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "tryon_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "session_photos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    session_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    file_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    angle = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    width = table.Column<int>(type: "int", nullable: false),
                    height = table.Column<int>(type: "int", nullable: false),
                    is_primary = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    quality_flags = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_photos", x => x.id);
                    table.CheckConstraint("ck_session_photos_angle", "[angle] IN (N'Front', N'Back', N'Left', N'Right', N'Detail')");
                    table.ForeignKey(
                        name: "fk_session_photos_stored_files_file_id",
                        column: x => x.file_id,
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_session_photos_tryon_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "tryon_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "outfit_set_components",
                columns: table => new
                {
                    set_product_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    component_product_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    component_variant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outfit_set_components", x => new { x.set_product_id, x.component_product_id });
                    table.CheckConstraint("ck_outfit_set_components_not_self", "set_product_id <> component_product_id");
                    table.ForeignKey(
                        name: "fk_outfit_set_components_product_variants_component_variant_id",
                        column: x => x.component_variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_outfit_set_components_products_component_product_id",
                        column: x => x.component_product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_outfit_set_components_products_set_product_id",
                        column: x => x.set_product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    product_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    variant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    file_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    angle = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    is_ai_reference = table.Column<bool>(type: "bit", nullable: false),
                    sort_order = table.Column<int>(type: "int", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_images", x => x.id);
                    table.CheckConstraint("ck_product_images_angle", "[angle] IN (N'Front', N'Back', N'Left', N'Right', N'Detail')");
                    table.CheckConstraint("ck_product_images_kind", "[kind] IN (N'Flat', N'OnModel', N'Detail')");
                    table.ForeignKey(
                        name: "fk_product_images_product_variants_variant_id",
                        column: x => x.variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_images_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_product_images_stored_files_file_id",
                        column: x => x.file_id,
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "display_pairings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    pairing_secret_hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    claimed_device_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    claimed_by_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    failed_attempts = table.Column<int>(type: "int", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_display_pairings", x => x.id);
                    table.ForeignKey(
                        name: "fk_display_pairings_display_devices_claimed_device_id",
                        column: x => x.claimed_device_id,
                        principalTable: "display_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "outfit_selection_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    selection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    product_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    variant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    category_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    layer_order = table.Column<int>(type: "int", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outfit_selection_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_outfit_selection_items_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_outfit_selection_items_outfit_selections_selection_id",
                        column: x => x.selection_id,
                        principalTable: "outfit_selections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_outfit_selection_items_product_variants_variant_id",
                        column: x => x.variant_id,
                        principalTable: "product_variants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_outfit_selection_items_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ai_usage_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    job_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    provider = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    model = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    operation = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    quality = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    attempt = table.Column<int>(type: "int", nullable: false),
                    success = table.Column<bool>(type: "bit", nullable: false),
                    duration_ms = table.Column<int>(type: "int", nullable: false),
                    credits_used = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    input_tokens = table.Column<int>(type: "int", nullable: true),
                    output_tokens = table.Column<int>(type: "int", nullable: true),
                    estimated_cost_usd = table.Column<decimal>(type: "decimal(10,4)", precision: 10, scale: 4, nullable: false),
                    actual_cost_usd = table.Column<decimal>(type: "decimal(10,4)", precision: 10, scale: 4, nullable: true),
                    provider_request_id = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    error_code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_usage_logs", x => x.id);
                    table.CheckConstraint("ck_ai_usage_logs_error_code", "[error_code] IN (N'InvalidInput', N'ContentRejected', N'RateLimited', N'Timeout', N'ProviderUnavailable', N'InvalidOutput', N'MaskQualityLow', N'QuotaExceeded', N'NotImplemented', N'BudgetExceeded', N'ProviderNotConfigured', N'Unknown')");
                    table.ForeignKey(
                        name: "fk_ai_usage_logs_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "generated_images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    session_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    job_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    parent_image_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    version_no = table.Column<int>(type: "int", nullable: false),
                    file_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    angle = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    view_group_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    is_color_preview = table.Column<bool>(type: "bit", nullable: false),
                    is_demo = table.Column<bool>(type: "bit", nullable: false),
                    is_favorite = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_generated_images", x => x.id);
                    table.CheckConstraint("ck_generated_images_angle", "[angle] IN (N'Front', N'Back', N'Left', N'Right', N'Detail')");
                    table.ForeignKey(
                        name: "fk_generated_images_generated_images_parent_image_id",
                        column: x => x.parent_image_id,
                        principalTable: "generated_images",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_generated_images_stored_files_file_id",
                        column: x => x.file_id,
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_generated_images_try_on_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "tryon_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "generation_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    organization_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    store_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    session_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    job_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    selection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    source_photo_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    base_image_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    revision_params = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    provider = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    model = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    prompt_template_version = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    provider_request_id = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    stage = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    request_fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    model_key = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    quality = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    idempotency_key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    attempt = table.Column<int>(type: "int", nullable: false),
                    max_attempts = table.Column<int>(type: "int", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    error_code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    error_message = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_generation_jobs", x => x.id);
                    table.CheckConstraint("ck_generation_jobs_attempts", "attempt >= 0 AND attempt <= max_attempts");
                    table.CheckConstraint("ck_generation_jobs_error_code", "[error_code] IN (N'InvalidInput', N'ContentRejected', N'RateLimited', N'Timeout', N'ProviderUnavailable', N'InvalidOutput', N'MaskQualityLow', N'QuotaExceeded', N'NotImplemented', N'BudgetExceeded', N'ProviderNotConfigured', N'Unknown')");
                    table.CheckConstraint("ck_generation_jobs_job_type", "[job_type] IN (N'OutfitSet', N'FreeCombination', N'Recolor', N'ProductSwap', N'VariantPreview')");
                    table.CheckConstraint("ck_generation_jobs_stage", "[stage] IN (N'Preparing', N'Generating', N'Validating')");
                    table.CheckConstraint("ck_generation_jobs_status", "[status] IN (N'Queued', N'Running', N'Succeeded', N'Failed', N'Cancelled')");
                    table.ForeignKey(
                        name: "fk_generation_jobs_generated_images_base_image_id",
                        column: x => x.base_image_id,
                        principalTable: "generated_images",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_generation_jobs_outfit_selections_selection_id",
                        column: x => x.selection_id,
                        principalTable: "outfit_selections",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_generation_jobs_session_photos_source_photo_id",
                        column: x => x.source_photo_id,
                        principalTable: "session_photos",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_generation_jobs_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_generation_jobs_try_on_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "tryon_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_logs_job_id",
                table: "ai_usage_logs",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_logs_organization_id_created_at",
                table: "ai_usage_logs",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_logs_store_id_created_at",
                table: "ai_usage_logs",
                columns: new[] { "store_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_organization_id_created_at",
                table: "audit_logs",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_categories_organization_id_slug",
                table: "categories",
                columns: new[] { "organization_id", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_consent_records_recorded_by_user_id",
                table: "consent_records",
                column: "recorded_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_consent_records_session_id",
                table: "consent_records",
                column: "session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_display_devices_store_id",
                table: "display_devices",
                column: "store_id");

            migrationBuilder.CreateIndex(
                name: "ix_display_devices_token_hash",
                table: "display_devices",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_display_devices_current_session",
                table: "display_devices",
                column: "current_session_id",
                unique: true,
                filter: "current_session_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_display_pairings_claimed_device_id",
                table: "display_pairings",
                column: "claimed_device_id");

            migrationBuilder.CreateIndex(
                name: "ix_display_pairings_code_hash",
                table: "display_pairings",
                column: "code_hash");

            migrationBuilder.CreateIndex(
                name: "ix_display_pairings_expires_at",
                table: "display_pairings",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_display_pairings_pairing_secret_hash",
                table: "display_pairings",
                column: "pairing_secret_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_generated_images_file_id",
                table: "generated_images",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_generated_images_job_id",
                table: "generated_images",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_generated_images_parent_image_id",
                table: "generated_images",
                column: "parent_image_id");

            migrationBuilder.CreateIndex(
                name: "ix_generated_images_session_id_version_no",
                table: "generated_images",
                columns: new[] { "session_id", "version_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_generated_images_view_group_id",
                table: "generated_images",
                column: "view_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_generation_jobs_base_image_id",
                table: "generation_jobs",
                column: "base_image_id");

            migrationBuilder.CreateIndex(
                name: "ix_generation_jobs_queue",
                table: "generation_jobs",
                column: "next_attempt_at",
                filter: "[status] = N'Queued'");

            migrationBuilder.CreateIndex(
                name: "ix_generation_jobs_running_lease",
                table: "generation_jobs",
                column: "lease_until",
                filter: "[status] = N'Running'");

            migrationBuilder.CreateIndex(
                name: "ix_generation_jobs_selection_id",
                table: "generation_jobs",
                column: "selection_id");

            migrationBuilder.CreateIndex(
                name: "ix_generation_jobs_session_id_idempotency_key",
                table: "generation_jobs",
                columns: new[] { "session_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_generation_jobs_session_id_request_fingerprint",
                table: "generation_jobs",
                columns: new[] { "session_id", "request_fingerprint" });

            migrationBuilder.CreateIndex(
                name: "ix_generation_jobs_source_photo_id",
                table: "generation_jobs",
                column: "source_photo_id");

            migrationBuilder.CreateIndex(
                name: "ix_generation_jobs_store_id",
                table: "generation_jobs",
                column: "store_id");

            migrationBuilder.CreateIndex(
                name: "ix_organizations_slug",
                table: "organizations",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outfit_selection_items_category_id",
                table: "outfit_selection_items",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_outfit_selection_items_product_id",
                table: "outfit_selection_items",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_outfit_selection_items_selection_id",
                table: "outfit_selection_items",
                column: "selection_id");

            migrationBuilder.CreateIndex(
                name: "ix_outfit_selection_items_variant_id",
                table: "outfit_selection_items",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outfit_selections_session_id",
                table: "outfit_selections",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_outfit_set_components_component_product_id",
                table: "outfit_set_components",
                column: "component_product_id");

            migrationBuilder.CreateIndex(
                name: "ix_outfit_set_components_component_variant_id",
                table: "outfit_set_components",
                column: "component_variant_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_images_file_id",
                table: "product_images",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_images_variant_id",
                table: "product_images",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "ux_product_images_ai_reference",
                table: "product_images",
                columns: new[] { "product_id", "variant_id", "angle" },
                unique: true,
                filter: "[is_ai_reference] = 1");

            migrationBuilder.CreateIndex(
                name: "ix_product_variants_product_id_code",
                table: "product_variants",
                columns: new[] { "product_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_product_variants_default",
                table: "product_variants",
                column: "product_id",
                unique: true,
                filter: "[is_default] = 1");

            migrationBuilder.CreateIndex(
                name: "ix_products_category_id",
                table: "products",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_products_organization_id_category_id_is_active",
                table: "products",
                columns: new[] { "organization_id", "category_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_products_organization_id_code",
                table: "products",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_session_photos_file_id",
                table: "session_photos",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_photos_session_id",
                table: "session_photos",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_stored_files_organization_id",
                table: "stored_files",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_stored_files_retention_until",
                table: "stored_files",
                column: "retention_until",
                filter: "deleted_at IS NULL AND retention_until IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_stored_files_storage_key",
                table: "stored_files",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stores_organization_id_code",
                table: "stores",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tryon_sessions_created_by_user_id",
                table: "tryon_sessions",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_tryon_sessions_retention_until",
                table: "tryon_sessions",
                column: "retention_until");

            migrationBuilder.CreateIndex(
                name: "ix_tryon_sessions_store_id_status",
                table: "tryon_sessions",
                columns: new[] { "store_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_user_claims_user_id",
                table: "user_claims",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_logins_user_id",
                table: "user_logins",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_store_roles_organization_id",
                table: "user_store_roles",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_store_roles_store_id",
                table: "user_store_roles",
                column: "store_id");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "users",
                column: "normalized_email");

            migrationBuilder.CreateIndex(
                name: "ix_users_organization_id",
                table: "users",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "users",
                column: "normalized_user_name",
                unique: true,
                filter: "[normalized_user_name] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_ai_usage_logs_generation_jobs_job_id",
                table: "ai_usage_logs",
                column: "job_id",
                principalTable: "generation_jobs",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_generated_images_generation_jobs_job_id",
                table: "generated_images",
                column: "job_id",
                principalTable: "generation_jobs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_generated_images_generation_jobs_job_id",
                table: "generated_images");

            migrationBuilder.DropTable(
                name: "ai_usage_logs");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "consent_records");

            migrationBuilder.DropTable(
                name: "display_pairings");

            migrationBuilder.DropTable(
                name: "outfit_selection_items");

            migrationBuilder.DropTable(
                name: "outfit_set_components");

            migrationBuilder.DropTable(
                name: "product_images");

            migrationBuilder.DropTable(
                name: "user_claims");

            migrationBuilder.DropTable(
                name: "user_logins");

            migrationBuilder.DropTable(
                name: "user_store_roles");

            migrationBuilder.DropTable(
                name: "user_tokens");

            migrationBuilder.DropTable(
                name: "display_devices");

            migrationBuilder.DropTable(
                name: "product_variants");

            migrationBuilder.DropTable(
                name: "products");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "generation_jobs");

            migrationBuilder.DropTable(
                name: "generated_images");

            migrationBuilder.DropTable(
                name: "outfit_selections");

            migrationBuilder.DropTable(
                name: "session_photos");

            migrationBuilder.DropTable(
                name: "stored_files");

            migrationBuilder.DropTable(
                name: "tryon_sessions");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "stores");

            migrationBuilder.DropTable(
                name: "organizations");
        }
    }
}
