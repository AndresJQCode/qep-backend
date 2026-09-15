using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameToOrders : Migration
    {
        // Escrita a mano (spec 2026-09-14; plan, Task 2). EF emparejó por nombre de tabla y de tipo, y
        // cambiaron los dos a la vez: lo que generó era DropTable + CreateTable, que borraba los datos.
        // Esto sólo renombra. PK y FK van por SQL porque EF no tiene operación para renombrarlas; en
        // una PK, RENAME CONSTRAINT renombra también su índice.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(name: "sales", schema: "quotations", newName: "orders", newSchema: "quotations");
            migrationBuilder.RenameTable(name: "sale_payment_proofs", schema: "quotations", newName: "order_payment_proofs", newSchema: "quotations");
            migrationBuilder.RenameTable(name: "sale_number_counters", schema: "quotations", newName: "order_number_counters", newSchema: "quotations");

            migrationBuilder.RenameColumn(name: "sale_number", schema: "quotations", table: "orders", newName: "order_number");
            migrationBuilder.RenameColumn(name: "sale_id", schema: "quotations", table: "order_payment_proofs", newName: "order_id");

            migrationBuilder.RenameIndex(name: "IX_sales_tenant", schema: "quotations", table: "orders", newName: "IX_orders_tenant");
            migrationBuilder.RenameIndex(name: "IX_sales_quotation", schema: "quotations", table: "orders", newName: "IX_orders_quotation");
            migrationBuilder.RenameIndex(name: "IX_sales_tenant_number", schema: "quotations", table: "orders", newName: "IX_orders_tenant_number");
            migrationBuilder.RenameIndex(name: "IX_sales_tenant_converted_at_number", schema: "quotations", table: "orders", newName: "IX_orders_tenant_converted_at_number");
            migrationBuilder.RenameIndex(name: "IX_sale_payment_proofs_sale", schema: "quotations", table: "order_payment_proofs", newName: "IX_order_payment_proofs_order");

            migrationBuilder.Sql("""ALTER TABLE quotations.orders RENAME CONSTRAINT "PK_sales" TO "PK_orders";""");
            migrationBuilder.Sql("""ALTER TABLE quotations.order_payment_proofs RENAME CONSTRAINT "PK_sale_payment_proofs" TO "PK_order_payment_proofs";""");
            migrationBuilder.Sql("""ALTER TABLE quotations.order_number_counters RENAME CONSTRAINT "PK_sale_number_counters" TO "PK_order_number_counters";""");
            migrationBuilder.Sql("""ALTER TABLE quotations.orders RENAME CONSTRAINT "FK_sales_quotations_quotation_id" TO "FK_orders_quotations_quotation_id";""");
            migrationBuilder.Sql("""ALTER TABLE quotations.order_payment_proofs RENAME CONSTRAINT "FK_sale_payment_proofs_sales_sale_id" TO "FK_order_payment_proofs_orders_order_id";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE quotations.order_payment_proofs RENAME CONSTRAINT "FK_order_payment_proofs_orders_order_id" TO "FK_sale_payment_proofs_sales_sale_id";""");
            migrationBuilder.Sql("""ALTER TABLE quotations.orders RENAME CONSTRAINT "FK_orders_quotations_quotation_id" TO "FK_sales_quotations_quotation_id";""");
            migrationBuilder.Sql("""ALTER TABLE quotations.order_number_counters RENAME CONSTRAINT "PK_order_number_counters" TO "PK_sale_number_counters";""");
            migrationBuilder.Sql("""ALTER TABLE quotations.order_payment_proofs RENAME CONSTRAINT "PK_order_payment_proofs" TO "PK_sale_payment_proofs";""");
            migrationBuilder.Sql("""ALTER TABLE quotations.orders RENAME CONSTRAINT "PK_orders" TO "PK_sales";""");

            migrationBuilder.RenameIndex(name: "IX_order_payment_proofs_order", schema: "quotations", table: "order_payment_proofs", newName: "IX_sale_payment_proofs_sale");
            migrationBuilder.RenameIndex(name: "IX_orders_tenant_converted_at_number", schema: "quotations", table: "orders", newName: "IX_sales_tenant_converted_at_number");
            migrationBuilder.RenameIndex(name: "IX_orders_tenant_number", schema: "quotations", table: "orders", newName: "IX_sales_tenant_number");
            migrationBuilder.RenameIndex(name: "IX_orders_quotation", schema: "quotations", table: "orders", newName: "IX_sales_quotation");
            migrationBuilder.RenameIndex(name: "IX_orders_tenant", schema: "quotations", table: "orders", newName: "IX_sales_tenant");

            migrationBuilder.RenameColumn(name: "order_id", schema: "quotations", table: "order_payment_proofs", newName: "sale_id");
            migrationBuilder.RenameColumn(name: "order_number", schema: "quotations", table: "orders", newName: "sale_number");

            migrationBuilder.RenameTable(name: "order_number_counters", schema: "quotations", newName: "sale_number_counters", newSchema: "quotations");
            migrationBuilder.RenameTable(name: "order_payment_proofs", schema: "quotations", newName: "sale_payment_proofs", newSchema: "quotations");
            migrationBuilder.RenameTable(name: "orders", schema: "quotations", newName: "sales", newSchema: "quotations");
        }
    }
}
