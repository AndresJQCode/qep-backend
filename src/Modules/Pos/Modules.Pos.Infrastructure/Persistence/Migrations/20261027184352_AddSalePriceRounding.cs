using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSalePriceRounding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "price_rounding_decimals",
                schema: "pos",
                table: "sales",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "price_rounding_mode",
                schema: "pos",
                table: "sales",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "price_rounding_decimals",
                schema: "pos",
                table: "sales");

            migrationBuilder.DropColumn(
                name: "price_rounding_mode",
                schema: "pos",
                table: "sales");
        }
    }
}
