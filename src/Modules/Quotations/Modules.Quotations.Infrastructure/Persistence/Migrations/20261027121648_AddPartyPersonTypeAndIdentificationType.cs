using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPartyPersonTypeAndIdentificationType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "identification_type",
                schema: "quotations",
                table: "quotation_parties",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "person_type",
                schema: "quotations",
                table: "quotation_parties",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "identification_type",
                schema: "quotations",
                table: "order_parties",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "person_type",
                schema: "quotations",
                table: "order_parties",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "identification_type",
                schema: "quotations",
                table: "quotation_parties");

            migrationBuilder.DropColumn(
                name: "person_type",
                schema: "quotations",
                table: "quotation_parties");

            migrationBuilder.DropColumn(
                name: "identification_type",
                schema: "quotations",
                table: "order_parties");

            migrationBuilder.DropColumn(
                name: "person_type",
                schema: "quotations",
                table: "order_parties");
        }
    }
}
