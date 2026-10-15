using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Pipeline.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDealOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_deals_source",
                schema: "pipeline",
                table: "deals");

            migrationBuilder.CreateTable(
                name: "deal_orders",
                schema: "pipeline",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    deal_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    total = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deal_orders", x => new { x.tenant_id, x.order_id });
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_deals_source",
                schema: "pipeline",
                table: "deals",
                sql: "source IN ('Manual','Conversation','Quotation','Order')");

            migrationBuilder.CreateIndex(
                name: "IX_deal_orders_customer",
                schema: "pipeline",
                table: "deal_orders",
                columns: new[] { "tenant_id", "customer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_deal_orders_deal",
                schema: "pipeline",
                table: "deal_orders",
                columns: new[] { "tenant_id", "deal_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deal_orders",
                schema: "pipeline");

            migrationBuilder.DropCheckConstraint(
                name: "CK_deals_source",
                schema: "pipeline",
                table: "deals");

            migrationBuilder.AddCheckConstraint(
                name: "CK_deals_source",
                schema: "pipeline",
                table: "deals",
                sql: "source IN ('Manual','Conversation','Quotation')");
        }
    }
}
