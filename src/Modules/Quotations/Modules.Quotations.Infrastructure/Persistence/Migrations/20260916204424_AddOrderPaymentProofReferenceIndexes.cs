using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderPaymentProofReferenceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_order_payment_proofs_file",
                schema: "quotations",
                table: "order_payment_proofs",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "IX_order_payment_proofs_public_key",
                schema: "quotations",
                table: "order_payment_proofs",
                column: "public_storage_key");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_order_payment_proofs_file",
                schema: "quotations",
                table: "order_payment_proofs");

            migrationBuilder.DropIndex(
                name: "IX_order_payment_proofs_public_key",
                schema: "quotations",
                table: "order_payment_proofs");
        }
    }
}
