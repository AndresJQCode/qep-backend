using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Shopify.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Spec 2026-11-17 §8: <c>product_links</c>, <c>product_imports</c>, <c>product_import_issues</c> y
    /// <c>stores.products_synced_at</c>. Aditiva: tres tablas nuevas y una columna nullable; el código viejo no las lee ni las
    /// escribe, así que corre igual contra el esquema nuevo durante el rollout.
    /// </summary>
    public partial class AddProductSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "products_synced_at",
                schema: "shopify",
                table: "stores",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "product_imports",
                schema: "shopify",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    total = table.Column<int>(type: "integer", nullable: false),
                    done = table.Column<int>(type: "integer", nullable: false),
                    created = table.Column<int>(type: "integer", nullable: false),
                    matched = table.Column<int>(type: "integer", nullable: false),
                    without_sku = table.Column<int>(type: "integer", nullable: false),
                    skipped = table.Column<int>(type: "integer", nullable: false),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cursor = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_imports", x => x.id);
                    table.CheckConstraint("CK_product_imports_mode", "mode IN ('All','Missing')");
                    table.CheckConstraint("CK_product_imports_status", "status IN ('Pending','Running','Done','Failed')");
                });

            migrationBuilder.CreateTable(
                name: "product_links",
                schema: "shopify",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shopify_product_id = table.Column<long>(type: "bigint", nullable: false),
                    shopify_variant_id = table.Column<long>(type: "bigint", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    variant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    shopify_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    shopify_image_id = table.Column<long>(type: "bigint", nullable: true),
                    image_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_links", x => x.id);
                    table.CheckConstraint("CK_product_links_origin", "origin IN ('Imported','MatchedBySku')");
                });

            migrationBuilder.CreateTable(
                name: "product_import_issues",
                schema: "shopify",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shopify_product_id = table.Column<long>(type: "bigint", nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_import_issues", x => x.id);
                    table.ForeignKey(
                        name: "FK_product_import_issues_product_imports_import_id",
                        column: x => x.import_id,
                        principalSchema: "shopify",
                        principalTable: "product_imports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_import_issues_import",
                schema: "shopify",
                table: "product_import_issues",
                columns: new[] { "import_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_product_imports_connection_created",
                schema: "shopify",
                table: "product_imports",
                columns: new[] { "connection_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_product_imports_connection_live",
                schema: "shopify",
                table: "product_imports",
                column: "connection_id",
                unique: true,
                filter: "status IN ('Pending','Running')");

            migrationBuilder.CreateIndex(
                name: "IX_product_imports_status_lease",
                schema: "shopify",
                table: "product_imports",
                columns: new[] { "status", "lease_until" });

            migrationBuilder.CreateIndex(
                name: "IX_product_links_connection_product",
                schema: "shopify",
                table: "product_links",
                columns: new[] { "connection_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "IX_product_links_connection_shopify_product",
                schema: "shopify",
                table: "product_links",
                columns: new[] { "connection_id", "shopify_product_id" });

            migrationBuilder.CreateIndex(
                name: "IX_product_links_connection_shopify_variant",
                schema: "shopify",
                table: "product_links",
                columns: new[] { "connection_id", "shopify_variant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_links_connection_variant",
                schema: "shopify",
                table: "product_links",
                columns: new[] { "connection_id", "variant_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_import_issues",
                schema: "shopify");

            migrationBuilder.DropTable(
                name: "product_links",
                schema: "shopify");

            migrationBuilder.DropTable(
                name: "product_imports",
                schema: "shopify");

            migrationBuilder.DropColumn(
                name: "products_synced_at",
                schema: "shopify",
                table: "stores");
        }
    }
}
