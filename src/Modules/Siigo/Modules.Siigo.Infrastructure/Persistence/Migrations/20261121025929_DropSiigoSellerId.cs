using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Siigo.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropSiigoSellerId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "seller_id",
                schema: "siigo",
                table: "settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "seller_id",
                schema: "siigo",
                table: "settings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }
    }
}
