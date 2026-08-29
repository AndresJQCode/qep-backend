using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomersNameTrigramIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // El indice GIN de abajo depende de los operadores que trae esta extension
            // (gin_trgm_ops). `IF NOT EXISTS`: ningun otro modulo la habilito todavia, pero si
            // alguno la agrega despues no hay conflicto.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            migrationBuilder.CreateIndex(
                name: "IX_customers_name_trgm",
                schema: "customers",
                table: "customers",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_customers_name_trgm",
                schema: "customers",
                table: "customers");
        }
    }
}
