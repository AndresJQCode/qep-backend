using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLineCostAndPaidAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "paid_at",
                schema: "quotations",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cost_captured_at",
                schema: "quotations",
                table: "order_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cost_source",
                schema: "quotations",
                table: "order_items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "unit_cost",
                schema: "quotations",
                table: "order_items",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            // Spec costo de producto §4.4: los pedidos ya pagados entran al reporte con la fecha de la marca de
            // pagado y, si no la tienen (los de la plataforma no la llenan), con la de conversión. Sus líneas no
            // congelan: el costo de antes nunca se registró y no se inventa.
            migrationBuilder.Sql("""
                UPDATE quotations.orders
                SET paid_at = COALESCE(paid_marked_at, converted_at)
                WHERE payment_status = 'FullPaymentReceived';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_orders_tenant_paid_at",
                schema: "quotations",
                table: "orders",
                columns: new[] { "tenant_id", "paid_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_orders_tenant_paid_at",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "paid_at",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "cost_captured_at",
                schema: "quotations",
                table: "order_items");

            migrationBuilder.DropColumn(
                name: "cost_source",
                schema: "quotations",
                table: "order_items");

            migrationBuilder.DropColumn(
                name: "unit_cost",
                schema: "quotations",
                table: "order_items");
        }
    }
}
