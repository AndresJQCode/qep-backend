using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderExternalKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_key",
                schema: "quotations",
                table: "orders",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_shipping_title",
                schema: "quotations",
                table: "orders",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_tenant_external_key",
                schema: "quotations",
                table: "orders",
                columns: new[] { "tenant_id", "source", "external_key" },
                unique: true,
                filter: "external_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_orders_tenant_external_key",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "external_key",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "external_shipping_title",
                schema: "quotations",
                table: "orders");
        }
    }
}
