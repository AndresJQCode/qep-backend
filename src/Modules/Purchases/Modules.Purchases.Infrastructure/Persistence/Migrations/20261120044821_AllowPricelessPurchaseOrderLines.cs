using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Purchases.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowPricelessPurchaseOrderLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "unit_cost",
                schema: "purchases",
                table: "purchase_order_lines",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(14,2)",
                oldPrecision: 14,
                oldScale: 2);
        }

        /// <inheritdoc />
        // Falla a propósito si hay filas sin costo (órdenes sin precio): volver atrás las convertiría en órdenes de costo 0.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "unit_cost",
                schema: "purchases",
                table: "purchase_order_lines",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(14,2)",
                oldPrecision: 14,
                oldScale: 2,
                oldNullable: true);
        }
    }
}
