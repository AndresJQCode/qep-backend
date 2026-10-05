using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrdersExportLayoutSheetName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "sheet_name",
                schema: "quotations",
                table: "orders_export_layouts",
                type: "character varying(31)",
                maxLength: 31,
                nullable: false,
                defaultValue: "Pedidos");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "sheet_name",
                schema: "quotations",
                table: "orders_export_layouts");
        }
    }
}
