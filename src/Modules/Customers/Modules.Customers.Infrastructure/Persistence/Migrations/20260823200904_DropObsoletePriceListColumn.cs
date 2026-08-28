using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropObsoletePriceListColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DROP COLUMN IF EXISTS en vez de DropColumn: en produccion la columna ya habia
            // sido eliminada por ReplaceCustomerPriceListWithAssignments, migracion del
            // modulo pricing que se borro del historial en 78f30a0 (nunca se mergeo a main)
            // sin revertirla primero. La entrada de esa migracion sigue en
            // __EFMigrationsHistory, asi que EF no la reaplica, pero el efecto en la columna
            // ya estaba hecho.
            migrationBuilder.Sql(
                "ALTER TABLE customers.customers DROP COLUMN IF EXISTS price_list_id;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "price_list_id",
                schema: "customers",
                table: "customers",
                type: "uuid",
                nullable: true);
        }
    }
}
