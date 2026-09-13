using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQuotationPartyTaxProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "party_vat_surplus",
                schema: "quotations",
                table: "quotations",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "party_with_retention",
                schema: "quotations",
                table: "quotations",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "party_vat_surplus",
                schema: "quotations",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "party_with_retention",
                schema: "quotations",
                table: "quotations");
        }
    }
}
