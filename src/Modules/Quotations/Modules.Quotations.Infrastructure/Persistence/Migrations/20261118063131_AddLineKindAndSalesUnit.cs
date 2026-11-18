using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLineKindAndSalesUnit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "quotations",
                table: "quotation_items",
                type: "text",
                nullable: false,
                defaultValue: "Physical");

            migrationBuilder.AddColumn<string>(
                name: "sales_unit",
                schema: "quotations",
                table: "quotation_items",
                type: "text",
                nullable: false,
                defaultValue: "Unit");

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "quotations",
                table: "order_items",
                type: "text",
                nullable: false,
                defaultValue: "Physical");

            migrationBuilder.AddColumn<string>(
                name: "sales_unit",
                schema: "quotations",
                table: "order_items",
                type: "text",
                nullable: false,
                defaultValue: "Unit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "kind",
                schema: "quotations",
                table: "quotation_items");

            migrationBuilder.DropColumn(
                name: "sales_unit",
                schema: "quotations",
                table: "quotation_items");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "quotations",
                table: "order_items");

            migrationBuilder.DropColumn(
                name: "sales_unit",
                schema: "quotations",
                table: "order_items");
        }
    }
}
