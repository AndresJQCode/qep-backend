using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVariantToSaleLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Spec 2026-10-20 §4 y §8: cada línea de venta gana la variante que vende. Se agrega nula, se
            // rellena y recién después pasa a NOT NULL: un AddColumn NOT NULL con default dejaría las
            // líneas viejas apuntando a Guid.Empty, que no es ninguna variante.
            migrationBuilder.AddColumn<Guid>(
                name: "variant_id",
                schema: "pos",
                table: "sale_lines",
                type: "uuid",
                nullable: true);

            // La variante por defecto de cada producto migrado nació con el id del producto (Catalog,
            // AddProductVariants), así que el relleno no lee el esquema de Catalog: cada módulo migra
            // sólo sus tablas. La foto (product_code, product_name) y los importes no se tocan: una
            // línea guardada nunca se recalcula. La huella de idempotencia de las ventas viejas, que
            // se calculó con productId, sigue reconociendo un reintento: ahora se calcula con
            // variantId y en una venta migrada los dos son el mismo id.
            migrationBuilder.Sql("""
                UPDATE pos.sale_lines SET variant_id = product_id WHERE variant_id IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "variant_id",
                schema: "pos",
                table: "sale_lines",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            // La consulta de uso de Catalog (Bootstrapper.CatalogVariantUsageChecker) filtra
            // pos.sale_lines por variant_id con JOIN a pos.sales por sale_id, en cada GET y PUT de
            // producto: sin índice recorre todas las líneas.
            migrationBuilder.CreateIndex(
                name: "IX_sale_lines_variant",
                schema: "pos",
                table: "sale_lines",
                column: "variant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sale_lines_variant",
                schema: "pos",
                table: "sale_lines");

            migrationBuilder.DropColumn(
                name: "variant_id",
                schema: "pos",
                table: "sale_lines");
        }
    }
}
