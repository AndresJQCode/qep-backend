using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSalesAndPaymentProofs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sale_number_counters",
                schema: "quotations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    next_value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_number_counters", x => new { x.tenant_id, x.year });
                });

            migrationBuilder.CreateTable(
                name: "sales",
                schema: "quotations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    quotation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payment_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    converted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    converted_by = table.Column<Guid>(type: "uuid", nullable: false),
                    ritual_collection_sync_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sales", x => x.id);
                    table.ForeignKey(
                        name: "FK_sales_quotations_quotation_id",
                        column: x => x.quotation_id,
                        principalSchema: "quotations",
                        principalTable: "quotations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sale_payment_proofs",
                schema: "quotations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sale_payment_proofs", x => x.id);
                    table.ForeignKey(
                        name: "FK_sale_payment_proofs_sales_sale_id",
                        column: x => x.sale_id,
                        principalSchema: "quotations",
                        principalTable: "sales",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sale_payment_proofs_sale",
                schema: "quotations",
                table: "sale_payment_proofs",
                column: "sale_id");

            migrationBuilder.CreateIndex(
                name: "IX_sales_quotation",
                schema: "quotations",
                table: "sales",
                column: "quotation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sales_tenant",
                schema: "quotations",
                table: "sales",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_sales_tenant_number",
                schema: "quotations",
                table: "sales",
                columns: new[] { "tenant_id", "sale_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sale_number_counters",
                schema: "quotations");

            migrationBuilder.DropTable(
                name: "sale_payment_proofs",
                schema: "quotations");

            migrationBuilder.DropTable(
                name: "sales",
                schema: "quotations");
        }
    }
}
