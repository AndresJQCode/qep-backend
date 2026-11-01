using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowCustomerWithoutAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_customers_complete_fields",
                schema: "customers",
                table: "customers");

            migrationBuilder.AddCheckConstraint(
                name: "CK_customers_complete_fields",
                schema: "customers",
                table: "customers",
                sql: "completeness = 'Incomplete' OR (cuc IS NOT NULL AND identification_type IS NOT NULL AND identification_number IS NOT NULL AND country IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_customers_complete_fields",
                schema: "customers",
                table: "customers");

            // Sin esto, la primera ficha completa sin dirección hace fallar el AddCheckConstraint de abajo y
            // deja la reversión a medias. La cadena vacía es la que el modelo anterior ya guardaba para "sin dato".
            migrationBuilder.Sql(
                "UPDATE customers.customers SET address = '' WHERE completeness = 'Complete' AND address IS NULL;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_customers_complete_fields",
                schema: "customers",
                table: "customers",
                sql: "completeness = 'Incomplete' OR (cuc IS NOT NULL AND identification_type IS NOT NULL AND identification_number IS NOT NULL AND address IS NOT NULL AND country IS NOT NULL)");
        }
    }
}
