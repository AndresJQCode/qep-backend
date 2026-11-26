using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddForeignCustomerAddresses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "city_id",
                schema: "customers",
                table: "customer_addresses",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "city_name",
                schema: "customers",
                table: "customer_addresses",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "country",
                schema: "customers",
                table: "customer_addresses",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "CO");

            migrationBuilder.AddCheckConstraint(
                name: "CK_customer_addresses_city_lane",
                schema: "customers",
                table: "customer_addresses",
                sql: "(country = 'CO' AND city_id IS NOT NULL AND city_name IS NULL) OR (country <> 'CO' AND city_id IS NULL AND city_name IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // city_id vuelve a NOT NULL: una direccion de afuera no tiene ciudad DIVIPOLA que
            // poner, y inventar una la dejaria apuntando a un lugar que no es. Falla fuerte y dice
            // por que, en vez de dejar que el ALTER reviente con un mensaje de columna nula.
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM customers.customer_addresses WHERE country <> 'CO') THEN
                        RAISE EXCEPTION 'Cannot revert AddForeignCustomerAddresses: customers.customer_addresses has addresses outside Colombia.';
                    END IF;
                END $$;");

            migrationBuilder.DropCheckConstraint(
                name: "CK_customer_addresses_city_lane",
                schema: "customers",
                table: "customer_addresses");

            migrationBuilder.DropColumn(
                name: "city_name",
                schema: "customers",
                table: "customer_addresses");

            migrationBuilder.DropColumn(
                name: "country",
                schema: "customers",
                table: "customer_addresses");

            migrationBuilder.AlterColumn<Guid>(
                name: "city_id",
                schema: "customers",
                table: "customer_addresses",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
