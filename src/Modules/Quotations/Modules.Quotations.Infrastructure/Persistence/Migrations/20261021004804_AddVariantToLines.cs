using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVariantToLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Spec 2026-10-20 §4 y §8: cada línea gana la variante que vende. Se agrega nula, se rellena y
            // recién después pasa a NOT NULL: un AddColumn NOT NULL con default dejaría las líneas viejas
            // apuntando a Guid.Empty, que no es ninguna variante.
            migrationBuilder.AddColumn<Guid>(
                name: "variant_id",
                schema: "quotations",
                table: "quotation_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "variant_id",
                schema: "quotations",
                table: "order_items",
                type: "uuid",
                nullable: true);

            // La variante por defecto de cada producto migrado nació con el id del producto
            // (Catalog, AddProductVariants), así que el relleno no necesita leer el esquema de Catalog:
            // cada módulo migra sólo sus tablas. Las fotos (product_code, product_name) no se tocan
            // (spec §8): una línea guardada nunca se recalcula. Los importes tampoco: ningún total cambia.
            migrationBuilder.Sql("""
                UPDATE quotations.quotation_items SET variant_id = product_id WHERE variant_id IS NULL;
                UPDATE quotations.order_items SET variant_id = product_id WHERE variant_id IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "variant_id",
                schema: "quotations",
                table: "quotation_items",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "variant_id",
                schema: "quotations",
                table: "order_items",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            // La consulta de uso de Catalog (CatalogVariantUsageChecker) filtra por variant_id en cada GET
            // y PUT de producto: sin índice recorre todas las líneas.
            migrationBuilder.CreateIndex(
                name: "IX_quotation_items_variant",
                schema: "quotations",
                table: "quotation_items",
                column: "variant_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_items_variant",
                schema: "quotations",
                table: "order_items",
                column: "variant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_quotation_items_variant",
                schema: "quotations",
                table: "quotation_items");

            migrationBuilder.DropIndex(
                name: "IX_order_items_variant",
                schema: "quotations",
                table: "order_items");

            migrationBuilder.DropColumn(
                name: "variant_id",
                schema: "quotations",
                table: "quotation_items");

            migrationBuilder.DropColumn(
                name: "variant_id",
                schema: "quotations",
                table: "order_items");
        }
    }
}
