using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RelaxCustomerCompleteFields : Migration
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
                sql: "completeness = 'Incomplete' OR (cuc IS NOT NULL AND country IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_customers_identification_pair",
                schema: "customers",
                table: "customers",
                sql: "(identification_type IS NULL) = (identification_number IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_customers_complete_fields",
                schema: "customers",
                table: "customers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_customers_identification_pair",
                schema: "customers",
                table: "customers");

            // Sin backfill a propósito: inventar un documento no es una opción. Si ya hay fichas completas sin
            // identificación, este AddCheckConstraint falla y hay que resolverlas a mano antes de revertir.
            migrationBuilder.AddCheckConstraint(
                name: "CK_customers_complete_fields",
                schema: "customers",
                table: "customers",
                sql: "completeness = 'Incomplete' OR (cuc IS NOT NULL AND identification_type IS NOT NULL AND identification_number IS NOT NULL AND country IS NOT NULL)");
        }
    }
}
