using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderBuyerAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "buyer_account_id",
                schema: "quotations",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_tenant_buyer_created",
                schema: "quotations",
                table: "orders",
                columns: new[] { "tenant_id", "buyer_account_id", "created_at" },
                filter: "buyer_account_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_orders_tenant_buyer_created",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "buyer_account_id",
                schema: "quotations",
                table: "orders");
        }
    }
}
