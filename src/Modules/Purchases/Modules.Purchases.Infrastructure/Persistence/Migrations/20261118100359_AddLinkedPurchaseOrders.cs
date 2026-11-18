using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Purchases.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkedPurchaseOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "linked_order_id",
                schema: "purchases",
                table: "purchase_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "linked_order_number",
                schema: "purchases",
                table: "purchase_orders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "supplier_tenant_id",
                schema: "purchases",
                table: "purchase_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "variant_id",
                schema: "purchases",
                table: "purchase_order_lines",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<decimal>(
                name: "quantity",
                schema: "purchases",
                table: "purchase_order_lines",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(10,2)",
                oldPrecision: 10,
                oldScale: 2);

            migrationBuilder.AddColumn<bool>(
                name: "created_in_catalog",
                schema: "purchases",
                table: "purchase_order_lines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "list_base_price",
                schema: "purchases",
                table: "purchase_order_lines",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "list_discount_percent",
                schema: "purchases",
                table: "purchase_order_lines",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "list_unit_price",
                schema: "purchases",
                table: "purchase_order_lines",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "supplier_variant_id",
                schema: "purchases",
                table: "purchase_order_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "purchases",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claimed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_messages", x => new { x.consumer, x.message_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_tenant_linked_order",
                schema: "purchases",
                table: "purchase_orders",
                columns: new[] { "tenant_id", "linked_order_id" },
                filter: "linked_order_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "purchases");

            migrationBuilder.DropIndex(
                name: "IX_purchase_orders_tenant_linked_order",
                schema: "purchases",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "linked_order_id",
                schema: "purchases",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "linked_order_number",
                schema: "purchases",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "supplier_tenant_id",
                schema: "purchases",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "created_in_catalog",
                schema: "purchases",
                table: "purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "list_base_price",
                schema: "purchases",
                table: "purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "list_discount_percent",
                schema: "purchases",
                table: "purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "list_unit_price",
                schema: "purchases",
                table: "purchase_order_lines");

            migrationBuilder.DropColumn(
                name: "supplier_variant_id",
                schema: "purchases",
                table: "purchase_order_lines");

            migrationBuilder.AlterColumn<Guid>(
                name: "variant_id",
                schema: "purchases",
                table: "purchase_order_lines",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "quantity",
                schema: "purchases",
                table: "purchase_order_lines",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(10,2)",
                oldPrecision: 10,
                oldScale: 2,
                oldNullable: true);
        }
    }
}
