using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuotationGlobalScaleFloor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "global_scale_floor",
                schema: "quotations",
                table: "quotations",
                type: "integer",
                nullable: true);

            // "Own" y no la cadena vacía que genera EF: las líneas que ya existen se valorizaron
            // antes de que el piso global existiera, y la agrupación nunca marcó nada, así que su
            // descuento salió de su propia cantidad. Una cadena vacía no es un valor del enum y
            // reventaría al materializar la primera cotización vieja que alguien abra.
            migrationBuilder.AddColumn<string>(
                name: "discount_origin",
                schema: "quotations",
                table: "quotation_items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Own");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "global_scale_floor",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "discount_origin",
                schema: "quotations",
                table: "quotation_items");
        }
    }
}
