using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SanalProva.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantConsistency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ÖN KONTROL: bileşik yabancı anahtarlar eklenmeden önce mevcut veride çapraz firma (veya çapraz şube) ilişkisi olmadığı doğrulanır.
            // İhlal varsa migration (tek transaction) açık bir hatayla DURUR ve hiçbir şey değişmez; veri incelenip (tests: TenantScan) düzeltilmeden ilerlenmez.
            // Eski tek sütunlu FK'lar ebeveynin varlığını zaten garanti ettiği için yalnızca kimlik dışı sütunlar (firma, şube) karşılaştırılır.
            Precheck(migrationBuilder, "ai_cost_reconciliations", ["usage_log_id", "organization_id"], "ai_usage_logs", ["id", "organization_id"]);
            Precheck(migrationBuilder, "ai_usage_logs", ["adjusts_log_id", "organization_id"], "ai_usage_logs", ["id", "organization_id"]);
            Precheck(migrationBuilder, "ai_usage_logs", ["job_id", "organization_id"], "generation_jobs", ["id", "organization_id"]);
            Precheck(migrationBuilder, "ai_usage_logs", ["store_id", "organization_id"], "stores", ["id", "organization_id"]);
            Precheck(migrationBuilder, "audit_logs", ["store_id", "organization_id"], "stores", ["id", "organization_id"]);
            Precheck(migrationBuilder, "consent_records", ["recorded_by_user_id", "organization_id"], "users", ["id", "organization_id"]);
            Precheck(migrationBuilder, "consent_records", ["session_id", "organization_id"], "tryon_sessions", ["id", "organization_id"]);
            Precheck(migrationBuilder, "display_devices", ["store_id", "organization_id"], "stores", ["id", "organization_id"]);
            Precheck(migrationBuilder, "display_devices", ["current_session_id", "store_id", "organization_id"], "tryon_sessions", ["id", "store_id", "organization_id"]);
            Precheck(migrationBuilder, "generated_images", ["parent_image_id", "organization_id"], "generated_images", ["id", "organization_id"]);
            Precheck(migrationBuilder, "generated_images", ["job_id", "organization_id"], "generation_jobs", ["id", "organization_id"]);
            Precheck(migrationBuilder, "generated_images", ["file_id", "organization_id"], "stored_files", ["id", "organization_id"]);
            Precheck(migrationBuilder, "generated_images", ["session_id", "organization_id"], "tryon_sessions", ["id", "organization_id"]);
            Precheck(migrationBuilder, "generation_jobs", ["base_image_id", "organization_id"], "generated_images", ["id", "organization_id"]);
            Precheck(migrationBuilder, "generation_jobs", ["selection_id", "organization_id"], "outfit_selections", ["id", "organization_id"]);
            Precheck(migrationBuilder, "generation_jobs", ["source_photo_id", "organization_id"], "session_photos", ["id", "organization_id"]);
            Precheck(migrationBuilder, "generation_jobs", ["store_id", "organization_id"], "stores", ["id", "organization_id"]);
            Precheck(migrationBuilder, "generation_jobs", ["session_id", "store_id", "organization_id"], "tryon_sessions", ["id", "store_id", "organization_id"]);
            Precheck(migrationBuilder, "outfit_selection_items", ["category_id", "organization_id"], "categories", ["id", "organization_id"]);
            Precheck(migrationBuilder, "outfit_selection_items", ["selection_id", "organization_id"], "outfit_selections", ["id", "organization_id"]);
            Precheck(migrationBuilder, "outfit_selection_items", ["variant_id", "organization_id"], "product_variants", ["id", "organization_id"]);
            Precheck(migrationBuilder, "outfit_selection_items", ["product_id", "organization_id"], "products", ["id", "organization_id"]);
            Precheck(migrationBuilder, "outfit_selections", ["session_id", "organization_id"], "tryon_sessions", ["id", "organization_id"]);
            Precheck(migrationBuilder, "outfit_set_components", ["component_variant_id", "organization_id"], "product_variants", ["id", "organization_id"]);
            Precheck(migrationBuilder, "outfit_set_components", ["component_product_id", "organization_id"], "products", ["id", "organization_id"]);
            Precheck(migrationBuilder, "outfit_set_components", ["set_product_id", "organization_id"], "products", ["id", "organization_id"]);
            Precheck(migrationBuilder, "product_images", ["variant_id", "organization_id"], "product_variants", ["id", "organization_id"]);
            Precheck(migrationBuilder, "product_images", ["product_id", "organization_id"], "products", ["id", "organization_id"]);
            Precheck(migrationBuilder, "product_images", ["file_id", "organization_id"], "stored_files", ["id", "organization_id"]);
            Precheck(migrationBuilder, "product_variants", ["product_id", "organization_id"], "products", ["id", "organization_id"]);
            Precheck(migrationBuilder, "products", ["category_id", "organization_id"], "categories", ["id", "organization_id"]);
            Precheck(migrationBuilder, "session_photos", ["file_id", "organization_id"], "stored_files", ["id", "organization_id"]);
            Precheck(migrationBuilder, "session_photos", ["session_id", "organization_id"], "tryon_sessions", ["id", "organization_id"]);
            Precheck(migrationBuilder, "tryon_sessions", ["created_by_user_id", "organization_id"], "users", ["id", "organization_id"]);
            Precheck(migrationBuilder, "tryon_sessions", ["store_id", "organization_id"], "stores", ["id", "organization_id"]);
            Precheck(migrationBuilder, "user_store_roles", ["user_id", "organization_id"], "users", ["id", "organization_id"]);
            Precheck(migrationBuilder, "user_store_roles", ["store_id", "organization_id"], "stores", ["id", "organization_id"]);
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM audit_logs a WHERE NOT EXISTS (SELECT 1 FROM organizations o WHERE o.id = a.organization_id)) THROW 51200, N'Tenant tutarlılık ihlali: audit_logs.organization_id var olmayan bir firmaya işaret ediyor. Migration durduruldu.', 1;");

            migrationBuilder.DropForeignKey(
                name: "fk_ai_cost_reconciliations_ai_usage_logs_usage_log_id",
                table: "ai_cost_reconciliations");

            migrationBuilder.DropForeignKey(
                name: "fk_ai_usage_logs_ai_usage_logs_adjusts_log_id",
                table: "ai_usage_logs");

            migrationBuilder.DropForeignKey(
                name: "fk_ai_usage_logs_stores_store_id",
                table: "ai_usage_logs");

            migrationBuilder.DropForeignKey(
                name: "fk_consent_records_asp_net_users_recorded_by_user_id",
                table: "consent_records");

            migrationBuilder.DropForeignKey(
                name: "fk_consent_records_tryon_sessions_session_id",
                table: "consent_records");

            migrationBuilder.DropForeignKey(
                name: "fk_display_devices_stores_store_id",
                table: "display_devices");

            migrationBuilder.DropForeignKey(
                name: "fk_generated_images_generated_images_parent_image_id",
                table: "generated_images");

            migrationBuilder.DropForeignKey(
                name: "fk_generated_images_generation_jobs_job_id",
                table: "generated_images");

            migrationBuilder.DropForeignKey(
                name: "fk_generated_images_stored_files_file_id",
                table: "generated_images");

            migrationBuilder.DropForeignKey(
                name: "fk_generated_images_try_on_sessions_session_id",
                table: "generated_images");

            migrationBuilder.DropForeignKey(
                name: "fk_generation_jobs_generated_images_base_image_id",
                table: "generation_jobs");

            migrationBuilder.DropForeignKey(
                name: "fk_generation_jobs_outfit_selections_selection_id",
                table: "generation_jobs");

            migrationBuilder.DropForeignKey(
                name: "fk_generation_jobs_session_photos_source_photo_id",
                table: "generation_jobs");

            migrationBuilder.DropForeignKey(
                name: "fk_generation_jobs_stores_store_id",
                table: "generation_jobs");

            migrationBuilder.DropForeignKey(
                name: "fk_generation_jobs_try_on_sessions_session_id",
                table: "generation_jobs");

            migrationBuilder.DropForeignKey(
                name: "fk_outfit_selection_items_categories_category_id",
                table: "outfit_selection_items");

            migrationBuilder.DropForeignKey(
                name: "fk_outfit_selection_items_outfit_selections_selection_id",
                table: "outfit_selection_items");

            migrationBuilder.DropForeignKey(
                name: "fk_outfit_selection_items_product_variants_variant_id",
                table: "outfit_selection_items");

            migrationBuilder.DropForeignKey(
                name: "fk_outfit_selection_items_products_product_id",
                table: "outfit_selection_items");

            migrationBuilder.DropForeignKey(
                name: "fk_outfit_selections_try_on_sessions_session_id",
                table: "outfit_selections");

            migrationBuilder.DropForeignKey(
                name: "fk_outfit_set_components_product_variants_component_variant_id",
                table: "outfit_set_components");

            migrationBuilder.DropForeignKey(
                name: "fk_outfit_set_components_products_component_product_id",
                table: "outfit_set_components");

            migrationBuilder.DropForeignKey(
                name: "fk_outfit_set_components_products_set_product_id",
                table: "outfit_set_components");

            migrationBuilder.DropForeignKey(
                name: "fk_product_images_product_variants_variant_id",
                table: "product_images");

            migrationBuilder.DropForeignKey(
                name: "fk_product_images_products_product_id",
                table: "product_images");

            migrationBuilder.DropForeignKey(
                name: "fk_product_images_stored_files_file_id",
                table: "product_images");

            migrationBuilder.DropForeignKey(
                name: "fk_product_variants_products_product_id",
                table: "product_variants");

            migrationBuilder.DropForeignKey(
                name: "fk_products_categories_category_id",
                table: "products");

            migrationBuilder.DropForeignKey(
                name: "fk_session_photos_stored_files_file_id",
                table: "session_photos");

            migrationBuilder.DropForeignKey(
                name: "fk_session_photos_tryon_sessions_session_id",
                table: "session_photos");

            migrationBuilder.DropForeignKey(
                name: "fk_tryon_sessions_asp_net_users_created_by_user_id",
                table: "tryon_sessions");

            migrationBuilder.DropForeignKey(
                name: "fk_tryon_sessions_stores_store_id",
                table: "tryon_sessions");

            migrationBuilder.DropForeignKey(
                name: "fk_user_store_roles_asp_net_users_user_id",
                table: "user_store_roles");

            migrationBuilder.DropForeignKey(
                name: "fk_user_store_roles_stores_store_id",
                table: "user_store_roles");

            migrationBuilder.DropIndex(
                name: "ix_tryon_sessions_created_by_user_id",
                table: "tryon_sessions");

            migrationBuilder.DropIndex(
                name: "ix_session_photos_file_id",
                table: "session_photos");

            migrationBuilder.DropIndex(
                name: "ix_session_photos_session_id",
                table: "session_photos");

            migrationBuilder.DropIndex(
                name: "ix_products_category_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "ix_product_images_file_id",
                table: "product_images");

            migrationBuilder.DropIndex(
                name: "ix_product_images_variant_id",
                table: "product_images");

            migrationBuilder.DropIndex(
                name: "ix_outfit_set_components_component_product_id",
                table: "outfit_set_components");

            migrationBuilder.DropIndex(
                name: "ix_outfit_set_components_component_variant_id",
                table: "outfit_set_components");

            migrationBuilder.DropIndex(
                name: "ix_outfit_selections_session_id",
                table: "outfit_selections");

            migrationBuilder.DropIndex(
                name: "ix_outfit_selection_items_category_id",
                table: "outfit_selection_items");

            migrationBuilder.DropIndex(
                name: "ix_outfit_selection_items_product_id",
                table: "outfit_selection_items");

            migrationBuilder.DropIndex(
                name: "ix_outfit_selection_items_selection_id",
                table: "outfit_selection_items");

            migrationBuilder.DropIndex(
                name: "ix_outfit_selection_items_variant_id",
                table: "outfit_selection_items");

            migrationBuilder.DropIndex(
                name: "ix_generation_jobs_base_image_id",
                table: "generation_jobs");

            migrationBuilder.DropIndex(
                name: "ix_generation_jobs_selection_id",
                table: "generation_jobs");

            migrationBuilder.DropIndex(
                name: "ix_generation_jobs_source_photo_id",
                table: "generation_jobs");

            migrationBuilder.DropIndex(
                name: "ix_generation_jobs_store_id",
                table: "generation_jobs");

            migrationBuilder.DropIndex(
                name: "ix_generated_images_file_id",
                table: "generated_images");

            migrationBuilder.DropIndex(
                name: "ix_generated_images_job_id",
                table: "generated_images");

            migrationBuilder.DropIndex(
                name: "ix_generated_images_parent_image_id",
                table: "generated_images");

            migrationBuilder.DropIndex(
                name: "ix_consent_records_recorded_by_user_id",
                table: "consent_records");

            migrationBuilder.DropIndex(
                name: "ix_consent_records_session_id",
                table: "consent_records");

            migrationBuilder.DropIndex(
                name: "ix_ai_usage_logs_adjusts_log_id",
                table: "ai_usage_logs");

            migrationBuilder.DropIndex(
                name: "ix_ai_usage_logs_job_id",
                table: "ai_usage_logs");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_users_id_organization_id",
                table: "users",
                columns: new[] { "id", "organization_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_tryon_sessions_id_organization_id",
                table: "tryon_sessions",
                columns: new[] { "id", "organization_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_tryon_sessions_id_store_id_organization_id",
                table: "tryon_sessions",
                columns: new[] { "id", "store_id", "organization_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_stores_id_organization_id",
                table: "stores",
                columns: new[] { "id", "organization_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_stored_files_id_organization_id",
                table: "stored_files",
                columns: new[] { "id", "organization_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_session_photos_id_organization_id",
                table: "session_photos",
                columns: new[] { "id", "organization_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_products_id_organization_id",
                table: "products",
                columns: new[] { "id", "organization_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_product_variants_id_organization_id",
                table: "product_variants",
                columns: new[] { "id", "organization_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_outfit_selections_id_organization_id",
                table: "outfit_selections",
                columns: new[] { "id", "organization_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_generation_jobs_id_organization_id",
                table: "generation_jobs",
                columns: new[] { "id", "organization_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_generated_images_id_organization_id",
                table: "generated_images",
                columns: new[] { "id", "organization_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_categories_id_organization_id",
                table: "categories",
                columns: new[] { "id", "organization_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_ai_usage_logs_id_organization_id",
                table: "ai_usage_logs",
                columns: new[] { "id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_user_store_roles_store_id_organization_id",
                table: "user_store_roles",
                columns: new[] { "store_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_user_store_roles_user_id_organization_id",
                table: "user_store_roles",
                columns: new[] { "user_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tryon_sessions_created_by_user_id_organization_id",
                table: "tryon_sessions",
                columns: new[] { "created_by_user_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tryon_sessions_store_id_organization_id",
                table: "tryon_sessions",
                columns: new[] { "store_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_session_photos_file_id_organization_id",
                table: "session_photos",
                columns: new[] { "file_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_session_photos_session_id_organization_id",
                table: "session_photos",
                columns: new[] { "session_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_products_category_id_organization_id",
                table: "products",
                columns: new[] { "category_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_variants_product_id_organization_id",
                table: "product_variants",
                columns: new[] { "product_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_images_file_id_organization_id",
                table: "product_images",
                columns: new[] { "file_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_images_product_id_organization_id",
                table: "product_images",
                columns: new[] { "product_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_images_variant_id_organization_id",
                table: "product_images",
                columns: new[] { "variant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outfit_set_components_component_product_id_organization_id",
                table: "outfit_set_components",
                columns: new[] { "component_product_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outfit_set_components_component_variant_id_organization_id",
                table: "outfit_set_components",
                columns: new[] { "component_variant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outfit_set_components_set_product_id_organization_id",
                table: "outfit_set_components",
                columns: new[] { "set_product_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outfit_selections_session_id_organization_id",
                table: "outfit_selections",
                columns: new[] { "session_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outfit_selection_items_category_id_organization_id",
                table: "outfit_selection_items",
                columns: new[] { "category_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outfit_selection_items_product_id_organization_id",
                table: "outfit_selection_items",
                columns: new[] { "product_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outfit_selection_items_selection_id_organization_id",
                table: "outfit_selection_items",
                columns: new[] { "selection_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_outfit_selection_items_variant_id_organization_id",
                table: "outfit_selection_items",
                columns: new[] { "variant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_generation_jobs_base_image_id_organization_id",
                table: "generation_jobs",
                columns: new[] { "base_image_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_generation_jobs_selection_id_organization_id",
                table: "generation_jobs",
                columns: new[] { "selection_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_generation_jobs_session_id_store_id_organization_id",
                table: "generation_jobs",
                columns: new[] { "session_id", "store_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_generation_jobs_source_photo_id_organization_id",
                table: "generation_jobs",
                columns: new[] { "source_photo_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_generation_jobs_store_id_organization_id",
                table: "generation_jobs",
                columns: new[] { "store_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_generated_images_file_id_organization_id",
                table: "generated_images",
                columns: new[] { "file_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_generated_images_job_id_organization_id",
                table: "generated_images",
                columns: new[] { "job_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_generated_images_parent_image_id_organization_id",
                table: "generated_images",
                columns: new[] { "parent_image_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_generated_images_session_id_organization_id",
                table: "generated_images",
                columns: new[] { "session_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_display_devices_current_session_id_store_id_organization_id",
                table: "display_devices",
                columns: new[] { "current_session_id", "store_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_display_devices_store_id_organization_id",
                table: "display_devices",
                columns: new[] { "store_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_consent_records_recorded_by_user_id_organization_id",
                table: "consent_records",
                columns: new[] { "recorded_by_user_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_consent_records_session_id_organization_id",
                table: "consent_records",
                columns: new[] { "session_id", "organization_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_store_id_organization_id",
                table: "audit_logs",
                columns: new[] { "store_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_logs_adjusts_log_id_organization_id",
                table: "ai_usage_logs",
                columns: new[] { "adjusts_log_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_logs_job_id_organization_id",
                table: "ai_usage_logs",
                columns: new[] { "job_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_logs_store_id_organization_id",
                table: "ai_usage_logs",
                columns: new[] { "store_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_cost_reconciliations_usage_log_id_organization_id",
                table: "ai_cost_reconciliations",
                columns: new[] { "usage_log_id", "organization_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_ai_cost_reconciliations_ai_usage_logs_usage_log_id_organization_id",
                table: "ai_cost_reconciliations",
                columns: new[] { "usage_log_id", "organization_id" },
                principalTable: "ai_usage_logs",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_ai_usage_logs_ai_usage_logs_adjusts_log_id_organization_id",
                table: "ai_usage_logs",
                columns: new[] { "adjusts_log_id", "organization_id" },
                principalTable: "ai_usage_logs",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_ai_usage_logs_generation_jobs_job_id_organization_id",
                table: "ai_usage_logs",
                columns: new[] { "job_id", "organization_id" },
                principalTable: "generation_jobs",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_ai_usage_logs_stores_store_id_organization_id",
                table: "ai_usage_logs",
                columns: new[] { "store_id", "organization_id" },
                principalTable: "stores",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_audit_logs_organizations_organization_id",
                table: "audit_logs",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_audit_logs_stores_store_id_organization_id",
                table: "audit_logs",
                columns: new[] { "store_id", "organization_id" },
                principalTable: "stores",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_consent_records_asp_net_users_recorded_by_user_id_organization_id",
                table: "consent_records",
                columns: new[] { "recorded_by_user_id", "organization_id" },
                principalTable: "users",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_consent_records_tryon_sessions_session_id_organization_id",
                table: "consent_records",
                columns: new[] { "session_id", "organization_id" },
                principalTable: "tryon_sessions",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_display_devices_stores_store_id_organization_id",
                table: "display_devices",
                columns: new[] { "store_id", "organization_id" },
                principalTable: "stores",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_display_devices_try_on_sessions_current_session_id_store_id_organization_id",
                table: "display_devices",
                columns: new[] { "current_session_id", "store_id", "organization_id" },
                principalTable: "tryon_sessions",
                principalColumns: new[] { "id", "store_id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_generated_images_generated_images_parent_image_id_organization_id",
                table: "generated_images",
                columns: new[] { "parent_image_id", "organization_id" },
                principalTable: "generated_images",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_generated_images_generation_jobs_job_id_organization_id",
                table: "generated_images",
                columns: new[] { "job_id", "organization_id" },
                principalTable: "generation_jobs",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_generated_images_stored_files_file_id_organization_id",
                table: "generated_images",
                columns: new[] { "file_id", "organization_id" },
                principalTable: "stored_files",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_generated_images_try_on_sessions_session_id_organization_id",
                table: "generated_images",
                columns: new[] { "session_id", "organization_id" },
                principalTable: "tryon_sessions",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_generation_jobs_generated_images_base_image_id_organization_id",
                table: "generation_jobs",
                columns: new[] { "base_image_id", "organization_id" },
                principalTable: "generated_images",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_generation_jobs_outfit_selections_selection_id_organization_id",
                table: "generation_jobs",
                columns: new[] { "selection_id", "organization_id" },
                principalTable: "outfit_selections",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_generation_jobs_session_photos_source_photo_id_organization_id",
                table: "generation_jobs",
                columns: new[] { "source_photo_id", "organization_id" },
                principalTable: "session_photos",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_generation_jobs_stores_store_id_organization_id",
                table: "generation_jobs",
                columns: new[] { "store_id", "organization_id" },
                principalTable: "stores",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_generation_jobs_try_on_sessions_session_id_store_id_organization_id",
                table: "generation_jobs",
                columns: new[] { "session_id", "store_id", "organization_id" },
                principalTable: "tryon_sessions",
                principalColumns: new[] { "id", "store_id", "organization_id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_outfit_selection_items_categories_category_id_organization_id",
                table: "outfit_selection_items",
                columns: new[] { "category_id", "organization_id" },
                principalTable: "categories",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_outfit_selection_items_outfit_selections_selection_id_organization_id",
                table: "outfit_selection_items",
                columns: new[] { "selection_id", "organization_id" },
                principalTable: "outfit_selections",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_outfit_selection_items_product_variants_variant_id_organization_id",
                table: "outfit_selection_items",
                columns: new[] { "variant_id", "organization_id" },
                principalTable: "product_variants",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_outfit_selection_items_products_product_id_organization_id",
                table: "outfit_selection_items",
                columns: new[] { "product_id", "organization_id" },
                principalTable: "products",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_outfit_selections_try_on_sessions_session_id_organization_id",
                table: "outfit_selections",
                columns: new[] { "session_id", "organization_id" },
                principalTable: "tryon_sessions",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_outfit_set_components_product_variants_component_variant_id_organization_id",
                table: "outfit_set_components",
                columns: new[] { "component_variant_id", "organization_id" },
                principalTable: "product_variants",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_outfit_set_components_products_component_product_id_organization_id",
                table: "outfit_set_components",
                columns: new[] { "component_product_id", "organization_id" },
                principalTable: "products",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_outfit_set_components_products_set_product_id_organization_id",
                table: "outfit_set_components",
                columns: new[] { "set_product_id", "organization_id" },
                principalTable: "products",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_product_images_product_variants_variant_id_organization_id",
                table: "product_images",
                columns: new[] { "variant_id", "organization_id" },
                principalTable: "product_variants",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_product_images_products_product_id_organization_id",
                table: "product_images",
                columns: new[] { "product_id", "organization_id" },
                principalTable: "products",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_product_images_stored_files_file_id_organization_id",
                table: "product_images",
                columns: new[] { "file_id", "organization_id" },
                principalTable: "stored_files",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_product_variants_products_product_id_organization_id",
                table: "product_variants",
                columns: new[] { "product_id", "organization_id" },
                principalTable: "products",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_products_categories_category_id_organization_id",
                table: "products",
                columns: new[] { "category_id", "organization_id" },
                principalTable: "categories",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_session_photos_stored_files_file_id_organization_id",
                table: "session_photos",
                columns: new[] { "file_id", "organization_id" },
                principalTable: "stored_files",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_session_photos_tryon_sessions_session_id_organization_id",
                table: "session_photos",
                columns: new[] { "session_id", "organization_id" },
                principalTable: "tryon_sessions",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_tryon_sessions_asp_net_users_created_by_user_id_organization_id",
                table: "tryon_sessions",
                columns: new[] { "created_by_user_id", "organization_id" },
                principalTable: "users",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_tryon_sessions_stores_store_id_organization_id",
                table: "tryon_sessions",
                columns: new[] { "store_id", "organization_id" },
                principalTable: "stores",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_user_store_roles_asp_net_users_user_id_organization_id",
                table: "user_store_roles",
                columns: new[] { "user_id", "organization_id" },
                principalTable: "users",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_user_store_roles_stores_store_id_organization_id",
                table: "user_store_roles",
                columns: new[] { "store_id", "organization_id" },
                principalTable: "stores",
                principalColumns: new[] { "id", "organization_id" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <summary>
        /// Çapraz firma/şube ön kontrolü: çocuk satır, ebeveyniyle (ilk sütun çifti = kimlik) birleşir ve geri kalan sütun çiftlerinden (firma, şube) herhangi biri farklıysa
        /// migration THROW ile durur. Mesaj ilişkiyi adıyla bildirir.
        /// </summary>
        private static void Precheck(MigrationBuilder migrationBuilder, string child, string[] columns, string parent, string[] parentColumns)
        {
            var mismatch = string.Join(" OR ", columns.Skip(1).Select((c, i) => $"c.[{c}] <> p.[{parentColumns[i + 1]}]"));
            var relation = $"{child}({string.Join(", ", columns)}) -> {parent}";
            migrationBuilder.Sql(
                $"IF EXISTS (SELECT 1 FROM [{child}] c JOIN [{parent}] p ON c.[{columns[0]}] = p.[{parentColumns[0]}] WHERE {mismatch}) " +
                $"THROW 51200, N'Tenant tutarlılık ihlali: {relation}. Çocuk satır ebeveyninden farklı firmada/şubede. Migration durduruldu; veriyi inceleyin.', 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_ai_cost_reconciliations_ai_usage_logs_usage_log_id_organization_id",
                table: "ai_cost_reconciliations");

            migrationBuilder.DropForeignKey(
                name: "fk_ai_usage_logs_ai_usage_logs_adjusts_log_id_organization_id",
                table: "ai_usage_logs");

            migrationBuilder.DropForeignKey(
                name: "fk_ai_usage_logs_generation_jobs_job_id_organization_id",
                table: "ai_usage_logs");

            migrationBuilder.DropForeignKey(
                name: "fk_ai_usage_logs_stores_store_id_organization_id",
                table: "ai_usage_logs");

            migrationBuilder.DropForeignKey(
                name: "fk_audit_logs_organizations_organization_id",
                table: "audit_logs");

            migrationBuilder.DropForeignKey(
                name: "fk_audit_logs_stores_store_id_organization_id",
                table: "audit_logs");

            migrationBuilder.DropForeignKey(
                name: "fk_consent_records_asp_net_users_recorded_by_user_id_organization_id",
                table: "consent_records");

            migrationBuilder.DropForeignKey(
                name: "fk_consent_records_tryon_sessions_session_id_organization_id",
                table: "consent_records");

            migrationBuilder.DropForeignKey(
                name: "fk_display_devices_stores_store_id_organization_id",
                table: "display_devices");

            migrationBuilder.DropForeignKey(
                name: "fk_display_devices_try_on_sessions_current_session_id_store_id_organization_id",
                table: "display_devices");

            migrationBuilder.DropForeignKey(
                name: "fk_generated_images_generated_images_parent_image_id_organization_id",
                table: "generated_images");

            migrationBuilder.DropForeignKey(
                name: "fk_generated_images_generation_jobs_job_id_organization_id",
                table: "generated_images");

            migrationBuilder.DropForeignKey(
                name: "fk_generated_images_stored_files_file_id_organization_id",
                table: "generated_images");

            migrationBuilder.DropForeignKey(
                name: "fk_generated_images_try_on_sessions_session_id_organization_id",
                table: "generated_images");

            migrationBuilder.DropForeignKey(
                name: "fk_generation_jobs_generated_images_base_image_id_organization_id",
                table: "generation_jobs");

            migrationBuilder.DropForeignKey(
                name: "fk_generation_jobs_outfit_selections_selection_id_organization_id",
                table: "generation_jobs");

            migrationBuilder.DropForeignKey(
                name: "fk_generation_jobs_session_photos_source_photo_id_organization_id",
                table: "generation_jobs");

            migrationBuilder.DropForeignKey(
                name: "fk_generation_jobs_stores_store_id_organization_id",
                table: "generation_jobs");

            migrationBuilder.DropForeignKey(
                name: "fk_generation_jobs_try_on_sessions_session_id_store_id_organization_id",
                table: "generation_jobs");

            migrationBuilder.DropForeignKey(
                name: "fk_outfit_selection_items_categories_category_id_organization_id",
                table: "outfit_selection_items");

            migrationBuilder.DropForeignKey(
                name: "fk_outfit_selection_items_outfit_selections_selection_id_organization_id",
                table: "outfit_selection_items");

            migrationBuilder.DropForeignKey(
                name: "fk_outfit_selection_items_product_variants_variant_id_organization_id",
                table: "outfit_selection_items");

            migrationBuilder.DropForeignKey(
                name: "fk_outfit_selection_items_products_product_id_organization_id",
                table: "outfit_selection_items");

            migrationBuilder.DropForeignKey(
                name: "fk_outfit_selections_try_on_sessions_session_id_organization_id",
                table: "outfit_selections");

            migrationBuilder.DropForeignKey(
                name: "fk_outfit_set_components_product_variants_component_variant_id_organization_id",
                table: "outfit_set_components");

            migrationBuilder.DropForeignKey(
                name: "fk_outfit_set_components_products_component_product_id_organization_id",
                table: "outfit_set_components");

            migrationBuilder.DropForeignKey(
                name: "fk_outfit_set_components_products_set_product_id_organization_id",
                table: "outfit_set_components");

            migrationBuilder.DropForeignKey(
                name: "fk_product_images_product_variants_variant_id_organization_id",
                table: "product_images");

            migrationBuilder.DropForeignKey(
                name: "fk_product_images_products_product_id_organization_id",
                table: "product_images");

            migrationBuilder.DropForeignKey(
                name: "fk_product_images_stored_files_file_id_organization_id",
                table: "product_images");

            migrationBuilder.DropForeignKey(
                name: "fk_product_variants_products_product_id_organization_id",
                table: "product_variants");

            migrationBuilder.DropForeignKey(
                name: "fk_products_categories_category_id_organization_id",
                table: "products");

            migrationBuilder.DropForeignKey(
                name: "fk_session_photos_stored_files_file_id_organization_id",
                table: "session_photos");

            migrationBuilder.DropForeignKey(
                name: "fk_session_photos_tryon_sessions_session_id_organization_id",
                table: "session_photos");

            migrationBuilder.DropForeignKey(
                name: "fk_tryon_sessions_asp_net_users_created_by_user_id_organization_id",
                table: "tryon_sessions");

            migrationBuilder.DropForeignKey(
                name: "fk_tryon_sessions_stores_store_id_organization_id",
                table: "tryon_sessions");

            migrationBuilder.DropForeignKey(
                name: "fk_user_store_roles_asp_net_users_user_id_organization_id",
                table: "user_store_roles");

            migrationBuilder.DropForeignKey(
                name: "fk_user_store_roles_stores_store_id_organization_id",
                table: "user_store_roles");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_users_id_organization_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_user_store_roles_store_id_organization_id",
                table: "user_store_roles");

            migrationBuilder.DropIndex(
                name: "ix_user_store_roles_user_id_organization_id",
                table: "user_store_roles");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_tryon_sessions_id_organization_id",
                table: "tryon_sessions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_tryon_sessions_id_store_id_organization_id",
                table: "tryon_sessions");

            migrationBuilder.DropIndex(
                name: "ix_tryon_sessions_created_by_user_id_organization_id",
                table: "tryon_sessions");

            migrationBuilder.DropIndex(
                name: "ix_tryon_sessions_store_id_organization_id",
                table: "tryon_sessions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_stores_id_organization_id",
                table: "stores");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_stored_files_id_organization_id",
                table: "stored_files");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_session_photos_id_organization_id",
                table: "session_photos");

            migrationBuilder.DropIndex(
                name: "ix_session_photos_file_id_organization_id",
                table: "session_photos");

            migrationBuilder.DropIndex(
                name: "ix_session_photos_session_id_organization_id",
                table: "session_photos");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_products_id_organization_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "ix_products_category_id_organization_id",
                table: "products");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_product_variants_id_organization_id",
                table: "product_variants");

            migrationBuilder.DropIndex(
                name: "ix_product_variants_product_id_organization_id",
                table: "product_variants");

            migrationBuilder.DropIndex(
                name: "ix_product_images_file_id_organization_id",
                table: "product_images");

            migrationBuilder.DropIndex(
                name: "ix_product_images_product_id_organization_id",
                table: "product_images");

            migrationBuilder.DropIndex(
                name: "ix_product_images_variant_id_organization_id",
                table: "product_images");

            migrationBuilder.DropIndex(
                name: "ix_outfit_set_components_component_product_id_organization_id",
                table: "outfit_set_components");

            migrationBuilder.DropIndex(
                name: "ix_outfit_set_components_component_variant_id_organization_id",
                table: "outfit_set_components");

            migrationBuilder.DropIndex(
                name: "ix_outfit_set_components_set_product_id_organization_id",
                table: "outfit_set_components");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_outfit_selections_id_organization_id",
                table: "outfit_selections");

            migrationBuilder.DropIndex(
                name: "ix_outfit_selections_session_id_organization_id",
                table: "outfit_selections");

            migrationBuilder.DropIndex(
                name: "ix_outfit_selection_items_category_id_organization_id",
                table: "outfit_selection_items");

            migrationBuilder.DropIndex(
                name: "ix_outfit_selection_items_product_id_organization_id",
                table: "outfit_selection_items");

            migrationBuilder.DropIndex(
                name: "ix_outfit_selection_items_selection_id_organization_id",
                table: "outfit_selection_items");

            migrationBuilder.DropIndex(
                name: "ix_outfit_selection_items_variant_id_organization_id",
                table: "outfit_selection_items");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_generation_jobs_id_organization_id",
                table: "generation_jobs");

            migrationBuilder.DropIndex(
                name: "ix_generation_jobs_base_image_id_organization_id",
                table: "generation_jobs");

            migrationBuilder.DropIndex(
                name: "ix_generation_jobs_selection_id_organization_id",
                table: "generation_jobs");

            migrationBuilder.DropIndex(
                name: "ix_generation_jobs_session_id_store_id_organization_id",
                table: "generation_jobs");

            migrationBuilder.DropIndex(
                name: "ix_generation_jobs_source_photo_id_organization_id",
                table: "generation_jobs");

            migrationBuilder.DropIndex(
                name: "ix_generation_jobs_store_id_organization_id",
                table: "generation_jobs");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_generated_images_id_organization_id",
                table: "generated_images");

            migrationBuilder.DropIndex(
                name: "ix_generated_images_file_id_organization_id",
                table: "generated_images");

            migrationBuilder.DropIndex(
                name: "ix_generated_images_job_id_organization_id",
                table: "generated_images");

            migrationBuilder.DropIndex(
                name: "ix_generated_images_parent_image_id_organization_id",
                table: "generated_images");

            migrationBuilder.DropIndex(
                name: "ix_generated_images_session_id_organization_id",
                table: "generated_images");

            migrationBuilder.DropIndex(
                name: "ix_display_devices_current_session_id_store_id_organization_id",
                table: "display_devices");

            migrationBuilder.DropIndex(
                name: "ix_display_devices_store_id_organization_id",
                table: "display_devices");

            migrationBuilder.DropIndex(
                name: "ix_consent_records_recorded_by_user_id_organization_id",
                table: "consent_records");

            migrationBuilder.DropIndex(
                name: "ix_consent_records_session_id_organization_id",
                table: "consent_records");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_categories_id_organization_id",
                table: "categories");

            migrationBuilder.DropIndex(
                name: "ix_audit_logs_store_id_organization_id",
                table: "audit_logs");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_ai_usage_logs_id_organization_id",
                table: "ai_usage_logs");

            migrationBuilder.DropIndex(
                name: "ix_ai_usage_logs_adjusts_log_id_organization_id",
                table: "ai_usage_logs");

            migrationBuilder.DropIndex(
                name: "ix_ai_usage_logs_job_id_organization_id",
                table: "ai_usage_logs");

            migrationBuilder.DropIndex(
                name: "ix_ai_usage_logs_store_id_organization_id",
                table: "ai_usage_logs");

            migrationBuilder.DropIndex(
                name: "ix_ai_cost_reconciliations_usage_log_id_organization_id",
                table: "ai_cost_reconciliations");

            migrationBuilder.CreateIndex(
                name: "ix_tryon_sessions_created_by_user_id",
                table: "tryon_sessions",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_photos_file_id",
                table: "session_photos",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_photos_session_id",
                table: "session_photos",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_products_category_id",
                table: "products",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_images_file_id",
                table: "product_images",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_images_variant_id",
                table: "product_images",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outfit_set_components_component_product_id",
                table: "outfit_set_components",
                column: "component_product_id");

            migrationBuilder.CreateIndex(
                name: "ix_outfit_set_components_component_variant_id",
                table: "outfit_set_components",
                column: "component_variant_id");

            migrationBuilder.CreateIndex(
                name: "ix_outfit_selections_session_id",
                table: "outfit_selections",
                column: "session_id");

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
                name: "ix_generation_jobs_base_image_id",
                table: "generation_jobs",
                column: "base_image_id");

            migrationBuilder.CreateIndex(
                name: "ix_generation_jobs_selection_id",
                table: "generation_jobs",
                column: "selection_id");

            migrationBuilder.CreateIndex(
                name: "ix_generation_jobs_source_photo_id",
                table: "generation_jobs",
                column: "source_photo_id");

            migrationBuilder.CreateIndex(
                name: "ix_generation_jobs_store_id",
                table: "generation_jobs",
                column: "store_id");

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
                name: "ix_consent_records_recorded_by_user_id",
                table: "consent_records",
                column: "recorded_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_consent_records_session_id",
                table: "consent_records",
                column: "session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_logs_adjusts_log_id",
                table: "ai_usage_logs",
                column: "adjusts_log_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_usage_logs_job_id",
                table: "ai_usage_logs",
                column: "job_id");

            migrationBuilder.AddForeignKey(
                name: "fk_ai_cost_reconciliations_ai_usage_logs_usage_log_id",
                table: "ai_cost_reconciliations",
                column: "usage_log_id",
                principalTable: "ai_usage_logs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_ai_usage_logs_ai_usage_logs_adjusts_log_id",
                table: "ai_usage_logs",
                column: "adjusts_log_id",
                principalTable: "ai_usage_logs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_ai_usage_logs_stores_store_id",
                table: "ai_usage_logs",
                column: "store_id",
                principalTable: "stores",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_consent_records_asp_net_users_recorded_by_user_id",
                table: "consent_records",
                column: "recorded_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_consent_records_tryon_sessions_session_id",
                table: "consent_records",
                column: "session_id",
                principalTable: "tryon_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_display_devices_stores_store_id",
                table: "display_devices",
                column: "store_id",
                principalTable: "stores",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_generated_images_generated_images_parent_image_id",
                table: "generated_images",
                column: "parent_image_id",
                principalTable: "generated_images",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_generated_images_generation_jobs_job_id",
                table: "generated_images",
                column: "job_id",
                principalTable: "generation_jobs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_generated_images_stored_files_file_id",
                table: "generated_images",
                column: "file_id",
                principalTable: "stored_files",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_generated_images_try_on_sessions_session_id",
                table: "generated_images",
                column: "session_id",
                principalTable: "tryon_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_generation_jobs_generated_images_base_image_id",
                table: "generation_jobs",
                column: "base_image_id",
                principalTable: "generated_images",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_generation_jobs_outfit_selections_selection_id",
                table: "generation_jobs",
                column: "selection_id",
                principalTable: "outfit_selections",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_generation_jobs_session_photos_source_photo_id",
                table: "generation_jobs",
                column: "source_photo_id",
                principalTable: "session_photos",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_generation_jobs_stores_store_id",
                table: "generation_jobs",
                column: "store_id",
                principalTable: "stores",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_generation_jobs_try_on_sessions_session_id",
                table: "generation_jobs",
                column: "session_id",
                principalTable: "tryon_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_outfit_selection_items_categories_category_id",
                table: "outfit_selection_items",
                column: "category_id",
                principalTable: "categories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_outfit_selection_items_outfit_selections_selection_id",
                table: "outfit_selection_items",
                column: "selection_id",
                principalTable: "outfit_selections",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_outfit_selection_items_product_variants_variant_id",
                table: "outfit_selection_items",
                column: "variant_id",
                principalTable: "product_variants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_outfit_selection_items_products_product_id",
                table: "outfit_selection_items",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_outfit_selections_try_on_sessions_session_id",
                table: "outfit_selections",
                column: "session_id",
                principalTable: "tryon_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_outfit_set_components_product_variants_component_variant_id",
                table: "outfit_set_components",
                column: "component_variant_id",
                principalTable: "product_variants",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_outfit_set_components_products_component_product_id",
                table: "outfit_set_components",
                column: "component_product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_outfit_set_components_products_set_product_id",
                table: "outfit_set_components",
                column: "set_product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_product_images_product_variants_variant_id",
                table: "product_images",
                column: "variant_id",
                principalTable: "product_variants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_product_images_products_product_id",
                table: "product_images",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_product_images_stored_files_file_id",
                table: "product_images",
                column: "file_id",
                principalTable: "stored_files",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_product_variants_products_product_id",
                table: "product_variants",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_products_categories_category_id",
                table: "products",
                column: "category_id",
                principalTable: "categories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_session_photos_stored_files_file_id",
                table: "session_photos",
                column: "file_id",
                principalTable: "stored_files",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_session_photos_tryon_sessions_session_id",
                table: "session_photos",
                column: "session_id",
                principalTable: "tryon_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_tryon_sessions_asp_net_users_created_by_user_id",
                table: "tryon_sessions",
                column: "created_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_tryon_sessions_stores_store_id",
                table: "tryon_sessions",
                column: "store_id",
                principalTable: "stores",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_user_store_roles_asp_net_users_user_id",
                table: "user_store_roles",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_user_store_roles_stores_store_id",
                table: "user_store_roles",
                column: "store_id",
                principalTable: "stores",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
