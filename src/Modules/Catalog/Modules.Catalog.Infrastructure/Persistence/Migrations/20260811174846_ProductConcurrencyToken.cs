using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue 1 y no el 0 que genera EF: Product.Create nace en 1, y con 0 las filas
            // que ya existían quedarían fuera de ese invariante para siempre. El valor concreto
            // no cambia el comportamiento —al token sólo le importa cambiar— pero una columna
            // donde "1" significa "nunca se editó" se lee sola.
            migrationBuilder.AddColumn<long>(
                name: "version",
                schema: "catalog",
                table: "products",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "version",
                schema: "catalog",
                table: "products");
        }
    }
}
