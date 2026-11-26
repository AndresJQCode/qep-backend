using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyPostalCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "postal_code",
                schema: "quotations",
                table: "quotation_parties",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "postal_code",
                schema: "quotations",
                table: "order_parties",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "postal_code",
                schema: "quotations",
                table: "quotation_parties");

            migrationBuilder.DropColumn(
                name: "postal_code",
                schema: "quotations",
                table: "order_parties");
        }
    }
}
