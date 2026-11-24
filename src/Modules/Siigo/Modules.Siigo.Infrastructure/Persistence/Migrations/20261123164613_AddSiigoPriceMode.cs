using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Siigo.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSiigoPriceMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "price_mode",
                schema: "siigo",
                table: "settings",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "BaseWithDiscount");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "price_mode",
                schema: "siigo",
                table: "settings");
        }
    }
}
