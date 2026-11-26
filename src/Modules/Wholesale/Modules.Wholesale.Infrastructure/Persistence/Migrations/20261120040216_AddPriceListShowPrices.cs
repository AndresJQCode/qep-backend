using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wholesale.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceListShowPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "show_prices",
                schema: "wholesale",
                table: "price_lists",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "show_prices",
                schema: "wholesale",
                table: "price_lists");
        }
    }
}
