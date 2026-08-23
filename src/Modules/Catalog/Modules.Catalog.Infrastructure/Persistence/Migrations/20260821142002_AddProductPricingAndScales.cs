using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductPricingAndScales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "discount",
                schema: "catalog",
                table: "products",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "price_base_cop",
                schema: "catalog",
                table: "products",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "price_base_usd",
                schema: "catalog",
                table: "products",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "price_final_cop",
                schema: "catalog",
                table: "products",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "price_final_usd",
                schema: "catalog",
                table: "products",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "product_price_scales",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_unit = table.Column<int>(type: "integer", nullable: false),
                    to_unit = table.Column<int>(type: "integer", nullable: false),
                    discount = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    restriction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    multiple = table.Column<int>(type: "integer", nullable: true),
                    packaging_unit = table.Column<int>(type: "integer", nullable: true),
                    final_usd = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    final_cop = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_price_scales", x => x.id);
                    table.ForeignKey(
                        name: "FK_product_price_scales_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_price_scales_product",
                schema: "catalog",
                table: "product_price_scales",
                column: "product_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_price_scales",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "discount",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "price_base_cop",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "price_base_usd",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "price_final_cop",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "price_final_usd",
                schema: "catalog",
                table: "products");
        }
    }
}
