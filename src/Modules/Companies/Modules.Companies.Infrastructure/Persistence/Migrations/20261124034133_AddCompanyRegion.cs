using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Companies.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyRegion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_companies_city_lane",
                schema: "companies",
                table: "companies");

            migrationBuilder.AddColumn<Guid>(
                name: "region_id",
                schema: "companies",
                table: "companies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_companies_city_lane",
                schema: "companies",
                table: "companies",
                sql: "(country = 'CO' AND city_id IS NOT NULL AND city_name IS NULL AND region_id IS NULL) OR (country <> 'CO' AND city_id IS NULL AND city_name IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_companies_city_lane",
                schema: "companies",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "region_id",
                schema: "companies",
                table: "companies");

            migrationBuilder.AddCheckConstraint(
                name: "CK_companies_city_lane",
                schema: "companies",
                table: "companies",
                sql: "(country = 'CO' AND city_id IS NOT NULL AND city_name IS NULL) OR (country <> 'CO' AND city_id IS NULL AND city_name IS NOT NULL)");
        }
    }
}
