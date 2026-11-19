using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Purchases.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseOrderEmailResult : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_email_at",
                schema: "purchases",
                table: "purchase_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_email_status",
                schema: "purchases",
                table: "purchase_orders",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_email_to",
                schema: "purchases",
                table: "purchase_orders",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_email_at",
                schema: "purchases",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "last_email_status",
                schema: "purchases",
                table: "purchase_orders");

            migrationBuilder.DropColumn(
                name: "last_email_to",
                schema: "purchases",
                table: "purchase_orders");
        }
    }
}
