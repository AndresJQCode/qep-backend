using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAppliesToTaxRateUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tax_rates_tenant_name_percentage_base",
                schema: "catalog",
                table: "tax_rates");

            migrationBuilder.CreateIndex(
                name: "IX_tax_rates_tenant_name_percentage_base",
                schema: "catalog",
                table: "tax_rates",
                columns: new[] { "tenant_id", "name", "percentage", "minimum_base", "applies_to" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tax_rates_tenant_name_percentage_base",
                schema: "catalog",
                table: "tax_rates");

            migrationBuilder.CreateIndex(
                name: "IX_tax_rates_tenant_name_percentage_base",
                schema: "catalog",
                table: "tax_rates",
                columns: new[] { "tenant_id", "name", "percentage", "minimum_base" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
