using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Buyers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBuyerAccountConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // xmin es una columna de sistema de PostgreSQL: el generador de Npgsql no emite SQL para ella (verificado con
            // `dotnet ef migrations script`). La operación sólo deja el token de concurrencia en el modelo de las migraciones.
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "buyers",
                table: "accounts",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "buyers",
                table: "accounts");
        }
    }
}
