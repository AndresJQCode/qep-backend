using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Quotations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialQuotations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "quotations");

            migrationBuilder.CreateTable(
                name: "quotation_number_counters",
                schema: "quotations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    next_value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotation_number_counters", x => new { x.tenant_id, x.year });
                });

            migrationBuilder.CreateTable(
                name: "quotations",
                schema: "quotations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quotation_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    advisor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: true),
                    payment_method = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    subtotal = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    tax_percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    total = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    billing_name_override = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    billing_address_override = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    delivery_address_override = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    delivery_city_override = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "quotation_history",
                schema: "quotations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    quotation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    details = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotation_history", x => x.id);
                    table.ForeignKey(
                        name: "FK_quotation_history_quotations_quotation_id",
                        column: x => x.quotation_id,
                        principalSchema: "quotations",
                        principalTable: "quotations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quotation_items",
                schema: "quotations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    quotation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    discount_percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotation_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_quotation_items_quotations_quotation_id",
                        column: x => x.quotation_id,
                        principalSchema: "quotations",
                        principalTable: "quotations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_quotation_history_quotation_event_at",
                schema: "quotations",
                table: "quotation_history",
                columns: new[] { "quotation_id", "event_at" });

            migrationBuilder.CreateIndex(
                name: "IX_quotation_items_quotation",
                schema: "quotations",
                table: "quotation_items",
                column: "quotation_id");

            migrationBuilder.CreateIndex(
                name: "IX_quotations_advisor",
                schema: "quotations",
                table: "quotations",
                column: "advisor_id");

            migrationBuilder.CreateIndex(
                name: "IX_quotations_client",
                schema: "quotations",
                table: "quotations",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "IX_quotations_created_at",
                schema: "quotations",
                table: "quotations",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_quotations_status",
                schema: "quotations",
                table: "quotations",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_quotations_tenant",
                schema: "quotations",
                table: "quotations",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_quotations_tenant_number",
                schema: "quotations",
                table: "quotations",
                columns: new[] { "tenant_id", "quotation_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "quotation_history",
                schema: "quotations");

            migrationBuilder.DropTable(
                name: "quotation_items",
                schema: "quotations");

            migrationBuilder.DropTable(
                name: "quotation_number_counters",
                schema: "quotations");

            migrationBuilder.DropTable(
                name: "quotations",
                schema: "quotations");
        }
    }
}
