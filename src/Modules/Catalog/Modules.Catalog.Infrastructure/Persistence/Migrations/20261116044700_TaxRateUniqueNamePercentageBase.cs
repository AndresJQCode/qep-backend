using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaxRateUniqueNamePercentageBase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tax_rates_tenant_name",
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

        // Aditiva para el rollout: el índice nuevo admite todo lo que admitía el viejo. El Down no: falla si ya hay dos
        // tasas del mismo tenant con el mismo nombre; hay que renombrar una antes de bajar.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tax_rates_tenant_name_percentage_base",
                schema: "catalog",
                table: "tax_rates");

            migrationBuilder.CreateIndex(
                name: "IX_tax_rates_tenant_name",
                schema: "catalog",
                table: "tax_rates",
                columns: new[] { "tenant_id", "name" },
                unique: true);
        }
    }
}
