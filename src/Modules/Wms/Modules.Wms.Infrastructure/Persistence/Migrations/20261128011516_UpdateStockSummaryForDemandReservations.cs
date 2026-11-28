using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UpdateStockSummaryForDemandReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Inventario básico §5.4: v_stock_summary suma las reservas de demanda abiertas. Aditiva: mismas siete columnas, mismo
            // orden y mismos tipos, así que los pods viejos la leen igual (sólo ven más en reserved y available).
            migrationBuilder.Sql(InventoryLedgerSql.CreateStockSummaryViewWithDemandReservations);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Vuelve a la definición que sólo mira saldos: las mismas siete columnas, así que CREATE OR REPLACE la admite.
            migrationBuilder.Sql(InventoryLedgerSql.CreateStockSummaryView);
        }
    }
}
