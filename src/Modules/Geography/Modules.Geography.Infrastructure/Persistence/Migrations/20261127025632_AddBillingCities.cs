using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Geography.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingCities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_cities",
                schema: "geography",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    siigo_country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    country_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    state_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    state_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    city_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    city_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_cities", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_cities_country_name",
                schema: "geography",
                table: "billing_cities",
                columns: new[] { "country_code", "normalized_name" });

            migrationBuilder.CreateIndex(
                name: "IX_billing_cities_country_state_city",
                schema: "geography",
                table: "billing_cities",
                columns: new[] { "country_code", "state_code", "city_code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_cities",
                schema: "geography");
        }
    }
}
