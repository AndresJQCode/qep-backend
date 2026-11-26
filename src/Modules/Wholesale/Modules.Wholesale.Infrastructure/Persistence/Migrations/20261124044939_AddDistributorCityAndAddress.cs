using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wholesale.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDistributorCityAndAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "address",
                schema: "wholesale",
                table: "distributors",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "city_id",
                schema: "wholesale",
                table: "distributors",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "city_name",
                schema: "wholesale",
                table: "distributors",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "address",
                schema: "wholesale",
                table: "distributors");

            migrationBuilder.DropColumn(
                name: "city_id",
                schema: "wholesale",
                table: "distributors");

            migrationBuilder.DropColumn(
                name: "city_name",
                schema: "wholesale",
                table: "distributors");
        }
    }
}
