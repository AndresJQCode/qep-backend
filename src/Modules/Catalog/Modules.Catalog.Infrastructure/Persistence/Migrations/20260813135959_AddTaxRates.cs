using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Catalog.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaxRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tax_rates",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    percentage = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tax_rates", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tax_rates_tenant",
                schema: "catalog",
                table: "tax_rates",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_tax_rates_tenant_name",
                schema: "catalog",
                table: "tax_rates",
                columns: new[] { "tenant_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tax_rates",
                schema: "catalog");
        }
    }
}
