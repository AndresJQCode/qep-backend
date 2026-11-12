using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Store.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreShowPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "show_prices",
                schema: "store",
                table: "stores",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "show_prices",
                schema: "store",
                table: "stores");
        }
    }
}
