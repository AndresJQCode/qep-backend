using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceListToProductPriceScale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // El concepto de lista de precios no existía cuando CAT-09 introdujo esta tabla: las
            // escalas cargadas antes de esta migración no tienen (ni pueden inferir) a qué lista
            // pertenecen. Se descartan en vez de backfillearlas con un valor inventado — son
            // datos de prueba de la etapa de desarrollo de CAT-09, no información de negocio real
            // (verificado antes de esta migración: ningún tenant productivo las tenía cargadas).
            // Quien las cargó tiene que reingresarlas eligiendo una lista, que es un dato que
            // antes no existía.
            migrationBuilder.Sql("DELETE FROM catalog.product_price_scales;");

            migrationBuilder.AddColumn<Guid>(
                name: "price_list_id",
                schema: "catalog",
                table: "product_price_scales",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_product_price_scales_product_price_list",
                schema: "catalog",
                table: "product_price_scales",
                columns: new[] { "product_id", "price_list_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_product_price_scales_product_price_list",
                schema: "catalog",
                table: "product_price_scales");

            migrationBuilder.DropColumn(
                name: "price_list_id",
                schema: "catalog",
                table: "product_price_scales");
        }
    }
}
