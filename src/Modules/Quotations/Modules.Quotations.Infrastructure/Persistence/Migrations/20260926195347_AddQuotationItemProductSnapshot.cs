using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuotationItemProductSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "product_code",
                schema: "quotations",
                table: "quotation_items",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "product_name",
                schema: "quotations",
                table: "quotation_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "product_code",
                schema: "quotations",
                table: "quotation_items");

            migrationBuilder.DropColumn(
                name: "product_name",
                schema: "quotations",
                table: "quotation_items");
        }
    }
}
