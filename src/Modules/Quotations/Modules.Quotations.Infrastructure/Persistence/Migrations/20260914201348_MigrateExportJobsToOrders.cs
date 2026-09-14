using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MigrateExportJobsToOrders : Migration
    {
        // Datos, no esquema (spec 2026-09-14, D4). Un job pendiente con kind 'Sales' no lo puede leer
        // EF desde que ExportJobKind dice Orders, y OrdersExportFilters ignoraría la clave SaleNumber.
        // Va en el mismo commit que el enum; la de RenameToOrders no podía llevarlo (plan, hallazgo 5).

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE quotations.export_jobs SET kind = 'Orders' WHERE kind = 'Sales';");
            migrationBuilder.Sql("""
                UPDATE quotations.export_jobs
                   SET filters = (filters - 'SaleNumber') || jsonb_build_object('OrderNumber', filters -> 'SaleNumber')
                 WHERE filters ? 'SaleNumber';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE quotations.export_jobs
                   SET filters = (filters - 'OrderNumber') || jsonb_build_object('SaleNumber', filters -> 'OrderNumber')
                 WHERE filters ? 'OrderNumber';
                """);
            migrationBuilder.Sql("UPDATE quotations.export_jobs SET kind = 'Sales' WHERE kind = 'Orders';");
        }
    }
}
