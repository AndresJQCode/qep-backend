using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "currency",
                schema: "catalog",
                table: "products",
                type: "character(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "description",
                schema: "catalog",
                table: "products",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "image_file_id",
                schema: "catalog",
                table: "products",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "price",
                schema: "catalog",
                table: "products",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tax_rate_id",
                schema: "catalog",
                table: "products",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_products_tax_rate_id",
                schema: "catalog",
                table: "products",
                column: "tax_rate_id");

            migrationBuilder.AddForeignKey(
                name: "FK_products_tax_rates_tax_rate_id",
                schema: "catalog",
                table: "products",
                column: "tax_rate_id",
                principalSchema: "catalog",
                principalTable: "tax_rates",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_products_tax_rates_tax_rate_id",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropIndex(
                name: "IX_products_tax_rate_id",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "currency",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "description",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "image_file_id",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "price",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "tax_rate_id",
                schema: "catalog",
                table: "products");
        }
    }
}
