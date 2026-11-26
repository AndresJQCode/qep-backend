using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Wholesale.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceListExclusions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<Guid>>(
                name: "excluded_category_ids",
                schema: "wholesale",
                table: "price_lists",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<List<Guid>>(
                name: "excluded_product_ids",
                schema: "wholesale",
                table: "price_lists",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "excluded_category_ids",
                schema: "wholesale",
                table: "price_lists");

            migrationBuilder.DropColumn(
                name: "excluded_product_ids",
                schema: "wholesale",
                table: "price_lists");
        }
    }
}
