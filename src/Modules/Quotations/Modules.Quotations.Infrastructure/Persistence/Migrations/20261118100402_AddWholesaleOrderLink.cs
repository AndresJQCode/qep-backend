using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWholesaleOrderLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_orders_source",
                schema: "quotations",
                table: "orders");

            migrationBuilder.AddColumn<Guid>(
                name: "buyer_tenant_id",
                schema: "quotations",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "purchase_order_id",
                schema: "quotations",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "wholesale_approved_at",
                schema: "quotations",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "wholesale_approved_by",
                schema: "quotations",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_purchase_order_line_id",
                schema: "quotations",
                table: "order_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_orders_source",
                schema: "quotations",
                table: "orders",
                sql: "source IN ('Quotation','Manual','Shopify','Store','Wholesale')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_orders_source",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "buyer_tenant_id",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "purchase_order_id",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "wholesale_approved_at",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "wholesale_approved_by",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "source_purchase_order_line_id",
                schema: "quotations",
                table: "order_items");

            migrationBuilder.AddCheckConstraint(
                name: "CK_orders_source",
                schema: "quotations",
                table: "orders",
                sql: "source IN ('Quotation','Manual','Shopify','Store')");
        }
    }
}
