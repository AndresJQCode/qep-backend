using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductKindAndSalesUnit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "catalog",
                table: "products",
                type: "text",
                nullable: false,
                defaultValue: "Physical");

            migrationBuilder.AddColumn<string>(
                name: "sales_unit",
                schema: "catalog",
                table: "products",
                type: "text",
                nullable: false,
                defaultValue: "Unit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "kind",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "sales_unit",
                schema: "catalog",
                table: "products");
        }
    }
}
