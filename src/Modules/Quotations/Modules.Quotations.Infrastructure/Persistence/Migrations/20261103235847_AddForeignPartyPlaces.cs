using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddForeignPartyPlaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "city_name",
                schema: "quotations",
                table: "quotation_parties",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "country",
                schema: "quotations",
                table: "quotation_parties",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "city_name",
                schema: "quotations",
                table: "order_parties",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "country",
                schema: "quotations",
                table: "order_parties",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "city_name",
                schema: "quotations",
                table: "quotation_parties");

            migrationBuilder.DropColumn(
                name: "country",
                schema: "quotations",
                table: "quotation_parties");

            migrationBuilder.DropColumn(
                name: "city_name",
                schema: "quotations",
                table: "order_parties");

            migrationBuilder.DropColumn(
                name: "country",
                schema: "quotations",
                table: "order_parties");
        }
    }
}
