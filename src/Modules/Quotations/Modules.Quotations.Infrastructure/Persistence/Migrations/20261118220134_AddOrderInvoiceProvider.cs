using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderInvoiceProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "invoice_external_id",
                schema: "quotations",
                table: "orders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "invoice_number",
                schema: "quotations",
                table: "orders",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "invoice_provider",
                schema: "quotations",
                table: "orders",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "invoice_external_id",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "invoice_number",
                schema: "quotations",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "invoice_provider",
                schema: "quotations",
                table: "orders");
        }
    }
}
