using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Shopify.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreEditSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "edit_payment_statuses",
                schema: "shopify",
                table: "stores",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{FullPaymentReceived,PartialPaymentReceived,PaymentPending,PendingVerification}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "edit_sync",
                schema: "shopify",
                table: "stores",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Manual");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "edit_payment_statuses",
                schema: "shopify",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "edit_sync",
                schema: "shopify",
                table: "stores");
        }
    }
}
