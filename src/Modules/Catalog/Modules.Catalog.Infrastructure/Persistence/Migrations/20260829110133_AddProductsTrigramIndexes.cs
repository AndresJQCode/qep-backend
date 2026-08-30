using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductsTrigramIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Los indices GIN de abajo dependen de los operadores que trae esta extension
            // (gin_trgm_ops). `IF NOT EXISTS`: Customers/Companies pueden haberla habilitado
            // ya, pero este modulo no puede asumir el orden de aplicacion de las migraciones
            // de otro.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.CreateIndex(
                name: "IX_products_code_trgm",
                schema: "catalog",
                table: "products",
                column: "code")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_products_name_trgm",
                schema: "catalog",
                table: "products",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_products_code_trgm",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_products_name_trgm",
                schema: "catalog",
                table: "products");
        }
    }
}
