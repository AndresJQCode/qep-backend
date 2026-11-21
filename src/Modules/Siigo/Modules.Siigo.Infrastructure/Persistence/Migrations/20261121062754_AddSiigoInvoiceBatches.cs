using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Siigo.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSiigoInvoiceBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invoice_batches",
                schema: "siigo",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_batches", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "invoice_batch_items",
                schema: "siigo",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    missing_codes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    invoice_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    available_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_batch_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_invoice_batch_items_invoice_batches_batch_id",
                        column: x => x.batch_id,
                        principalSchema: "siigo",
                        principalTable: "invoice_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_invoice_batch_items_batch_order",
                schema: "siigo",
                table: "invoice_batch_items",
                columns: new[] { "batch_id", "order_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoice_batch_items_claim",
                schema: "siigo",
                table: "invoice_batch_items",
                columns: new[] { "status", "available_at" },
                filter: "status IN ('Pending', 'Sending')");

            migrationBuilder.CreateIndex(
                name: "IX_invoice_batches_tenant_created",
                schema: "siigo",
                table: "invoice_batches",
                columns: new[] { "tenant_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invoice_batch_items",
                schema: "siigo");

            migrationBuilder.DropTable(
                name: "invoice_batches",
                schema: "siigo");
        }
    }
}
