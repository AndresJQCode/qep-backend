using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleLineCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cost_captured_at",
                schema: "pos",
                table: "sale_lines",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cost_source",
                schema: "pos",
                table: "sale_lines",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "unit_cost",
                schema: "pos",
                table: "sale_lines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cost_captured_at",
                schema: "pos",
                table: "sale_lines");

            migrationBuilder.DropColumn(
                name: "cost_source",
                schema: "pos",
                table: "sale_lines");

            migrationBuilder.DropColumn(
                name: "unit_cost",
                schema: "pos",
                table: "sale_lines");
        }
    }
}
