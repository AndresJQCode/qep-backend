using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWithholdingAppliesTo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "applies_to",
                schema: "quotations",
                table: "quotation_withholdings",
                type: "text",
                nullable: false,
                defaultValue: "All");

            migrationBuilder.AddColumn<string>(
                name: "applies_to",
                schema: "quotations",
                table: "order_withholdings",
                type: "text",
                nullable: false,
                defaultValue: "All");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "applies_to",
                schema: "quotations",
                table: "quotation_withholdings");

            migrationBuilder.DropColumn(
                name: "applies_to",
                schema: "quotations",
                table: "order_withholdings");
        }
    }
}
