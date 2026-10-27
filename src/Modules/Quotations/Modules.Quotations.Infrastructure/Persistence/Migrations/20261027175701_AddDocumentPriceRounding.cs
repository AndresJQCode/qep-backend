using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentPriceRounding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "price_rounding_decimals",
                schema: "quotations",
                table: "quotations",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "price_rounding_mode",
                schema: "quotations",
                table: "quotations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "price_rounding_decimals",
                schema: "quotations",
                table: "orders",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "price_rounding_mode",
                schema: "quotations",
                table: "orders",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "price_rounding_decimals",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "price_rounding_mode",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "price_rounding_decimals",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "price_rounding_mode",
                schema: "quotations",
                table: "orders");
        }
    }
}
