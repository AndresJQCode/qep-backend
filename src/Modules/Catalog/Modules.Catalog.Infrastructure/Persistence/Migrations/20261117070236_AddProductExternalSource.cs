using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Productos desde Shopify (spec 2026-11-17 §5.4, §8). <b>Aditiva:</b> una columna nullable que
    /// el código viejo no lee ni escribe; durante el rollout los pods viejos siguen funcionando, y
    /// todas las filas de antes quedan en null (productos propios).
    /// </summary>
    public partial class AddProductExternalSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_source",
                schema: "catalog",
                table: "products",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "external_source",
                schema: "catalog",
                table: "products");
        }
    }
}
