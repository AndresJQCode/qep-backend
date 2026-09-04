using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductPriceChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "product_price_changes",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scale_from_unit = table.Column<int>(type: "integer", nullable: true),
                    scale_to_unit = table.Column<int>(type: "integer", nullable: true),
                    previous_value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    new_value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_price_changes", x => x.id);
                    table.ForeignKey(
                        name: "FK_product_price_changes_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_price_changes_product_id",
                schema: "catalog",
                table: "product_price_changes",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_price_changes_tenant",
                schema: "catalog",
                table: "product_price_changes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_price_changes_tenant_changed_at",
                schema: "catalog",
                table: "product_price_changes",
                columns: new[] { "tenant_id", "changed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_price_changes",
                schema: "catalog");
        }
    }
}
