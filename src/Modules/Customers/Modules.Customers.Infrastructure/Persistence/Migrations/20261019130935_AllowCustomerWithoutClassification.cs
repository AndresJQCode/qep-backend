using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Customers.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowCustomerWithoutClassification : Migration
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
                sql: "completeness = 'Incomplete' OR (cuc IS NOT NULL AND identification_type IS NOT NULL AND identification_number IS NOT NULL AND address IS NOT NULL AND country IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_customers_complete_fields",
                schema: "customers",
                table: "customers");

            migrationBuilder.AddCheckConstraint(
                name: "CK_customers_complete_fields",
                schema: "customers",
                table: "customers",
                sql: "completeness = 'Incomplete' OR (cuc IS NOT NULL AND identification_type IS NOT NULL AND identification_number IS NOT NULL AND address IS NOT NULL AND country IS NOT NULL AND classification_id IS NOT NULL)");
        }
    }
}
