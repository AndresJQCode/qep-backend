using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Companies.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Spec 2026-11-17 §5.1: <c>companies.charges_vat</c>, <c>NOT NULL DEFAULT true</c>; toda empresa existente sigue
    /// cobrando IVA. Aditiva, pero el despliegue del spec 2026-11-17 va con corte (R5): <c>Recreate</c> o el backend a
    /// cero réplicas, porque un pod viejo rechazaría <c>bankAccounts: []</c> y recalcularía con IVA lo que no lo lleva.
    /// </summary>
    public partial class AddCompanyChargesVat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "charges_vat",
                schema: "companies",
                table: "companies",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "charges_vat",
                schema: "companies",
                table: "companies");
        }
    }
}
