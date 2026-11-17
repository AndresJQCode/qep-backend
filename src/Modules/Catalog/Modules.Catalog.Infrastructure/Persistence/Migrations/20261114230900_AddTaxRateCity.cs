using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaxRateCity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "city_id",
                schema: "catalog",
                table: "tax_rates",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tax_rates_tenant_type_city",
                schema: "catalog",
                table: "tax_rates",
                columns: new[] { "tenant_id", "type", "city_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tax_rates_tenant_type_city",
                schema: "catalog",
                table: "tax_rates");

            migrationBuilder.DropColumn(
                name: "city_id",
                schema: "catalog",
                table: "tax_rates");
        }
    }
}
